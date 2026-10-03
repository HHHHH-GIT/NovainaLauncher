using System.Text.Json;

namespace Launcher.Core;

// Keep native inheritance on disk, but show installed instances rather than their internal parents.
internal static class VersionVisibility
{
    internal const string InstanceMarker = ".ikun-instance.json";
    internal const string DependencyMarker = ".ikun-dependency";
    internal static HashSet<string> Dependencies(string root)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = Directory.GetDirectories(Path.Combine(root, "versions"));
        foreach (var directory in directories)
        {
            var id = Path.GetFileName(directory);
            if (File.Exists(Path.Combine(directory, DependencyMarker))) hidden.Add(id);
            if (File.Exists(Path.Combine(directory, InstanceMarker)) || VersionManagementService.ReadProfile(root, id) is not null) instances.Add(id);
        }
        // Upgrade installations made before markers were introduced, without deleting any files.
        var records = Path.Combine(AppPaths.Data, "downloads", "installs");
        if (Directory.Exists(records))
            foreach (var record in Directory.EnumerateFiles(records, "*.json"))
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(record));
                    var json = document.RootElement;
                    if (json.TryGetProperty("Root", out var savedRoot) && string.Equals(Path.GetFullPath(savedRoot.GetString()!), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                        && json.TryGetProperty("Name", out var name) && name.GetString() is { } id && Path.GetFileName(id) == id
                        && File.Exists(Path.Combine(root, "versions", id, id + ".json"))) instances.Add(id);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        foreach (var instance in instances)
        {
            string? cursor = instance;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cursor is not null && Path.GetFileName(cursor) == cursor && visited.Add(cursor))
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "versions", cursor, cursor + ".json")));
                    cursor = document.RootElement.TryGetProperty("inheritsFrom", out var parent) ? parent.GetString() : null;
                    if (cursor is not null) hidden.Add(cursor);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { break; }
        }
        hidden.ExceptWith(instances);
        hidden.RemoveWhere(id => directories.Any(directory => Path.GetFileName(directory).Equals(id, StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(Path.Combine(directory, "mods")) || Directory.Exists(Path.Combine(directory, "saves")) || File.Exists(Path.Combine(directory, "options.txt")))));
        return hidden;
    }
}
