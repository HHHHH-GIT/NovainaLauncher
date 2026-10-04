using CmlLib.Core;

namespace Launcher.Core;

public sealed record FileDownloadRequest(string Url, string Path, long? Size = null, string? Sha1 = null, string? Sha512 = null, bool Official = false, string? CachePath = null);
public interface IFileDownloadService
{
    Task DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress> progress, CancellationToken token);
}
public interface IModCatalogService
{
    Task<IReadOnlyList<DownloadCatalogItem>> SearchAsync(CatalogKind kind, ContentPlatform platform, string query, VersionInfo? target, DownloadSources sources, CancellationToken token, int offset = 0);
    Task<IReadOnlyList<ContentRelease>> ReleasesAsync(DownloadCatalogItem project, VersionInfo? target, DownloadSources sources, CancellationToken token);
    Task<ContentRelease> ReleaseAsync(ContentPlatform platform, string projectId, string versionId, CatalogKind kind, DownloadSources sources, CancellationToken token);
}
public sealed record InstallationFailure(string File, string Reason, string? Source = null);
public sealed record InstallationCheck(int Checked, int Total, IReadOnlyList<InstallationFailure> Failures)
{
    public bool IsValid => Checked == Total && Failures.Count == 0;
}
public interface IInstallationVerifier
{
    Task<InstallationCheck> VerifyAsync(MinecraftLauncher launcher, string version, IProgress<InstallationCheck> progress, CancellationToken token);
}
public sealed record GameInstallRequest(InstallPlan Plan, string Staging, JavaRuntimeInfo? Java, string? ReadOnlyCache, string DataDirectory, string? DownloadCacheRoot = null);
public interface IGameInstallEngine
{
    Task<string> InstallAsync(GameInstallRequest request, Action<DownloadTaskState, string, FileDownloadProgress?> update, CancellationToken token);
}
