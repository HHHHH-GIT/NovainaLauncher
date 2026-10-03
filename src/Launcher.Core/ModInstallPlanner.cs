using System.Security.Cryptography;
using System.Text;

namespace Launcher.Core;

/// <summary>Reads verified JARs before confirmation. Optional projects enter the plan only when they actually provide a required ID.</summary>
public sealed class ModInstallPlanner(IModCatalogService catalog)
{
    public static string CacheFile(string root, ContentFile file) => Path.Combine(root, ".ikun-downloads", "cache", "content", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Url + file.Sha1 + file.Sha512))));
    public async Task<IReadOnlyList<ContentRelease>> ResolveAsync(ContentRelease release, VersionInfo target, string directory, DownloadSources sources, int javaMajor, CancellationToken token)
    {
        var resolver = new ContentDependencyResolver(catalog);
        var selected = (await resolver.ResolveAsync(release, target, directory, sources, token)).ToList();
        async Task<List<ModMetadata>> Read(ContentRelease candidate)
        {
            var result = new List<ModMetadata>();
            foreach (var file in candidate.Files)
            {
                token.ThrowIfCancellationRequested();
                var path = CacheFile(target.Root, file);
                if (!File.Exists(path) || !await DownloadHash.MatchesAsync(path, file.Sha1, file.Sha512, token))
                    await new SegmentedDownloader(sources.Http).DownloadAsync(new(file.Url, path, file.Size, file.Sha1, file.Sha512), new InlineProgress<FileDownloadProgress>(_ => { }), token);
                result.AddRange(await Task.Run(() => InstallCompatibility.ReadJar(path, token), token));
            }
            return result;
        }
        var metadata = new List<ModMetadata>();
        foreach (var item in selected) metadata.AddRange(await Read(item));
        var replacing = selected.SelectMany(r => r.Files).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(directory, "mods");
        if (Directory.Exists(folder))
            foreach (var path in Directory.EnumerateFiles(folder, "*.jar").Where(p => !replacing.Contains(Path.GetFileName(p)))) metadata.AddRange(await Task.Run(() => InstallCompatibility.ReadJar(path, token), token));
        var builtin = new HashSet<string>(["minecraft", "java", "fabricloader", "forge", "neoforge", "fml", "neofml"], StringComparer.OrdinalIgnoreCase);
        string[] Missing() => metadata.SelectMany(m => m.Constraints).Where(c => !c.Conflict && !builtin.Contains(c.Id) && !metadata.Any(m => m.Id == c.Id || m.Provides.Contains(c.Id))).Select(c => c.Id).Distinct().ToArray();
        // Do not guess arbitrary project identities. Probe only publisher-linked optional dependencies,
        // plus the canonical Fabric API for its component IDs, then verify the actual contained IDs.
        for (int pass = 0; pass < 3 && Missing().Length > 0; pass++)
        {
            var missing = Missing(); bool added = false;
            var candidates = selected.SelectMany(r => r.Dependencies.Where(d => d.Kind == "optional").Select(d => (r.Platform, Dependency: d))).ToList();
            if (DownloadCatalogService.LoaderKey(target.Loader) == "fabric" && missing.Any(id => id.StartsWith("fabric-", StringComparison.Ordinal)))
                candidates.Add((release.Platform, new(null, release.Platform == ContentPlatform.Modrinth ? "P7dR8mSH" : "306612", "optional")));
            foreach (var candidate in candidates.Distinct().Take(8))
            {
                var dep = candidate.Dependency;
                if (selected.Any(r => r.Platform == candidate.Platform && r.ProjectId == dep.ProjectId)) continue;
                ContentRelease? extra = dep.VersionId is not null ? await catalog.ReleaseAsync(candidate.Platform, dep.ProjectId ?? "", dep.VersionId, CatalogKind.Mod, sources, token) : (await catalog.ReleasesAsync(new(dep.ProjectId!, dep.ProjectId!, "", CatalogKind.Mod, candidate.Platform), target, sources, token)).FirstOrDefault();
                if (extra is null) continue;
                var provides = await Read(extra);
                if (!provides.Any(m => missing.Contains(m.Id) || m.Provides.Any(missing.Contains))) continue;
                foreach (var item in await resolver.ResolveAsync(extra, target, directory, sources, token))
                {
                    var prior = selected.FirstOrDefault(r => r.Platform == item.Platform && r.ProjectId == item.ProjectId);
                    if (prior is not null) { if (prior.Id != item.Id) throw new InvalidOperationException("必需依赖版本冲突：" + item.Name); continue; }
                    selected.Add(item); metadata.AddRange(item.Id == extra.Id ? provides : await Read(item));
                }
                added = true;
            }
            if (!added) break;
        }
        InstallCompatibility.CheckMetadata(metadata, target, javaMajor);
        return selected;
    }
}
