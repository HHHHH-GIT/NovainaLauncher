using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Core;

public sealed record GameProfile(string Name, string MinecraftVersion);

/// <summary>Renames an installed instance, including local children that inherit its JSON or client JAR.</summary>
public sealed class VersionManagementService
{
    private const string ProfileFile = ".ikun-profile.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GameProfile? ReadProfile(string root, string id)
    {
        try { return JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(Path.Combine(root, "versions", id, ProfileFile))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public Task DeleteAsync(VersionInfo version, CancellationToken token = default) => Task.Run(() =>
    {
        var parent = Path.GetFullPath(Path.Combine(version.Root, "versions"));
        var source = Path.GetFullPath(Path.Combine(parent, version.Id));
        if (!string.Equals(Path.GetDirectoryName(source), parent, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("无效的游戏目录");
        foreach (var directory in Directory.EnumerateDirectories(parent))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(directory, source, StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.Combine(directory, Path.GetFileName(directory) + ".json");
            if (!File.Exists(file)) continue;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var key in new[] { "inheritsFrom", "jar" })
                    if (json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                        string.Equals(value.GetString(), version.Id, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"“{Path.GetFileName(directory)}”仍依赖此版本");
            }
            catch (JsonException) { }
        }
        token.ThrowIfCancellationRequested();
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(source, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
    }, token);

    public Task<VersionInfo> RenameAsync(VersionInfo version, string name, CancellationToken token = default) => Task.Run(() =>
    {
        name = name.Trim();
        ValidateName(name);
        if (name == version.Id) return version;
        var parent = Path.GetFullPath(Path.Combine(version.Root, "versions"));
        var source = Path.Combine(parent, version.Id);
        var target = Path.Combine(parent, name);
        if (Path.GetDirectoryName(Path.GetFullPath(source)) != parent || Path.GetDirectoryName(Path.GetFullPath(target)) != parent)
            throw new InvalidOperationException("无效的游戏目录");
        if (Directory.Exists(target) && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("已有同名游戏");
        if (File.Exists(target)) throw new IOException("已有同名文件");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("链接目录无法改名");
        token.ThrowIfCancellationRequested();
        var originalJson = Path.Combine(source, version.Id + ".json");
        var originalText = File.ReadAllText(originalJson);
        var own = JsonNode.Parse(originalText)?.AsObject() ?? throw new InvalidDataException("版本文件损坏");
        own["id"] = name;
        if (own["jar"]?.GetValue<string>() is { } ownJar && ownJar.Equals(version.Id, StringComparison.OrdinalIgnoreCase)) own["jar"] = name;
        var jarPath = Path.Combine(source, version.Id + ".jar");
        var hasJar = File.Exists(jarPath);
        if (!string.Equals(name, version.Id, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(Path.Combine(source, name + ".json")) || hasJar && File.Exists(Path.Combine(source, name + ".jar"))))
            throw new IOException("目标文件名已存在");

        var updates = new List<(string Path, string Original, string Updated)>();
        foreach (var directory in Directory.EnumerateDirectories(parent))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(directory, source, StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.Combine(directory, Path.GetFileName(directory) + ".json");
            if (!File.Exists(file)) continue;
            var text = File.ReadAllText(file);
            JsonObject? json;
            try { json = JsonNode.Parse(text)?.AsObject(); } catch (JsonException) { continue; }
            if (json is null) continue;
            var changed = false;
            foreach (var property in new[] { "inheritsFrom", "jar" })
                if (json[property] is JsonValue value && value.TryGetValue<string>(out var reference) && string.Equals(reference, version.Id, StringComparison.OrdinalIgnoreCase))
                { json[property] = name; changed = true; }
            if (changed) updates.Add((file, text, json.ToJsonString(JsonOptions)));
        }
        var details = VersionService.ReadDetails(version.Root, version.Id);
        var profilePath = Path.Combine(source, ProfileFile);
        var originalProfile = File.Exists(profilePath) ? File.ReadAllText(profilePath) : null;
        var profile = JsonSerializer.Serialize(new GameProfile(name, details.MinecraftVersion), JsonOptions);
        var staging = Path.Combine(parent, ".ikun-rename-" + Guid.NewGuid().ToString("N"));
        var location = source;
        var jsonRenamed = false;
        var jarRenamed = false;
        var applied = new List<(string Path, string Original)>();
        token.ThrowIfCancellationRequested();
        // The short commit is indivisible to cancellation; any file error rolls it back.
        try
        {
            Directory.Move(source, staging); location = staging;
            File.Move(Path.Combine(location, version.Id + ".json"), Path.Combine(location, name + ".json")); jsonRenamed = true;
            if (hasJar) { File.Move(Path.Combine(location, version.Id + ".jar"), Path.Combine(location, name + ".jar")); jarRenamed = true; }
            WriteAtomic(Path.Combine(location, name + ".json"), own.ToJsonString(JsonOptions));
            WriteAtomic(Path.Combine(location, ProfileFile), profile);
            Directory.Move(staging, target); location = target;
            foreach (var update in updates)
            {
                WriteAtomic(update.Path, update.Updated);
                applied.Add((update.Path, update.Original));
            }
            return VersionService.ReadDetails(version.Root, name);
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var update in applied.AsEnumerable().Reverse()) WriteAtomic(update.Path, update.Original);
                if (jsonRenamed)
                {
                    File.Move(Path.Combine(location, name + ".json"), Path.Combine(location, version.Id + ".json"));
                    WriteAtomic(Path.Combine(location, version.Id + ".json"), originalText);
                }
                if (jarRenamed) File.Move(Path.Combine(location, name + ".jar"), Path.Combine(location, version.Id + ".jar"));
                var restoredProfile = Path.Combine(location, ProfileFile);
                if (originalProfile is null) File.Delete(restoredProfile); else WriteAtomic(restoredProfile, originalProfile);
                if (location != source) Directory.Move(location, source);
            }
            catch (Exception rollback) { throw new IOException($"改名失败，恢复目录失败：{location}", new AggregateException(failure, rollback)); }
            throw;
        }
    }, token);

    public static void MoveSettings(LauncherSettings settings, VersionInfo before, VersionInfo after)
    {
        var oldKey = AppPaths.VersionKey(before); var newKey = AppPaths.VersionKey(after);
        if (settings.JavaOverrides.Remove(oldKey, out var java)) settings.JavaOverrides[newKey] = java;
        if (settings.IsolationOverrides.Remove(oldKey, out var isolation)) settings.IsolationOverrides[newKey] = isolation;
        if (settings.SelectedVersionId == before.Id && string.Equals(Path.GetFullPath(settings.GameRoot), Path.GetFullPath(before.Root), StringComparison.OrdinalIgnoreCase))
            settings.SelectedVersionId = after.Id;
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ') || name is "." or ".." || name.StartsWith(".ikun-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("游戏名称包含无效字符");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
            throw new ArgumentException("此名称无法作为文件夹名称");
    }

    private static void WriteAtomic(string path, string text)
    {
        var temporary = path + ".ikun-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
