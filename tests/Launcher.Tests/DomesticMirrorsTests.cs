using System.Net;
using System.Net.Http;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class DomesticMirrorsTests
{
    [Theory]
    [InlineData("https://mediafilez.forgecdn.net/files/1/2/a.jar", "https://mod.mcimirror.top/files/1/2/a.jar")]
    [InlineData("https://cdn.modrinth.com/data/p/versions/v/a.jar", "https://mod.mcimirror.top/data/p/versions/v/a.jar")]
    [InlineData("https://resources.download.minecraft.net/ab/hash", "https://bmclapi2.bangbang93.com/assets/ab/hash")]
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/a.jar", "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/a.jar")]
    [InlineData("https://authlib-injector.yushi.moe/artifact/latest.json", "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json")]
    [InlineData("https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.8%2B9/OpenJDK21U-jre_x64_windows_hotspot_21.0.8_9.zip", "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/21/jre/x64/windows/OpenJDK21U-jre_x64_windows_hotspot_21.0.8_9.zip")]
    public void Every_Resource_Uses_Mirror_First(string original, string mirror)
    {
        Assert.Equal(mirror, DownloadSources.Mirror(new(original), new(true, true)));
        Assert.Null(DownloadSources.Mirror(new(original), new(false, false)));
    }
    [Fact] public async Task Range_Requests_Reuse_The_Mirror_Node_And_Keep_Range_Header()
    {
        var requests = new List<string>();
        using var sources = new DownloadSources(new(true, true), transport: new Handler(r =>
        {
            requests.Add(r.RequestUri!.Host);
            if (r.RequestUri.Host == "bmclapi2.bangbang93.com") return new(HttpStatusCode.Found) { Headers = { Location = new("https://node.test/asset.jar") } };
            if (requests.Count == 3) Assert.Equal("bytes=0-3", r.Headers.Range!.ToString());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        }));
        using (var first = await sources.Http.GetAsync("https://libraries.minecraft.net/asset.jar")) first.EnsureSuccessStatusCode();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://libraries.minecraft.net/asset.jar"); request.Headers.Range = new(0, 3);
        using var second = await sources.Http.SendAsync(request); second.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "bmclapi2.bangbang93.com", "node.test", "node.test" }, requests);
    }
    [Fact] public async Task Failed_Content_Mirror_Falls_Back_Without_Leaking_CurseForge_Key()
    {
        var hosts = new List<string>();
        using var sources = new DownloadSources(new(true, true, "private-key"), transport: new Handler(r =>
        {
            hosts.Add(r.RequestUri!.Host); Assert.False(r.Headers.Contains("x-api-key"));
            return new(r.RequestUri.Host == "mod.mcimirror.top" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        }));
        using var response = await sources.Http.GetAsync("https://mediafilez.forgecdn.net/files/1/2/a.jar"); response.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "mod.mcimirror.top", "mediafilez.forgecdn.net" }, hosts);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(callback(request)); }
}
