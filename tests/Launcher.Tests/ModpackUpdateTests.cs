using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class ModpackUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ikun-pack 中文 " + Guid.NewGuid());
    public ModpackUpdateTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));
    [Fact] public async Task Zip_Import_Identifies_Manifest_Without_Network_And_Rejects_Ordinary_Zip()
    {
        var modrinth = Path.Combine(_root, "modrinth.zip");
        using (var zip = ZipFile.Open(modrinth, ZipArchiveMode.Create))
            Entry(zip, "modrinth.index.json", "{\"formatVersion\":1,\"game\":\"minecraft\",\"dependencies\":{\"minecraft\":\"1.20.1\"},\"files\":[]}");
        var curse = Path.Combine(_root, "curse.zip");
        using (var zip = ZipFile.Open(curse, ZipArchiveMode.Create))
            Entry(zip, "manifest.json", "{\"manifestType\":\"minecraftModpack\",\"manifestVersion\":1,\"minecraft\":{\"version\":\"1.20.1\"},\"files\":[]}");
        var ordinary = Path.Combine(_root, "ordinary.zip");
        using (var zip = ZipFile.Open(ordinary, ZipArchiveMode.Create)) Entry(zip, "README.txt", "ordinary archive");
        Assert.Equal("Modrinth", await ModpackService.InspectArchiveAsync(modrinth));
        Assert.Equal("CurseForge", await ModpackService.InspectArchiveAsync(curse));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModpackService.InspectArchiveAsync(ordinary));
    }
    [Fact] public async Task Removing_Ended_Task_Preserves_Game_Files_And_Does_Not_Remove_Active_Task()
    {
        using var http = new HttpClient(); using var log = new LogService(Path.Combine(_root, "logs"));
        var queue = new DownloadTaskService(new(new JavaService(http, log), new VersionService(), log), _ => false);
        var terminal = new TaskCompletionSource<DownloadTaskInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool activeRemoved = true;
        queue.Changed += state =>
        {
            if (state.State == DownloadTaskState.Queued) activeRemoved = queue.Remove(state.Id);
            if (!state.IsActive) terminal.TrySetResult(state);
        };
        var directory = Path.Combine(_root, "versions", "existing"); Directory.CreateDirectory(directory);
        var saved = Path.Combine(directory, "keep.json"); await File.WriteAllTextAsync(saved, "existing game");
        var id = queue.Enqueue(new("broken-import", _root, null, null, null, null, null, null, new(false, false), _root, [], LocalArchive: Path.Combine(_root, "missing.zip")));
        var result = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(activeRemoved); Assert.Equal(DownloadTaskState.Failed, result.State);
        Assert.True(queue.Remove(id)); Assert.False(queue.Remove(id));
        await queue.CancelAndWaitAsync(); Assert.False(queue.IsBusy);
        Assert.Equal("existing game", await File.ReadAllTextAsync(saved));
        Assert.True(new DownloadTaskInfo(Guid.NewGuid(), "finished", DownloadTaskState.Completed, "ok").CanRemove);
        Assert.True(new DownloadTaskInfo(Guid.NewGuid(), "cancelled", DownloadTaskState.Cancelled, "cancelled").CanRemove);
    }
    private static string Sha512(byte[] bytes) => Convert.ToHexString(SHA512.HashData(bytes));
    private static void Entry(ZipArchive zip, string name, string text)
    { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }

    [Fact] public async Task Export_Reimports_Mod_Configuration_Without_Shared_Game_Data_And_Cancellation_Leaves_No_Part()
    {
        var game = Path.Combine(_root, "instance"); Directory.CreateDirectory(Path.Combine(game, "mods")); Directory.CreateDirectory(Path.Combine(game, "config"));
        File.WriteAllText(Path.Combine(game, "mods", "test.jar"), "fixture"); File.WriteAllText(Path.Combine(game, "config", "test.json"), "{}");
        Directory.CreateDirectory(Path.Combine(game, "saves")); File.WriteAllText(Path.Combine(game, "saves", "private"), "world");
        File.WriteAllText(Path.Combine(game, "launcher_accounts.json"), "private");
        var version = new VersionInfo("instance", _root, "Forge", 17) { GameName = "中文世界", MinecraftVersion = "1.20.1", LoaderVersion = "1.20.1-47.3.0" };
        var output = Path.Combine(_root, "中文世界.mrpack"); var service = new ModpackService();
        await service.ExportAsync(version, game, output, null, default);
        using var sources = new DownloadSources(new(false, false), transport: new Handler(_ => throw new Exception("Offline export must not request network")));
        var pack = await service.ReadAsync(output, sources, new(), default);
        Assert.Equal("1.20.1", pack.Minecraft); Assert.Equal("47.3.0", pack.Loader!.Version); Assert.Equal("Forge", pack.Loader.Loader);
        var extracted = Path.Combine(_root, "import");
        await service.ExtractOverridesAsync(output, pack, extracted, "new", null, default);
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(extracted, "mods", "test.jar")));
        Assert.True(File.Exists(Path.Combine(extracted, "config", "test.json")));
        Assert.False(Directory.Exists(Path.Combine(extracted, "saves"))); Assert.False(File.Exists(Path.Combine(extracted, "launcher_accounts.json")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(version, game, output + ".cancel", null, cancelled.Token));
        Assert.Empty(Directory.GetFiles(_root, "*.part"));
    }
    [Fact] public async Task Mrpack_Installs_All_Files_In_Staging_And_Only_One_Visible_Instance()
    {
        var oldData = AppPaths.Data; AppPaths.ConfigureData(Path.Combine(_root, "data"));
        try
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            var packPath = Path.Combine(_root, "fixture.mrpack");
            using (var archive = ZipFile.Open(packPath, ZipArchiveMode.Create))
            {
                Entry(archive, "modrinth.index.json", JsonSerializer.Serialize(new { formatVersion = 1, game = "minecraft", versionId = "1", name = "fixture",
                    dependencies = new Dictionary<string, string> { ["minecraft"] = "1.20.1" },
                    files = new[] { new { path = "mods/test.jar", downloads = new[] { "https://fixture.test/mod.jar" }, fileSize = payload.Length,
                        hashes = new { sha1 = Sha1(payload), sha512 = Sha512(payload) } } } }));
                Entry(archive, "overrides/config/test.json", "original"); Entry(archive, "client-overrides/config/test.json", "client");
            }
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new { id = "1.20.1", type = "release", mainClass = "net.minecraft.client.main.Main", libraries = Array.Empty<object>(), javaVersion = new { majorVersion = 17 } });
            HttpResponseMessage Server(HttpRequestMessage request)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("version_manifest_v2.json")) return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { versions = new[] { new { id = "1.20.1", type = "release", url = "https://fixture.test/version.json", sha1 = Sha1(metadata) } } })) };
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith("version.json") ? metadata : payload) };
            }
            using var http = new HttpClient(); using var log = new LogService(Path.Combine(_root, "logs"));
            var installer = new DownloadInstallService(new JavaService(http, log), new(), log, sourceFactory: (p, ct) => new(p, ct, new Handler(Server)), gameEngine: new FixtureEngine());
            var plan = new InstallPlan("我的整合包", Path.Combine(_root, "game"), null, null, null, null, null, null,
                new(false, false), Path.Combine(_root, "runtime"), [], LocalArchive: packPath) { AutoPrepareJava = false };
            var queue = new DownloadTaskService(installer, _ => false);
            var observations = new List<DownloadTaskInfo>(); queue.Changed += p => { lock (observations) observations.Add(p); };
            var result = await queue.WaitAsync(queue.Enqueue(plan));
            Assert.Equal(DownloadTaskState.Completed, result.State);
            var installed = result.InstalledVersion;
            Assert.Equal(100, result.Percent); Assert.Equal(7, result.OverallProgress!.TotalSteps);
            DownloadTaskInfo[] snapshots; lock (observations) snapshots = observations.ToArray();
            Assert.All(snapshots, p => Assert.Equal(7, p.OverallProgress!.TotalSteps));
            Assert.True(snapshots.Zip(snapshots.Skip(1), (a, b) => a.Percent <= b.Percent).All(x => x));
            Assert.Contains(snapshots, p => p.HasStageProgress && p.StagePercent == 100 && p.Percent < 100);
            Assert.Contains(snapshots, p => p.HasStageProgress && p.StagePercent == 0 && p.Percent > 0);
            Assert.Equal(plan.Name, installed!.Id);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(plan.Directory, "mods", "test.jar")));
            Assert.Equal("client", File.ReadAllText(Path.Combine(plan.Directory, "config", "test.json")));
            Assert.Equal(plan.Name, Assert.Single(await new VersionService().ScanAsync(plan.Root)).Id);
            Assert.True(File.Exists(Path.Combine(plan.Root, "versions", "1.20.1", "1.20.1.json")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(plan.Root, ".ikun-downloads")) .Where(path => Path.GetFileName(path) != "cache"));
        }
        finally { AppPaths.ConfigureData(oldData); }
    }
    [Fact] public async Task Invalid_Override_Path_Is_Rejected_And_Client_Unsupported_Files_Are_Skipped()
    {
        var archivePath = Path.Combine(_root, "invalid.mrpack");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Entry(zip, "overrides/../../escape.txt", "invalid");
            Entry(zip, "modrinth.index.json", "{\"formatVersion\":1,\"game\":\"minecraft\",\"name\":\"fixture\",\"dependencies\":{\"minecraft\":\"1.20.1\"},\"files\":[{\"path\":\"server.jar\",\"env\":{\"client\":\"unsupported\"}}]}");
        }
        var pack = new ModpackDefinition("fixture", "1.20.1", null, [], "overrides", true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModpackService().ExtractOverridesAsync(archivePath, pack, Path.Combine(_root, "target"), "instance", null, default));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.Throws<InvalidDataException>(() => ModpackService.SafePath(_root, "C:/escape"));
        using var sources = new DownloadSources(new(false, false), transport: new Handler(_ => throw new Exception("must not request")));
        Assert.Empty((await new ModpackService().ReadAsync(archivePath, sources, new(), default)).Files);
    }
    [Fact] public async Task CurseForge_Pack_Resolves_Files_With_One_Bulk_Request_And_Exact_Loader()
    {
        var archive = Path.Combine(_root, "curse.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) Entry(zip, "manifest.json", "{\"manifestType\":\"minecraftModpack\",\"manifestVersion\":1,\"name\":\"CF\",\"overrides\":\"overrides\",\"minecraft\":{\"version\":\"1.20.1\",\"modLoaders\":[{\"id\":\"forge-47.3.0\",\"primary\":true}]},\"files\":[{\"projectID\":12,\"fileID\":34,\"required\":true}]}");
        int requests = 0;
        using var sources = new DownloadSources(new(false, true), transport: new Handler(request =>
        {
            requests++; Assert.Equal(HttpMethod.Post, request.Method); Assert.EndsWith("/v1/mods/files", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":34,\"modId\":12,\"displayName\":\"fixture\",\"fileName\":\"test.jar\",\"downloadUrl\":\"https://edge.forgecdn.net/test.jar\",\"fileLength\":4,\"hashes\":[{\"algo\":1,\"value\":\"" + Sha1([1,2,3,4]) + "\"}],\"dependencies\":[],\"gameVersions\":[\"1.20.1\",\"Forge\"]}]} ") };
        }));
        var pack = await new ModpackService().ReadAsync(archive, sources, new(), default);
        Assert.Equal(1, requests); Assert.Equal("Forge", pack.Loader!.Loader); Assert.Equal("47.3.0", pack.Loader.Version);
        Assert.Equal("mods/test.jar", Assert.Single(pack.Files).Path);
    }
    [Fact] public async Task Delete_Blocks_Referenced_Game_And_Chinese_Display_Keeps_Original_Search_Title()
    {
        foreach (var id in new[] { "parent", "child" }) Directory.CreateDirectory(Path.Combine(_root, "versions", id));
        File.WriteAllText(Path.Combine(_root, "versions", "parent", "parent.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "versions", "child", "child.json"), "{\"inheritsFrom\":\"parent\"}");
        await Assert.ThrowsAsync<IOException>(() => new VersionManagementService().DeleteAsync(new("parent", _root, "原版", 17)));
        Assert.True(Directory.Exists(Path.Combine(_root, "versions", "parent")));
        var catalog = new DownloadCatalogService();
        var item = catalog.Localize(new("sodium", "Sodium", "description", CatalogKind.Mod));
        Assert.Equal("钠(Sodium)", item.DisplayName); Assert.Equal("Sodium", item.Name); Assert.Equal("description", item.Description);
        Assert.Equal("Untranslated", catalog.Localize(new("x", "Untranslated", "", CatalogKind.Mod)).DisplayName);
    }
    private sealed class FixtureEngine : IGameInstallEngine
    {
        public Task<string> InstallAsync(GameInstallRequest request, Action<DownloadTaskState, string, FileDownloadProgress?> update, CancellationToken token)
        {
            Assert.False(request.Plan.AutoPrepareJava); Assert.Null(request.Java);
            update(DownloadTaskState.Downloading, "第一批", new(100, 100, 100, []));
            update(DownloadTaskState.Installing, "处理加载器", null);
            update(DownloadTaskState.Downloading, "第二批", new(0, 1000, 100, []));
            update(DownloadTaskState.Verifying, "复检", null);
            var directory = Path.Combine(request.Staging, "versions", request.Plan.Name); Directory.CreateDirectory(directory);
            Assert.NotNull(request.DownloadCacheRoot);
            Assert.NotEqual(request.Plan.Root, request.DownloadCacheRoot);
            File.WriteAllText(Path.Combine(directory, request.Plan.Name + ".json"), JsonSerializer.Serialize(new { id = request.Plan.Name, inheritsFrom = request.Plan.Game!.Id }));
            File.WriteAllText(Path.Combine(directory, ".ikun-profile.json"), JsonSerializer.Serialize(new GameProfile(request.Plan.Name, request.Plan.Game.Id)));
            return Task.FromResult(request.Plan.Name);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
}
