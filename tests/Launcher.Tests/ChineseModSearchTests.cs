using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Launcher.App.Controls;
using Launcher.Core;
using SkiaSharp;
using Xunit;

namespace Launcher.Tests;

public sealed class ChineseModSearchTests
{
    [Fact]
    public async Task Community_Names_Are_Cached_And_Resolve_Chinese_Names_And_Abbreviations()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ikun-chinese-names-" + Guid.NewGuid());
        try
        {
            var table = "# fixture\nproject;1;custom_mod;穿透视线;Visible World;VW\n";
            var network = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(table) });
            var index = new ChineseModSearchIndex(folder, network); await index.WarmAsync(default);
            Assert.Equal("Visible World", Assert.Single(index.ResolveLocal("穿透 视线")));
            Assert.Equal("Visible World", Assert.Single(index.ResolveLocal("VW")));
            Assert.Equal("Sodium", index.ResolveLocal("钠")[0]); Assert.Equal("Create", index.ResolveLocal("机械动力")[0]);
            var offline = new Handler(_ => throw new Exception("Fresh disk cache must not request the network"));
            var restarted = new ChineseModSearchIndex(folder, offline); await restarted.WarmAsync(default);
            Assert.Equal("Visible World", Assert.Single(restarted.ResolveLocal("穿透")));
            Assert.Equal(0, offline.Requests);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task Chinese_Search_Resolves_English_Keyword_And_Keeps_Successful_Results()
    {
        var queries = new ConcurrentBag<string>();
        using var sources = new DownloadSources(new(false, false), transport: new Handler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query).Split('&').Single(v => v.StartsWith("?query="))[7..]; queries.Add(query);
            if (query != "Sodium") return new(HttpStatusCode.BadRequest) { Content = new StringContent("unsupported") };
            var json = JsonSerializer.Serialize(new { hits = new[] { new { project_id = "AANobbMI", title = "Sodium", description = "fixture", icon_url = "https://fixture.test/icon.webp", slug = "sodium" } } });
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        var results = await new DownloadCatalogService().SearchAsync(CatalogKind.Mod, ContentPlatform.Modrinth, "钠", null, sources, default);
        Assert.Equal("AANobbMI", Assert.Single(results).Id); Assert.Equal("Sodium", Assert.Single(queries));
    }
    [Fact]
    public async Task Chinese_Original_Query_Is_Used_Only_When_Translation_Has_No_Results()
    {
        var queries = new List<string>();
        using var sources = new DownloadSources(new(false, false), transport: new Handler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query).Split('&').Single(v => v.StartsWith("?query="))[7..]; queries.Add(query);
            string json = query == "钠" ? "{\"hits\":[{\"project_id\":\"fixture\",\"title\":\"钠\"}]}" : "{\"hits\":[]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        var results = await new DownloadCatalogService().SearchAsync(CatalogKind.Mod, ContentPlatform.Modrinth, "钠", null, sources, default);
        Assert.Equal("fixture", Assert.Single(results).Id);
        Assert.Equal(new[] { "Sodium", "钠" }, queries);
    }
    [Fact]
    public void WebP_Icon_Is_Decoded_To_A_Frozen_Thumbnail()
    {
        var file = Path.Combine(Path.GetTempPath(), "ikun-icon-" + Guid.NewGuid() + ".webp");
        try
        {
            using var bitmap = new SKBitmap(8, 8); using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Green);
            using var webp = bitmap.Encode(SKEncodedImageFormat.Webp, 100); using (var output = File.Create(file)) webp.SaveTo(output);
            var image = ModProjectIcon.Decode(file);
            Assert.True(image.IsFrozen); Assert.Equal(64, image.PixelWidth); Assert.Equal(64, image.PixelHeight);
            var pixels = new byte[64 * 64 * 4]; image.CopyPixels(pixels, 64 * 4, 0);
            Assert.True(pixels[1] > pixels[0] && pixels[1] > pixels[2]);
        }
        finally { File.Delete(file); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Requests); return Task.FromResult(callback(request)); }
    }
}
