using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.VersionLoader;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;
[Collection("AppPaths")]
public sealed class DownloadRefactorTests
{
    [Fact] public void Local_Mod_Facets_Clean_Duplicates_And_Link_Game_Loader_And_File()
    {
        ContentRelease Release(string id, string[] games, string[] loaders) => new(id, "demo", id, ContentPlatform.Modrinth, CatalogKind.Mod, games, loaders, true, [new(id + ".jar", "https://fixture.test/" + id, 10, null)], []);
        var fabric = Release("fabric", [" 1.20.1 ", "1.20.1", "1.21.1"], [" fabric ", "FABRIC"]);
        var forge = Release("forge", ["1.20.1"], ["forge"]);
        var index = new ModReleaseIndex([fabric, fabric, forge, Release("new", ["1.20.10"], ["neoforge"]), Release("unknown", [], [])]);
        Assert.Equal("1.21.1", index.GameVersions[0]); Assert.Equal("1.20.10", index.GameVersions[1]);
        Assert.Equal(new[] { "Fabric", "Forge" }, index.Loaders("1.20.1"));
        Assert.Single(index.Releases("1.20.1", "Fabric")); Assert.Single(index.Releases("1.21.1", "Fabric"));
        Assert.Empty(index.Releases("1.21.1", "Forge"));
        Assert.Single(index.Releases(ModReleaseIndex.Unspecified, ModReleaseIndex.Unspecified));
    }
    [Fact] public async Task Mod_Releases_Without_Target_Return_All_Game_And_Loader_Variants()
    {
        string? url = null;
        var json = JsonSerializer.Serialize(new[] {
            new { id = "new", project_id = "demo", name = "New Forge", game_versions = new[] { "1.21.1" }, loaders = new[] { "forge" }, environment = "server_only", dependencies = Array.Empty<object>(), files = new[] { new { filename = "new.jar", url = "https://fixture.test/new.jar", size = 10, hashes = new { sha1 = "abc" } } } },
            new { id = "old", project_id = "demo", name = "Old Fabric", game_versions = new[] { "1.16.5" }, loaders = new[] { "fabric" }, environment = "client_and_server", dependencies = Array.Empty<object>(), files = new[] { new { filename = "old.jar", url = "https://fixture.test/old.jar", size = 10, hashes = new { sha1 = "abc" } } } }
        });
        using var sources = new DownloadSources(new(false, false), transport: new Handler(r => { url = r.RequestUri!.ToString(); return new(HttpStatusCode.OK) { Content = new StringContent(json) }; }));
        var releases = await new DownloadCatalogService().ReleasesAsync(new("demo", "Demo", "", CatalogKind.Mod), null, sources, default);
        Assert.Equal(2, releases.Count); Assert.DoesNotContain("game_versions", url); Assert.DoesNotContain("loaders=", url);
        Assert.Contains("include_changelog=false", url);
        Assert.Contains(releases, r => r.Loaders.Contains("forge")); Assert.Contains(releases, r => r.Loaders.Contains("fabric"));
    }
    [Fact] public async Task Legacy_Installed_Instance_Hides_Parents_While_Keeping_Inheritance_Intact()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikun-visible-" + Guid.NewGuid()); var oldData = AppPaths.Data;
        try
        {
            AppPaths.ConfigureData(Path.Combine(root, "data"));
            foreach (var id in new[] { "1.7.10", "1.7.10-forge", "我的游戏", "独立原版" }) Directory.CreateDirectory(Path.Combine(root, "versions", id));
            File.WriteAllText(Path.Combine(root, "versions", "1.7.10", "1.7.10.json"), "{\"id\":\"1.7.10\",\"mainClass\":\"net.minecraft.client.main.Main\",\"libraries\":[]}");
            File.WriteAllText(Path.Combine(root, "versions", "独立原版", "独立原版.json"), "{\"id\":\"独立原版\",\"mainClass\":\"net.minecraft.client.main.Main\",\"libraries\":[]}");
            File.WriteAllText(Path.Combine(root, "versions", "1.7.10-forge", "1.7.10-forge.json"), "{\"id\":\"1.7.10-forge\",\"inheritsFrom\":\"1.7.10\",\"mainClass\":\"net.minecraftforge.Main\"}");
            File.WriteAllText(Path.Combine(root, "versions", "我的游戏", "我的游戏.json"), "{\"id\":\"我的游戏\",\"inheritsFrom\":\"1.7.10-forge\"}");
            var records = Path.Combine(AppPaths.Data, "downloads", "installs"); Directory.CreateDirectory(records);
            File.WriteAllText(Path.Combine(records, "legacy.json"), JsonSerializer.Serialize(new { Root = root, Name = "我的游戏" }));
            var visible = await new VersionService().ScanAsync(root);
            Assert.Equal(new[] { "我的游戏", "独立原版" }.Order(), visible.Select(v => v.Id).Order());
            Assert.All(visible, v => Assert.True(v.IsValid));
            Assert.Equal("Forge", VersionService.ReadDetails(root, "我的游戏").Loader);
            Assert.True(File.Exists(Path.Combine(root, "versions", "1.7.10", "1.7.10.json")));
        }
        finally { AppPaths.ConfigureData(oldData); Directory.Delete(root, true); }
    }
    [Fact] public async Task Vanilla_Search_Uses_No_Fake_Loader_And_Popular_Uses_Downloads()
    {
        string? url = null;
        using var sources = new DownloadSources(new(false, false), transport: new Handler(r => { url = Uri.UnescapeDataString(r.RequestUri!.ToString()); return new(HttpStatusCode.OK) { Content = new StringContent("{\"hits\":[]}") }; }));
        var catalog = new DownloadCatalogService();
        await catalog.SearchAsync(CatalogKind.Mod, ContentPlatform.Modrinth, "Sodium", new("fixture", Path.GetTempPath(), "原版", 17) { MinecraftVersion = "1.20.1" }, sources, default);
        Assert.DoesNotContain("categories:minecraft", url); Assert.Contains("index=relevance", url);
        await catalog.SearchAsync(CatalogKind.Mod, ContentPlatform.Modrinth, "", null, sources, default);
        Assert.Contains("index=downloads", url); Assert.DoesNotContain("versions:", url);
    }
    [Fact] public async Task Integrity_Expands_Parents_And_Repairs_Client_Assets_And_Libraries()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikun-integrity-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            byte[] jar;
            using (var memory = new MemoryStream()) { using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) { using var writer = new StreamWriter(zip.CreateEntry("fixture.class").Open()); writer.Write("fixture"); } jar = memory.ToArray(); }
            var asset = new byte[] { 1, 2, 3, 4 };
            string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            var index = JsonSerializer.SerializeToUtf8Bytes(new { objects = new Dictionary<string, object> { ["fixture/file"] = new { hash = Hash(asset), size = asset.Length } } });
            var path = new MinecraftPath(root);
            Directory.CreateDirectory(Path.Combine(root, "versions", "parent")); Directory.CreateDirectory(Path.Combine(root, "versions", "instance"));
            var profile = new { id = "parent", mainClass = "fixture.Main", type = "release", assetIndex = new { id = "fixture", url = "https://fixture.test/index.json", sha1 = Hash(index), size = index.Length }, downloads = new { client = new { url = "https://fixture.test/client.jar", sha1 = Hash(jar), size = jar.Length } }, libraries = new[] { new { name = "fixture:library:1", downloads = new { artifact = new { path = "fixture/library/1/library-1.jar", url = "https://fixture.test/library.jar", sha1 = Hash(jar), size = jar.Length } } } } };
            await File.WriteAllTextAsync(path.GetVersionJsonPath("parent"), JsonSerializer.Serialize(profile));
            await File.WriteAllTextAsync(path.GetVersionJsonPath("instance"), "{\"id\":\"instance\",\"inheritsFrom\":\"parent\"}");
            using var sources = new DownloadSources(new(false, false), transport: new Handler(r => new(HttpStatusCode.OK) { Content = new ByteArrayContent(r.RequestUri!.AbsolutePath.EndsWith(".json") ? index : r.RequestUri.AbsolutePath.EndsWith(".jar") ? jar : asset) }));
            var parameters = MinecraftLauncherParameters.CreateDefault(path, sources.Http); parameters.VersionLoader = new LocalJsonVersionLoader(path);
            foreach (var extractor in parameters.FileExtractors!.Where(f => f.GetType().Name.Contains("Java")).ToArray()) parameters.FileExtractors!.Remove(extractor);
            parameters.GameInstaller = new InstallerGameFiles(sources, root, root, new InlineProgress<FileDownloadProgress>(_ => { }));
            var launcher = new MinecraftLauncher(parameters);
            await launcher.InstallAsync("instance");
            var verifier = new InstallationVerifier(); var progress = new InlineProgress<InstallationCheck>(_ => { });
            var initial = await verifier.VerifyAsync(launcher, "instance", progress, default); Assert.True(initial.IsValid); Assert.True(initial.Total >= 4);
            File.Delete(path.GetVersionJarPath("parent"));
            await File.WriteAllTextAsync(Path.Combine(root, "libraries", "fixture", "library", "1", "library-1.jar"), "broken");
            await File.WriteAllTextAsync(Path.Combine(root, "assets", "objects", Hash(asset)[..2], Hash(asset)), "broken");
            var damaged = await verifier.VerifyAsync(launcher, "instance", progress, default); Assert.Equal(3, damaged.Failures.Count);
            var repaired = await verifier.RepairAndVerifyAsync(launcher, "instance", progress, default); Assert.True(repaired.IsValid);
            Assert.Equal(jar, await File.ReadAllBytesAsync(path.GetVersionJarPath("parent")));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(handler(request)); } }
}
