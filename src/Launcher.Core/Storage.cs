using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XboxAuthNet.Game.Accounts.JsonStorage;

namespace Launcher.Core;

public static class AppPaths
{
    public static string Data { get; private set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
    public static string Runtime => System.IO.Path.Combine(AppContext.BaseDirectory, "runtime");
    public static string LegacyData => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iKunLauncherNext", "data");
    public static string LegacyRuntime => System.IO.Path.Combine(LegacyData, "runtime");
    public static void ConfigureData(string directory) => Data = System.IO.Path.GetFullPath(directory);
    public static string DefaultGameRoot
    {
        get
        {
            // Source runs resolve the repository root; published builds use their own directory.
            var cursor = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; cursor is not null && i < 6; i++, cursor = cursor.Parent)
                if (File.Exists(System.IO.Path.Combine(cursor.FullName, "iKunLauncherNext.slnx")))
                    return System.IO.Path.Combine(cursor.FullName, ".minecraft");
            return System.IO.Path.Combine(AppContext.BaseDirectory, ".minecraft");
        }
    }
    public static string NormalizeRoot(string path)
    {
        path = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        return System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)).Equals(".minecraft", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(System.IO.Path.Combine(path, "versions")) ? path : System.IO.Path.Combine(path, ".minecraft");
    }
    public static string VersionKey(VersionInfo version) => version.Root + "|" + version.Id;
}

public sealed class SettingsStore
{
    private readonly string _path;
    public SettingsStore(string? directory = null) => _path = System.IO.Path.Combine(directory ?? AppPaths.Data, "settings.json");
    public LauncherSettings Load()
    {
        try { return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_path + ".tmp", _path, true);
    }
}

public sealed class SecretStore
{
    private readonly string _directory;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("iKunLauncherNext.v1");
    public SecretStore(string? directory = null) => _directory = directory ?? AppPaths.Data;
    private string FilePath(string name) => System.IO.Path.Combine(_directory, name + ".protected");
    public byte[]? Read(string name)
    {
        var path = FilePath(name);
        if (!File.Exists(path)) return null;
        return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
    }
    public void Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        var path = FilePath(name);
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true);
    }
    public T? ReadJson<T>(string name) => Read(name) is { } bytes ? JsonSerializer.Deserialize<T>(bytes) : default;
    public void WriteJson<T>(string name, T value) => Write(name, JsonSerializer.SerializeToUtf8Bytes(value));
    public void Delete(string name) => File.Delete(FilePath(name));
}

internal sealed class ProtectedJsonStorage(SecretStore store, string name) : IJsonStorage
{
    public JsonNode? ReadAsJsonNode() => store.Read(name) is { } bytes ? JsonNode.Parse(bytes) : null;
    public void Write(JsonNode node, JsonSerializerOptions? serializerOptions) => store.Write(name, Encoding.UTF8.GetBytes(node.ToJsonString(serializerOptions)));
}
