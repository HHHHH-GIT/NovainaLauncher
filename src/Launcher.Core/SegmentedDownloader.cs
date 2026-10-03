using System.Collections.Concurrent;
using System.Net;
using System.Diagnostics;
using Downloader;

namespace Launcher.Core;

/// <summary>Compatibility facade: transport and Range fallback belong to Downloader.</summary>
public sealed class SegmentedDownloader(HttpClient http) : IFileDownloadService
{
    public Task DownloadAsync(string url, string path, IProgress<JavaDownloadProgress> progress, CancellationToken token) => DownloadFileAsync(url, path, new InlineProgress<FileDownloadProgress>(p => progress.Report(new(JavaDownloadStage.Downloading, p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Connections))), token);
    public Task DownloadFileAsync(string url, string path, IProgress<FileDownloadProgress> progress, CancellationToken token, bool official = false) => DownloadAsync(new(url, path, Official: official), progress, token);

    public async Task DownloadAsync(FileDownloadRequest request, IProgress<FileDownloadProgress> progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path))!);
        bool hasHash = !string.IsNullOrWhiteSpace(request.Sha1) || !string.IsNullOrWhiteSpace(request.Sha512);
        if (hasHash && request.CachePath is { } cached && File.Exists(cached) && (request.Size is not > 0 || new FileInfo(cached).Length == request.Size) && await DownloadHash.MatchesAsync(cached, request.Sha1, request.Sha512, token))
        { token.ThrowIfCancellationRequested(); File.Copy(cached, request.Path, true); return; }
        Exception? failure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            // No final filename is exposed until both transfer and validation have succeeded.
            var temporary = request.Path + "." + Guid.NewGuid().ToString("N") + ".verify";
            try
            {
                using var facade = new HttpClient(new ForwardHandler(http, request.Official || attempt >= 2 || failure is InvalidDataException)) { Timeout = Timeout.InfiniteTimeSpan };
                facade.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
                await using var download = new DownloadService(new DownloadConfiguration
                {
                    BufferBlockSize = 65536, ChunkCount = request.Size is > 0 and < 1048576 ? 1 : 4,
                    ParallelDownload = true, ParallelCount = 4, MinimumSizeOfChunking = 1048576,
                    MaxTryAgainOnFailure = 1, BlockTimeout = 30000, HttpClientTimeout = 60000,
                    EnableAutoResumeDownload = false, ClearPackageOnCompletionWithFailure = true,
                    CheckDiskSizeBeforeDownload = false, CustomHttpClientFactory = () => facade,
                    RequestConfiguration = new RequestConfiguration { AutomaticDecompression = DecompressionMethods.None, UserAgent = "NovainaLauncher/1.0", Headers = new WebHeaderCollection { { "Accept-Encoding", "identity" } } }
                });
                var chunks = new ConcurrentDictionary<string, DownloadConnectionProgress>();
                Exception? transferError = null;
                bool wasCancelled = false;
                download.DownloadFileCompleted += (_, e) => { transferError = e.Error; wasCancelled = e.Cancelled; };
                var ids = new ConcurrentDictionary<string, int>(); int next = 0; long last = 0, lastBytes = 0; var reportGate = new object();
                var watch = Stopwatch.StartNew();
                var samples = new Dictionary<string, Queue<(double Time, long Bytes)>>();
                Downloader.DownloadProgressChangedEventArgs? latest = null;
                double Speed(string key, long bytes)
                {
                    if (!samples.TryGetValue(key, out var history)) samples[key] = history = new();
                    var now = watch.Elapsed.TotalSeconds;
                    history.Enqueue((now, bytes));
                    while (history.Count > 2 && now - history.Peek().Time > 1) history.Dequeue();
                    var first = history.Peek();
                    return Math.Max(0, (bytes - first.Bytes) / Math.Max(.001, now - first.Time));
                }
                download.ChunkDownloadProgressChanged += (_, p) =>
                {
                    var id = ids.GetOrAdd(p.ProgressId, _ => Interlocked.Increment(ref next));
                    chunks[p.ProgressId] = new(id, p.ReceivedBytesSize, p.TotalBytesToReceive > 0 ? p.TotalBytesToReceive : null, p.BytesPerSecondSpeed, p.ProgressPercentage >= 100 ? "完成" : "下载中", request.Path + ":" + id, Path.GetFileName(request.Path));
                };
                void Report(Downloader.DownloadProgressChangedEventArgs p, bool final = false)
                {
                    lock (reportGate)
                    {
                    var now = Environment.TickCount64;
                    if (!final && now - Interlocked.Read(ref last) < 100) return;
                    Interlocked.Exchange(ref last, now);
                    var rows = chunks.Values.OrderBy(c => c.Id).Take(final ? chunks.Count : Math.Max(1, p.ActiveChunks)).Select(c => c with { BytesPerSecond = final ? 0 : Speed(c.Id.ToString(), c.DownloadedBytes) }).ToArray();
                    if (rows.Length == 0) rows = [new(1, p.ReceivedBytesSize, p.TotalBytesToReceive > 0 ? p.TotalBytesToReceive : null, p.BytesPerSecondSpeed, final ? "完成" : "下载中", request.Path + ":1", Path.GetFileName(request.Path))];
                    lastBytes = Math.Max(lastBytes, p.ReceivedBytesSize);
                    progress.Report(new(lastBytes, p.TotalBytesToReceive > 0 ? p.TotalBytesToReceive : null, final ? 0 : Speed("total", lastBytes), rows));
                    }
                }
                download.DownloadProgressChanged += (_, p) => { Volatile.Write(ref latest, p); Report(p, p.ProgressPercentage >= 100); };
                using var monitorStop = new CancellationTokenSource();
                async Task Monitor()
                {
                    try { using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100)); while (await timer.WaitForNextTickAsync(monitorStop.Token)) if (Volatile.Read(ref latest) is { } p) Report(p, p.ProgressPercentage >= 100); }
                    catch (OperationCanceledException) { }
                }
                var monitor = Monitor();
                // The cancellation token is owned by the library; disposal waits for chunks and storage.
                try { await download.DownloadFileTaskAsync(request.Url, temporary, token).ConfigureAwait(false); }
                finally { monitorStop.Cancel(); await monitor.ConfigureAwait(false); }
                token.ThrowIfCancellationRequested();
                if (transferError is not null) throw new IOException("下载连接失败：" + transferError.Message, transferError);
                if (wasCancelled) throw new OperationCanceledException(token);
                if (!File.Exists(temporary)) throw new IOException("下载未完整结束：" + download.Status);
                if (request.Size is > 0 && new FileInfo(temporary).Length != request.Size) throw new InvalidDataException("文件大小不符");
                if (!await DownloadHash.MatchesAsync(temporary, request.Sha1, request.Sha512, token)) throw new InvalidDataException("文件 SHA 校验失败");
                File.Move(temporary, request.Path, true);
                if (hasHash && request.CachePath is { } cache)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    var copy = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.Copy(request.Path, copy); token.ThrowIfCancellationRequested(); File.Move(copy, cache, true); }
                    finally { if (File.Exists(copy)) File.Delete(copy); }
                }
                return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or HttpRequestException or OperationCanceledException)
            { failure = ex; }
            finally
            {
                foreach (var path in new[] { temporary, temporary + ".download" })
                    if (File.Exists(path)) File.Delete(path);
            }
        }
        var message = $"文件下载失败：{Path.GetFileName(request.Path)} · 三次尝试（镜像 / 官方） · {failure?.Message}";
        if (failure is InvalidDataException) throw new InvalidDataException(message, failure);
        throw new IOException(message, failure);
    }
    private sealed class ForwardHandler(HttpClient client, bool official) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (official) copy.Headers.TryAddWithoutValidation("X-IKun-Official", "1");
            var response = await client.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, token);
            if (copy.Method == HttpMethod.Get && copy.Headers.Range?.Ranges.SingleOrDefault() is { From: not null } range && response.StatusCode == HttpStatusCode.PartialContent)
            {
                var actual = response.Content.Headers.ContentRange;
                if (actual?.From != range.From || range.To is not null && actual.To != range.To)
                { response.Dispose(); throw new InvalidDataException("来源返回了错误的分段范围"); }
            }
            return response;
        }
    }
}
