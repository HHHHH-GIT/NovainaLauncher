using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class ModCacheHotfixTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Named_Flat_Forge_Profile_Uses_Actual_Client_Version(bool clientVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "ikun-client-version-" + Guid.NewGuid());
        var folder = Path.Combine(root, "versions", "临时内战包"); Directory.CreateDirectory(folder);
        try
        {
            var profile = new Dictionary<string, object>
            {
                ["id"] = "临时内战包", ["mainClass"] = "cpw.mods.modlauncher.Launcher",
                ["libraries"] = new[] { new { name = "net.minecraftforge:forgespi:7.0.0" } },
                ["arguments"] = new { game = new[] { "--fml.mcVersion", "1.20.1", "--fml.forgeVersion", "47.4.13" } }
            };
            if (clientVersion) profile["clientVersion"] = "1.20.1";
            File.WriteAllText(Path.Combine(folder, "临时内战包.json"), JsonSerializer.Serialize(profile));
            File.WriteAllText(Path.Combine(folder, ".ikun-profile.json"), "{\"Name\":\"临时内战包\",\"MinecraftVersion\":\"临时内战包\"}");
            var info = VersionService.ReadDetails(root, "临时内战包");
            Assert.Equal("临时内战包", info.GameName); Assert.Equal("1.20.1", info.MinecraftVersion);
            Assert.Equal("Forge · 1.20.1", info.DownloadTargetTag);
            Assert.Equal("47.4.13", info.LoaderVersion); Assert.Equal(17, info.RequiredJava);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Parsed_Cache_Survives_Restart_And_Preserves_Expired_Data_And_Cancellation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ikun-mod-cache-" + Guid.NewGuid());
        var project = new DownloadCatalogItem("demo", "Demo", "", CatalogKind.Mod);
        ContentRelease[] releases = [new("file", "demo", "Demo file", ContentPlatform.Modrinth, CatalogKind.Mod,
            ["1.20.1"], ["forge"], true, [new("demo.jar", "https://fixture.test/demo.jar", 10, null)], [])];
        try
        {
            var cache = new ModReleaseCache(folder);
            var saved = new CachedModReleases(releases, new(releases), DateTime.UtcNow.AddHours(-1));
            await cache.SaveAsync(project, saved, default);
            Assert.Same(saved, cache.ReadMemory(project));
            var restarted = new ModReleaseCache(folder);
            var stale = await restarted.ReadAsync(project, default);
            Assert.NotNull(stale); Assert.False(stale!.IsFresh);
            Assert.Equal("file", Assert.Single(stale.Index.Releases("1.20.1", "Forge")).Id);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.SaveAsync(project, saved with { SavedUtc = DateTime.UtcNow }, cts.Token));
            Assert.Same(stale, restarted.ReadMemory(project));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
            await restarted.SaveAsync(project, saved with { SavedUtc = DateTime.UtcNow }, default);
            Assert.True((await new ModReleaseCache(folder).ReadAsync(project, default))!.IsFresh);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task CurseForge_Pages_Are_Fetched_In_Parallel_Without_Losing_Or_Duplicating_Files()
    {
        var handler = new PagesHandler();
        using var sources = new DownloadSources(new(false, true), transport: handler);
        var releases = await new DownloadCatalogService().ReleasesAsync(new("demo", "Demo", "", CatalogKind.Mod, ContentPlatform.CurseForge), null, sources, default);
        Assert.Equal(201, releases.Count); Assert.Equal(201, releases.DistinctBy(r => r.Id).Count());
        Assert.InRange(handler.Maximum, 2, 4); Assert.Equal("200", releases[^1].Id);
    }
    private sealed class PagesHandler : HttpMessageHandler
    {
        private int _active;
        public int Maximum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var offset = int.Parse(request.RequestUri!.Query.Split('&').Single(p => p.StartsWith("index="))[6..]);
            var active = Interlocked.Increment(ref _active);
            int seen;
            do { seen = Maximum; } while (active > seen && Interlocked.CompareExchange(ref Maximum, active, seen) != seen);
            try
            {
                await Task.Delay(30, token);
                var json = JsonSerializer.Serialize(new { pagination = new { totalCount = 201 }, data = Enumerable.Range(offset, Math.Min(50, 201 - offset)).Select(i => new
                {
                    id = i, modId = 1, displayName = "File " + i, fileName = i + ".jar", downloadUrl = "https://fixture.test/" + i,
                    fileLength = 10, gameVersions = new[] { "1.20.1", "Forge" }, hashes = Array.Empty<object>(), dependencies = Array.Empty<object>()
                }) });
                return new(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
