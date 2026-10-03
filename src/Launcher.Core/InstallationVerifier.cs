using System.IO.Compression;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Files;

namespace Launcher.Core;

public sealed class InstallationVerifier : IInstallationVerifier
{
    public async Task<InstallationCheck> VerifyAsync(MinecraftLauncher launcher, string version, IProgress<InstallationCheck> progress, CancellationToken token)
    {
        var files = await ExtractManifestAsync(launcher, version, token);
        var failures = new List<InstallationFailure>(); int checkedCount = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var reason = await CheckFileAsync(file, token);
            if (reason is not null) failures.Add(new(file.Path!, reason, file.Url));
            checkedCount++;
            if (checkedCount % 32 == 0 || checkedCount == files.Length) progress.Report(new(checkedCount, files.Length, failures.ToArray()));
        }
        return new(checkedCount, files.Length, failures);
    }
    public static async Task<string?> CheckFileAsync(GameFile file, CancellationToken token)
    {
        if (!File.Exists(file.Path)) return "缺失文件";
        if (new FileInfo(file.Path).Length == 0 || file.Size > 0 && new FileInfo(file.Path).Length != file.Size) return "大小不符";
        if (!string.IsNullOrWhiteSpace(file.Hash)) return await DownloadHash.MatchesAsync(file.Path, file.Hash, null, token) ? null : "SHA-1 不符";
        try
        {
            if (file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(file.Path);
                if (zip.Entries.Count == 0) return "空归档";
                // Verify entry streams as well as the central directory, with cancellation.
                foreach (var entry in zip.Entries.Where(e => e.Length > 0))
                { token.ThrowIfCancellationRequested(); await using var stream = entry.Open(); await stream.CopyToAsync(Stream.Null, token); }
            }
            else if (file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            { await using var stream = File.OpenRead(file.Path); using var _ = await JsonDocument.ParseAsync(stream, cancellationToken: token); }
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return "结构无效：" + ex.Message; }
    }
    public async Task<InstallationCheck> RepairAndVerifyAsync(MinecraftLauncher launcher, string version, IProgress<InstallationCheck> progress, CancellationToken token)
    {
        var first = await VerifyAsync(launcher, version, progress, token);
        if (first.IsValid) return first;
        var manifest = (await ExtractManifestAsync(launcher, version, token)).ToDictionary(f => f.Path!, StringComparer.OrdinalIgnoreCase);
        var repairable = first.Failures.Select(f => manifest.GetValueOrDefault(f.File)).OfType<GameFile>().Where(f => !string.IsNullOrWhiteSpace(f.Url)).ToArray();
        foreach (var file in repairable) { token.ThrowIfCancellationRequested(); if (File.Exists(file.Path)) File.Delete(file.Path); }
        if (repairable.Length > 0) await launcher.GameInstaller.Install(repairable, null, null, token);
        var result = await VerifyAsync(launcher, version, progress, token);
        if (!result.IsValid) throw new InvalidDataException("完整性检查未通过：" + string.Join("；", result.Failures.Take(5).Select(f => Path.GetFileName(f.File) + " · " + f.Reason)));
        return result;
    }
    private static async Task<GameFile[]> ExtractManifestAsync(MinecraftLauncher launcher, string id, CancellationToken token)
    {
        var all = new List<GameFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var version = await launcher.GetVersionAsync(id, token); version is not null; version = version.ParentVersion)
        {
            if (!seen.Add(version.Id)) throw new InvalidDataException("版本继承循环");
            using var profile = JsonDocument.Parse(await File.ReadAllTextAsync(launcher.MinecraftPath.GetVersionJsonPath(version.Id), token));
            if (profile.RootElement.TryGetProperty("assetIndex", out var index))
            {
                var file = new GameFile("资源索引 " + index.GetProperty("id").GetString()) { Path = Path.Combine(launcher.MinecraftPath.Assets, "indexes", index.GetProperty("id").GetString() + ".json"), Url = index.GetProperty("url").GetString(), Hash = index.GetProperty("sha1").GetString(), Size = index.TryGetProperty("size", out var size) ? size.GetInt64() : 0 };
                // An invalid index must be repaired before the core can enumerate its objects.
                if (await CheckFileAsync(file, token) is not null) await launcher.GameInstaller.Install([file], null, null, token);
                if (await CheckFileAsync(file, token) is { } failure) throw new InvalidDataException("资源索引不完整：" + failure);
                all.Add(file);
            }
            all.AddRange(await launcher.ExtractFiles(version, token));
        }
        return all.Where(f => !string.IsNullOrEmpty(f.Path)).Distinct(GameFilePathComparer.Default).ToArray();
    }
}
