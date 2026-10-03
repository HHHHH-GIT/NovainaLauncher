using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installer.NeoForge.Installers;
using CmlLib.Core.Installer.NeoForge.Versions;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.VersionLoader;

namespace Launcher.Core;

public sealed partial class DownloadInstallService(JavaService java, VersionService versions, LogService log, string? readOnlyCache = null, Func<SourcePolicy, CancellationToken, DownloadSources>? sourceFactory = null, IGameInstallEngine? gameEngine = null)
{
    private readonly DownloadCatalogService _catalog = new();
    public async Task<VersionInfo?> InstallAsync(InstallPlan plan, Action<DownloadTaskState, string, FileDownloadProgress?> update, CancellationToken token, Action<InstallationWorkProgress>? reportOverall = null)
    {
        Directory.CreateDirectory(plan.Root);
        string staging = Path.Combine(plan.Root, ".ikun-downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var overall = new InstallationProgressTracker(InstallationWorkProgress.StepsFor(plan), reportOverall);
            using var sources = sourceFactory?.Invoke(plan.Sources, token) ?? new DownloadSources(plan.Sources, token);
            var progress = new InlineProgress<FileDownloadProgress>(p => update(DownloadTaskState.Downloading, sources.ActualSource, p));
            return plan.Content?.Kind == CatalogKind.Modpack || plan.LocalArchive is not null ? await ModpackAsync(plan, staging, sources, progress, update, token, overall)
                : plan.Game is not null ? await GameAsync(plan, staging, sources, progress, update, token, overall: overall) : await ContentAsync(plan, staging, sources, progress, update, token, overall);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private async Task<VersionInfo> GameAsync(InstallPlan plan, string staging, DownloadSources sources, IProgress<FileDownloadProgress> progress, Action<DownloadTaskState, string, FileDownloadProgress?> update, CancellationToken token, string? sharedCache = null, bool nested = false, string? cacheWriteRoot = null, InstallationProgressTracker? overall = null)
    {
        VersionManagementService.ValidateName(plan.Name);
        var destination = Path.Combine(plan.Root, "versions", plan.Name);
        if (Directory.Exists(destination)) throw new IOException("同名游戏已存在");
        overall?.Start("检查游戏");
        update(DownloadTaskState.Checking, "检查兼容性", null);
        var forge = plan.Loader?.Loader == "Forge" ? await _catalog.LoadersAsync(plan.Game!.Id, "Forge", sources, token) : [];
        var issues = InstallCompatibility.Game(plan, forge);
        if (issues.Count > 0) throw new InvalidOperationException(issues[0].Message);
        var game = plan.Game!;
        string gameDir = Path.Combine(staging, "versions", game.Id); Directory.CreateDirectory(gameDir);
        string metadataPath = Path.Combine(gameDir, game.Id + ".json");
        await new SegmentedDownloader(sources.Http).DownloadAsync(new(game.Url, metadataPath, Sha1: game.Sha1, CachePath: Path.Combine(cacheWriteRoot ?? plan.Root, ".ikun-downloads", "cache", "versions", game.Id, game.Id + ".json")), progress, token);
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, token));
        int? javaMajor = metadata.RootElement.TryGetProperty("javaVersion", out var jv) ? jv.GetProperty("majorVersion").GetInt32() : VersionService.LegacyJava(game.Id);
        if (javaMajor is null) throw new InvalidOperationException("此 Minecraft 的 Java 需求未知");
        overall?.Complete("准备 Java");
        // Modern Forge processors need a runtime able to run this game. Old Forge requires Java 8.
        update(DownloadTaskState.Java, "准备 Java " + javaMajor, null);
        var runtime = JavaService.Select(plan.Javas, javaMajor, null);
        runtime ??= await java.DownloadAsync(javaMajor.Value, new InlineProgress<JavaDownloadProgress>(p => { if (p.Stage == JavaDownloadStage.Downloading) progress.Report(new(p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Connections)); else update(DownloadTaskState.Java, "准备 Java " + javaMajor, null); }), token, plan.JavaDirectory);
        overall?.Complete("安装与校验游戏");
        var engine = gameEngine ?? (HostedGameInstallEngine.IsConfigured ? new HostedGameInstallEngine() : new NativeGameInstallEngine());
        var cacheRoot = sharedCache ?? readOnlyCache;
        await engine.InstallAsync(new(plan, staging, runtime, cacheRoot is null ? null : Path.GetFullPath(cacheRoot), AppPaths.Data, cacheWriteRoot), update, token);
        var parsed = (await versions.ScanAsync(staging, token)).SingleOrDefault(v => v.Id == plan.Name && v.IsValid) ?? throw new InvalidDataException("安装后的版本无法解析");
        overall?.Complete("提交游戏");
        update(nested ? DownloadTaskState.Installing : DownloadTaskState.Committing, nested ? "准备整合包基础游戏" : "提交游戏", null);
        await CommitGameAsync(staging, plan.Root, plan.Name, token);
        if (!nested) try
        {
            var records = Path.Combine(AppPaths.Data, "downloads", "installs"); Directory.CreateDirectory(records);
            await File.WriteAllTextAsync(Path.Combine(records, Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new { Verification = File.Exists(Path.Combine(staging, "installation-check.json")) ? JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "installation-check.json"), token)) : null, plan.Name, plan.Root, Minecraft = game.Id, Loader = plan.Loader?.Loader, LoaderVersion = plan.Loader?.Version, OptiFine = plan.OptiFine?.Edition, Date = DateTimeOffset.Now }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Write("安装记录无法保存：" + error.Message, LogLevel.Warning); }
        if (!nested) log.Write($"已安装 {plan.Name}", featured: true);
        overall?.Complete("游戏已就绪");
        return parsed with { Root = plan.Root };
    }
    private static async Task CommitGameAsync(string staging, string root, string instance, CancellationToken token)
    {
        var instanceDir = Path.Combine(staging, "versions", instance);
        var newDependencies = Directory.EnumerateDirectories(Path.Combine(staging, "versions")).Select(Path.GetFileName)
            .Where(id => id != instance && !Directory.Exists(Path.Combine(root, "versions", id!))).ToArray();
        await File.WriteAllTextAsync(Path.Combine(instanceDir, VersionVisibility.InstanceMarker), "{}", token);
        // Commit verified shared files atomically, repairing corrupt cache entries while retaining
        // all parent profiles. The named instance becomes visible only after shared files are ready.
        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != VersionVisibility.DependencyMarker && !p.StartsWith(instanceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && new[] { "libraries", "assets", "versions" }.Contains(Path.GetRelativePath(staging, p).Split(Path.DirectorySeparatorChar)[0])).ToArray();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); var target = Path.Combine(root, Path.GetRelativePath(staging, file));
            if (!File.Exists(file)) throw new FileNotFoundException("提交文件丢失", file);
        }
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); var target = Path.Combine(root, Path.GetRelativePath(staging, file));
            if (File.Exists(target) && await SameAsync(file, target, token)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var verified = target + "." + Guid.NewGuid().ToString("N") + ".verified";
            try { File.Copy(file, verified); token.ThrowIfCancellationRequested(); File.Move(verified, target, true); }
            finally { if (File.Exists(verified)) File.Delete(verified); }
        }
        foreach (var dependency in newDependencies)
            await File.WriteAllTextAsync(Path.Combine(root, "versions", dependency!, VersionVisibility.DependencyMarker), "", token);
        token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(root, "versions"));
        Directory.Move(instanceDir, Path.Combine(root, "versions", instance));
    }
    private static async Task<bool> SameAsync(string a, string b, CancellationToken token)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        await using var stream = File.OpenRead(a);
        return await DownloadHash.MatchesAsync(b, Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(stream, token)), null, token);
    }
    private async Task<VersionInfo?> ContentAsync(InstallPlan plan, string staging, DownloadSources sources, IProgress<FileDownloadProgress> progress, Action<DownloadTaskState, string, FileDownloadProgress?> update, CancellationToken token, InstallationProgressTracker overall)
    {
        var target = plan.Target ?? throw new InvalidOperationException("请选择目标游戏");
        var rootRelease = plan.Content ?? throw new InvalidOperationException("请选择内容版本");
        overall.Start("准备所选文件");
        update(DownloadTaskState.Checking, "准备所选文件", null);
        var registryPath = Path.Combine(AppPaths.Data, "downloads", "installed-content.json");
        var registry = File.Exists(registryPath) ? JsonSerializer.Deserialize<List<InstalledContent>>(await File.ReadAllTextAsync(registryPath, token)) ?? [] : new List<InstalledContent>();
        ContentRelease[] releases = [rootRelease];
        if (rootRelease.Files.Count == 0) throw new InvalidOperationException("此版本没有可下载文件");
        overall.Complete("下载与校验文件");
        var downloads = new List<(ContentRelease Release, ContentFile File, string Temp, string Destination)>();
        long totalContentBytes = releases.SelectMany(r => r.Files).Sum(f => f.Size);
        long completedContentBytes = 0;
        string folder = rootRelease.Kind == CatalogKind.Mod ? "mods" : rootRelease.Kind == CatalogKind.ResourcePack ? "resourcepacks" : "shaderpacks";
        foreach (var release in releases)
            foreach (var file in release.Files)
            {
                if (Path.GetFileName(file.Name) != file.Name || file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Name != file.Name.TrimEnd(' ', '.')) throw new InvalidDataException("服务器返回无效文件名");
                var destination = Path.Combine(plan.Directory, folder, file.Name);
                var temp = Path.Combine(staging, file.Name);
                if (downloads.Any(d => d.File.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase))) throw new IOException("依赖文件名冲突：" + file.Name);
                if (File.Exists(destination))
                {
                    if (string.IsNullOrWhiteSpace(file.Sha1) && string.IsNullOrWhiteSpace(file.Sha512) || !await DownloadHash.MatchesAsync(destination, file.Sha1, file.Sha512, token)) throw new IOException("同名文件不同，拒绝覆盖：" + file.Name);
                    File.Copy(destination, temp);
                }
                else
                {
                    await new SegmentedDownloader(sources.Http).DownloadAsync(new(file.Url, temp, file.Size, file.Sha1, file.Sha512, CachePath: ModInstallPlanner.CacheFile(plan.Root, file)), new InlineProgress<FileDownloadProgress>(p => progress.Report(p with { DownloadedBytes = completedContentBytes + p.DownloadedBytes, TotalBytes = totalContentBytes > 0 ? totalContentBytes : null })), token);
                    update(DownloadTaskState.Verifying, "校验 " + file.Name, null);
                    if (!await DownloadHash.MatchesAsync(temp, file.Sha1, file.Sha512, token)) throw new InvalidDataException("校验失败：" + file.Name);
                }
                downloads.Add((release, file, temp, destination));
                completedContentBytes += file.Size;
            }
        overall.Complete("提交内容");
        update(DownloadTaskState.Committing, "提交内容", null);
        var committed = new List<string>();
        try
        {
            foreach (var download in downloads)
            {
                token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(download.Destination)!);
                if (!File.Exists(download.Destination)) { File.Move(download.Temp, download.Destination); committed.Add(download.Destination); }
                registry.RemoveAll(i => i.Platform == download.Release.Platform && i.ProjectId == download.Release.ProjectId && string.Equals(i.Directory, plan.Directory, StringComparison.OrdinalIgnoreCase));
                registry.Add(new(plan.Directory, download.Release.Platform, download.Release.ProjectId, download.Release.Id, download.Destination));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
            await File.WriteAllTextAsync(registryPath + ".tmp", JsonSerializer.Serialize(registry), token); token.ThrowIfCancellationRequested(); File.Move(registryPath + ".tmp", registryPath, true);
        }
        catch { foreach (var file in committed) File.Delete(file); throw; }
        log.Write("已下载 " + rootRelease.Name, featured: true);
        overall.Complete("内容已就绪");
        return null;
    }
}
