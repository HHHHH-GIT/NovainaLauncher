using System.Net;

namespace Launcher.Core;

public static class DownloadConnectionBudget
{
    internal static readonly HttpRequestOptionsKey<TimeSpan> ResponseTimeout = new("iKun.ResponseTimeout");
    private static readonly SemaphoreSlim Pool = new(Environment.GetEnvironmentVariable("IKUN_INSTALL_HOST") == "1" ? 4 : 8);
    private static readonly SemaphoreSlim Reservation = new(1);
    public static async Task<IDisposable> ReserveHostAsync(CancellationToken token)
    {
        await Reservation.WaitAsync(token);
        int acquired = 0;
        try { for (; acquired < 4; acquired++) await Pool.WaitAsync(token); return new Lease(4); }
        catch { if (acquired > 0) Pool.Release(acquired); throw; }
        finally { Reservation.Release(); }
    }
    private sealed class Lease(int count) : IDisposable
    {
        private int _count = count;
        public void Dispose() { var n = Interlocked.Exchange(ref _count, 0); if (n > 0) Pool.Release(n); }
    }
    public sealed class Handler(HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await Pool.WaitAsync(token);
            var lease = new Lease(1);
            try
            {
                // Waiting for a local slot is not a remote response timeout.
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (request.Options.TryGetValue(ResponseTimeout, out var limit)) attempt.CancelAfter(limit);
                var response = await base.SendAsync(request, attempt.Token);
                response.Content = new LeasedContent(response.Content, lease);
                return response;
            }
            catch { lease.Dispose(); throw; }
        }
    }
    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent _content; private readonly IDisposable _lease;
        public LeasedContent(HttpContent content, IDisposable lease)
        { _content = content; _lease = lease; foreach (var h in content.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _content.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => _content.CopyToAsync(stream, token);
        protected override Task<Stream> CreateContentReadStreamAsync() => _content.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => _content.ReadAsStreamAsync(token);
        protected override bool TryComputeLength(out long length) { length = _content.Headers.ContentLength ?? -1; return length >= 0; }
        protected override void Dispose(bool disposing) { if (disposing) { _content.Dispose(); _lease.Dispose(); } base.Dispose(disposing); }
    }
}
