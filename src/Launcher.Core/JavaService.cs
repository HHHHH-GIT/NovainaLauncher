using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Launcher.Core;

public sealed class JavaService(HttpClient http, LogService log)
{
    private readonly SemaphoreSlim _downloadGate = new(1);
    private readonly Dictionary<string, JavaRuntimeInfo> _recentDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Stamp, JavaRuntimeInfo Info)> _cache = new(StringComparer.OrdinalIgnoreCase);
    public async Task<IReadOnlyList<JavaRuntimeInfo>> ScanAsync(string gameRoot, IEnumerable<string> overrides, CancellationToken token = default, string? downloadDirectory = null)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in overrides) Add(value);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) Add(Path.Combine(folder.Trim('"'), "java.exe"));
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) Add(Path.Combine(javaHome, "bin", "java.exe"));
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (var keyName in new[] { @"SOFTWARE\JavaSoft", @"SOFTWARE\Eclipse Adoptium", @"SOFTWARE\Microsoft\JDK" })
            {
                using var key = registry.OpenSubKey(keyName);
                if (key is not null) ScanRegistry(key, 0);
            }
        }
        var folders = new[] { AppPaths.Runtime, AppPaths.LegacyRuntime, string.IsNullOrWhiteSpace(downloadDirectory) ? AppPaths.Runtime : Path.GetFullPath(downloadDirectory), Path.Combine(gameRoot, "runtime"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java") };
        await Task.Run(() => { foreach (var folder in folders) ScanFolder(folder, 0); }, token);
        var found = new List<JavaRuntimeInfo>();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var stamp = File.GetLastWriteTimeUtc(candidate);
            if (_cache.TryGetValue(candidate, out var cached) && cached.Stamp == stamp) { found.Add(cached.Info); continue; }
            if (await ProbeAsync(candidate, token) is { } info) { _cache[candidate] = (stamp, info); found.Add(info); }
        }
        return found.OrderByDescending(x => x.Major).ThenByDescending(x => x.Version).ToList();
        void Add(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
                if (Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(Path.GetDirectoryName(path)!, "java.exe");
                if (File.Exists(path)) candidates.Add(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { }
        }
        void ScanFolder(string folder, int depth)
        {
            if (depth > 6 || !Directory.Exists(folder)) return;
            token.ThrowIfCancellationRequested();
            try
            {
                Add(Path.Combine(folder, "java.exe")); Add(Path.Combine(folder, "bin", "java.exe"));
                if (candidates.Contains(Path.Combine(folder, "bin", "java.exe"))) return;
                foreach (var child in Directory.EnumerateDirectories(folder))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) ScanFolder(child, depth + 1);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        void ScanRegistry(RegistryKey key, int depth)
        {
            if (depth > 4) return;
            foreach (var value in new[] { "JavaHome", "Path", "InstallationPath" })
                if (key.GetValue(value) is string path) Add(Path.Combine(path, "bin", "java.exe"));
            foreach (var child in key.GetSubKeyNames()) { using var sub = key.OpenSubKey(child); if (sub is not null) ScanRegistry(sub, depth + 1); }
        }
    }

    public static async Task<JavaRuntimeInfo?> ProbeAsync(string path, CancellationToken token = default)
    {
        using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("-XshowSettings:properties"); process.StartInfo.ArgumentList.Add("-version");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(timeout.Token);
            return Parse(path, await stdout + "\n" + await stderr);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }

    public static JavaRuntimeInfo? Parse(string path, string output)
    {
        var text = Regex.Match(output, @"java\.version\s*=\s*(\S+)").Groups[1].Value;
        if (text.Length == 0) text = Regex.Match(output, "version\\s+\"([^\"]+)\"").Groups[1].Value;
        var numbers = Regex.Matches(text, @"\d+").Select(m => int.Parse(m.Value)).Take(4).ToArray();
        if (numbers.Length == 0) return null;
        int major = numbers[0] == 1 && numbers.Length > 1 ? numbers[1] : numbers[0];
        var version = new Version(numbers.ElementAtOrDefault(0), numbers.ElementAtOrDefault(1), numbers.ElementAtOrDefault(2), numbers.ElementAtOrDefault(3));
        var arch = Regex.Match(output, @"os\.arch\s*=\s*(\S+)").Groups[1].Value;
        arch = arch.Contains("64") || output.Contains("64-Bit") ? "x64" : "x86";
        var vendor = Regex.Match(output, @"java\.vendor\s*=\s*([^\r\n]+)").Groups[1].Value.Trim();
        return new(path, major, version, arch, vendor.Length > 0 ? vendor : "Java");
    }
    public static JavaRuntimeInfo? Select(IEnumerable<JavaRuntimeInfo> runtimes, int? required, string? manual = null)
    {
        if (manual is not null) return runtimes.FirstOrDefault(x => x.Path.Equals(manual, StringComparison.OrdinalIgnoreCase) && x.Architecture == "x64" && (required is null || x.Major == required));
        return required is null ? null : runtimes.Where(x => x.Major == required && x.Architecture == "x64").OrderByDescending(x => x.Version).FirstOrDefault();
    }
    public async Task<JavaRuntimeInfo?> PrepareForInstallAsync(int major, InstallPlan plan, IProgress<JavaDownloadProgress> progress, CancellationToken token, string? gameRoot = null)
    {
        token.ThrowIfCancellationRequested();
        var runtime = Select(plan.Javas, major);
        if (runtime is null && plan.AutoPrepareJava)
        {
            // Queued plans can predate the initial scan or a preceding task's Java installation.
            var installed = await Task.Run(() => ScanAsync(gameRoot ?? plan.Root, plan.Javas.Select(j => j.Path), token, plan.JavaDirectory), token).ConfigureAwait(false);
            runtime = Select(installed, major);
        }
        if (runtime is not null) return runtime;
        if (plan.AutoPrepareJava) return await DownloadAsync(major, progress, token, plan.JavaDirectory).ConfigureAwait(false);
        // Vanilla, Fabric and the local OptiFine patcher do not execute Java during installation.
        if (plan.Loader?.Loader is "Forge" or "NeoForge")
            throw new InvalidOperationException($"安装 {plan.Loader.Loader} 需要 Java {major}，自动下载已关闭，请在“启动与Java”中扫描、添加或手动下载");
        return null;
    }

    public async Task<JavaRuntimeInfo> DownloadAsync(int major, IProgress<JavaDownloadProgress> progress, CancellationToken token, string? downloadDirectory = null)
    {
        await _downloadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var key = Path.GetFullPath(string.IsNullOrWhiteSpace(downloadDirectory) ? AppPaths.Runtime : downloadDirectory) + "|" + major;
            if (_recentDownloads.TryGetValue(key, out var ready) && File.Exists(ready.Path))
            { token.ThrowIfCancellationRequested(); progress.Report(new(JavaDownloadStage.Completed, 0, null, 0, [])); return ready; }
            var installed = await DownloadLockedAsync(major, progress, token, downloadDirectory).ConfigureAwait(false);
            _recentDownloads[key] = installed; return installed;
        }
        finally { _downloadGate.Release(); }
    }
    private async Task<JavaRuntimeInfo> DownloadLockedAsync(int major, IProgress<JavaDownloadProgress> progress, CancellationToken token, string? downloadDirectory)
    {
        log.Write($"正在下载 Java {major}", featured: true);
        progress.Report(new(JavaDownloadStage.Preparing, 0, null, 0, []));
        using var sources = new DownloadSources(new(true, true), token, diagnostic: message => log.Write(message), transport: new JavaTransport(http));
        var metadata = await sources.JsonAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse", token).ConfigureAwait(false);
        if (metadata.GetArrayLength() == 0)
            metadata = await sources.JsonAsync($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse", token).ConfigureAwait(false);
        if (metadata.GetArrayLength() == 0) throw new InvalidOperationException($"没有可下载的 Java {major}");
        var package = metadata[0].GetProperty("binary").GetProperty("package");
        var hash = package.GetProperty("checksum").GetString()!;
        var rootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(downloadDirectory) ? AppPaths.Runtime : downloadDirectory);
        var destination = Path.Combine(rootDirectory, $"java-{major}-{hash[..12]}");
        Directory.CreateDirectory(rootDirectory);
        if (Directory.Exists(destination))
        {
            var existing = Directory.EnumerateFiles(destination, "java.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (existing is not null && await ProbeAsync(existing, token).ConfigureAwait(false) is { } cached) return cached;
            throw new InvalidDataException("缓存的 Java 不可用，请选择本地 Java");
        }
        var archive = destination + ".zip.part";
        var staging = destination + ".extracting-" + Guid.NewGuid().ToString("N");
        var relay = new DownloadProgressRelay(progress);
        try
        {
            var download = new SegmentedDownloader(sources.Http);
            var url = package.GetProperty("link").GetString()!;
            await download.DownloadAsync(url, archive, relay, token).ConfigureAwait(false);
            relay.Phase(JavaDownloadStage.Verifying);
            try { await VerifyArchiveAsync(archive, hash, token).ConfigureAwait(false); }
            catch (InvalidDataException) when (DownloadSources.Mirror(new Uri(url), sources.Policy) is not null)
            {
                log.Write("Java 镜像文件校验失败，回退官方源", LogLevel.Warning);
                await download.DownloadFileAsync(url, archive, new InlineProgress<FileDownloadProgress>(p => relay.Report(new(JavaDownloadStage.Downloading, p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Connections))), token, official: true).ConfigureAwait(false);
                relay.Phase(JavaDownloadStage.Verifying);
                await VerifyArchiveAsync(archive, hash, token).ConfigureAwait(false);
            }
            relay.Phase(JavaDownloadStage.Extracting);
            await ExtractArchiveAsync(archive, staging, token).ConfigureAwait(false);
            var executable = Directory.EnumerateFiles(staging, "java.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("下载包中未找到 Java");
            var info = await ProbeAsync(executable, token).ConfigureAwait(false) ?? throw new InvalidDataException("下载的 Java 无法运行");
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            relay.Phase(JavaDownloadStage.Completed);
            return info with { Path = Path.Combine(destination, Path.GetRelativePath(staging, executable)) };
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            // Only this operation's staging directory is removed; installed runtimes are preserved.
            var runtimeRoot = rootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(staging).StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
    public static async Task VerifyArchiveAsync(string archive, string expectedHash, CancellationToken token)
    {
        await using var stream = File.OpenRead(archive);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Java 下载校验失败");
    }
    public static async Task ExtractArchiveAsync(string archive, string directory, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(directory);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(directory, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载包包含无效路径");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(output, 65536, token).ConfigureAwait(false);
        }
    }
    private sealed class DownloadProgressRelay(IProgress<JavaDownloadProgress> target) : IProgress<JavaDownloadProgress>
    {
        private JavaDownloadProgress _last = new(JavaDownloadStage.Preparing, 0, null, 0, []);
        public void Report(JavaDownloadProgress value) { _last = value; target.Report(value); }
        public void Phase(JavaDownloadStage stage) => target.Report(_last with { Stage = stage, BytesPerSecond = 0 });
    }
    public async Task DownloadFileAsync(string url, string path, IProgress<double>? progress, CancellationToken token, HttpClient? sourceHttp = null)
    {
        await new SegmentedDownloader(sourceHttp ?? http).DownloadFileAsync(url, path, new InlineProgress<FileDownloadProgress>(p => progress?.Report(p.Percent)), token);
    }
    // Reuse the injected transport (including test handlers) without disposing the shared client.
    private sealed class JavaTransport(HttpClient client) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
}
