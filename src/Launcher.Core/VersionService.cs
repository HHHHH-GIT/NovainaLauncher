using System.Text.Json;
using System.Text.RegularExpressions;
using CmlLib.Core;
using CmlLib.Core.VersionLoader;

namespace Launcher.Core;

public sealed class VersionService
{
    public static MinecraftLauncher CreateLocalLauncher(string root, HttpClient? http = null)
    {
        var path = new MinecraftPath(root);
        var parameters = http is null ? MinecraftLauncherParameters.CreateDefault(path) : MinecraftLauncherParameters.CreateDefault(path, http);
        parameters.VersionLoader = new LocalJsonVersionLoader(path);
        // A selected, verified Java is supplied explicitly. Do not query runtime manifests at launch.
        if (parameters.FileExtractors is not null)
        {
            foreach (var extractor in parameters.FileExtractors.ToArray())
                if (extractor.GetType().Name.Contains("Java", StringComparison.Ordinal)) parameters.FileExtractors.Remove(extractor);
        }
        return new MinecraftLauncher(parameters);
    }

    public Task<IReadOnlyList<VersionInfo>> ScanAsync(string root, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var result = new List<VersionInfo>();
        var directory = Path.Combine(root, "versions");
        if (!Directory.Exists(directory)) return (IReadOnlyList<VersionInfo>)result;
        var hidden = VersionVisibility.Dependencies(root);
        var launcher = CreateLocalLauncher(root);
        foreach (var entry in Directory.EnumerateDirectories(directory).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(entry);
            if (hidden.Contains(id)) continue;
            if (!File.Exists(Path.Combine(entry, id + ".json"))) continue;
            try
            {
                var version = ReadDetails(root, id);
                await launcher.GetVersionAsync(id, cancellationToken);
                result.Add(version);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { result.Add(new(id, root, "未知", null, "版本文件损坏或父版本缺失")); }
        }
        return (IReadOnlyList<VersionInfo>)result;
    }, cancellationToken);

    public static (int? Java, string Loader) ReadMetadata(string root, string id)
    {
        var version = ReadDetails(root, id);
        return (version.RequiredJava, version.Loader);
    }

    public static VersionInfo ReadDetails(string root, string id)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? java = null;
        var loader = "原版";
        var loaderVersion = "";
        string? parentId = null;
        var versionType = "release";
        string? cursor = id;
        string baseId = "";
        while (cursor is not null)
        {
            if (!visited.Add(cursor)) throw new InvalidDataException("版本继承循环");
            if (Path.GetFileName(cursor) != cursor) throw new InvalidDataException("无效的版本 ID");
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "versions", cursor, cursor + ".json")));
            var json = doc.RootElement;
            // A flattened launcher profile may use the instance name as its ID.
            // Read the actual client version before considering profile/parent IDs.
            if (baseId.Length == 0)
            {
                baseId = new[] { "clientVersion", "minecraftVersion", "gameVersion" }
                    .Select(key => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    .Append(GameArgument(json, "--fml.mcVersion"))
                    .Append(VersionManagementService.ReadProfile(root, cursor)?.MinecraftVersion)
                    .Append(json.TryGetProperty("id", out var identifier) ? identifier.GetString() : cursor)
                    .FirstOrDefault(IsMinecraftVersion) ?? "";
            }
            if (cursor == id)
            {
                parentId = json.TryGetProperty("inheritsFrom", out var inherited) ? inherited.GetString() : null;
                versionType = json.TryGetProperty("type", out var type) ? type.GetString() ?? "release" : "release";
            }
            if (java is null && json.TryGetProperty("javaVersion", out var jv) && jv.TryGetProperty("majorVersion", out var major)) java = major.GetInt32();
            // Instance names must not affect loader detection after a rename.
            var raw = string.Join(" ", new[] { "libraries", "mainClass", "arguments", "minecraftArguments" }
                .Select(property => json.TryGetProperty(property, out var value) ? value.GetRawText() : ""));
            if (loader == "原版")
            {
                loader = raw.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase) ? "NeoForge"
                    : raw.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase) ? "Forge"
                    : raw.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase) ? "Fabric"
                    : raw.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase) ? "Quilt"
                    : raw.Contains("optifine", StringComparison.OrdinalIgnoreCase) ? "OptiFine" : "原版";
                if (loader != "原版" && json.TryGetProperty("libraries", out var libraries))
                {
                    var marker = loader switch { "NeoForge" => "net.neoforged:neoforge:", "Forge" => "net.minecraftforge:forge:", "Fabric" => "net.fabricmc:fabric-loader:", "Quilt" => "org.quiltmc:quilt-loader:", _ => "optifine:OptiFine:" };
                    foreach (var library in libraries.EnumerateArray())
                        if (library.TryGetProperty("name", out var name) && name.GetString() is { } coordinate && coordinate.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                        { loaderVersion = coordinate.Split(':')[2]; break; }
                }
            }
            if (loaderVersion.Length == 0 && loader is "Forge" or "NeoForge")
                loaderVersion = GameArgument(json, loader == "Forge" ? "--fml.forgeVersion" : "--fml.neoForgeVersion") ?? "";
            cursor = json.TryGetProperty("inheritsFrom", out var parent) ? parent.GetString() : null;
        }
        var profile = VersionManagementService.ReadProfile(root, id);
        return new(id, root, loader, java ?? LegacyJava(baseId))
        {
            GameName = profile?.Name ?? id,
            MinecraftVersion = baseId, LoaderVersion = loaderVersion, ParentId = parentId, VersionType = versionType
        };
    }

    private static bool IsMinecraftVersion(string? value) => value is not null && Regex.IsMatch(value,
        @"^(?:\d+\.\d+(?:\.\d+)?(?:[- ](?:pre|rc|Pre-Release|Release Candidate)[- ]?\d+)?|\d{2}w\d{2}[a-z]|[ab]\d+(?:\.\d+)+|rd-\d+|inf-\d+)$", RegexOptions.IgnoreCase);

    private static string? GameArgument(JsonElement json, string key)
    {
        if (json.TryGetProperty("arguments", out var arguments) && arguments.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.Array)
        {
            var values = game.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()).ToArray();
            for (var i = 0; i + 1 < values.Length; i++) if (values[i] == key) return values[i + 1];
        }
        if (json.TryGetProperty("minecraftArguments", out var legacy) && legacy.ValueKind == JsonValueKind.String)
        {
            var match = Regex.Match(legacy.GetString()!, Regex.Escape(key) + @"\s+([^\s]+)");
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    public static int? LegacyJava(string id)
    {
        if (!Version.TryParse(id, out var v)) return null;
        if (v.Major == 1)
        {
            if (v.Minor <= 16) return 8;
            if (v.Minor == 17) return 16;
            if (v.Minor < 20 || (v.Minor == 20 && v.Build < 5)) return 17;
            return 21;
        }
        return null; // Calendar versions and snapshots must declare their requirements.
    }

    public static string GameDirectory(VersionInfo version, IsolationMode mode)
    {
        var directory = Path.Combine(version.Root, "versions", version.Id);
        bool isolated = mode == IsolationMode.Isolated || mode == IsolationMode.Auto &&
            (Directory.Exists(Path.Combine(directory, "mods")) || Directory.Exists(Path.Combine(directory, "saves")) || File.Exists(Path.Combine(directory, "options.txt")));
        return isolated ? directory : version.Root;
    }
    public static string ResolveGameDirectory(VersionInfo version, LauncherSettings settings) => Path.GetFullPath(GameDirectory(version,
        settings.IsolationOverrides.TryGetValue(AppPaths.VersionKey(version), out var mode) ? mode : IsolationMode.Auto));
}
