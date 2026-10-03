using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Core;

public sealed record ModpackFile(string Path, IReadOnlyList<string> Urls, long Size, string? Sha1, string? Sha512);
public sealed record ModpackDefinition(string Name, string Minecraft, LoaderCatalogVersion? Loader,
    IReadOnlyList<ModpackFile> Files, string Overrides, bool IsModrinth, string? OptiFine = null);

/// <summary>Standard MRPACK/CurseForge manifests; game installation remains in CmlLib.</summary>
public sealed class ModpackService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string Text(JsonElement value, string name) => DownloadCatalogService.Str(value, name);

    /// <summary>Identify an archive from its manifest, independently of its extension and without network requests.</summary>
    public static async Task<string> InspectArchiveAsync(string archivePath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("modrinth.index.json") ?? archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("未识别到整合包清单，支持 Modrinth／CurseForge 的 ZIP 和 MRPACK");
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包清单过大");
        await using var input = entry.Open();
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: token).ConfigureAwait(false);
        var json = document.RootElement;
        if (!json.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("整合包文件清单无效");
        if (entry.Name == "modrinth.index.json" && Text(json, "game") == "minecraft"
            && json.TryGetProperty("formatVersion", out var format) && format.TryGetInt32(out var version) && version == 1
            && json.TryGetProperty("dependencies", out var dependencies) && Text(dependencies, "minecraft").Length > 0)
            return "Modrinth";
        if (entry.Name == "manifest.json" && Text(json, "manifestType") == "minecraftModpack"
            && json.TryGetProperty("manifestVersion", out var manifest) && manifest.TryGetInt32(out var manifestVersion) && manifestVersion == 1
            && json.TryGetProperty("minecraft", out var minecraft) && Text(minecraft, "version").Length > 0)
            return "CurseForge";
        throw new InvalidDataException("不支持的整合包清单格式");
    }

    public async Task<ModpackDefinition> ReadAsync(string archivePath, DownloadSources sources,
        DownloadCatalogService catalog, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry("modrinth.index.json") ?? archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("此文件不是 Modrinth 或 CurseForge 整合包");
        if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("整合包清单过大");
        await using var input = entry.Open();
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: token).ConfigureAwait(false);
        var json = document.RootElement;
        if (entry.Name == "modrinth.index.json")
        {
            if (Text(json, "game") != "minecraft" || json.GetProperty("formatVersion").GetInt32() != 1)
                throw new InvalidDataException("不支持的整合包格式");
            var dependencies = json.GetProperty("dependencies");
            var mc = Text(dependencies, "minecraft");
            var loader = ParseLoader(dependencies, mc);
            var files = new List<ModpackFile>();
            foreach (var file in json.GetProperty("files").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (file.TryGetProperty("env", out var env) && Text(env, "client") == "unsupported") continue;
                var hashes = file.GetProperty("hashes");
                var sha1 = Text(hashes, "sha1"); var sha512 = Text(hashes, "sha512");
                if (sha1.Length != 40 || sha512.Length != 128 || !sha1.All(Uri.IsHexDigit) || !sha512.All(Uri.IsHexDigit))
                    throw new InvalidDataException("整合包文件缺少有效校验信息");
                files.Add(new(Text(file, "path"), file.GetProperty("downloads").EnumerateArray().Select(v => v.GetString()!).ToArray(),
                    file.GetProperty("fileSize").GetInt64(), sha1, sha512));
            }
            ValidateFiles(files);
            string? optifine = null;
            if (archive.GetEntry("ikun.extra.json") is { } extra)
            {
                await using var stream = extra.Open();
                using var info = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                optifine = Text(info.RootElement, "optifine") is { Length: > 0 } edition ? edition : null;
            }
            return new(Text(json, "name"), mc, loader, files, "overrides", true, optifine);
        }
        if (Text(json, "manifestType") != "minecraftModpack" || json.GetProperty("manifestVersion").GetInt32() != 1)
            throw new InvalidDataException("不支持的 CurseForge 整合包格式");
        var minecraft = json.GetProperty("minecraft");
        var minecraftVersion = Text(minecraft, "version");
        var loaders = minecraft.GetProperty("modLoaders").EnumerateArray().ToArray();
        var primary = loaders.FirstOrDefault(v => v.TryGetProperty("primary", out var p) && p.GetBoolean());
        if (primary.ValueKind == JsonValueKind.Undefined && loaders.Length == 1) primary = loaders[0];
        LoaderCatalogVersion? curseLoader = null;
        if (loaders.Length > 0)
        {
            if (primary.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("整合包没有明确的主加载器");
            var coordinate = Text(primary, "id"); var dash = coordinate.IndexOf('-');
            if (dash <= 0) throw new InvalidDataException("整合包加载器格式无效");
            curseLoader = MakeLoader(coordinate[..dash], coordinate[(dash + 1)..], minecraftVersion);
        }
        var references = json.GetProperty("files").EnumerateArray().ToArray();
        var knownFiles = new Dictionary<string, ContentRelease>();
        try
        {
            foreach (var release in await catalog.CurseForgeFilesAsync(references.Select(file => file.GetProperty("fileID").GetInt64()), sources, token).ConfigureAwait(false)) knownFiles[release.Id] = release;
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or IOException or TimeoutException or InvalidOperationException) { }
        // The existing source adapter supplies MCIM/official fallback and cancellation.
        var result = new ModpackFile[references.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, references.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, ct) =>
        {
            var reference = references[index];
            var release = knownFiles.GetValueOrDefault(reference.GetProperty("fileID").ToString()) ?? await catalog.ReleaseAsync(ContentPlatform.CurseForge, reference.GetProperty("projectID").ToString(),
                reference.GetProperty("fileID").ToString(), CatalogKind.Mod, sources, ct).ConfigureAwait(false);
            if (release.ProjectId != reference.GetProperty("projectID").ToString()) throw new InvalidDataException("整合包文件项目不匹配");
            var file = release.Files.FirstOrDefault() ?? throw new IOException("整合包中的文件未开放下载：" + release.Name);
            var folder = file.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods" : "resourcepacks";
            if (folder == "resourcepacks")
            {
                var project = await sources.JsonAsync($"https://api.curseforge.com/v1/mods/{reference.GetProperty("projectID")}", ct).ConfigureAwait(false);
                if (project.GetProperty("data").TryGetProperty("classId", out var category) && category.TryGetInt32(out var classId) && classId == 6552) folder = "shaderpacks";
            }
            result[index] = new(folder + "/" + file.Name, [file.Url], file.Size, file.Sha1, file.Sha512);
        }).ConfigureAwait(false);
        ValidateFiles(result);
        var overrides = Text(json, "overrides").TrimEnd('/');
        if (overrides.Length == 0) overrides = "overrides";
        SafePath(Path.GetTempPath(), overrides);
        return new(Text(json, "name"), minecraftVersion, curseLoader, result, overrides, false);
    }

    private static LoaderCatalogVersion? ParseLoader(JsonElement dependencies, string mc)
    {
        var loaders = dependencies.EnumerateObject().Where(p => p.Name != "minecraft").ToArray();
        if (loaders.Length > 1) throw new InvalidDataException("不支持多个加载器组合的整合包");
        return loaders.Length == 0 ? null : MakeLoader(loaders[0].Name, loaders[0].Value.GetString() ?? "", mc);
    }
    private static LoaderCatalogVersion MakeLoader(string kind, string version, string mc)
    {
        var label = kind.ToLowerInvariant() switch { "forge" => "Forge", "fabric" or "fabric-loader" => "Fabric", "neoforge" => "NeoForge", _ => throw new InvalidDataException("暂不支持此整合包加载器：" + kind) };
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(mc)) throw new InvalidDataException("整合包缺少游戏或加载器版本");
        return new(version, mc, label);
    }
    private static void ValidateFiles(IEnumerable<ModpackFile> files)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            SafePath(Path.GetTempPath(), file.Path);
            if (!paths.Add(file.Path.Replace('\\', '/'))) throw new InvalidDataException("整合包文件路径重复：" + file.Path);
            if (file.Size < 0 || file.Urls.Count == 0 || file.Urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
                throw new InvalidDataException("整合包文件下载地址无效：" + file.Path);
            if (string.IsNullOrEmpty(file.Sha1) && string.IsNullOrEmpty(file.Sha512)) throw new InvalidDataException("整合包文件缺少校验信息：" + file.Path);
        }
    }
    public static string SafePath(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("整合包包含无效路径：" + relative);
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length > 255 || stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
                throw new InvalidDataException("整合包包含无效文件名：" + part);
        }
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(parent, Path.Combine(parts)));
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("整合包路径超出目标目录");
        return target;
    }
    public Task ExtractOverridesAsync(string archivePath, ModpackDefinition pack, string target, string instance,
        IProgress<string>? progress, CancellationToken token) => Task.Run(async () =>
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var prefixes = pack.IsModrinth ? new[] { "overrides/", "client-overrides/" } : new[] { pack.Overrides + "/" };
        foreach (var prefix in prefixes)
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName[prefix.Length..].TrimEnd('/');
            if (relative.Length == 0) continue;
            var output = SafePath(target, relative);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("整合包包含符号链接");
            if (relative.Equals(instance + ".json", StringComparison.OrdinalIgnoreCase) || relative.Equals(instance + ".jar", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".ikun-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("整合包不能覆盖版本配置");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); progress?.Report("解压 " + relative);
            await using var input = entry.Open(); await using var file = File.Create(output);
            await input.CopyToAsync(file, token).ConfigureAwait(false);
        }
    }, token);

    public Task ExportAsync(VersionInfo version, string directory, string destination, IProgress<string>? progress,
        CancellationToken token) => Task.Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(version.MinecraftVersion)) throw new InvalidDataException("无法识别 Minecraft 版本");
        var dependencies = new JsonObject { ["minecraft"] = version.MinecraftVersion };
        if (version.Loader is "Forge" or "Fabric" or "NeoForge")
        {
            if (version.LoaderVersion.Length == 0) throw new InvalidDataException("无法识别加载器版本");
            var coordinate = version.LoaderVersion;
            if (version.Loader == "Forge" && coordinate.StartsWith(version.MinecraftVersion + "-", StringComparison.Ordinal)) coordinate = coordinate[(version.MinecraftVersion.Length + 1)..].Split('-')[0];
            dependencies[version.Loader == "Fabric" ? "fabric-loader" : version.Loader.ToLowerInvariant()] = coordinate;
        }
        else if (version.Loader is not ("原版" or "Vanilla" or "OptiFine")) throw new InvalidDataException("暂不支持导出此加载器");
        var folders = new[] { "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs", "scripts", "patchouli_books", "datapacks" };
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var files = folders.Where(folder => System.IO.Directory.Exists(Path.Combine(directory, folder)))
            .SelectMany(folder => System.IO.Directory.EnumerateFiles(Path.Combine(directory, folder), "*", enumeration))
            .Concat(new[] { "options.txt", "optionsof.txt", "optionsshaders.txt" }.Select(name => Path.Combine(directory, name)).Where(File.Exists)).ToArray();
        var outputPath = Path.GetFullPath(destination);
        if (files.Any(file => string.Equals(Path.GetFullPath(file), outputPath, StringComparison.OrdinalIgnoreCase))) throw new IOException("导出路径不能覆盖游戏文件");
        var temp = outputPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var index = archive.CreateEntry("modrinth.index.json");
                await using (var stream = index.Open())
                    await JsonSerializer.SerializeAsync(stream, new { formatVersion = 1, game = "minecraft", versionId = "1.0.0", name = version.GameName,
                        summary = "Exported with Novaina Launcher", files = Array.Empty<object>(), dependencies }, JsonOptions, token).ConfigureAwait(false);
                // OptiFine standalone has no standard MRPACK dependency; retain an explicit iKun extension.
                var optifine = FindOptiFine(version);
                if (optifine is not null && version.Loader == "OptiFine")
                {
                    await using var stream = archive.CreateEntry("ikun.extra.json").Open();
                    await JsonSerializer.SerializeAsync(stream, new { optifine }, cancellationToken: token).ConfigureAwait(false);
                }
                else if (version.Loader == "OptiFine") throw new InvalidDataException("无法识别 OptiFine 版本");
                int count = 0;
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested(); var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                    progress?.Report($"导出 {++count}/{files.Length} · {relative}");
                    var entry = archive.CreateEntry("overrides/" + relative, CompressionLevel.Fastest);
                    await using var input = File.OpenRead(file); await using var output = entry.Open();
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested(); File.Move(temp, outputPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }, token);
    private static string? FindOptiFine(VersionInfo version)
    {
        if (version.Loader != "OptiFine" || version.LoaderVersion.Length == 0) return null;
        var prefix = version.MinecraftVersion + "_";
        return version.LoaderVersion.StartsWith(prefix, StringComparison.Ordinal) ? version.LoaderVersion[prefix.Length..] : version.LoaderVersion;
    }
}
