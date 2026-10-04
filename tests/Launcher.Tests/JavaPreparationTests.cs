using System.IO;
using System.Net.Http;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class JavaPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novaina-java-" + Guid.NewGuid().ToString("N"));
    private InstallPlan Plan(string? loader, bool automatic, params JavaRuntimeInfo[] javas) =>
        new("instance", _root, new("1.20.1", "release", "https://fixture.test/version.json", ""),
            loader is null ? null : new("fixture", "1.20.1", loader), null, null, null, null,
            new(false, false), Path.Combine(_root, "runtime"), javas) { AutoPrepareJava = automatic };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Matching_Local_Java_Is_Reused_Without_Downloading(bool automatic)
    {
        using var http = new HttpClient(new NoNetwork()); using var log = new LogService(Path.Combine(_root, "logs"));
        var matching = new JavaRuntimeInfo("local-java", 17, new(17, 0, 12), "x64", "fixture");
        var wrong = matching with { Major = 21, Version = new(21, 0, 4) };
        var actual = await new JavaService(http, log).PrepareForInstallAsync(17, Plan("Forge", automatic, wrong, matching), new InlineProgress<JavaDownloadProgress>(_ => { }), default);
        Assert.Same(matching, actual); Assert.False(Directory.Exists(Path.Combine(_root, "runtime")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Fabric")]
    public async Task Disabled_Automatic_Java_Allows_Installation_Without_Java_Processors(string? loader)
    {
        using var http = new HttpClient(new NoNetwork()); using var log = new LogService(Path.Combine(_root, "logs"));
        Assert.Null(await new JavaService(http, log).PrepareForInstallAsync(17, Plan(loader, false), new InlineProgress<JavaDownloadProgress>(_ => { }), default));
    }

    [Theory]
    [InlineData("Forge")]
    [InlineData("NeoForge")]
    public async Task Disabled_Automatic_Java_Explains_Missing_Installer_Runtime(string loader)
    {
        using var http = new HttpClient(new NoNetwork()); using var log = new LogService(Path.Combine(_root, "logs"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new JavaService(http, log).PrepareForInstallAsync(17, Plan(loader, false), new InlineProgress<JavaDownloadProgress>(_ => { }), default));
        Assert.Contains("Java 17", error.Message); Assert.Contains("自动下载已关闭", error.Message);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("不应请求 Java 下载");
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
