namespace Launcher.Core;

/// <summary>Local, deduplicated facets. Selecting a target never restricts the downloaded catalog.</summary>
public sealed class ModReleaseIndex
{
    public const string Unspecified = "未标注";
    private readonly Dictionary<string, Dictionary<string, List<ContentRelease>>> _buckets = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> GameVersions { get; }
    public IReadOnlyList<ContentRelease> AllReleases { get; }
    public ModReleaseIndex(IEnumerable<ContentRelease> source, CancellationToken token = default)
    {
        AllReleases = source.Where(v => v.Files.Count > 0).DistinctBy(v => (v.Platform, v.Id)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in AllReleases)
        {
            token.ThrowIfCancellationRequested();
            if (release.Files.Count == 0) continue;
            var key = release.Platform + ":" + (string.IsNullOrWhiteSpace(release.Id) ? release.Files[0].Url : release.Id);
            if (!seen.Add(key)) continue;
            var games = Clean(release.GameVersions, s => s.Trim());
            var loaders = Clean(release.Loaders, LoaderLabel);
            foreach (var game in games)
            {
                if (!_buckets.TryGetValue(game, out var byLoader)) _buckets[game] = byLoader = new(StringComparer.OrdinalIgnoreCase);
                foreach (var loader in loaders)
                {
                    if (!byLoader.TryGetValue(loader, out var versions)) byLoader[loader] = versions = [];
                    versions.Add(release);
                }
            }
        }
        GameVersions = _buckets.Keys.OrderByDescending(s => Version.TryParse(s, out _))
            .ThenByDescending(s => Version.TryParse(s, out var version) ? version : null)
            .ThenBy(s => s == Unspecified).ThenByDescending(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string[] Clean(IEnumerable<string> source, Func<string, string> normalize)
    {
        var clean = source.Where(s => !string.IsNullOrWhiteSpace(s)).Select(normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return clean.Length == 0 ? [Unspecified] : clean;
    }
    public static string LoaderLabel(string loader) => loader.Trim().ToLowerInvariant() switch
    {
        "fabric" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge", "quilt" => "Quilt",
        "liteloader" => "LiteLoader", "minecraft" => "原版", "" => Unspecified, _ => loader.Trim()
    };
    public IReadOnlyList<string> Loaders(string? game) => game is not null && _buckets.TryGetValue(game, out var loaders)
        ? loaders.Keys.OrderBy(s => s == Unspecified).ThenBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray() : [];
    public IReadOnlyList<ContentRelease> Releases(string? game, string? loader) => game is not null && loader is not null && _buckets.TryGetValue(game, out var loaders) && loaders.TryGetValue(loader, out var releases)
        ? releases : [];
}
