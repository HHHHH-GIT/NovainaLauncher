using System.Text.Json;
namespace Launcher.Core;
public sealed class ContentDependencyResolver(IModCatalogService catalog)
{
    public async Task<IReadOnlyList<ContentRelease>> ResolveAsync(ContentRelease rootRelease, VersionInfo target, string directory, DownloadSources sources, CancellationToken token, IReadOnlyList<ContentRelease>? pinned = null)
    {
        var registryPath = Path.Combine(AppPaths.Data, "downloads", "installed-content.json");
        var registry = File.Exists(registryPath) ? JsonSerializer.Deserialize<List<InstalledContent>>(await File.ReadAllTextAsync(registryPath, token)) ?? [] : [];
        var installed = registry.Where(i => string.Equals(i.Directory, directory, StringComparison.OrdinalIgnoreCase) && File.Exists(i.File)).ToArray();
        var releases = new Dictionary<string, ContentRelease>();
        var conflicting = new HashSet<string>();
        async Task Resolve(ContentRelease release)
        {
            token.ThrowIfCancellationRequested(); InstallCompatibility.Content(release, target);
            if (release.Kind != rootRelease.Kind) throw new InvalidOperationException("必需依赖类型不同，请先在对应分类配置");
            string key = release.Platform + ":" + release.ProjectId;
            if (releases.TryGetValue(key, out var prior)) { if (prior.Id != release.Id) throw new InvalidOperationException("必需依赖需要不同版本：" + release.ProjectId); return; }
            if (installed.Any(i => i.Platform == release.Platform && i.ProjectId == release.ProjectId && i.VersionId != release.Id)) throw new InvalidOperationException("已安装同项目的其他版本，请先到管理页处理");
            releases.Add(key, release);
            foreach (var dep in release.Dependencies)
            {
                if (rootRelease.Kind != CatalogKind.Mod) continue; // Resource/shader downloads do not silently install rendering mods.
                if (dep.Kind == "incompatible") { if (dep.ProjectId is not null) conflicting.Add(release.Platform + ":" + dep.ProjectId); if (dep.VersionId is not null) { var bad = await catalog.ReleaseAsync(release.Platform, dep.ProjectId ?? "", dep.VersionId, release.Kind, sources, token); conflicting.Add(release.Platform + ":" + bad.ProjectId); } continue; }
                if (dep.Kind != "required") continue;
                ContentRelease dependency;
                if (pinned is not null)
                    dependency = pinned.FirstOrDefault(r => r.Platform == release.Platform && (dep.VersionId is not null ? r.Id == dep.VersionId : r.ProjectId == dep.ProjectId)) ?? throw new InvalidOperationException("依赖清单已变化，请重新确认");
                else if (dep.VersionId is not null) dependency = await catalog.ReleaseAsync(release.Platform, dep.ProjectId ?? "", dep.VersionId, release.Kind, sources, token);
                else if (dep.ProjectId is not null)
                {
                    var existingDependency = installed.FirstOrDefault(i => i.Platform == release.Platform && i.ProjectId == dep.ProjectId);
                    if (existingDependency is not null)
                        dependency = await catalog.ReleaseAsync(release.Platform, dep.ProjectId, existingDependency.VersionId, release.Kind, sources, token);
                    else
                    {
                        var candidates = await catalog.ReleasesAsync(new(dep.ProjectId, dep.ProjectId, "", release.Kind, release.Platform), target, sources, token);
                        dependency = candidates.FirstOrDefault() ?? throw new InvalidOperationException("依赖没有兼容版本：" + dep.ProjectId);
                    }
                }
                else throw new InvalidDataException("必需依赖缺少项目或版本 ID");
                await Resolve(dependency);
            }
        }
        await Resolve(rootRelease);
        if (pinned is not null)
            foreach (var extra in pinned) await Resolve(extra);
        if (conflicting.Overlaps(releases.Keys.Concat(installed.Select(i => i.Platform + ":" + i.ProjectId)))) throw new InvalidOperationException("检测到平台声明的 Mod 冲突");
        return releases.Values.ToArray();
    }
}
public sealed record InstalledContent(string Directory, ContentPlatform Platform, string ProjectId, string VersionId, string File);
