using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installer.NeoForge.Installers;
using CmlLib.Core.Installer.NeoForge.Versions;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.VersionLoader;
namespace Launcher.Core;
public sealed class NativeGameInstallEngine : IGameInstallEngine
{
    private readonly DownloadCatalogService _catalog = new();
    public async Task<string> InstallAsync(GameInstallRequest request, Action<DownloadTaskState, string, FileDownloadProgress?> update, CancellationToken token)
    {
        var plan = request.Plan; var staging = request.Staging; var runtime = request.Java; var readOnlyCache = request.ReadOnlyCache;
        var game = plan.Game!;
        using var sources = new DownloadSources(plan.Sources, token);
        var progress = new InlineProgress<FileDownloadProgress>(p => update(DownloadTaskState.Downloading, sources.ActualSource, p));
        var parameters = MinecraftLauncherParameters.CreateDefault(new MinecraftPath(staging), sources.Http);
        parameters.VersionLoader = new LocalJsonVersionLoader(parameters.MinecraftPath!);
        if (parameters.FileExtractors is not null)
            foreach (var extractor in parameters.FileExtractors.ToArray())
                if (extractor.GetType().Name.Contains("Java", StringComparison.Ordinal)) parameters.FileExtractors.Remove(extractor);
        parameters.GameInstaller = new InstallerGameFiles(sources, staging, request.DownloadCacheRoot ?? plan.Root, progress, readOnlyCache);
        var launcher = new MinecraftLauncher(parameters);
        using (var profile = JsonDocument.Parse(await File.ReadAllTextAsync(launcher.MinecraftPath.GetVersionJsonPath(game.Id), token)))
            if (profile.RootElement.TryGetProperty("assetIndex", out var index))
            {
                var file = new CmlLib.Core.Files.GameFile("资源索引") { Path = Path.Combine(launcher.MinecraftPath.Assets, "indexes", index.GetProperty("id").GetString() + ".json"), Url = index.GetProperty("url").GetString(), Hash = index.GetProperty("sha1").GetString(), Size = index.TryGetProperty("size", out var size) ? size.GetInt64() : 0 };
                await launcher.GameInstaller.Install([file], null, null, token);
            }
        await launcher.InstallAsync(game.Id, token);
        string installedId = game.Id;
        if (plan.Loader is { } loader)
        {
            update(DownloadTaskState.Installing, "安装 " + loader.Loader, null);
            var output = new InlineProgress<string>(line => update(DownloadTaskState.Installing, line, null));
            if (loader.Loader == "Forge")
            {
                var list = await _catalog.ForgeVersionsAsync(game.Id, sources, token);
                var selected = list.Single(v => v.ForgeVersionName == loader.Version);
                {
                    var installer = new ForgeInstallerVersionMapper().CreateInstaller(selected);
                    await installer.Install(launcher.MinecraftPath, launcher.GameInstaller, new ForgeInstallOptions { JavaPath = runtime.Path, CancellationToken = token, InstallerOutput = output, SkipIfAlreadyInstalled = false });
                    token.ThrowIfCancellationRequested(); installedId = installer.VersionName;
                }
            }
            else if (loader.Loader == "NeoForge")
            {
                var selected = new NeoForgeVersion(game.Id, loader.Version);
                var installer = new NeoForgeInstallerVersionMapper().CreateInstaller(selected);
                await installer.Install(launcher.MinecraftPath, launcher.GameInstaller, new NeoForgeInstallOptions { JavaPath = runtime.Path, CancellationToken = token, InstallerOutput = output, SkipIfAlreadyInstalled = false });
                token.ThrowIfCancellationRequested(); installedId = installer.VersionName;
            }
            else
            {
                installedId = FabricInstaller.GetVersionName(game.Id, loader.Version);
                await using var profileStream = await new FabricInstaller(sources.Http).GetProfileJson(game.Id, loader.Version);
                var profilePath = launcher.MinecraftPath.GetVersionJsonPath(installedId);
                Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
                await using var profileOutput = File.Create(profilePath);
                await profileStream.CopyToAsync(profileOutput, token);
            }
            await launcher.InstallAsync(installedId, token);
        }
        if (plan.OptiFine is { } of)
        {
            var file = Path.Combine(staging, of.FileName);
            bool downloaded = false;
            if (plan.Sources.GameMirror)
            {
                try { await new SegmentedDownloader(sources.Http).DownloadFileAsync($"https://bmclapi2.bangbang93.com/optifine/{Uri.EscapeDataString(of.MinecraftVersion)}/{Uri.EscapeDataString(of.Type)}/{Uri.EscapeDataString(of.Patch)}", file, progress, token); downloaded = true; }
                catch (Exception error) when (!token.IsCancellationRequested && error is IOException or HttpRequestException or TaskCanceledException) { }
            }
            if (!downloaded) await new SegmentedDownloader(sources.Http).DownloadFileAsync(await _catalog.OptiFineOfficialUrlAsync(of, sources, token), file, progress, token);
            update(DownloadTaskState.Verifying, "检查 OptiFine", null);
            using (var zip = ZipFile.OpenRead(file)) if (zip.GetEntry("optifine/OptiFineTweaker.class") is null) throw new InvalidDataException("OptiFine 文件无效");
            update(DownloadTaskState.Installing, "安装 OptiFine", null);
            if (plan.Loader is null) { installedId = await OptiFineLocalInstaller.InstallAsync(staging, of, file, token); await launcher.InstallAsync(installedId, token); }
            else { var mods = Path.Combine(staging, "versions", installedId, "mods"); Directory.CreateDirectory(mods); File.Copy(file, Path.Combine(mods, of.FileName)); }
        }
        update(DownloadTaskState.Installing, "解析本地版本", null);
        // A named inheriting profile is the instance; never rename or overwrite a shared base profile.
        if (installedId != plan.Name)
        {
            var finalDir = Path.Combine(staging, "versions", plan.Name); Directory.CreateDirectory(finalDir);
            var profile = new JsonObject { ["id"] = plan.Name, ["inheritsFrom"] = installedId, ["type"] = game.Type };
            await File.WriteAllTextAsync(Path.Combine(finalDir, plan.Name + ".json"), profile.ToJsonString(), token);
            var sourceMods = Path.Combine(staging, "versions", installedId, "mods");
            if (Directory.Exists(sourceMods)) Directory.Move(sourceMods, Path.Combine(finalDir, "mods"));
        }
        Directory.CreateDirectory(Path.Combine(staging, "versions", plan.Name, "mods"));
        update(DownloadTaskState.Verifying, "复检游戏文件", null);
        var report = new InlineProgress<InstallationCheck>(p => update(DownloadTaskState.Verifying, $"检查文件 {p.Checked} / {p.Total}", null));
        var installerFiles = (InstallerGameFiles)parameters.GameInstaller!;
        await installerFiles.VerifyProcessorOutputsAsync(token);
        var result = await new InstallationVerifier().RepairAndVerifyAsync(launcher, plan.Name, report, token);
        await File.WriteAllTextAsync(Path.Combine(staging, "installation-check.json"), JsonSerializer.Serialize(new { result.Checked, result.Total, result.Failures, Basis = "发行大小与 SHA-1；无公开哈希的文件验证 ZIP/JSON 结构；处理器声明的 SHA-1 输出" }), token);
        return plan.Name;
    }
}
