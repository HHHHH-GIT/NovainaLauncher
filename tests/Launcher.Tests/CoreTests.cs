using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public class CoreTests
{
    [Fact]
    public void OfflinePlayer_Generates_Valid_Uuid()
    {
        var profile = AccountService.Offline("Steve");
        Assert.Equal("Steve", profile.Name);
        Assert.Equal(AccountKind.Offline, profile.Kind);
        Assert.False(string.IsNullOrWhiteSpace(profile.Uuid));
        Assert.Equal(32, profile.Uuid.Length);

        // Deterministic check
        var profile2 = AccountService.Offline("Steve");
        Assert.Equal(profile.Uuid, profile2.Uuid);
    }

    [Fact]
    public void Java_Parse_Extracts_Version_And_Arch()
    {
        var sampleOutput = @"
java.version = 21.0.2
os.arch = amd64
java.vendor = Eclipse Adoptium
";
        var info = JavaService.Parse(@"C:\Java\bin\java.exe", sampleOutput);
        Assert.NotNull(info);
        Assert.Equal(21, info.Major);
        Assert.Equal("x64", info.Architecture);
        Assert.Equal("Eclipse Adoptium", info.Vendor);
    }

    [Fact]
    public void LegacyJava_Maps_Known_Minecraft_Versions()
    {
        Assert.Equal(8, VersionService.LegacyJava("1.12.2"));
        Assert.Equal(8, VersionService.LegacyJava("1.16.5"));
        Assert.Equal(16, VersionService.LegacyJava("1.17"));
        Assert.Equal(17, VersionService.LegacyJava("1.18.2"));
        Assert.Equal(17, VersionService.LegacyJava("1.20.1"));
        Assert.Equal(21, VersionService.LegacyJava("1.20.6"));
        Assert.Equal(21, VersionService.LegacyJava("1.21.0"));
    }

    [Fact]
    public void LogService_Redacts_Tokens_And_Passwords()
    {
        using var log = new LogService();
        log.RegisterSecret("super_secret_token_12345");

        var redacted = log.Redact("Connecting with token: super_secret_token_12345");
        Assert.DoesNotContain("super_secret_token_12345", redacted);
        Assert.Contains("[已隐藏]", redacted);
    }

    [Fact]
    public void DanmakuQueue_Filters_And_Queues_Logs()
    {
        var queue = new DanmakuQueue();
        var info = new LauncherLogEvent(DateTimeOffset.Now, LogLevel.Info, "Test Info", false);
        var warn = new LauncherLogEvent(DateTimeOffset.Now, LogLevel.Warning, "Test Warning", true);

        // Off mode should not queue
        queue.Add(info, DanmakuMode.Off);
        Assert.Equal(0, queue.Count);

        // Selected mode queues warnings/errors/featured
        queue.Add(info, DanmakuMode.Selected);
        Assert.Equal(0, queue.Count);

        queue.Add(warn, DanmakuMode.Selected);
        Assert.Equal(1, queue.Count);

        var popped = queue.Take();
        Assert.NotNull(popped);
        Assert.Equal("Test Warning", popped.Message);
        Assert.Equal(0, queue.Count);
    }
}
