using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Launcher.Core;

public sealed record ModConstraint(string Id, string Range, bool Conflict = false, bool Maven = false);
public sealed record ModMetadata(string Id, string Version, string Loader, IReadOnlyList<string> Provides, IReadOnlyList<ModConstraint> Constraints, bool IsNested = false);

public static class InstallCompatibility
{
    public static string? ExactForge(OptiFineCatalogVersion optiFine, IEnumerable<LoaderCatalogVersion> forge)
    {
        string value = Regex.Replace(optiFine.ForgeVersion ?? "", "^Forge\\s*", "", RegexOptions.IgnoreCase).Trim().TrimStart('#');
        if (value.Length == 0 || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        var matches = forge.Where(v => v.MinecraftVersion == optiFine.MinecraftVersion && (v.Version == value || !value.Contains('.') && v.Version.Split('.').Last() == value)).Select(v => v.Version).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public static IReadOnlyList<CompatibilityIssue> Game(InstallPlan plan, IEnumerable<LoaderCatalogVersion> forge)
    {
        var errors = new List<CompatibilityIssue>();
        if (plan.Game is null) return [new("game", "请选择 Minecraft 版本")];
        if (plan.Loader is { } l && l.MinecraftVersion != plan.Game.Id) errors.Add(new("loader-version", "加载器与 Minecraft 版本不一致"));
        if (plan.OptiFine is { } of)
        {
            if (of.MinecraftVersion != plan.Game.Id) errors.Add(new("optifine-version", "OptiFine 与 Minecraft 版本不一致"));
            if (plan.Loader?.Loader is "Fabric" or "NeoForge") errors.Add(new("optifine-loader", "此加载器不能与 OptiFine 组合"));
            if (plan.Loader?.Loader == "Forge" && ExactForge(of, forge) != plan.Loader.Version) errors.Add(new("optifine-forge", "Forge 版本与 OptiFine 声明不匹配"));
        }
        return errors;
    }
    public static void Content(ContentRelease release, VersionInfo target)
    {
        if (!release.ClientSupported) throw new InvalidOperationException("此文件不支持客户端");
        if (!release.GameVersions.Contains(target.MinecraftVersion)) throw new InvalidOperationException("文件与 Minecraft 版本不匹配");
        if (release.Kind == CatalogKind.Mod && !release.Loaders.Contains(DownloadCatalogService.LoaderKey(target.Loader))) throw new InvalidOperationException("Mod 与加载器不匹配");
        if (release.Files.Count == 0) throw new InvalidOperationException("作者未授权分发此文件，请前往项目页面");
    }
    public static IReadOnlyList<ModMetadata> ReadJar(string path, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadZip(zip, token, 0);
    }
    private static IReadOnlyList<ModMetadata> ReadZip(ZipArchive zip, CancellationToken token, int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 8) throw new InvalidDataException("嵌套 Mod 层数过多");
        var result = new List<ModMetadata>();
        if (zip.GetEntry("fabric.mod.json") is { } fabric)
        {
            using var stream = fabric.Open(); using var json = JsonDocument.Parse(stream);
            var j = json.RootElement;
            if (DownloadCatalogService.Str(j, "environment") == "server") throw new InvalidOperationException("Mod 仅适用于服务端");
            var constraints = new List<ModConstraint>();
            foreach (var key in new[] { "depends", "breaks" })
                if (j.TryGetProperty(key, out var deps))
                    foreach (var d in deps.EnumerateObject())
                    {
                        var range = d.Value.ValueKind == JsonValueKind.Array ? string.Join(" || ", d.Value.EnumerateArray().Select(v => v.GetString())) : d.Value.GetString();
                        constraints.Add(new(d.Name, range ?? "", key == "breaks"));
                    }
            var provides = j.TryGetProperty("provides", out var ps) ? ps.EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
            result.Add(new(DownloadCatalogService.Str(j, "id"), DownloadCatalogService.Str(j, "version"), "fabric", provides, constraints, depth > 0));
            if (j.TryGetProperty("jars", out var jars))
                foreach (var jar in jars.EnumerateArray())
                    if (zip.GetEntry(DownloadCatalogService.Str(jar, "file")) is { } entry)
                    { using var nested = entry.Open(); using var copy = new MemoryStream(); nested.CopyTo(copy); copy.Position = 0; using var nestedZip = new ZipArchive(copy); result.AddRange(ReadZip(nestedZip, token, depth + 1)); }
        }
        else if ((zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml")) is { } toml)
        {
            using var reader = new StreamReader(toml.Open());
            var table = Toml.ToModel(reader.ReadToEnd());
            var loader = toml.FullName.EndsWith("neoforge.mods.toml") ? "neoforge" : "forge";
            var dependencyMap = table.TryGetValue("dependencies", out var ds) ? ds as TomlTable : null;
            if (table.TryGetValue("mods", out var mods) && mods is TomlTableArray modArray)
                foreach (var mod in modArray)
                {
                    string id = mod["modId"].ToString()!;
                    string version = mod["version"].ToString()!;
                    if (version.Contains("${file.jarVersion}"))
                    {
                        var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
                        using var mr = manifest is null ? null : new StreamReader(manifest.Open());
                        var match = Regex.Match(mr?.ReadToEnd() ?? "", @"(?m)^Implementation-Version:\s*(.+)$");
                        if (!match.Success) throw new InvalidDataException($"{id} 的版本占位符无法解析");
                        version = match.Groups[1].Value.Trim();
                    }
                    var constraints = new List<ModConstraint>();
                    if (table.TryGetValue("loaderVersion", out var loaderRange)) constraints.Add(new(loader == "forge" ? "fml" : "neofml", loaderRange.ToString()!, Maven: true));
                    if (dependencyMap?.TryGetValue(id, out var md) == true && md is TomlTableArray depArray)
                        foreach (var d in depArray)
                        {
                            bool required = d.TryGetValue("mandatory", out var mandatory) && mandatory is true || d.TryGetValue("type", out var type) && type.ToString() == "required";
                            bool conflict = d.TryGetValue("type", out var ct) && ct.ToString() == "incompatible";
                            if (!required && !conflict) continue;
                            if (d.TryGetValue("side", out var side) && side.ToString() == "SERVER") continue;
                            constraints.Add(new(d["modId"].ToString()!, d.TryGetValue("versionRange", out var vr) ? vr.ToString()! : "*", conflict, true));
                        }
                    result.Add(new(id, version, loader, [], constraints, depth > 0));
                }
            if (zip.GetEntry("META-INF/jarjar/metadata.json") is { } jarjar)
            {
                using var jarStream = jarjar.Open(); using var jarJson = JsonDocument.Parse(jarStream);
                foreach (var jar in jarJson.RootElement.GetProperty("jars").EnumerateArray())
                    if (zip.GetEntry(DownloadCatalogService.Str(jar, "path")) is { } entry)
                    { using var nested = entry.Open(); using var copy = new MemoryStream(); nested.CopyTo(copy); copy.Position = 0; using var nz = new ZipArchive(copy); var nestedMods = ReadZip(nz, token, depth + 1); result.AddRange(nestedMods); }
            }
        }
        else if (depth == 0 && zip.GetEntry("optifine/Config.class") is null) throw new InvalidDataException("Mod 缺少可解析的 JSON／TOML 元数据");
        return result;
    }
    public static void CheckMetadata(IEnumerable<ModMetadata> all, VersionInfo target, int javaMajor)
    {
        var loaderVersion = target.LoaderVersion.StartsWith(target.MinecraftVersion + "-", StringComparison.Ordinal) ? target.LoaderVersion[(target.MinecraftVersion.Length + 1)..] : target.LoaderVersion;
        var mods = all.ToArray(); var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["minecraft"] = target.MinecraftVersion, ["java"] = javaMajor + ".0.0", ["fabricloader"] = loaderVersion, ["forge"] = loaderVersion, ["neoforge"] = loaderVersion, ["fml"] = loaderVersion.Split('.').FirstOrDefault() ?? "" };
        // NeoFML is a separately versioned loader; read it from the actual launch chain.
        string? profileId = target.Id; var visited = new HashSet<string>();
        while (profileId is not null && visited.Add(profileId))
        {
            var profilePath = Path.Combine(target.Root, "versions", profileId, profileId + ".json");
            if (!File.Exists(profilePath)) break;
            using var document = JsonDocument.Parse(File.ReadAllText(profilePath));
            var profile = document.RootElement;
            if (profile.TryGetProperty("libraries", out var libraries))
                foreach (var library in libraries.EnumerateArray())
                    if (DownloadCatalogService.Str(library, "name") is { } coordinate && coordinate.StartsWith("net.neoforged.fancymodloader:loader:")) versions["neofml"] = coordinate.Split(':')[2];
            profileId = profile.TryGetProperty("inheritsFrom", out var parent) ? parent.GetString() : null;
        }
        // Fabric commonly embeds the same API component in Sodium and Fabric API. Choose a
        // compatible nested candidate; duplicated top-level files remain an installation conflict.
        mods = mods.GroupBy(m => m.Id).Select(group =>
        {
            var explicitMods = group.Where(m => !m.IsNested).ToArray();
            if (explicitMods.Length > 1) throw new InvalidOperationException("重复 Mod：" + group.Key);
            if (explicitMods.Length == 1) return explicitMods[0];
            var incoming = mods.SelectMany(m => m.Constraints).Where(c => c.Id == group.Key).ToArray();
            var candidates = group.Where(m => incoming.All(c => Matches(m.Version, c.Range, c.Maven) is { } match && (c.Conflict ? !match : match)) && m.Constraints.Where(c => versions.ContainsKey(c.Id)).All(c => Matches(versions[c.Id], c.Range, c.Maven) is { } match && (c.Conflict ? !match : match))).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("嵌套依赖没有兼容版本：" + group.Key);
            return candidates.OrderByDescending(m => Semver.SemVersion.TryParse(Normalize(m.Version), Semver.SemVersionStyles.Any, out var parsed) ? parsed : null, Comparer<Semver.SemVersion?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : b is null ? 1 : a.ComparePrecedenceTo(b))).First();
        }).ToArray();
        foreach (var mod in mods)
        {
            if (mod.Loader != DownloadCatalogService.LoaderKey(target.Loader)) throw new InvalidOperationException($"{mod.Id} 的加载器不匹配");
            versions[mod.Id] = mod.Version;
            foreach (var provided in mod.Provides) versions.TryAdd(provided, mod.Version);
        }
        foreach (var mod in mods)
            foreach (var c in mod.Constraints)
            {
                bool present = versions.TryGetValue(c.Id, out var version) && !string.IsNullOrWhiteSpace(version);
                if (!present) { if (!c.Conflict) throw new InvalidOperationException($"{mod.Id} 缺少依赖 {c.Id}"); continue; }
                var match = Matches(version!, c.Range, c.Maven);
                if (match is null) throw new InvalidOperationException($"{mod.Id} 的必要约束无法解析：{c.Id} {c.Range}");
                if (c.Conflict ? match.Value : !match.Value) throw new InvalidOperationException($"{mod.Id} 与 {c.Id} {version} 不兼容（{c.Range}）");
            }
    }
    public static bool? Matches(string version, string range, bool maven = false)
    {
        if (range is "*" or "") return range == "*" ? true : null;
        if (maven && !range.StartsWith('[') && !range.StartsWith('(')) return Matches(version, ">=" + range);
        int? Compare(string a, string b)
        {
            if (a == b) return 0;
            if (Semver.SemVersion.TryParse(Normalize(a), Semver.SemVersionStyles.Any, out var av) && Semver.SemVersion.TryParse(Normalize(b), Semver.SemVersionStyles.Any, out var bv)) return av.ComparePrecedenceTo(bv);
            if (Regex.IsMatch(a, @"^\d+(\.\d+)*$") && Regex.IsMatch(b, @"^\d+(\.\d+)*$"))
            { var aa = a.Split('.').Select(long.Parse).ToArray(); var bb = b.Split('.').Select(long.Parse).ToArray(); for (int i = 0; i < Math.Max(aa.Length, bb.Length); i++) { int c = (i < aa.Length ? aa[i] : 0).CompareTo(i < bb.Length ? bb[i] : 0); if (c != 0) return c; } return 0; }
            return null;
        }
        if (maven && (range.StartsWith('[') || range.StartsWith('(')))
        {
            var intervals = Regex.Matches(range, @"([\[\(])([^\]\)]*)([\]\)])");
            if (intervals.Count == 0 || string.Concat(intervals.Select(m => m.Value)).Replace(",", "") != range.Replace(",", "")) return null;
            bool result = false;
            foreach (Match interval in intervals)
            {
                var parts = interval.Groups[2].Value.Split(',');
                if (parts.Length == 1) { var c = Compare(version, parts[0]); if (c is null) return null; result |= c == 0; continue; }
                if (parts.Length != 2) return null;
                var lower = parts[0].Length == 0 ? 1 : Compare(version, parts[0]); var upper = parts[1].Length == 0 ? -1 : Compare(version, parts[1]);
                if (lower is null || upper is null) return null;
                result |= (lower > 0 || lower == 0 && interval.Groups[1].Value == "[") && (upper < 0 || upper == 0 && interval.Groups[3].Value == "]");
            }
            return result;
        }
        bool any = false;
        foreach (var alternative in range.Split("||", StringSplitOptions.TrimEntries))
        {
            bool match = true;
            foreach (var predicate in alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Regex.Match(predicate, @"^(>=|<=|>|<|=|~|\^)?(\S+)$");
                if (!p.Success) return null;
                var bound = p.Groups[2].Value; var op = p.Groups[1].Value;
                if (bound == "*") continue;
                if (Regex.IsMatch(bound, @"^\d+(\.\d+)*\.[xX*]$")) { match &= version.StartsWith(bound[..^1], StringComparison.Ordinal); continue; }
                var comparison = Compare(version, bound); if (comparison is null) return null;
                if (op == "^")
                {
                    var parts = Normalize(bound).Split('.');
                    int prefix = parts[0] != "0" ? 1 : parts[1] != "0" ? 2 : 3;
                    match &= comparison >= 0 && Normalize(version).Split('.').Take(prefix).SequenceEqual(parts.Take(prefix));
                }
                else match &= op switch { ">=" => comparison >= 0, "<=" => comparison <= 0, ">" => comparison > 0, "<" => comparison < 0, "~" => comparison >= 0 && Normalize(version).Split('.').Take(bound.Split('.').Length < 2 ? 1 : 2).SequenceEqual(Normalize(bound).Split('.').Take(bound.Split('.').Length < 2 ? 1 : 2)), _ => comparison == 0 };
            }
            any |= match;
        }
        return any;
    }
    private static string Normalize(string version)
    {
        // Fabric accepts an empty prerelease as the lower boundary of a release family.
        if (Regex.IsMatch(version, @"^\d+(\.\d+){0,2}-$")) version += "0";
        if (!Regex.IsMatch(version, @"^\d+(\.\d+){0,2}([+-].*)?$")) return version;
        var m = Regex.Match(version, @"^(\d+(\.\d+)*)(.*)$");
        return m.Groups[1].Value + string.Concat(Enumerable.Repeat(".0", Math.Max(0, 3 - m.Groups[1].Value.Split('.').Length))) + m.Groups[3].Value;
    }
}
