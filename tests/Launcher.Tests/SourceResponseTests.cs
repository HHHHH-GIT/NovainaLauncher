using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

[Collection("AppPaths")]
public sealed class SourceResponseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mirror_200_With_Interrupted_Body_Falls_Back_To_Official(bool timeout)
    {
        var hosts = new List<string>(); var diagnostics = new List<string>();
        using var sources = new DownloadSources(new(false, true), transport: new Handler((request, token) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri.Host == "mod.mcimirror.top"
                    ? new StreamContent(new InterruptedStream(timeout)) : new StringContent("{\"hits\":[]}")
            });
        }), diagnostic: diagnostics.Add);
        var json = await sources.JsonAsync("https://api.modrinth.com/v2/search?query=private-query", default);
        Assert.Equal(0, json.GetProperty("hits").GetArrayLength());
        Assert.Equal(new[] { "mod.mcimirror.top", "api.modrinth.com" }, hosts);
        Assert.Contains(diagnostics, line => line.Contains("读取响应正文"));
        Assert.DoesNotContain(diagnostics, line => line.Contains("private-query"));
    }
    [Fact]
    public async Task Rate_Limit_Is_Preserved_And_Cooldown_Does_Not_Send_Another_Request()
    {
        int count = 0;
        using var sources = new DownloadSources(new(false, false), transport: new Handler((request, token) =>
        {
            count++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        }));
        string url = "https://rate-" + Guid.NewGuid().ToString("N") + ".test/catalog";
        var first = await Assert.ThrowsAsync<HttpRequestException>(() => sources.JsonAsync(url, default));
        var second = await Assert.ThrowsAsync<HttpRequestException>(() => sources.JsonAsync(url, default));
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(1, count);
    }
    [Fact]
    public async Task User_Cancellation_Does_Not_Request_Official_Fallback()
    {
        int count = 0;
        using var cancellation = new CancellationTokenSource();
        using var sources = new DownloadSources(new(false, true), transport: new Handler((request, token) =>
        {
            count++; cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sources.JsonAsync("https://api.modrinth.com/v2/search", cancellation.Token));
        Assert.Equal(1, count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Icon_Interrupted_Body_Falls_Back_And_Broken_Cache_Is_Replaced(bool timeout)
    {
        string oldData = AppPaths.Data;
        string folder = Path.Combine(Path.GetTempPath(), "ikun-icon-fallback-" + Guid.NewGuid());
        AppPaths.ConfigureData(folder);
        try
        {
            var hosts = new List<string>();
            using var sources = new DownloadSources(new(false, true), transport: new Handler((request, token) =>
            {
                hosts.Add(request.RequestUri!.Host);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = request.RequestUri.Host == "mod.mcimirror.top" ? new InterruptedContent(timeout) : new StringContent("valid image fixture") });
            }));
            const string url = "https://cdn.modrinth.com/data/fixture/icon.webp";
            bool Valid(string file) => File.ReadAllText(file) == "valid image fixture" ? true : throw new InvalidDataException("broken image");
            string file = (await sources.CacheIconAsync(url, default, Valid))!;
            Assert.NotNull(file); Assert.True(Valid(file));
            Assert.Equal(new[] { "mod.mcimirror.top", "cdn.modrinth.com" }, hosts);
            await sources.CacheIconAsync(url, default, Valid);
            Assert.Equal(2, hosts.Count);
            File.WriteAllText(file, "broken");
            Assert.Equal(file, await sources.CacheIconAsync(url, default, Valid));
            Assert.True(Valid(file)); Assert.Equal(4, hosts.Count);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
        }
        finally { AppPaths.ConfigureData(oldData); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task Version_List_Rejects_Mirror_Error_Object_And_Uses_Official_Array()
    {
        var hosts = new List<string>();
        using var sources = new DownloadSources(new(false, true), transport: new Handler((request, token) =>
        {
            hosts.Add(request.RequestUri!.Host);
            string json = request.RequestUri.Host == "mod.mcimirror.top" ? "{\"error\":\"temporarily unavailable\"}"
                : "[{\"id\":\"v1\",\"project_id\":\"fixture\",\"name\":\"v1\",\"game_versions\":[\"1.20.1\"],\"loaders\":[\"fabric\"],\"dependencies\":[],\"files\":[]}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        var files = await new DownloadCatalogService().ReleasesAsync(new("fixture", "Fixture", "", CatalogKind.Mod, ContentPlatform.Modrinth), null, sources, default);
        Assert.Equal("v1", Assert.Single(files).Id);
        Assert.Equal(new[] { "mod.mcimirror.top", "api.modrinth.com" }, hosts);
    }
    private sealed class InterruptedContent(bool timeout) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(timeout ? new OperationCanceledException() : new IOException("response body interrupted"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class InterruptedStream(bool timeout) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => ValueTask.FromException<int>(timeout ? new OperationCanceledException() : new IOException("response body interrupted"));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token);
    }
}
