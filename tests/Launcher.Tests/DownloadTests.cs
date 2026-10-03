using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class DownloadTests
{
    private static readonly byte[] Payload = CreatePayload();
    private static byte[] CreatePayload() { var b = new byte[8 * 1024 * 1024]; for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 31); return b; }
    [Fact]
    public async Task Four_Connections_Write_Exact_Offsets_And_Report_Speed()
    {
        using var handler = new DownloadServer(true); using var http = new HttpClient(handler);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".part");
        var snapshots = new List<JavaDownloadProgress>();
        try
        {
            await new SegmentedDownloader(http).DownloadAsync("https://fixture.test/java.zip", path, new InlineProgress(p => snapshots.Add(p)), default);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
            Assert.InRange(handler.Requests, 4, 12);
            Assert.Equal(4, snapshots.Last().Connections.Count);
            Assert.Equal(100, snapshots.Last().Percent);
            Assert.Contains(snapshots, s => s.BytesPerSecond > 0);
            Assert.All(snapshots.Last().Connections, c => Assert.Equal("完成", c.State));
            Assert.True(snapshots.Zip(snapshots.Skip(1), (a, b) => a.DownloadedBytes <= b.DownloadedBytes).All(x => x));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Server_Without_Ranges_Uses_One_Connection(bool unknownLength)
    {
        using var handler = new DownloadServer(false) { UnknownLength = unknownLength }; using var http = new HttpClient(handler);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".part"); JavaDownloadProgress? last = null;
        try
        {
            await new SegmentedDownloader(http).DownloadAsync("https://fixture.test/java.zip", path, new InlineProgress(p => last = p), default);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(path)); Assert.InRange(handler.Requests, 1, 6);
            Assert.Single(last!.Connections); Assert.Equal(unknownLength ? null : Payload.LongLength, last.TotalBytes);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task Cancellation_Stops_Active_Download_And_Removes_Partial_File()
    {
        using var handler = new DownloadServer(true); using var http = new HttpClient(handler); using var cts = new CancellationTokenSource();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".part");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SegmentedDownloader(http).DownloadAsync("https://fixture.test/java.zip", path, new InlineProgress(p => { if (p.DownloadedBytes > 0) cts.Cancel(); }), cts.Token));
        Assert.False(File.Exists(path)); Assert.Equal(0, handler.ActiveStreams);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Invalid_Range_Or_Connection_Failure_Cleans_Up(bool badRange)
    {
        using var handler = new DownloadServer(true) { BadRange = badRange, FailConnection = !badRange }; using var http = new HttpClient(handler);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".part");
        Exception? failure = null;
        try { await new SegmentedDownloader(http).DownloadAsync(new("https://fixture.test/java.zip", path, Payload.Length, Convert.ToHexString(SHA1.HashData(Payload))), new InlineProgress<FileDownloadProgress>(_ => { }), default); }
        catch (Exception error) { failure = error; }
        if (failure is null) { Assert.True(badRange); Assert.Equal(Payload, await File.ReadAllBytesAsync(path)); File.Delete(path); }
        Assert.False(File.Exists(path)); Assert.Equal(0, handler.ActiveStreams);
    }
    [Fact]
    public async Task Archive_Verification_And_Extraction_Observe_Cancellation()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip"); var folder = path + ".extracting";
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            { using var file = zip.CreateEntry("runtime/bin/java.exe").Open(); file.Write(Payload, 0, 4096); }
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            await JavaService.VerifyArchiveAsync(path, hash, default);
            await Assert.ThrowsAsync<InvalidDataException>(() => JavaService.VerifyArchiveAsync(path, new string('0', 64), default));
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JavaService.VerifyArchiveAsync(path, hash, cts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JavaService.ExtractArchiveAsync(path, folder, cts.Token));
            Assert.Empty(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
            await JavaService.ExtractArchiveAsync(path, folder, default);
            Assert.Equal(4096, new FileInfo(Path.Combine(folder, "runtime/bin/java.exe")).Length);
        }
        finally { File.Delete(path); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class InlineProgress(Action<JavaDownloadProgress> callback) : IProgress<JavaDownloadProgress> { public void Report(JavaDownloadProgress value) => callback(value); }
    [Fact]
    public async Task Java_Service_Hash_Failure_Removes_Its_Archive_And_Staging()
    {
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        const int major = 99999;
        var destination = Path.Combine(AppPaths.Runtime, $"java-{major}-{hash[..12]}");
        using var handler = new DownloadServer(true) { MetadataHash = hash }; using var http = new HttpClient(handler);
        using var log = new LogService(Path.Combine(Path.GetTempPath(), "java-test-" + Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidDataException>(() => new JavaService(http, log).DownloadAsync(major, new InlineProgress(_ => { }), default));
        Assert.False(File.Exists(destination + ".zip.part"));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(AppPaths.Runtime, Path.GetFileName(destination) + ".extracting-*"));
    }
    private sealed class DownloadServer(bool ranges) : HttpMessageHandler
    {
        public bool UnknownLength, BadRange, FailConnection;
        public string? MetadataHash;
        public int Requests, ActiveStreams;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/v3/assets/") && MetadataHash is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new[] { new { binary = new { package = new { checksum = MetadataHash, link = "https://fixture.test/java.zip" } } } })) });
            Interlocked.Increment(ref Requests); var r = request.Headers.Range?.Ranges.Single();
            bool probe = request.Method == HttpMethod.Head || r is { From: 0, To: <= 1 };
            if (!probe && FailConnection) throw new HttpRequestException("fixture disconnected");
            bool ranged = ranges && r is not null;
            int from = ranged ? (int)(r!.From ?? 0) : 0; int length = ranged ? (int)((r!.To ?? Payload.Length - 1) - from + 1) : Payload.Length;
            Interlocked.Increment(ref ActiveStreams);
            var response = new HttpResponseMessage(ranged ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new StreamContent(new SlowStream(Payload, from, length, probe && ranges ? 0 : 5, () => Interlocked.Decrement(ref ActiveStreams))) };
            if (!UnknownLength) response.Content.Headers.ContentLength = length;
            response.Headers.AcceptRanges.Add(ranges ? "bytes" : "none");
            if (ranged) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from + (!probe && BadRange ? 1 : 0), from + length - 1, Payload.Length);
            return Task.FromResult(response);
        }
    }
    private sealed class SlowStream(byte[] data, int start, int count, int delay, Action closed) : Stream
    {
        private int _position; private bool _closed;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => count; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (delay > 0) await Task.Delay(delay, token);
            token.ThrowIfCancellationRequested(); int length = Math.Min(buffer.Length, count - _position);
            data.AsMemory(start + _position, length).CopyTo(buffer); _position += length; return length;
        }
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (!_closed) { _closed = true; closed(); } base.Dispose(disposing); }
    }
}

