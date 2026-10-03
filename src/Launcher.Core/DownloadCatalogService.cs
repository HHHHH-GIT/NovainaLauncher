using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using HtmlAgilityPack;

namespace Launcher.Core;

public sealed class DownloadCatalogService : IModCatalogService
{
    private readonly ChineseModSearchIndex _chineseNames;
    public DownloadCatalogService(ChineseModSearchIndex? chineseNames = null) => _chineseNames = chineseNames ?? new();
    public Task WarmChineseNamesAsync(CancellationToken token) => _chineseNames.WarmAsync(token);
    public DownloadCatalogItem Localize(DownloadCatalogItem item) => item.Kind == CatalogKind.Mod ? item with { ChineseName = _chineseNames.ChineseName(item.Name) } : item;
    private static string Q(string value) => Uri.EscapeDataString(value);
    internal static string Str(JsonElement json, string key) => json.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    private static string[] Strings(JsonElement json, string key) => json.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
    public async Task<IReadOnlyList<GameCatalogVersion>> GamesAsync(DownloadSources sources, CancellationToken token)
    {
        var json = await sources.JsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", token, TimeSpan.FromHours(1));
        Directory.CreateDirectory(Path.Combine(AppPaths.Data, "downloads"));
        await File.WriteAllTextAsync(Path.Combine(AppPaths.Data, "downloads", "manifest.json"), json.GetRawText(), token);
        return json.GetProperty("versions").EnumerateArray().Select(j => new GameCatalogVersion(Str(j, "id"), Str(j, "type"), Str(j, "url"), Str(j, "sha1"))).ToArray();
    }
    public async Task<IReadOnlyList<LoaderCatalogVersion>> LoadersAsync(string mc, string loader, DownloadSources sources, CancellationToken token)
    {
        if (loader == "Fabric")
        {
            var json = await sources.JsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{Q(mc)}", token);
            return json.EnumerateArray().Select(j => new LoaderCatalogVersion(Str(j.GetProperty("loader"), "version"), mc, loader)).ToArray();
        }
        if (loader == "Forge")
        {
            var list = await ForgeVersionsAsync(mc, sources, token);
            return list.Reverse().Select(v => new LoaderCatalogVersion(v.ForgeVersionName, mc, loader)).ToArray();
        }
        if (loader == "NeoForge")
        {
            if (!Version.TryParse(mc, out var v) || v.Major != 1 || v.Minor < 20) return [];
            if (mc == "1.20.1") return []; // Unsupported legacy NeoForge format is excluded before enqueue.
            // NeoForge's official release coordinate encodes the Minecraft minor/patch.
            string coordinate = mc == "1.20.1" ? "forge" : "neoforge";
            var xml = await sources.Http.GetStringAsync($"https://maven.neoforged.net/releases/net/neoforged/{coordinate}/maven-metadata.xml", token);
            string prefix = mc == "1.20.1" ? "1.20.1-" : $"{v.Minor}.{Math.Max(0, v.Build)}.";
            return XDocument.Parse(xml).Descendants("version").Select(e => e.Value).Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).Reverse().Select(s => new LoaderCatalogVersion(s, mc, loader)).ToArray();
        }
        return [];
    }
    public async Task<IReadOnlyList<ForgeVersion>> ForgeVersionsAsync(string mc, DownloadSources sources, CancellationToken token)
    {
        if (sources.Policy.GameMirror && Version.TryParse(mc, out var v) && v >= new Version(1, 12, 2))
            try
            {
                var json = await sources.JsonAsync($"https://bmclapi2.bangbang93.com/forge/minecraft/{Q(mc)}", token);
                return json.EnumerateArray().Select(j =>
                {
                    var forge = Str(j, "version");
                    var coordinate = mc + "-" + forge + (Str(j, "branch") is { Length: > 0 } branch ? "-" + branch : "");
                    return new ForgeVersion(mc, forge)
                    {
                        Files = j.GetProperty("files").EnumerateArray().Select(f => new ForgeVersionFile { Type = Str(f, "category"), SHA1 = Str(f, "hash"), DirectUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-{Str(f, "category")}.{Str(f, "format")}" }).ToArray()
                    };
                }).ToArray();
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or JsonException or TaskCanceledException or TimeoutException) { }
        var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(new MinecraftPath(Path.Combine(AppPaths.Data, "downloads", "catalog")), sources.Http));
        return (await new ForgeInstaller(launcher, sources.Http).GetForgeVersions(mc).WaitAsync(token)).Reverse().ToArray();
    }
    public async Task<IReadOnlyList<OptiFineCatalogVersion>> OptiFineAsync(string mc, DownloadSources sources, CancellationToken token)
    {
        if (sources.Policy.GameMirror)
            try
            {
                var json = await sources.JsonAsync($"https://bmclapi2.bangbang93.com/optifine/{Q(mc)}", token);
                return json.EnumerateArray().Select(j => new OptiFineCatalogVersion(Str(j, "mcversion"), Str(j, "type"), Str(j, "patch"), Str(j, "filename"), Str(j, "forge"))).ToArray();
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or JsonException or TaskCanceledException or TimeoutException) { }
        var document = new HtmlDocument(); document.LoadHtml(await sources.Http.GetStringAsync("https://optifine.net/downloads", token));
        var result = new List<OptiFineCatalogVersion>();
        foreach (var row in document.DocumentNode.SelectNodes("//tr[contains(@class,'downloadLine')]") ?? new HtmlNodeCollection(document.DocumentNode))
        {
            var url = row.SelectSingleNode(".//td[@class='colMirror']/a")?.GetAttributeValue("href", "") ?? "";
            var match = Regex.Match(url, @"[?&]f=((?:preview_)?OptiFine_([^_]+)_(.+)\.jar)");
            if (!match.Success || match.Groups[2].Value != mc) continue;
            var edition = match.Groups[3].Value; var split = edition.LastIndexOf('_'); if (split < 0) continue;
            result.Add(new(mc, edition[..split], edition[(split + 1)..], match.Groups[1].Value, HtmlEntity.DeEntitize(row.SelectSingleNode(".//td[@class='colForge']")?.InnerText ?? "").Trim()));
        }
        return result;
    }
    public async Task<string> OptiFineOfficialUrlAsync(OptiFineCatalogVersion version, DownloadSources sources, CancellationToken token)
    {
        var document = new HtmlDocument(); document.LoadHtml(await sources.Http.GetStringAsync("https://optifine.net/adloadx?f=" + Q(version.FileName), token));
        var link = document.DocumentNode.SelectSingleNode("//a[@onclick='onDownload()']")?.GetAttributeValue("href", null) ?? throw new InvalidDataException("无法获取 OptiFine 官方下载地址");
        var uri = new Uri(new Uri("https://optifine.net/"), link);
        if (uri.Host != "optifine.net" || uri.Scheme != "https") throw new InvalidDataException("OptiFine 下载地址无效");
        return uri.ToString();
    }
    public async Task<IReadOnlyList<DownloadCatalogItem>> SearchAsync(CatalogKind kind, ContentPlatform platform, string query, VersionInfo? target, DownloadSources sources, CancellationToken token, int offset = 0)
    {
        if (kind != CatalogKind.Mod || !ChineseModSearchIndex.ContainsChinese(query))
            return (await SearchRemoteAsync(kind, platform, query, target, sources, token, offset).ConfigureAwait(false)).Select(Localize).ToArray();
        var aliases = await _chineseNames.ResolveAsync(query, token).ConfigureAwait(false);
        var queries = aliases.Append(query.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Exception? failure = null;
        var succeeded = false;
        // Search the best translation first; other aliases/original text are fallbacks.
        foreach (var keyword in queries)
        {
            try
            {
                var page = await SearchRemoteAsync(kind, platform, keyword, target, sources, token, offset).ConfigureAwait(false);
                succeeded = true;
                if (page.Count > 0) return page.DistinctBy(item => (item.Platform, item.Id)).Select(Localize).ToArray();
            }
            catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.TooManyRequests) { throw; }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
            { failure = error; }
        }
        token.ThrowIfCancellationRequested();
        if (!succeeded && failure is not null) throw failure;
        return [];
    }
    private static async Task<IReadOnlyList<DownloadCatalogItem>> SearchRemoteAsync(CatalogKind kind, ContentPlatform platform, string query, VersionInfo? target, DownloadSources sources, CancellationToken token, int offset)
    {
        if (platform == ContentPlatform.Modrinth)
        {
            var facets = new List<string[]> { new[] { "project_type:" + (kind == CatalogKind.Modpack ? "modpack" : kind == CatalogKind.Mod ? "mod" : kind == CatalogKind.ResourcePack ? "resourcepack" : "shader") } };
            if (target is not null && target.MinecraftVersion.Length > 0) facets.Add(["versions:" + target.MinecraftVersion]);
            if (kind == CatalogKind.Mod && target is not null && LoaderKey(target.Loader) is var loader && loader != "minecraft") facets.Add(["categories:" + loader]);
            var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";
            var json = await sources.JsonAsync($"https://api.modrinth.com/v2/search?query={Q(query)}&facets={Q(JsonSerializer.Serialize(facets))}&index={index}&limit=30&offset={offset}", token);
            return json.GetProperty("hits").EnumerateArray().Select(j => new DownloadCatalogItem(Str(j, "project_id"), Str(j, "title"), Str(j, "description"), kind, platform, Str(j, "icon_url"), "https://modrinth.com/" + (kind == CatalogKind.Modpack ? "modpack/" : kind == CatalogKind.Mod ? "mod/" : kind == CatalogKind.ResourcePack ? "resourcepack/" : "shader/") + Str(j, "slug"))).ToArray();
        }
        int classId = kind == CatalogKind.Modpack ? 4471 : kind == CatalogKind.Mod ? 6 : kind == CatalogKind.ResourcePack ? 12 : 6552;
        var filter = target is null ? "" : "&gameVersion=" + Q(target.MinecraftVersion);
        var result = await sources.JsonAsync($"https://api.curseforge.com/v1/mods/search?gameId=432&classId={classId}&searchFilter={Q(query)}&sortField=2&sortOrder=desc&pageSize=30&index={offset}{filter}", token);
        return result.GetProperty("data").EnumerateArray().Select(j => new DownloadCatalogItem(j.GetProperty("id").ToString(), Str(j, "name"), Str(j, "summary"), kind, platform, j.TryGetProperty("logo", out var logo) ? Str(logo, "thumbnailUrl") : null, j.TryGetProperty("links", out var link) ? Str(link, "websiteUrl") : null)).ToArray();
    }
    public static string LoaderKey(string loader) => loader.Contains("NeoForge", StringComparison.OrdinalIgnoreCase) ? "neoforge" : loader.Contains("Fabric", StringComparison.OrdinalIgnoreCase) ? "fabric" : loader.Contains("Forge", StringComparison.OrdinalIgnoreCase) ? "forge" : "minecraft";
    public Task<IReadOnlyList<ContentRelease>> ReleasesAsync(DownloadCatalogItem project, VersionInfo? target, DownloadSources sources, CancellationToken token)
        => ReleasesAsync(project, target, sources, token, false);
    public async Task<IReadOnlyList<ContentRelease>> ReleasesAsync(DownloadCatalogItem project, VersionInfo? target, DownloadSources sources, CancellationToken token, bool refresh)
    {
        if (project.Platform == ContentPlatform.Modrinth)
        {
            var filters = "?include_changelog=false" + (target is null ? "" : "&game_versions=" + Q(JsonSerializer.Serialize(new[] { target.MinecraftVersion })));
            if (target is not null && project.Kind == CatalogKind.Mod && LoaderKey(target.Loader) != "minecraft") filters += "&loaders=" + Q(JsonSerializer.Serialize(new[] { LoaderKey(target.Loader) }));
            var json = await sources.JsonAsync($"https://api.modrinth.com/v2/project/{Q(project.Id)}/version{filters}", token, refresh: refresh, validate: j => j.ValueKind == JsonValueKind.Array).ConfigureAwait(false);
            return json.EnumerateArray().Select(j => ParseModrinth(j, project.Kind)).ToArray();
        }
        var results = new List<ContentRelease>();
        var filter = target is null ? "" : "&gameVersion=" + Q(target.MinecraftVersion);
        async Task<JsonElement> PageAsync(int offset) => await sources.JsonAsync($"https://api.curseforge.com/v1/mods/{Q(project.Id)}/files?pageSize=50&index={offset}{filter}", token,
            refresh: refresh, validate: j => j.ValueKind == JsonValueKind.Object && j.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array).ConfigureAwait(false);
        var first = await PageAsync(0).ConfigureAwait(false);
        var firstPage = first.GetProperty("data").EnumerateArray().Select(j => ParseCurseForge(j, project.Kind)).ToArray();
        results.AddRange(firstPage);
        if (firstPage.Length == 50 && first.TryGetProperty("pagination", out var pagination) && pagination.TryGetProperty("totalCount", out var total))
        {
            // Four bounded requests per batch instead of awaiting every page in sequence.
            for (var offset = 50; offset < Math.Min(total.GetInt64(), 10000); offset += 200)
            {
                var pages = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => offset + i * 50)
                    .Where(index => index < Math.Min(total.GetInt64(), 10000)).Select(PageAsync)).ConfigureAwait(false);
                foreach (var page in pages) results.AddRange(page.GetProperty("data").EnumerateArray().Select(j => ParseCurseForge(j, project.Kind)));
            }
        }
        else if (firstPage.Length == 50)
        for (int offset = 50; offset < 10000; offset += 50)
        {
            var cf = await PageAsync(offset).ConfigureAwait(false);
            var page = cf.GetProperty("data").EnumerateArray().Select(j => ParseCurseForge(j, project.Kind)).ToArray();
            results.AddRange(page);
            if (page.Length < 50) break;
        }
        return results.Where(r => target is null || r.ClientSupported && (project.Kind != CatalogKind.Mod || r.Loaders.Contains(LoaderKey(target.Loader)))).DistinctBy(r => r.Id).ToArray();
    }
    public async Task<ContentRelease> ReleaseAsync(ContentPlatform platform, string projectId, string versionId, CatalogKind kind, DownloadSources sources, CancellationToken token)
    {
        var url = platform == ContentPlatform.Modrinth ? $"https://api.modrinth.com/v2/version/{Q(versionId)}" : $"https://api.curseforge.com/v1/mods/{Q(projectId)}/files/{Q(versionId)}";
        var j = await sources.JsonAsync(url, token);
        if (platform == ContentPlatform.Modrinth)
        {
            var release = ParseModrinth(j, kind);
            var project = await sources.JsonAsync($"https://api.modrinth.com/v2/project/{Q(release.ProjectId)}", token);
            var actualKind = Str(project, "project_type") switch { "resourcepack" => CatalogKind.ResourcePack, "shader" => CatalogKind.Shader, _ => CatalogKind.Mod };
            return release with { Kind = actualKind, ClientSupported = release.ClientSupported && Str(project, "client_side") != "unsupported" };
        }
        return ParseCurseForge(j.GetProperty("data"), kind);
    }
    public async Task<IReadOnlyList<ContentRelease>> CurseForgeFilesAsync(IEnumerable<long> fileIds, DownloadSources sources, CancellationToken token)
    {
        var results = new List<ContentRelease>();
        foreach (var batch in fileIds.Distinct().Chunk(100))
        {
            var json = await sources.PostJsonAsync("https://api.curseforge.com/v1/mods/files", new { fileIds = batch }, token,
                validate: j => j.ValueKind == JsonValueKind.Object && j.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array).ConfigureAwait(false);
            results.AddRange(json.GetProperty("data").EnumerateArray().Select(j => ParseCurseForge(j, CatalogKind.Mod)));
        }
        return results;
    }
    private static ContentRelease ParseModrinth(JsonElement j, CatalogKind kind)
    {
        bool client = !j.TryGetProperty("environment", out var env) || (env.ValueKind == JsonValueKind.Object ? Str(env, "client") != "unsupported" : env.GetString() is not ("server_only" or "dedicated_server_only"));
        var files = j.GetProperty("files").EnumerateArray().OrderByDescending(f => f.TryGetProperty("primary", out var p) && p.GetBoolean()).Take(1).Select(f => new ContentFile(Str(f, "filename"), Str(f, "url"), f.GetProperty("size").GetInt64(), Str(f.GetProperty("hashes"), "sha1") is { Length: > 0 } h ? h : null, Str(f.GetProperty("hashes"), "sha512") is { Length: > 0 } h2 ? h2 : null)).ToArray();
        var deps = j.GetProperty("dependencies").EnumerateArray().Select(d => new ContentDependency(Str(d, "version_id") is { Length: > 0 } v ? v : null, Str(d, "project_id") is { Length: > 0 } p ? p : null, Str(d, "dependency_type"))).ToArray();
        return new(Str(j, "id"), Str(j, "project_id"), Str(j, "name"), ContentPlatform.Modrinth, kind, Strings(j, "game_versions"), Strings(j, "loaders"), client, files, deps);
    }
    private static ContentRelease ParseCurseForge(JsonElement j, CatalogKind kind)
    {
        var versions = Strings(j, "gameVersions");
        var hash = j.GetProperty("hashes").EnumerateArray().FirstOrDefault(h => h.GetProperty("algo").GetInt32() == 1);
        var url = Str(j, "downloadUrl");
        var files = url.Length == 0 ? Array.Empty<ContentFile>() : [new ContentFile(Str(j, "fileName"), url, j.GetProperty("fileLength").GetInt64(), hash.ValueKind == JsonValueKind.Object ? Str(hash, "value") : null)];
        var deps = j.GetProperty("dependencies").EnumerateArray().Where(d => d.GetProperty("relationType").GetInt32() is 3 or 5).Select(d => new ContentDependency(null, d.GetProperty("modId").ToString(), d.GetProperty("relationType").GetInt32() == 3 ? "required" : "incompatible")).ToArray();
        var loaders = versions.Select(v => v.ToLowerInvariant()).Where(v => v is "forge" or "fabric" or "neoforge" or "quilt").ToArray();
        if (loaders.Length == 0 && versions.Any(v => Version.TryParse(v, out var old) && old.Major == 1 && old.Minor <= 12)) loaders = ["forge"];
        return new(j.GetProperty("id").ToString(), j.GetProperty("modId").ToString(), Str(j, "displayName"), ContentPlatform.CurseForge, kind, versions.Where(v => char.IsDigit(v.FirstOrDefault())).ToArray(), loaders, versions.Contains("Client") || !versions.Contains("Server"), files, deps);
    }
}
