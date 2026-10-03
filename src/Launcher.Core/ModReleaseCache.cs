using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Launcher.Core;

public sealed record CachedModReleases(ContentRelease[] Releases, ModReleaseIndex Index, DateTime SavedUtc)
{
    public bool IsFresh => DateTime.UtcNow - SavedUtc < TimeSpan.FromMinutes(15);
}

/// <summary>Parsed catalogs are immediately reusable; expired catalogs remain usable during refresh.</summary>
public sealed class ModReleaseCache
{
    private readonly string _directory;
    private readonly Dictionary<string, CachedModReleases> _memory = new();
    private readonly object _gate = new();
    public ModReleaseCache(string? directory = null) => _directory = directory ?? Path.Combine(AppPaths.Data, "downloads", "mod-versions");
    private static string Key(DownloadCatalogItem project) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{project.Platform}|{project.Kind}|{project.Id}")));
    public CachedModReleases? ReadMemory(DownloadCatalogItem project)
    { lock (_gate) return _memory.GetValueOrDefault(Key(project)); }

    public async Task<CachedModReleases?> ReadAsync(DownloadCatalogItem project, CancellationToken token)
    {
        if (ReadMemory(project) is { } cached) return cached;
        try
        {
            await using var file = File.OpenRead(Path.Combine(_directory, Key(project) + ".json"));
            var snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(file, cancellationToken: token).ConfigureAwait(false);
            if (snapshot is not { Format: 1, Releases: not null }) return null;
            var index = await Task.Run(() => new ModReleaseIndex(snapshot.Releases, token), token).ConfigureAwait(false);
            return Remember(project, new(snapshot.Releases, index, snapshot.SavedUtc));
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public async Task SaveAsync(DownloadCatalogItem project, CachedModReleases entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Remember(project, entry);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Key(project) + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = File.Create(temporary))
                await JsonSerializer.SerializeAsync(file, new Snapshot(1, entry.SavedUtc, entry.Releases), cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private CachedModReleases Remember(DownloadCatalogItem project, CachedModReleases entry)
    {
        lock (_gate)
        {
            _memory[Key(project)] = entry;
            // Limit resident indexes; evicted entries remain in the disk cache.
            if (_memory.Count > 24) _memory.Remove(_memory.MinBy(pair => pair.Value.SavedUtc).Key);
        }
        return entry;
    }
    private sealed record Snapshot(int Format, DateTime SavedUtc, ContentRelease[] Releases);
}
