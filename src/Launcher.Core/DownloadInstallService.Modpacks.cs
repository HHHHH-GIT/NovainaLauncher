namespace Launcher.Core;

public sealed partial class DownloadInstallService
{
    private async Task<VersionInfo> ModpackAsync(InstallPlan plan, string staging, DownloadSources sources,
        IProgress<FileDownloadProgress> progress, Action<DownloadTaskState, string, FileDownloadProgress?> update, CancellationToken token, InstallationProgressTracker overall)
    {
        VersionManagementService.ValidateName(plan.Name);
        if (Directory.Exists(plan.Directory) || File.Exists(plan.Directory)) throw new IOException("同名游戏已存在");
        overall.Start("获取整合包");
        var archive = Path.Combine(staging, "pack.zip");
        if (plan.LocalArchive is { } local)
        {
            await using var input = File.OpenRead(local); await using var output = File.Create(archive);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
        else
        {
            var file = plan.Content?.Files.FirstOrDefault() ?? throw new InvalidOperationException("请选择整合包版本");
            await new SegmentedDownloader(sources.Http).DownloadAsync(new(file.Url, archive, file.Size, file.Sha1, file.Sha512,
                CachePath: ModInstallPlanner.CacheFile(plan.Root, file)), progress, token).ConfigureAwait(false);
        }
        overall.Complete("读取整合包");
        update(DownloadTaskState.Checking, "读取整合包", null);
        var service = new ModpackService();
        var pack = await service.ReadAsync(archive, sources, _catalog, token).ConfigureAwait(false);
        var game = (await _catalog.GamesAsync(sources, token).ConfigureAwait(false)).FirstOrDefault(v => v.Id == pack.Minecraft)
            ?? throw new InvalidDataException("找不到整合包所需的 Minecraft：" + pack.Minecraft);
        LoaderCatalogVersion? loader = null;
        if (pack.Loader is { } required)
        {
            loader = (await _catalog.LoadersAsync(pack.Minecraft, required.Loader, sources, token).ConfigureAwait(false))
                .FirstOrDefault(v => v.Version == required.Version || v.Version == pack.Minecraft + "-" + required.Version);
            if (loader is null) throw new InvalidDataException("找不到整合包指定的加载器：" + required.Loader + " " + required.Version);
        }
        OptiFineCatalogVersion? extra = null;
        if (pack.OptiFine is { } edition)
            extra = (await _catalog.OptiFineAsync(pack.Minecraft, sources, token).ConfigureAwait(false)).FirstOrDefault(v => v.Edition == edition)
                ?? throw new InvalidDataException("找不到整合包指定的 OptiFine");
        // Nested root keeps both the verified game and pack payload out of the live library until commit.
        overall.Complete("安装基础游戏");
        var gameRoot = Path.Combine(staging, "game");
        var gameStage = Path.Combine(staging, "engine"); Directory.CreateDirectory(gameStage);
        var gamePlan = plan with { Root = gameRoot, Game = game, Loader = loader, OptiFine = extra,
            Content = null, LocalArchive = null, Target = null, TargetDirectory = null };
        await GameAsync(gamePlan, gameStage, sources, progress, update, token, readOnlyCache ?? plan.Root, nested: true, cacheWriteRoot: plan.Root).ConfigureAwait(false);
        overall.Complete("下载整合包文件");
        var instanceDirectory = Path.Combine(gameRoot, "versions", plan.Name);
        var total = pack.Files.Sum(f => f.Size);
        var states = new System.Collections.Concurrent.ConcurrentDictionary<int, FileDownloadProgress>();
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(token);
        await Parallel.ForEachAsync(Enumerable.Range(0, pack.Files.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = siblings.Token }, async (index, ct) =>
        {
            try
            {
                var file = pack.Files[index]; var output = ModpackService.SafePath(instanceDirectory, file.Path);
                if (file.Path.Equals(plan.Name + ".json", StringComparison.OrdinalIgnoreCase) || file.Path.Equals(plan.Name + ".jar", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("整合包不能覆盖版本文件");
                var report = new InlineProgress<FileDownloadProgress>(p =>
                {
                    states[index] = p;
                    var current = states.Values.ToArray();
                    progress.Report(new(current.Sum(p => p.DownloadedBytes), total, current.Sum(p => p.BytesPerSecond),
                        current.SelectMany(p => p.Connections).ToArray()));
                });
                Exception? failure = null;
                foreach (var url in file.Urls)
                {
                    try
                    {
                        await new SegmentedDownloader(sources.Http).DownloadAsync(new(url, output, file.Size, file.Sha1, file.Sha512,
                            CachePath: ModInstallPlanner.CacheFile(plan.Root, new(Path.GetFileName(file.Path), url, file.Size, file.Sha1, file.Sha512))), report, ct).ConfigureAwait(false);
                        failure = null; break;
                    }
                    catch (Exception error) when (!ct.IsCancellationRequested && error is IOException or HttpRequestException or TimeoutException) { failure = error; }
                }
                if (failure is not null) throw new IOException("整合包文件下载失败：" + file.Path, failure);
                states[index] = new(file.Size, file.Size, 0, []);
            }
            catch { siblings.Cancel(); throw; }
        }).ConfigureAwait(false);
        overall.Complete("应用整合包配置");
        update(DownloadTaskState.Installing, "应用整合包配置", null);
        await service.ExtractOverridesAsync(archive, pack, instanceDirectory, plan.Name,
            new InlineProgress<string>(message => update(DownloadTaskState.Installing, message, null)), token).ConfigureAwait(false);
        overall.Complete("验证整合包版本");
        update(DownloadTaskState.Verifying, "验证整合包版本", null);
        var installed = (await versions.ScanAsync(gameRoot, token).ConfigureAwait(false)).SingleOrDefault(v => v.Id == plan.Name && v.IsValid)
            ?? throw new InvalidDataException("整合包版本无法解析");
        overall.Complete("提交整合包");
        update(DownloadTaskState.Committing, "提交整合包", null);
        await CommitGameAsync(gameRoot, plan.Root, plan.Name, token).ConfigureAwait(false);
        try
        {
            var records = Path.Combine(AppPaths.Data, "downloads", "installs"); Directory.CreateDirectory(records);
            await File.WriteAllTextAsync(Path.Combine(records, Guid.NewGuid().ToString("N") + ".json"), System.Text.Json.JsonSerializer.Serialize(new {
                plan.Name, plan.Root, Minecraft = pack.Minecraft, Modpack = pack.Name, Loader = pack.Loader?.Loader, LoaderVersion = pack.Loader?.Version,
                Files = pack.Files.Count, Date = DateTimeOffset.Now }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Write("整合包记录无法保存：" + error.Message, LogLevel.Warning); }
        log.Write("整合包已就绪：" + plan.Name, featured: true);
        overall.Complete("整合包已就绪");
        return installed with { Root = plan.Root };
    }
}
