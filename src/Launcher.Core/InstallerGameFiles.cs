using System.Collections.Concurrent;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;

namespace Launcher.Core;

/// <summary>The native checker / update-task pipeline stays in CmlLib; only byte transport is adapted.</summary>
public sealed class InstallerGameFiles : ParallelGameInstaller
{
    private readonly DownloadSources _sources;
    private readonly string _staging, _existing;
    private readonly string? _cache;
    private readonly IProgress<FileDownloadProgress> _progress;
    private readonly ConcurrentDictionary<string, FileDownloadProgress> _transfers = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _pendingDownloads = new();
    private long _total, _completed, _last;
    public InstallerGameFiles(DownloadSources sources, string stagingRoot, string existingRoot, IProgress<FileDownloadProgress> progress, string? readOnlyCache = null) : base(4, 4, 16, sources.Http)
    { _sources = sources; _staging = stagingRoot; _existing = existingRoot; _progress = progress; _cache = readOnlyCache; CheckFileChecksum = true; CheckFileSize = true; }
    protected override async ValueTask Install(IEnumerable<GameFile> files, CancellationToken token)
    {
        var list = files.Distinct(GameFilePathComparer.Default).Select(f => f with { Hash = f.Hash?.ToLowerInvariant() }).ToArray();
        _total = list.Sum(f => Math.Max(0, f.Size)); _completed = 0; _transfers.Clear();
        foreach (var f in list)
            if (File.Exists(f.Path) && (f.Size <= 0 || new FileInfo(f.Path).Length == f.Size) && await DownloadHash.MatchesAsync(f.Path, f.Hash, null, token)) { _completed += Math.Max(0, f.Size); CaptureProcessorOutputs(f.Path!); }
        try { await base.Install(list, token); }
        finally
        {
            // The core can stop its pipeline before cancelled transport workers finish closing files.
            // Drain those workers before a caller removes the task's staging directory.
            while (!_pendingDownloads.IsEmpty) await Task.WhenAll(_pendingDownloads.Values.Select(value => value.Task));
        }
        Report(true);
    }
    protected override async Task Download(GameFile file, IProgress<ByteProgress>? byteProgress, CancellationToken token)
    {
        var id = Guid.NewGuid(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDownloads[id] = done;
        try { token.ThrowIfCancellationRequested(); await DownloadCoreAsync(file, byteProgress, token); }
        finally { _pendingDownloads.TryRemove(id, out _); done.TrySetResult(); }
    }
    private async Task DownloadCoreAsync(GameFile file, IProgress<ByteProgress>? byteProgress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Url)) throw new InvalidDataException("安装文件缺少路径或来源：" + file.Name);
        var relative = Path.GetRelativePath(_staging, file.Path);
        foreach (var root in new[] { _existing, Path.Combine(_existing, ".ikun-downloads", "cache"), _cache }.OfType<string>())
        {
            var cached = Path.Combine(root, relative);
            if (!relative.StartsWith("..") && !string.IsNullOrWhiteSpace(file.Hash) && File.Exists(cached) && (file.Size <= 0 || new FileInfo(cached).Length == file.Size) && await DownloadHash.MatchesAsync(cached, file.Hash, null, token))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!); File.Copy(cached, file.Path, true);
                Interlocked.Add(ref _completed, Math.Max(0, file.Size)); CaptureProcessorOutputs(file.Path); Report(); return;
            }
        }
        var relay = new InlineProgress<FileDownloadProgress>(p => { _transfers[file.Path] = p; byteProgress?.Report(new(p.TotalBytes ?? 0, p.DownloadedBytes)); Report(); });
        try
        {
            var cache = relative.StartsWith("..") ? null : Path.Combine(_existing, ".ikun-downloads", "cache", relative);
            await new SegmentedDownloader(_sources.Http).DownloadAsync(new(file.Url, file.Path, file.Size > 0 ? file.Size : null, file.Hash, CachePath: cache), relay, token);
            CaptureProcessorOutputs(file.Path);
            Interlocked.Add(ref _completed, Math.Max(0, file.Size));
        }
        catch (Exception error) when (!token.IsCancellationRequested) { throw new IOException($"{file.Name} · 下载 / 校验 · {_sources.ActualSource} · {error.Message}", error); }
        finally { _transfers.TryRemove(file.Path, out _); Report(); }
    }
    private void Report(bool final = false)
    {
        var now = Environment.TickCount64;
        if (!final && now - Interlocked.Read(ref _last) < 100) return;
        Interlocked.Exchange(ref _last, now);
        var current = _transfers.Values.ToArray();
        _progress.Report(new(Math.Min(_total, Interlocked.Read(ref _completed) + current.Sum(p => p.DownloadedBytes)), _total > 0 ? _total : null, current.Sum(p => p.BytesPerSecond), current.SelectMany(p => p.Connections).Where(c => c.State != "完成").Take(8).ToArray()));
    }
    private readonly ConcurrentDictionary<string, string> _processorOutputs = new(StringComparer.OrdinalIgnoreCase);
    private void CaptureProcessorOutputs(string path)
    {
        if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return;
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var entry = archive.GetEntry("install_profile.json"); if (entry is null) return;
        using var stream = entry.Open(); using var doc = System.Text.Json.JsonDocument.Parse(stream);
        var profile = doc.RootElement;
        if (!profile.TryGetProperty("processors", out var processors)) return;
        var mapping = new Dictionary<string, string?> { ["SIDE"] = "client" };
        if (profile.TryGetProperty("data", out var data))
            foreach (var item in data.EnumerateObject())
                if (item.Value.TryGetProperty("client", out var client)) mapping[item.Name] = CmlLib.Core.Installer.Forge.ForgeMapper.ToFullPath(client.ToString(), Path.Combine(_staging, "libraries"), Path.DirectorySeparatorChar).Trim('\'');
        foreach (var processor in processors.EnumerateArray())
        {
            if (processor.TryGetProperty("sides", out var sides) && !sides.EnumerateArray().Any(x => x.GetString() == "client")) continue;
            if (!processor.TryGetProperty("outputs", out var outputs)) continue;
            foreach (var item in outputs.EnumerateObject())
            {
                var file = CmlLib.Core.Installer.Forge.ForgeMapper.Interpolation(item.Name, mapping, false);
                var hash = CmlLib.Core.Installer.Forge.ForgeMapper.Interpolation(item.Value.ToString(), mapping, false).Trim('\'');
                if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("无法解析处理器输出校验值");
                file = CmlLib.Core.Installer.Forge.ForgeMapper.ToFullPath(file, Path.Combine(_staging, "libraries"), Path.DirectorySeparatorChar).Trim('\'');
                if (file.Contains('{')) throw new InvalidDataException("无法解析处理器输出路径");
                _processorOutputs[file] = hash;
            }
        }
    }
    public async Task VerifyProcessorOutputsAsync(CancellationToken token)
    {
        foreach (var output in _processorOutputs)
            if (!File.Exists(output.Key) || !await DownloadHash.MatchesAsync(output.Key, output.Value, null, token))
            {
                var relative = Path.GetRelativePath(_staging, output.Key);
                bool repaired = false;
                foreach (var root in new[] { _existing, Path.Combine(_existing, ".ikun-downloads", "cache"), _cache }.OfType<string>())
                {
                    var cached = Path.Combine(root, relative);
                    if (!relative.StartsWith("..") && File.Exists(cached) && await DownloadHash.MatchesAsync(cached, output.Value, null, token))
                    { Directory.CreateDirectory(Path.GetDirectoryName(output.Key)!); File.Copy(cached, output.Key, true); repaired = true; break; }
                }
                if (!repaired)
                {
                    string actual = "缺失";
                    if (File.Exists(output.Key)) { await using var stream = File.OpenRead(output.Key); actual = Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(stream, token)); }
                    throw new InvalidDataException($"处理器输出校验失败：{relative} · 预期 {output.Value} · 实际 {actual}");
                }
            }
    }
}
