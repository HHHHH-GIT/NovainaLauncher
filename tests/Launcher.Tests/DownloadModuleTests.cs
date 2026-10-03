using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class DownloadModuleTests
{
    [Fact] public void OptiFine_Requires_Exact_Declared_Forge_And_Game()
    {
        var forge = new[] { new LoaderCatalogVersion("47.2.18", "1.20.1", "Forge"), new LoaderCatalogVersion("47.2.19", "1.20.1", "Forge") };
        var of = new OptiFineCatalogVersion("1.20.1", "HD_U", "I6", "OptiFine.jar", "Forge 47.2.18");
        Assert.Equal("47.2.18", InstallCompatibility.ExactForge(of, forge));
        var plan = Plan(new GameCatalogVersion("1.20.1", "release", "", "")) with { Loader = forge[0], OptiFine = of };
        Assert.Empty(InstallCompatibility.Game(plan, forge));
        Assert.NotEmpty(InstallCompatibility.Game(plan with { Loader = forge[1] }, forge));
        Assert.NotEmpty(InstallCompatibility.Game(plan with { Loader = new("0.16", "1.20.1", "Fabric") }, forge));
        Assert.NotEmpty(InstallCompatibility.Game(plan with { OptiFine = of with { ForgeVersion = "N/A" } }, forge));
        Assert.NotEmpty(InstallCompatibility.Game(plan with { OptiFine = of with { MinecraftVersion = "1.20.2" } }, forge));
        Assert.Equal("14.23.5.2847", InstallCompatibility.ExactForge(of with { MinecraftVersion = "1.12.2", ForgeVersion = "#2847" }, [new("14.23.5.2847", "1.12.2", "Forge")]));
    }
    [Theory]
    [InlineData("1.20.1", ">=1.20 <1.21", false, true)]
    [InlineData("1.21.1", "[1.20,1.21)", true, false)]
    [InlineData("47.2.18", "[47.2.18]", true, true)]
    [InlineData("0.16.9", "^0.16.0", false, true)]
    [InlineData("0.17.0", "^0.16.0", false, false)]
    [InlineData("1.20.1", "1.20.x", false, true)]
    [InlineData("1.20.1", ">=1.20 <1.20.2-", false, true)]
    [InlineData("1.20.2", ">=1.20 <1.20.2-", false, false)]
    [InlineData("1.21.1", "[1.20,1.21),[1.21.1,1.22)", true, true)]
    public void Version_Predicates(string version, string range, bool maven, bool expected) => Assert.Equal(expected, InstallCompatibility.Matches(version, range, maven));
    [Fact] public void Nested_Api_Candidates_Select_Compatible_Version_Without_Allowing_Duplicate_Mods()
    {
        var target = new VersionInfo("fixture", Path.GetTempPath(), "Fabric", 17) { MinecraftVersion = "1.20.1", LoaderVersion = "0.16.9" };
        ModMetadata consumer = new("consumer", "1.0.0", "fabric", [], [new("api", ">=2"), new("minecraft", ">=1.20 <1.20.2-")]);
        ModMetadata older = new("api", "1.0.0", "fabric", [], [], true);
        ModMetadata newer = older with { Version = "2.0.0" };
        InstallCompatibility.CheckMetadata([consumer, older, newer], target, 17);
        Assert.Throws<InvalidOperationException>(() => InstallCompatibility.CheckMetadata([consumer, older], target, 17));
        Assert.Throws<InvalidOperationException>(() => InstallCompatibility.CheckMetadata([consumer, consumer, newer], target, 17));
    }
    [Fact] public void Missing_Dependencies_Conflicts_And_Unknown_Constraints_Are_Blocked()
    {
        var target = new VersionInfo("fixture", Path.GetTempPath(), "Fabric", 17) { MinecraftVersion = "1.20.1", LoaderVersion = "0.16.9" };
        ModMetadata mod = new("demo", "1.0.0", "fabric", [], [new("minecraft", ">=1.20 <1.21"), new("fabricloader", ">=0.16"), new("java", ">=17"), new("api", ">=2")]);
        Assert.Throws<InvalidOperationException>(() => InstallCompatibility.CheckMetadata([mod], target, 17));
        ModMetadata api = new("api", "2.0.0", "fabric", [], []);
        InstallCompatibility.CheckMetadata([mod, api], target, 17);
        Assert.Throws<InvalidOperationException>(() => InstallCompatibility.CheckMetadata([mod with { Constraints = [new("api", "*", true)] }, api], target, 17));
        Assert.Throws<InvalidOperationException>(() => InstallCompatibility.CheckMetadata([mod with { Constraints = [new("minecraft", "unparseable-range")] }], target, 17));
        Assert.Throws<InvalidOperationException>(() => InstallCompatibility.CheckMetadata([mod, mod, api], target, 17));
    }
    [Fact] public async Task Sources_Fallback_And_Never_Leak_CurseForge_Key_On_Redirect()
    {
        var seen = new List<(string Host, bool Key)>();
        using var transport = new Handler(r =>
        {
            seen.Add((r.RequestUri!.Host, r.Headers.Contains("x-api-key")));
            if (r.RequestUri.Host == "mod.mcimirror.top") return new(HttpStatusCode.ServiceUnavailable);
            if (r.RequestUri.Host == "api.curseforge.com") { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://fixture.test/file"); return response; }
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        using var sources = new DownloadSources(new(true, true, "fixture-secret"), transport: transport);
        await sources.JsonAsync("https://api.curseforge.com/v1/mods/search", default);
        Assert.Equal(new[] { ("mod.mcimirror.top", false), ("api.curseforge.com", true), ("fixture.test", false) }, seen);
        using var unavailable = new DownloadSources(new(false, false), transport: new Handler(_ => throw new Exception("must not send")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unavailable.JsonAsync("https://api.curseforge.com/v1/mods/search", default));
    }
    [Fact] public async Task Manual_Mod_Install_Skips_Compatibility_And_Dependencies_But_Checks_File_Hash()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikun-install-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var previousData = AppPaths.Data;
        AppPaths.ConfigureData(Path.Combine(root, "data"));
        try
        {
            var api = Jar("api", "2.0.0", new { minecraft = "1.20.1", fabricloader = ">=0.16" });
            var demo = Jar("demo", "1.0.0", new { minecraft = "1.21.1", api = ">=2.0.0", java = ">=21" });
            ContentFile file(string name, byte[] data) => new(name, "https://fixture.test/" + name, data.Length, Convert.ToHexString(SHA1.HashData(data)));
            var release = new ContentRelease("demo-v1", "demo-project", "demo", ContentPlatform.Modrinth, CatalogKind.Mod, ["1.21.1"], ["forge"], false, [file("demo.jar", demo)], [new("api-v2", "api-project", "required")]);
            HttpResponseMessage Server(HttpRequestMessage request)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("project/api-project")) return new(HttpStatusCode.OK) { Content = new StringContent("{\"project_type\":\"mod\",\"client_side\":\"required\"}") };
                if (request.RequestUri!.AbsolutePath.EndsWith("api-v2")) return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = "api-v2", project_id = "api-project", name = "API", game_versions = new[] { "1.20.1" }, loaders = new[] { "fabric" }, dependencies = Array.Empty<object>(), files = new[] { new { filename = "api.jar", url = "https://fixture.test/api.jar", size = api.Length, primary = true, hashes = new { sha1 = Convert.ToHexString(SHA1.HashData(api)) } } } })) };
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith("demo.jar") ? demo : api) };
            }
            using var http = new HttpClient(); using var log = new LogService(Path.Combine(root, "logs"));
            var service = new DownloadInstallService(new JavaService(http, log), new VersionService(), log, sourceFactory: (p, ct) => new DownloadSources(p, ct, new Handler(Server)));
            var target = new VersionInfo("fixture", root, "Fabric", 17) { MinecraftVersion = "1.20.1", LoaderVersion = "0.16.9" };
            var directory = Path.Combine(root, "versions", "fixture");
            var plan = Plan(null) with { Name = "demo", Root = root, Target = target, TargetDirectory = directory, Content = release };
            await service.InstallAsync(plan, (_, _, _) => { }, default);
            Assert.False(File.Exists(Path.Combine(directory, "mods", "api.jar"))); Assert.True(File.Exists(Path.Combine(directory, "mods", "demo.jar")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".ikun-downloads")).Where(p => Path.GetFileName(p) != "cache"));
            // Identical files are reused, regardless of platform version labels.
            await service.InstallAsync(plan, (_, _, _) => { }, default);
            await service.InstallAsync(plan with { Content = release with { Id = "demo-v2" } }, (_, _, _) => { }, default);
            // Hash failure never commits a new file and always removes this task's staging folder.
            var bad = release with { Id = "bad", ProjectId = "bad-project", Files = [file("bad.jar", demo) with { Sha1 = new string('0', 40) }], Dependencies = [] };
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(plan with { Content = bad }, (_, _, _) => { }, default));
            Assert.False(File.Exists(Path.Combine(directory, "mods", "bad.jar")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".ikun-downloads")).Where(p => Path.GetFileName(p) != "cache"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallAsync(plan, (_, _, _) => { }, cancelled.Token));
        }
        finally { AppPaths.ConfigureData(previousData); Directory.Delete(root, true); }
    }
    private static InstallPlan Plan(GameCatalogVersion? game) => new("fixture", Path.GetTempPath(), game, null, null, null, null, null, new(false, false), AppPaths.Runtime, []);
    private static byte[] Jar(string id, string version, object depends)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open())) writer.Write(JsonSerializer.Serialize(new { schemaVersion = 1, id, version, environment = "client", depends }));
        return output.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
}
