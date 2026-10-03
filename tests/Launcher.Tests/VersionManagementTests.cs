using System.IO;
using System.Text.Json;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class VersionManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ikun rename 中文 " + Guid.NewGuid());
    private readonly VersionManagementService _service = new();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string WriteVersion(string id, string? parent = null, string? jar = null)
    {
        var directory = Path.Combine(_root, "versions", id); Directory.CreateDirectory(directory);
        var json = new Dictionary<string, object?>
        {
            ["id"] = id, ["type"] = "release", ["mainClass"] = "net.minecraft.client.main.Main",
            ["time"] = "2020-01-01T00:00:00Z", ["releaseTime"] = "2020-01-01T00:00:00Z",
            ["minecraftArguments"] = "--version ${version_name} --gameDir ${game_directory}",
            ["assets"] = "legacy", ["libraries"] = Array.Empty<object>()
        };
        if (parent is not null) json["inheritsFrom"] = parent;
        if (jar is not null) json["jar"] = jar;
        File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(json));
        return directory;
    }
    [Fact]
    public async Task Rename_Updates_Parents_Jars_Selection_And_Core_Launch_Arguments()
    {
        var directory = WriteVersion("1.12.2");
        await File.WriteAllBytesAsync(Path.Combine(directory, "1.12.2.jar"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(directory, "saves", "世界"));
        await File.WriteAllTextAsync(Path.Combine(directory, "saves", "世界", "level.dat"), "world");
        WriteVersion("我的模组", "1.12.2", "1.12.2");
        var before = VersionService.ReadDetails(_root, "1.12.2");
        Assert.Equal("1.12.2", before.GameName);
        var settings = new LauncherSettings { GameRoot = _root, SelectedVersionId = before.Id };
        settings.JavaOverrides[AppPaths.VersionKey(before)] = "fixture-java";
        settings.IsolationOverrides[AppPaths.VersionKey(before)] = IsolationMode.Isolated;
        var after = await _service.RenameAsync(before, "童年 世界");
        VersionManagementService.MoveSettings(settings, before, after);
        Assert.Equal("童年 世界", after.GameName); Assert.Equal("童年 世界", after.Id);
        Assert.Equal("1.12.2", after.MinecraftVersion); Assert.Equal(8, after.RequiredJava);
        Assert.Equal("童年 世界", settings.SelectedVersionId);
        Assert.Equal("fixture-java", settings.JavaOverrides[AppPaths.VersionKey(after)]);
        Assert.False(settings.JavaOverrides.ContainsKey(AppPaths.VersionKey(before)));
        var renamedDirectory = VersionService.ResolveGameDirectory(after, settings);
        Assert.True(File.Exists(Path.Combine(renamedDirectory, "童年 世界.jar")));
        Assert.True(File.Exists(Path.Combine(renamedDirectory, "saves", "世界", "level.dat")));
        Assert.False(Directory.Exists(directory));
        using var childJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "versions", "我的模组", "我的模组.json")));
        Assert.Equal(after.Id, childJson.RootElement.GetProperty("inheritsFrom").GetString());
        Assert.Equal(after.Id, childJson.RootElement.GetProperty("jar").GetString());
        Assert.Equal(8, VersionService.ReadDetails(_root, "我的模组").RequiredJava);
        var launcher = VersionService.CreateLocalLauncher(_root);
        foreach (var id in new[] { after.Id, "我的模组" })
        {
            var parsed = await launcher.GetVersionAsync(id);
            using var process = launcher.BuildProcess(parsed, new MLaunchOption { JavaPath = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Session = MSession.CreateOfflineSession("fixture"), MaximumRamMb = 1024 });
            var arguments = process.StartInfo.Arguments + string.Join(" ", process.StartInfo.ArgumentList);
            Assert.Contains(Path.Combine(renamedDirectory, "童年 世界.jar"), arguments);
            Assert.DoesNotContain(Path.Combine(directory, "1.12.2.jar"), arguments);
        }
        var scanned = await new VersionService().ScanAsync(_root);
        Assert.Equal(2, scanned.Count); Assert.All(scanned, version => Assert.True(version.IsValid, version.Error));
    }
    [Fact] public async Task Rename_Conflict_And_Cancel_Leave_Original_Files_Untouched()
    {
        var directory = WriteVersion("1.20.1"); WriteVersion("已有游戏");
        var before = VersionService.ReadDetails(_root, "1.20.1");
        var original = File.ReadAllText(Path.Combine(directory, "1.20.1.json"));
        await Assert.ThrowsAsync<IOException>(() => _service.RenameAsync(before, "已有游戏"));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.RenameAsync(before, "新游戏", cts.Token));
        Assert.Equal(original, File.ReadAllText(Path.Combine(directory, "1.20.1.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "新游戏")));
    }
    [Fact] public async Task Locked_Child_Rolls_Back_Rename_Without_Losing_Game_Data()
    {
        var directory = WriteVersion("1.20.1"); var child = WriteVersion("child", "1.20.1");
        await File.WriteAllBytesAsync(Path.Combine(directory, "1.20.1.jar"), [9, 8]);
        var before = VersionService.ReadDetails(_root, "1.20.1");
        using (var locked = File.Open(Path.Combine(child, "child.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => _service.RenameAsync(before, "新游戏"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.True(File.Exists(Path.Combine(directory, "1.20.1.jar")));
        Assert.Equal(17, VersionService.ReadDetails(_root, "child").RequiredJava);
        Assert.False(Directory.Exists(Path.Combine(_root, "versions", "新游戏")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(_root, "versions")), x => Path.GetFileName(x).StartsWith(".ikun-"));
    }
    [Theory] [InlineData("../outside")] [InlineData("CON")] [InlineData("bad:name")] [InlineData(".")]
    public async Task Invalid_Folder_Names_Are_Rejected(string name)
    {
        WriteVersion("1.21");
        await Assert.ThrowsAsync<ArgumentException>(() => _service.RenameAsync(VersionService.ReadDetails(_root, "1.21"), name));
        Assert.True(Directory.Exists(Path.Combine(_root, "versions", "1.21")));
    }
    [Fact] public void Loader_Details_Are_Separated_From_Game_Name()
    {
        WriteVersion("1.20.1"); var folder = WriteVersion("fabric-loader-0.16.10-1.20.1", "1.20.1");
        var path = Path.Combine(folder, Path.GetFileName(folder) + ".json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        node["libraries"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.10" });
        File.WriteAllText(path, node.ToJsonString());
        var version = VersionService.ReadDetails(_root, Path.GetFileName(folder));
        Assert.Equal("fabric-loader-0.16.10-1.20.1", version.GameName); Assert.Equal("Fabric", version.TagLabel);
        Assert.Equal("0.16.10", version.LoaderVersion); Assert.Equal("1.20.1", version.ParentId);
    }
    [Fact] public async Task Game_Name_Cannot_Change_Loader_Type()
    {
        WriteVersion("1.20.1");
        var renamed = await _service.RenameAsync(VersionService.ReadDetails(_root, "1.20.1"), "OptiFine 小世界");
        Assert.Equal("原版", renamed.Loader);
        Assert.Equal("1.20.1", renamed.MinecraftVersion);
    }
    [Theory]
    [InlineData("内战1111")]
    [InlineData("1.20.1-Fabric 0.19.5")]
    [InlineData("1.19-OptiFine_H9")]
    public void Unrenamed_Games_Display_Their_Actual_Instance_Name(string id)
    {
        WriteVersion(id);
        var version = VersionService.ReadDetails(_root, id);
        Assert.Equal(id, version.GameName);
        Assert.DoesNotContain(id, version.TagLabel);
        Assert.Equal("原版", version.TagLabel);
    }
}
