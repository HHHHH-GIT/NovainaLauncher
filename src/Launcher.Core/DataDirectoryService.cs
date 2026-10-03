using System.Text.Json;

namespace Launcher.Core;

// The locator is read before any service opens files. A running process keeps its original path.
public sealed class DataDirectoryService
{
    private readonly string _applicationDirectory;
    private readonly string _legacyDirectory;
    private string Locator => Path.Combine(_applicationDirectory, "launcher-paths.json");
    public string DefaultDirectory => Path.Combine(_applicationDirectory, "data");
    public DataDirectoryService(string? applicationDirectory = null, string? legacyDirectory = null)
    {
        _applicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        _legacyDirectory = Path.GetFullPath(legacyDirectory ?? AppPaths.LegacyData);
    }
    public async Task<string> InitializeAsync(CancellationToken token = default)
    {
        string directory = DefaultDirectory;
        if (File.Exists(Locator))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Locator, token));
            var value = json.RootElement.GetProperty("DataDirectory").GetString();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("数据目录配置无效");
            directory = Path.GetFullPath(value, _applicationDirectory);
        }
        else if (!HasFiles(directory) && HasFiles(_legacyDirectory) && !Same(directory, _legacyDirectory))
        {
            var settings = new SettingsStore(_legacyDirectory).Load();
            var legacyRuntime = Path.Combine(_legacyDirectory, "runtime");
            if (Directory.Exists(legacyRuntime) && (string.IsNullOrWhiteSpace(settings.JavaDownloadDirectory) || Same(settings.JavaDownloadDirectory, AppPaths.Runtime)))
                settings.JavaDownloadDirectory = legacyRuntime;
            await CopyDataAsync(_legacyDirectory, directory, settings, token);
            new SettingsStore(directory).Save(settings);
        }
        Directory.CreateDirectory(directory);
        // Verify both locations now, rather than failing later when the first account is saved.
        var probe = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(probe, "", token); File.Delete(probe);
        WriteLocation(directory);
        return directory;
    }
    public void WriteLocation(string directory)
    {
        var value = Same(directory, DefaultDirectory) ? "data" : Path.GetFullPath(directory);
        var temporary = Locator + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { DataDirectory = value }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Locator, true);
    }
    public async Task MigrateAsync(string source, string destination, LauncherSettings settings, CancellationToken token = default)
    {
        await CopyDataAsync(source, destination, settings, token);
        // Commit the locator last; any earlier error leaves the running location untouched.
        WriteLocation(destination);
    }
    public static async Task CopyDataAsync(string source, string destination, LauncherSettings settings, CancellationToken token = default)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (Same(source, destination) || Within(destination, source) || Within(source, destination))
            throw new InvalidOperationException("请选择独立于当前数据目录的位置");
        if (File.Exists(destination) || HasFiles(destination)) throw new InvalidOperationException("请选择空目录");
        var exclusions = new[] { Path.Combine(source, "runtime"), AppPaths.Runtime, settings.JavaDownloadDirectory, settings.GameRoot }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(Path.GetFullPath).ToArray();
        if (exclusions.Any(x => Same(destination, x) || Within(destination, x))) throw new InvalidOperationException("数据目录不能位于 Java 或游戏目录中");
        var staging = destination.TrimEnd(Path.DirectorySeparatorChar) + ".migrating-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            await CopyFolder(source, staging);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination)) Directory.Delete(destination); // Only an empty target is accepted.
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        async Task CopyFolder(string from, string to)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                token.ThrowIfCancellationRequested();
                if (exclusions.Any(x => Same(entry, x) || Within(entry, x))) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var target = Path.Combine(to, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) { Directory.CreateDirectory(target); await CopyFolder(entry, target); }
                else
                {
                    await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    await input.CopyToAsync(output, token);
                }
            }
        }
    }
    public static LauncherSettings ReadImport(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("请选择有效的 settings.json");
        var settings = json.RootElement.Deserialize<LauncherSettings>() ?? throw new InvalidDataException("设置文件无效");
        if (!Enum.IsDefined(settings.UiAnimationMode) || !Enum.IsDefined(settings.Danmaku) || !Enum.IsDefined(settings.DanmakuStyle)
            || !Enum.IsDefined(settings.MemoryAllocationMode) || settings.WindowTransparencyPercent is < 0 or > 50 || settings.MemoryMb < 0
            || settings.Theme is not ("System" or "Light" or "Dark")) throw new InvalidDataException("设置文件包含无效选项");
        if (settings.JavaOverrides is null || settings.IsolationOverrides is null || settings.DanmakuBlockRules is null)
            throw new InvalidDataException("设置文件内容不完整");
        foreach (var rule in settings.DanmakuBlockRules)
            if (rule is null || !Enum.IsDefined(rule.Kind) || rule.Pattern is null || rule.Enabled && DanmakuFilter.Validate(rule.Kind, rule.Pattern) is not null)
                throw new InvalidDataException("弹幕屏蔽规则无效");
        return settings;
    }
    public static string ImportSettings(string dataDirectory, LauncherSettings settings)
    {
        var path = Path.Combine(dataDirectory, "settings.json");
        var backup = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".bak";
        if (File.Exists(path)) File.Copy(path, backup);
        new SettingsStore(dataDirectory).Save(settings);
        return backup;
    }
    private static bool HasFiles(string path) => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string candidate, string parent) => Path.GetFullPath(candidate).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
