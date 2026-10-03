using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Core;
using SkiaSharp;

namespace Launcher.App.Services;

/// <summary>Only validated local textures reach the viewer. Credentials never reach image requests.</summary>
public sealed class AccountSkinService : IAccountSkinService, IDisposable
{
    private readonly AccountService _accounts;
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly string _steve;
    private readonly SemaphoreSlim _network = new(2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, AccountSkinInfo> _cache = new();
    private readonly Dictionary<string, Fetch> _fetches = new();
    private readonly HashSet<Task> _pendingFetches = new();
    private readonly object _gate = new();
    private Task<AccountSkinInfo>? _default;
    private int _disposed;
    public event Action<string>? SkinChanged;
    private sealed class Fetch(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<AccountSkinInfo> Task { get; set; } = null!;
        public int Readers;
    }
    public AccountSkinService(AccountService accounts, string? directory = null, HttpMessageHandler? transport = null, string? steve = null)
    {
        _accounts = accounts;
        _directory = directory ?? Path.Combine(AppPaths.Data, "skins");
        _steve = steve ?? Path.Combine(BundledAssets.SkinViewerDirectory, "steve.png");
        _http = new HttpClient(transport ?? new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NovainaLauncher/1.0 (account-skins)");
    }
    private static string Key(AccountProfile account) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Id)))[..24];
    private string Metadata(AccountProfile account) => Path.Combine(_directory, "accounts", Key(account) + ".json");
    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_directory, relative));
        if (!full.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("皮肤缓存路径无效");
        return full;
    }
    public string TextureFile(AccountSkinInfo skin) => Resolve(skin.Texture);
    public string HeadFile(AccountSkinInfo skin) => Resolve(skin.Head);
    private Task<AccountSkinInfo> DefaultAsync()
    {
        lock (_gate) return _default ??= Task.Run(async () => (await StoreTextureAsync(await File.ReadAllBytesAsync(_steve), SkinModel.Classic, _lifetime.Token)) with { IsDefault = true });
    }
    public async Task<AccountSkinInfo> GetCachedAsync(AccountProfile? account, CancellationToken token = default)
    {
        if (account is null) return await DefaultAsync().WaitAsync(token);
        if (_cache.TryGetValue(account.Id, out var cached) && File.Exists(TextureFile(cached)) && File.Exists(HeadFile(cached))) return cached;
        var result = await Task.Run(async () =>
        {
            try
            {
                var skin = JsonSerializer.Deserialize<AccountSkinInfo>(await File.ReadAllTextAsync(Metadata(account), token));
                if (skin is not null)
                {
                    Validate(await File.ReadAllBytesAsync(TextureFile(skin), token));
                    using var head = SKBitmap.Decode(HeadFile(skin));
                    if (head is { Width: 8, Height: 8 }) return skin;
                }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or ArgumentException) { }
            return (await DefaultAsync().WaitAsync(token)) with { Updated = DateTimeOffset.MinValue };
        }, token);
        _cache[account.Id] = result;
        return result;
    }
    public async Task<AccountSkinInfo> RefreshAsync(AccountProfile account, bool force, CancellationToken token)
    {
        var cached = await GetCachedAsync(account, token);
        if (account.Kind == AccountKind.Offline || !force && cached.Updated > DateTimeOffset.UtcNow.AddMinutes(-15) && _cache.ContainsKey(account.Id) && File.Exists(Metadata(account))) return cached;
        Fetch entry;
        lock (_gate)
        {
            if (!_fetches.TryGetValue(account.Id, out entry!))
            {
                entry = new Fetch(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                entry.Task = FetchAsync(account, entry.Cancellation.Token);
                _fetches.Add(account.Id, entry);
                _pendingFetches.Add(entry.Task);
                _ = entry.Task.ContinueWith(completed => { lock (_gate) _pendingFetches.Remove(completed); }, TaskScheduler.Default);
            }
            entry.Readers++;
        }
        try { return await entry.Task.WaitAsync(token); }
        finally
        {
            lock (_gate)
            {
                if (--entry.Readers == 0)
                {
                    _fetches.Remove(account.Id);
                    if (!entry.Task.IsCompleted) entry.Cancellation.Cancel();
                    _ = entry.Task.ContinueWith(completed => { _ = completed.Exception; entry.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
            }
        }
    }
    private async Task<AccountSkinInfo> FetchAsync(AccountProfile account, CancellationToken token)
    {
        var uuid = account.Uuid.Replace("-", "");
        if (uuid.Length != 32 || uuid.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("角色 UUID 无效");
        var endpoint = account.Kind == AccountKind.LittleSkin
            ? "https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile/" + uuid
            : "https://sessionserver.mojang.com/session/minecraft/profile/" + uuid;
        var bytes = await RequestAsync(new HttpRequestMessage(HttpMethod.Get, endpoint), token);
        var descriptor = ParseProfile(bytes, false);
        var skin = descriptor.Url is null ? (await DefaultAsync()) with { Updated = DateTimeOffset.UtcNow } : await StoreTextureAsync(await ReadImageAsync(descriptor.Url, token), descriptor.Model, token);
        await SaveAsync(account, skin, token);
        return skin;
    }
    public static (string? Url, SkinModel Model) ParseProfile(byte[] json, bool authenticated)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (authenticated)
        {
            if (!root.TryGetProperty("skins", out var skins)) return (null, SkinModel.Classic);
            foreach (var skin in skins.EnumerateArray())
                if (skin.TryGetProperty("state", out var state) && state.GetString() == "ACTIVE")
                    return (skin.GetProperty("url").GetString(), skin.TryGetProperty("variant", out var variant) && variant.GetString()?.Equals("SLIM", StringComparison.OrdinalIgnoreCase) == true ? SkinModel.Slim : SkinModel.Classic);
            return (null, SkinModel.Classic);
        }
        if (!root.TryGetProperty("properties", out var properties)) return (null, SkinModel.Classic);
        foreach (var property in properties.EnumerateArray())
        {
            if (property.GetProperty("name").GetString() != "textures") continue;
            using var texture = JsonDocument.Parse(Convert.FromBase64String(property.GetProperty("value").GetString()!));
            if (!texture.RootElement.GetProperty("textures").TryGetProperty("SKIN", out var skin)) return (null, SkinModel.Classic);
            return (skin.GetProperty("url").GetString(), skin.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("model", out var model) && model.GetString() == "slim" ? SkinModel.Slim : SkinModel.Classic);
        }
        return (null, SkinModel.Classic);
    }
    private async Task<byte[]> ReadImageAsync(string url, CancellationToken token)
    {
        var uri = new Uri(url);
        if (uri.Scheme == "http" && uri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase)) uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        if (uri.Scheme != "https") throw new InvalidDataException("皮肤地址必须使用 HTTPS");
        return await RequestAsync(new HttpRequestMessage(HttpMethod.Get, uri), token);
    }
    private async Task<byte[]> RequestAsync(HttpRequestMessage request, CancellationToken token)
    {
        using (request)
        {
            await _network.WaitAsync(token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("皮肤响应过大");
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var target = new MemoryStream();
                var buffer = new byte[16384]; int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (target.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("皮肤响应过大");
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                return target.ToArray();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested) { throw new TimeoutException("皮肤来源响应超时"); }
            finally { _network.Release(); }
        }
    }
    public static void Validate(byte[] bytes)
    {
        if (bytes.Length > 2 * 1024 * 1024 || bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("请选择 PNG 皮肤");
        // Read dimensions before decoding to avoid allocating arbitrary image sizes.
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width != 64 || height is not (64 or 32)) throw new InvalidDataException("皮肤尺寸需为 64×64 或 64×32");
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null || bitmap.Width != width || bitmap.Height != height) throw new InvalidDataException("皮肤图片已损坏");
    }
    public static byte[] CreateHead(byte[] texture)
    {
        Validate(texture);
        using var skin = SKBitmap.Decode(texture);
        using var head = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(head))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(skin, new SKRect(8, 8, 16, 16), new SKRect(0, 0, 8, 8));
            // Legacy opaque hat regions mean no overlay; modern skins have explicit alpha.
            bool transparent = false;
            for (int y = 8; y < 16; y++) for (int x = 40; x < 48; x++) if (skin.GetPixel(x, y).Alpha < 255) transparent = true;
            if (skin.Height == 64 || transparent) canvas.DrawBitmap(skin, new SKRect(40, 8, 48, 16), new SKRect(0, 0, 8, 8));
        }
        return head.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
    private async Task<AccountSkinInfo> StoreTextureAsync(byte[] bytes, SkinModel model, CancellationToken token)
    {
        return await Task.Run(async () =>
        {
            Validate(bytes);
            if (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) == 32) model = SkinModel.Classic;
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var texture = "textures/" + hash + ".png";
            var head = "heads/" + hash + ".png";
            await AtomicAsync(Resolve(texture), bytes, token);
            await AtomicAsync(Resolve(head), CreateHead(bytes), token);
            return new AccountSkinInfo(texture, head, model, DateTimeOffset.UtcNow);
        }, token);
    }
    private static async Task AtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task SaveAsync(AccountProfile account, AccountSkinInfo skin, CancellationToken token)
    {
        await AtomicAsync(Metadata(account), JsonSerializer.SerializeToUtf8Bytes(skin), token);
        _cache[account.Id] = skin;
        SkinChanged?.Invoke(account.Id);
    }
    private async Task CancelFetchAsync(AccountProfile account)
    {
        Task? pending = null;
        lock (_gate)
        {
            if (_fetches.TryGetValue(account.Id, out var entry)) { entry.Cancellation.Cancel(); pending = entry.Task; }
        }
        // A public profile request started before upload must not overwrite the newly applied skin.
        if (pending is not null) try { await pending; } catch (Exception) { }
    }
    public async Task<AccountSkinInfo> ImportAsync(string file, CancellationToken token)
    {
        if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("皮肤文件过大");
        return await StoreTextureAsync(await File.ReadAllBytesAsync(file, token), SkinModel.Classic, token);
    }
    public async Task<AccountSkinInfo> ApplyOfflineAsync(AccountProfile account, AccountSkinInfo skin, CancellationToken token)
    {
        if (account.Kind != AccountKind.Offline) throw new InvalidOperationException("请通过账户的皮肤服务应用");
        var bytes = await File.ReadAllBytesAsync(TextureFile(skin), token); Validate(bytes);
        if (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) == 32) skin = skin with { Model = SkinModel.Classic };
        skin = skin with { Updated = DateTimeOffset.UtcNow, IsDefault = false };
        await SaveAsync(account, skin, token); return skin;
    }
    public async Task<AccountSkinInfo> UploadMicrosoftAsync(AccountProfile account, AccountSkinInfo skin, string clientId, CancellationToken token)
    {
        if (account.Kind != AccountKind.Microsoft) throw new InvalidOperationException("此账户不支持微软上传");
        await CancelFetchAsync(account); token.ThrowIfCancellationRequested();
        var bytes = await File.ReadAllBytesAsync(TextureFile(skin), token); Validate(bytes);
        var session = await _accounts.SessionAsync(account, clientId, token);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(skin.Model == SkinModel.Slim ? "slim" : "classic"), "variant");
        var image = new ByteArrayContent(bytes); image.Headers.ContentType = new MediaTypeHeaderValue("image/png"); form.Add(image, "file", "skin.png");
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        await RequestAsync(request, token);
        var profile = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var descriptor = ParseProfile(await RequestAsync(profile, token), true);
        var applied = descriptor.Url is null ? skin : await StoreTextureAsync(await ReadImageAsync(descriptor.Url, token), descriptor.Model, token);
        await SaveAsync(account, applied, token); return applied;
    }
    public async Task ExportAsync(AccountSkinInfo skin, string file, CancellationToken token) => await AtomicAsync(file, await File.ReadAllBytesAsync(TextureFile(skin), token), token);
    public async Task CancelAndWaitAsync()
    {
        Task[] pending;
        lock (_gate) { foreach (var entry in _fetches.Values) entry.Cancellation.Cancel(); pending = _pendingFetches.ToArray(); }
        try { await Task.WhenAll(pending); } catch (OperationCanceledException) { } catch (HttpRequestException) { } catch (TimeoutException) { }
        if (_default is { } initializing) await initializing;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _http.Dispose(); _lifetime.Dispose();
    }
}
