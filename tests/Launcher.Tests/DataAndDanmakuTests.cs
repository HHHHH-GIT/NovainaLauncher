using System.Text.Json;
using System.IO;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class DataAndDanmakuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ikun-data-tests-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }

    [Fact] public async Task Upgrade_Preserves_Secrets_And_Leaves_Java_In_Legacy_Directory()
    {
        var app = Folder("app"); var legacy = Folder("legacy");
        new SettingsStore(legacy).Save(new() { Theme = "Dark" });
        new SecretStore(legacy).WriteJson("accounts", new[] { new AccountProfile { Name = "fixture" } });
        Directory.CreateDirectory(Path.Combine(legacy, "runtime")); File.WriteAllText(Path.Combine(legacy, "runtime", "java.exe"), "fixture");
        Directory.CreateDirectory(Path.Combine(legacy, "skins")); File.WriteAllText(Path.Combine(legacy, "skins", "fixture.png"), "skin");
        var service = new DataDirectoryService(app, legacy);
        var data = await service.InitializeAsync();
        Assert.Equal(Path.Combine(app, "data"), data);
        Assert.Equal("fixture", new SecretStore(data).ReadJson<AccountProfile[]>("accounts")![0].Name);
        Assert.True(File.Exists(Path.Combine(data, "skins", "fixture.png")));
        Assert.False(Directory.Exists(Path.Combine(data, "runtime")));
        Assert.True(File.Exists(Path.Combine(legacy, "runtime", "java.exe")));
        Assert.Equal(Path.Combine(legacy, "runtime"), new SettingsStore(data).Load().JavaDownloadDirectory);
        using var locator = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "launcher-paths.json")));
        Assert.Equal("data", locator.RootElement.GetProperty("DataDirectory").GetString());
        Assert.Equal(data, await service.InitializeAsync());
    }

    [Fact] public async Task Migration_Copies_Data_But_Excludes_Java_And_Game_Files()
    {
        var app = Folder("app"); var source = Folder("source"); var destination = Folder("destination");
        var java = Path.Combine(source, "custom-java"); var game = Path.Combine(source, ".minecraft");
        Directory.CreateDirectory(java); Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(java, "runtime.zip"), "java"); File.WriteAllText(Path.Combine(game, "game.jar"), "game");
        File.WriteAllText(Path.Combine(source, "launcher.log"), "log");
        var settings = new LauncherSettings { JavaDownloadDirectory = java, GameRoot = game };
        new SettingsStore(source).Save(settings);
        var service = new DataDirectoryService(app, Folder("empty-legacy")); service.WriteLocation(source);
        await service.MigrateAsync(source, destination, settings);
        Assert.True(File.Exists(Path.Combine(destination, "settings.json")));
        Assert.Equal("log", File.ReadAllText(Path.Combine(destination, "launcher.log")));
        Assert.False(Directory.Exists(Path.Combine(destination, "custom-java"))); Assert.False(Directory.Exists(Path.Combine(destination, ".minecraft")));
        Assert.True(File.Exists(Path.Combine(java, "runtime.zip"))); Assert.True(File.Exists(Path.Combine(game, "game.jar")));
        Assert.Equal(destination, await service.InitializeAsync());
    }

    [Fact] public async Task Failed_Or_Cancelled_Migration_Keeps_Original_Locator()
    {
        var app = Folder("app"); var source = Folder("source"); var target = Folder("target");
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(target, "existing.txt"), "keep");
        var service = new DataDirectoryService(app, Folder("legacy")); service.WriteLocation(source);
        var locator = File.ReadAllText(Path.Combine(app, "launcher-paths.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MigrateAsync(source, target, new()));
        Assert.Equal(locator, File.ReadAllText(Path.Combine(app, "launcher-paths.json")));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var cancelled = Path.Combine(_root, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MigrateAsync(source, cancelled, new(), cts.Token));
        Assert.False(Directory.Exists(cancelled)); Assert.Empty(Directory.EnumerateDirectories(_root, "*.migrating-*"));
        Assert.Equal(locator, File.ReadAllText(Path.Combine(app, "launcher-paths.json")));
    }

    [Fact] public void Import_Uses_Defaults_Backs_Up_Settings_And_Preserves_Accounts()
    {
        var data = Folder("data"); var import = Path.Combine(_root, "import.json");
        new SettingsStore(data).Save(new() { Theme = "Light" }); new SecretStore(data).WriteJson("accounts", "keep");
        File.WriteAllText(import, "{\"Theme\":\"Dark\"}");
        var settings = DataDirectoryService.ReadImport(import);
        Assert.Equal(DanmakuStyle.Glass, settings.DanmakuStyle); Assert.Empty(settings.DanmakuBlockRules);
        var backup = DataDirectoryService.ImportSettings(data, settings);
        Assert.Equal("Dark", new SettingsStore(data).Load().Theme);
        Assert.Contains("Light", File.ReadAllText(backup)); Assert.Equal("keep", new SecretStore(data).ReadJson<string>("accounts"));
        File.WriteAllText(import, "{\"WindowTransparencyPercent\":101}");
        Assert.Throws<InvalidDataException>(() => DataDirectoryService.ReadImport(import));
        Assert.Equal("Dark", new SettingsStore(data).Load().Theme);
    }

    [Theory]
    [InlineData(DanmakuBlockedPresets.Log4j, LogLevel.Info, "[LOG4J] hello")]
    [InlineData(DanmakuBlockedPresets.AuthlibInjector, LogLevel.Info, "[authlib-injector] hello")]
    [InlineData(DanmakuBlockedPresets.Warn, LogLevel.Warning, "hello")]
    [InlineData(DanmakuBlockedPresets.Error, LogLevel.Error, "hello")]
    public void Preset_Filters_Are_Opt_In(DanmakuBlockedPresets preset, LogLevel level, string message)
    {
        var entry = new LauncherLogEvent(DateTimeOffset.Now, level, message);
        Assert.False(new DanmakuFilter(DanmakuBlockedPresets.None, []).IsBlocked(entry));
        Assert.True(new DanmakuFilter(preset, []).IsBlocked(entry));
        Assert.False(new DanmakuFilter(preset, []).IsBlocked(new(DateTimeOffset.Now, LogLevel.Info, "normal message")));
    }

    [Fact] public void Exact_Matches_Whole_Message_And_Regex_Is_Validated_And_Bounded()
    {
        var filter = new DanmakuFilter(DanmakuBlockedPresets.None, [new(true, DanmakuMatchKind.Exact, "ready"), new(true, DanmakuMatchKind.Regex, "^download.*done$"), new(false, DanmakuMatchKind.Exact, "visible")]);
        bool Blocked(string text) => filter.IsBlocked(new(DateTimeOffset.Now, LogLevel.Info, text));
        Assert.True(Blocked("READY")); Assert.False(Blocked("already ready")); Assert.True(Blocked("Download 1 done")); Assert.False(Blocked("visible"));
        Assert.NotNull(DanmakuFilter.Validate(DanmakuMatchKind.Regex, "["));
        var costly = new DanmakuFilter(DanmakuBlockedPresets.None, [new(true, DanmakuMatchKind.Regex, "^(a+)+$")]);
        var message = new LauncherLogEvent(DateTimeOffset.Now, LogLevel.Info, new string('a', 10000) + "!");
        Assert.False(costly.IsBlocked(message)); Assert.False(costly.IsBlocked(message));
    }

    [Fact] public async Task Filtered_Danmaku_Does_Not_Remove_Log_And_Log_Can_Resume()
    {
        using var log = new LogService(Folder("logs")); var queue = new DanmakuQueue();
        var filter = new DanmakuFilter(DanmakuBlockedPresets.Log4j, []);
        log.Emitted += entry => { if (!filter.IsBlocked(entry)) queue.Add(entry, DanmakuMode.All); };
        log.Write("log4j hidden from danmaku"); log.Write("visible");
        await log.PauseFileAsync(); Assert.Equal(1, queue.Count);
        Assert.Contains("log4j hidden from danmaku", File.ReadAllText(log.FilePath));
        log.ResumeFile(); log.Write("after resume"); await log.PauseFileAsync();
        Assert.Contains("after resume", File.ReadAllText(log.FilePath));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
