using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Launcher.Core;

public sealed class LogService : IDisposable
{
    private Channel<LauncherLogEvent> _fileQueue = NewQueue();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task _writer;
    private bool _paused, _disposed;
    private static Channel<LauncherLogEvent> NewQueue() => Channel.CreateUnbounded<LauncherLogEvent>(new UnboundedChannelOptions { SingleReader = true });
    public event Action<LauncherLogEvent>? Emitted;
    public string FilePath { get; }
    public LogService(string? directory = null)
    {
        directory ??= System.IO.Path.Combine(AppPaths.Data, "logs");
        Directory.CreateDirectory(directory);
        FilePath = System.IO.Path.Combine(directory, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        _writer = Task.Run(WriteFileAsync);
    }
    public void RegisterSecret(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret)) lock (_gate) _secrets.Add(secret);
    }
    public string Redact(string value)
    {
        lock (_gate)
            foreach (var secret in _secrets.OrderByDescending(x => x.Length)) value = value.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        value = Regex.Replace(value, "(?i)(access_token|refresh_token|accessToken|refreshToken|clientToken|password|Authorization)[\\\"\\s:=]+[^\\s,\\\"}]+", "$1=[已隐藏]");
        value = Regex.Replace(value, "(?i)(Bearer\\s+)[^\\s\\\"]+", "$1[已隐藏]");
        value = Regex.Replace(value, "(?i)(--accessToken\\s+)[^\\s]+", "$1[已隐藏]");
        return value;
    }
    public void Write(string message, LogLevel level = LogLevel.Info, bool featured = false)
    {
        var entry = new LauncherLogEvent(DateTimeOffset.Now, level, Redact(message), featured);
        lock (_gate) _fileQueue.Writer.TryWrite(entry);
        Emitted?.Invoke(entry);
    }
    private async Task WriteFileAsync()
    {
        try
        {
            await using var writer = new StreamWriter(FilePath, true) { AutoFlush = true };
            await foreach (var item in _fileQueue.Reader.ReadAllAsync()) await writer.WriteLineAsync(item.Text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Emitted?.Invoke(new(DateTimeOffset.Now, LogLevel.Warning, "日志文件无法写入", true));
        }
    }
    public async Task PauseFileAsync()
    {
        Task pending;
        lock (_gate) { _paused = true; _fileQueue.Writer.TryComplete(); pending = _writer; }
        await pending;
    }
    public void ResumeFile()
    {
        lock (_gate)
        {
            if (_disposed || !_paused) return;
            _fileQueue = NewQueue(); _paused = false; _writer = Task.Run(WriteFileAsync);
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _fileQueue.Writer.TryComplete(); }
        _writer.Wait(TimeSpan.FromSeconds(2));
    }
}

public sealed class DanmakuQueue
{
    private readonly LinkedList<LauncherLogEvent> _items = new();
    private readonly object _gate = new();
    private string? _lastMessage;
    private DateTimeOffset _lastTime;
    public int Count { get { lock (_gate) return _items.Count; } }
    public void Add(LauncherLogEvent entry, DanmakuMode mode)
    {
        if (mode == DanmakuMode.Off || (mode == DanmakuMode.Selected && !entry.Featured && entry.Level == LogLevel.Info)) return;
        lock (_gate)
        {
            if (entry.Message == _lastMessage && entry.Time - _lastTime < TimeSpan.FromSeconds(3)) return;
            _lastMessage = entry.Message; _lastTime = entry.Time;
            if (_items.Count >= 200)
            {
                var candidate = _items.First;
                while (candidate is not null && candidate.Value.Level == LogLevel.Error) candidate = candidate.Next;
                if (candidate is null && entry.Level != LogLevel.Error) return;
                _items.Remove(candidate ?? _items.First!);
            }
            _items.AddLast(entry);
        }
    }
    public LauncherLogEvent? Take() { lock (_gate) { if (_items.First is not { } node) return null; _items.RemoveFirst(); return node.Value; } }
    public void Clear() { lock (_gate) _items.Clear(); }
}
