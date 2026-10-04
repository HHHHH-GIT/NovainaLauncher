using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class ContentAndMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ikun 内容 " + Guid.NewGuid());
    private readonly VersionContentService _service = new();
    public ContentAndMemoryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Theory]
    [InlineData(8192, 0, 4096)] [InlineData(8192, 20, 5632)] [InlineData(8192, 400, 5632)] [InlineData(200, 0, 0)] [InlineData(256, 0, 256)]
    public void Memory_Leaves_Headroom_And_Caps_Mod_Adjustment(int available, int mods, int expected)
    {
        Assert.Equal(expected, MemoryService.Recommend(available, mods));
        Assert.InRange(MemoryService.Recommend(available, mods), 0, available);
    }
    [Fact] public async Task Browsing_Empty_Content_Does_Not_Change_Isolation_Or_Create_Directories()
    {
        var versionDirectory = Path.Combine(_root, "versions", "fabric"); Directory.CreateDirectory(versionDirectory);
        var version = new VersionInfo("fabric", _root, "Fabric", 21);
        var settings = new LauncherSettings();
        Assert.Equal(_root, VersionService.ResolveGameDirectory(version, settings));
        Assert.Empty(await _service.ScanAsync(_root, VersionContentKind.Mod));
        Assert.False(Directory.Exists(Path.Combine(_root, "mods")));
        settings.IsolationOverrides[AppPaths.VersionKey(version)] = IsolationMode.Isolated;
        Assert.Equal(versionDirectory, VersionService.ResolveGameDirectory(version, settings));
        settings.IsolationOverrides[AppPaths.VersionKey(version)] = IsolationMode.Shared;
        Directory.CreateDirectory(Path.Combine(versionDirectory, "mods"));
        Assert.Equal(_root, VersionService.ResolveGameDirectory(version, settings));
    }
    [Fact] public async Task Mod_Import_Toggle_Count_And_Conflict_Are_Consistent()
    {
        var source = Path.Combine(_root, "中文 Mod.jar"); await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await _service.ImportAsync(_root, VersionContentKind.Mod, source);
        var item = Assert.Single(await _service.ScanAsync(_root, VersionContentKind.Mod));
        Assert.True(item.Enabled); Assert.Equal(1, MemoryService.CountEnabledMods(_root));
        await _service.ToggleAsync(_root, item);
        item = Assert.Single(await _service.ScanAsync(_root, VersionContentKind.Mod));
        Assert.False(item.Enabled); Assert.Equal("中文 Mod.jar", item.Name); Assert.Equal(0, MemoryService.CountEnabledMods(_root));
        await Assert.ThrowsAsync<IOException>(() => _service.ImportAsync(_root, VersionContentKind.Mod, source));
        await _service.ToggleAsync(_root, item);
        Assert.Equal(1, MemoryService.CountEnabledMods(_root));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(_root, "mods", "中文 Mod.jar")));
    }
    [Theory] [InlineData(VersionContentKind.ResourcePack)] [InlineData(VersionContentKind.ShaderPack)]
    public async Task Resource_Disable_Moves_Out_Of_Game_Search_Path(VersionContentKind kind)
    {
        var source = Path.Combine(_root, "pack.zip"); await File.WriteAllBytesAsync(source, [3, 2, 1]);
        await _service.ImportAsync(_root, kind, source);
        var item = Assert.Single(await _service.ScanAsync(_root, kind));
        await _service.ToggleAsync(_root, item);
        var disabled = Assert.Single(await _service.ScanAsync(_root, kind));
        Assert.False(disabled.Enabled); Assert.Contains(".disabled", disabled.Path);
        Assert.False(File.Exists(item.Path));
        await _service.ToggleAsync(_root, disabled); Assert.True(File.Exists(item.Path));
    }
    [Fact] public async Task Save_Export_And_Import_Roundtrip_Preserves_Nested_Files()
    {
        var world = Path.Combine(_root, "source", "世界 一"); Directory.CreateDirectory(Path.Combine(world, "region"));
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "local fixture");
        await File.WriteAllBytesAsync(Path.Combine(world, "region", "r.0.0.mca"), [0, 7, 255]);
        await _service.ImportAsync(_root, VersionContentKind.Save, world);
        var item = Assert.Single(await _service.ScanAsync(_root, VersionContentKind.Save));
        var archive = Path.Combine(_root, "世界.zip"); await _service.ExportSaveAsync(_root, item, archive);
        var otherRoot = Path.Combine(_root, "other");
        await _service.ImportAsync(otherRoot, VersionContentKind.Save, archive);
        var other = Assert.Single(await _service.ScanAsync(otherRoot, VersionContentKind.Save));
        Assert.Equal(item.Name, other.Name);
        Assert.Equal(new byte[] { 0, 7, 255 }, await File.ReadAllBytesAsync(Path.Combine(other.Path, "region", "r.0.0.mca")));
    }
    [Fact] public async Task Unsafe_Zip_And_Cancellation_Do_Not_Commit_Content()
    {
        var archive = Path.Combine(_root, "unsafe.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("escape"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportAsync(_root, VersionContentKind.Save, archive));
        Assert.False(File.Exists(Path.Combine(_root, "saves", "escape.txt")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "saves")));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ImportAsync(_root, VersionContentKind.Save, archive, cts.Token));
    }
    [Fact] public async Task File_Operations_Reject_Items_Outside_Managed_Directory()
    {
        var outside = Path.Combine(_root, "outside.jar"); await File.WriteAllTextAsync(outside, "fixture");
        var item = new VersionContentItem(outside, "outside.jar", VersionContentKind.Mod, true, false, 7, DateTime.Now);
        await Assert.ThrowsAsync<IOException>(() => _service.ToggleAsync(_root, item));
        await Assert.ThrowsAsync<IOException>(() => _service.DeleteAsync(_root, item));
        Assert.True(File.Exists(outside));
    }
    [Fact] public async Task Delete_Uses_Windows_Recycle_Bin_For_A_Local_Fixture()
    {
        var mods = Path.Combine(_root, "mods"); Directory.CreateDirectory(mods);
        var file = Path.Combine(mods, "recycle-fixture-" + Guid.NewGuid() + ".jar");
        await File.WriteAllTextAsync(file, "temporary test fixture");
        var item = Assert.Single(await _service.ScanAsync(_root, VersionContentKind.Mod));
        await _service.DeleteAsync(_root, item);
        Assert.False(File.Exists(file)); Assert.Empty(await _service.ScanAsync(_root, VersionContentKind.Mod));
    }
    [Fact] public void New_Settings_Defaults_And_Persistence()
    {
        var store = new SettingsStore(_root); var settings = store.Load();
        Assert.Equal(MemoryAllocationMode.Smart, settings.MemoryAllocationMode); Assert.Equal(0, settings.WindowTransparencyPercent);
        Assert.True(settings.AutoPrepareJava);
        settings.AutoPrepareJava = false;
        settings.MemoryAllocationMode = MemoryAllocationMode.Manual; settings.WindowTransparencyPercent = 35; settings.JavaDownloadDirectory = Path.Combine(_root, "Java 自定义");
        store.Save(settings); var restored = store.Load();
        Assert.Equal(MemoryAllocationMode.Manual, restored.MemoryAllocationMode); Assert.Equal(35, restored.WindowTransparencyPercent); Assert.Equal(settings.JavaDownloadDirectory, restored.JavaDownloadDirectory);
        Assert.False(restored.AutoPrepareJava);
    }
    [Fact] public async Task Custom_Java_Directory_Cleans_Failed_Download_And_Preserves_Installed_Files()
    {
        var custom = Path.Combine(_root, "Java 下载"); Directory.CreateDirectory(custom);
        var marker = Path.Combine(custom, "existing.txt"); await File.WriteAllTextAsync(marker, "keep");
        using var http = new HttpClient(new JavaFixture()); using var log = new LogService(Path.Combine(_root, "logs"));
        var java = new JavaService(http, log);
        await Assert.ThrowsAsync<InvalidDataException>(() => java.DownloadAsync(21, new InlineProgress(), default, custom));
        Assert.True(File.Exists(marker)); Assert.Single(Directory.EnumerateFileSystemEntries(custom));
    }
    private sealed class InlineProgress : IProgress<JavaDownloadProgress> { public void Report(JavaDownloadProgress value) { } }
    private sealed class JavaFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri!.Host == "api.adoptium.net"
                ? new StringContent("[{\"binary\":{\"package\":{\"checksum\":\"" + new string('0', 64) + "\",\"link\":\"https://fixture.test/java.zip\"}}}]", Encoding.UTF8, "application/json")
                : (HttpContent)new ByteArrayContent([1, 2, 3]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
