using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Launcher.Core;

/// <summary>Each operation owns its HTTP client and frozen source policy.</summary>
public sealed class DownloadSources : IDisposable
{
    public HttpClient Http { get; }
    public SourcePolicy Policy { get; }
    private readonly CancellationTokenRegistration _cancellation;
    private readonly bool _cacheEnabled;
    private readonly Action<string>? _diagnostic;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RateLimits = new();
    public string ActualSource { get; private set; } = "";
    public DownloadSources(SourcePolicy policy, CancellationToken lifetime = default, HttpMessageHandler? transport = null, Action<string>? diagnostic = null)
    {
        Policy = policy;
        _diagnostic = diagnostic;
        _cacheEnabled = transport is null;
        Http = new HttpClient(new SourceHandler(policy, lifetime, value => ActualSource = value, new DownloadConnectionBudget.Handler(transport ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false }))) { Timeout = TimeSpan.FromMinutes(10) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("NovainaLauncher/1.0");
        _cancellation = lifetime.Register(() => Http.CancelPendingRequests());
    }
    public static string? Mirror(Uri uri, SourcePolicy policy)
    {
        var path = uri.PathAndQuery;
        if (policy.GameMirror)
        {
            if (new[] { "launchermeta.mojang.com", "launcher.mojang.com", "piston-meta.mojang.com", "piston-data.mojang.com" }.Contains(uri.Host)) return "https://bmclapi2.bangbang93.com" + path;
            if (uri.Host == "resources.download.minecraft.net") return "https://bmclapi2.bangbang93.com/assets" + path;
            if (uri.Host is "libraries.minecraft.net" or "maven.minecraftforge.net" or "files.minecraftforge.net" or "maven.fabricmc.net") return "https://bmclapi2.bangbang93.com/maven" + path;
            if (uri.Host == "maven.neoforged.net") return "https://bmclapi2.bangbang93.com/maven" + path.Replace("/releases/", "/");
            if (uri.Host == "meta.fabricmc.net") return "https://bmclapi2.bangbang93.com/fabric-meta" + path;
            if (uri.Host == "authlib-injector.yushi.moe") return "https://bmclapi2.bangbang93.com/mirrors/authlib-injector" + path;
            if (uri.Host == "github.com" && System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath,
                @"^/adoptium/temurin(\d+)-binaries/releases/download/[^/]+/(OpenJDK\d+U-(jre|jdk)_x64_windows_hotspot_[^/]+\.zip)$") is { Success: true } java)
                return $"https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{java.Groups[1].Value}/{java.Groups[3].Value}/x64/windows/{java.Groups[2].Value}";
        }
        if (policy.ContentMirror)
        {
            if (uri.Host == "api.modrinth.com") return "https://mod.mcimirror.top/modrinth" + path;
            if (uri.Host == "api.curseforge.com") return "https://mod.mcimirror.top/curseforge" + path;
            if (uri.Host is "cdn.modrinth.com" or "media.forgecdn.net" or "mediafilez.forgecdn.net" or "edge.forgecdn.net" or "mediafile.forgecdn.net") return "https://mod.mcimirror.top" + path;
        }
        return null;
    }
    public async Task<JsonElement> JsonAsync(string url, CancellationToken token, TimeSpan? freshness = null, bool refresh = false, Func<JsonElement, bool>? validate = null)
        => await JsonRequestAsync(url, token, freshness, refresh, validate, null).ConfigureAwait(false);
    public Task<JsonElement> PostJsonAsync(string url, object payload, CancellationToken token, Func<JsonElement, bool>? validate = null)
        => JsonRequestAsync(url, token, null, false, validate, JsonSerializer.Serialize(payload));
    private async Task<JsonElement> JsonRequestAsync(string url, CancellationToken token, TimeSpan? freshness, bool refresh, Func<JsonElement, bool>? validate, string? body)
    {
        var directory = Path.Combine(AppPaths.Data, "downloads", "metadata");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Policy.GameMirror}|{Policy.ContentMirror}|{url}" + (body is null ? "" : "|POST|" + body))));
        var cache = Path.Combine(directory, key + ".json");
        var original = new Uri(url);
        if (original.Host == "api.curseforge.com" && !Policy.ContentMirror && string.IsNullOrWhiteSpace(Policy.CurseForgeKey)) throw new InvalidOperationException("CurseForge 来源不可用：请配置 API Key");
        if (_cacheEnabled && !refresh && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < (freshness ?? TimeSpan.FromMinutes(15)))
        {
            try { using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(cache, token)); if (validate is null || validate(cached.RootElement)) { ActualSource = "缓存"; return cached.RootElement.Clone(); } }
            catch (JsonException) { }
        }
        var mirror = Mirror(original, Policy);
        var primary = mirror is null ? original : new Uri(mirror);
        var canFallback = mirror is not null && (original.Host != "api.curseforge.com" || !string.IsNullOrWhiteSpace(Policy.CurseForgeKey));
        JsonElement result;
        try
        {
            try { result = await ReadJsonAsync(primary, mirror is not null, token, validate, body).ConfigureAwait(false); }
            catch (Exception error) when (canFallback && !token.IsCancellationRequested && IsSourceFailure(error))
            {
                _diagnostic?.Invoke($"{primary.Host} 回退至 {original.Host}");
                result = await ReadJsonAsync(original, false, token, validate, body).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (_cacheEnabled && !token.IsCancellationRequested && File.Exists(cache) && IsSourceFailure(error))
        {
            using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(cache, token));
            if (validate is not null && !validate(cached.RootElement)) throw;
            ActualSource = "离线缓存"; return cached.RootElement.Clone();
        }
        if (!_cacheEnabled) return result;
        Directory.CreateDirectory(directory);
        var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, result.GetRawText(), token); token.ThrowIfCancellationRequested(); File.Move(temporary, cache, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result;
    }
    private static bool IsSourceFailure(Exception error) => error is HttpRequestException or IOException or InvalidDataException or JsonException or TimeoutException or OperationCanceledException;
    private async Task<JsonElement> ReadJsonAsync(Uri endpoint, bool mirror, CancellationToken token, Func<JsonElement, bool>? validate, string? body = null)
    {
        token.ThrowIfCancellationRequested();
        if (RateLimits.TryGetValue(endpoint.Host, out var until) && until > DateTimeOffset.UtcNow)
            throw new HttpRequestException($"{endpoint.Host} 限流，{Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds)} 秒后重试", null, HttpStatusCode.TooManyRequests);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, endpoint);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        // JSON owns source fallback so a stalled HTTP 200 body also falls back.
        request.Headers.Add("X-IKun-Official", "1");
        request.Headers.Add("X-IKun-Metadata", "1");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var clock = Stopwatch.StartNew();
        var phase = "连接 / 响应头";
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                RateLimits[endpoint.Host] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{endpoint.Host} · HTTP {(int)response.StatusCode} ({response.ReasonPhrase})", null, response.StatusCode);
            phase = "读取响应正文";
            timeout.CancelAfter(TimeSpan.FromSeconds(mirror ? 12 : 35));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var decompressed = response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase)
                ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true) : null;
            using var doc = await JsonDocument.ParseAsync((Stream?)decompressed ?? stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (validate is not null && !validate(doc.RootElement)) throw new InvalidDataException($"{endpoint.Host} 返回的数据格式不正确");
            return doc.RootElement.Clone();
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsSourceFailure(error))
        {
            string reason = error is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}"
                : error is OperationCanceledException ? "超时" : error is JsonException ? "无效 JSON" : "传输失败";
            _diagnostic?.Invoke($"{endpoint.Host}{endpoint.AbsolutePath} · {phase} · {clock.Elapsed.TotalSeconds:F1}s · {reason}");
            if (error is OperationCanceledException) throw new TimeoutException($"{endpoint.Host} · {phase}超时 ({clock.Elapsed.TotalSeconds:F1}s)", error);
            throw;
        }
    }

    public async Task<string?> CacheIconAsync(string? url, CancellationToken token, Func<string, bool>? validate = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        string directory = Path.Combine(AppPaths.Data, "downloads", "icons"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url!))) + ".image");
        if (File.Exists(path))
        {
            if (await ValidIconAsync(path, validate, token).ConfigureAwait(false)) { ActualSource = "缓存"; return path; }
            File.Delete(path);
        }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var mirror = Mirror(uri, Policy);
        var endpoints = mirror is null ? new[] { uri } : new[] { new Uri(mirror), uri };
        try
        {
            foreach (var endpoint in endpoints)
            {
                try
                {
                    if (RateLimits.TryGetValue(endpoint.Host, out var until) && until > DateTimeOffset.UtcNow)
                        throw new HttpRequestException($"{endpoint.Host} 暂时限流", null, HttpStatusCode.TooManyRequests);
                    using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    request.Headers.Add("X-IKun-Official", "1");
                    request.Headers.Add("X-IKun-Metadata", "1");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(35));
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        RateLimits[endpoint.Host] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
                    response.EnsureSuccessStatusCode();
                    timeout.CancelAfter(TimeSpan.FromSeconds(endpoint == uri ? 15 : 8));
                    await using (var output = File.Create(temp))
                        await response.Content.CopyToAsync(output, timeout.Token).ConfigureAwait(false);
                    if (!await ValidIconAsync(temp, validate, token).ConfigureAwait(false)) throw new InvalidDataException("无法解码图标");
                    token.ThrowIfCancellationRequested(); File.Move(temp, path, true); return path;
                }
                catch (Exception error) when (!token.IsCancellationRequested && IsSourceFailure(error))
                { _diagnostic?.Invoke($"{endpoint.Host} · 图标读取失败 · {(error is OperationCanceledException ? "超时" : error is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}" : "传输或图片格式异常")}"); }
            }
            return null;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task<bool> ValidIconAsync(string path, Func<string, bool>? validate, CancellationToken token)
    {
        try { return new FileInfo(path).Length > 0 && (validate is null || await Task.Run(() => validate(path), token).ConfigureAwait(false)); }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException) { return false; }
    }
    public void Dispose() { _cancellation.Dispose(); Http.Dispose(); }
    private sealed class SourceHandler(SourcePolicy policy, CancellationToken lifetime, Action<string> source, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        private readonly ConcurrentDictionary<string, (Uri Node, DateTimeOffset Expires)> _nodes = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime, token);
            var original = request.RequestUri!;
            var mirror = request.Headers.Contains("X-IKun-Official") ? null : Mirror(original, policy);
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cts.Token);
            async Task<HttpResponseMessage> Send(Uri uri, int redirects = 0, string? provider = null, string? route = null)
            {
                using var copy = new HttpRequestMessage(request.Method, uri);
                foreach (var header in request.Headers.Where(h => !h.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) && h.Key is not ("X-IKun-Official" or "X-IKun-Metadata"))) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (body is not null) { copy.Content = new ByteArrayContent(body); foreach (var h in request.Content!.Headers) copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value); }
                if (uri.Host == "api.curseforge.com")
                {
                    if (string.IsNullOrWhiteSpace(policy.CurseForgeKey)) throw new InvalidOperationException("CurseForge 来源不可用：请配置 API Key");
                    copy.Headers.TryAddWithoutValidation("x-api-key", policy.CurseForgeKey);
                }
                copy.Options.Set(DownloadConnectionBudget.ResponseTimeout, TimeSpan.FromSeconds(request.Headers.Contains("X-IKun-Metadata") ? 8 : 25));
                var result = await base.SendAsync(copy, cts.Token).ConfigureAwait(false);
                if ((int)result.StatusCode is 301 or 302 or 303 or 307 or 308 && result.Headers.Location is { } location)
                {
                    if (redirects >= 8) { result.Dispose(); throw new HttpRequestException("下载重定向过多"); }
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    result.Dispose();
                    if (next.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("下载源返回非 HTTPS 地址");
                    return await Send(next, redirects + 1, provider, route);
                }
                if (route is not null && redirects > 0 && result.IsSuccessStatusCode && _nodes.Count < 512)
                    _nodes[route] = (uri, DateTimeOffset.UtcNow.AddMinutes(1));
                source(provider is not null ? provider + (uri.Host is "bmclapi2.bangbang93.com" or "mod.mcimirror.top" ? "" : " · " + uri.Host) : uri.Host == "bmclapi2.bangbang93.com" ? "BMCLAPI" : uri.Host == "mod.mcimirror.top" ? "MCIM" : uri.Host);
                return result;
            }
            if (mirror is not null)
            {
                var provider = mirror.Contains("mcimirror.top") ? "MCIM" : mirror.Contains("tuna.tsinghua.edu.cn") ? "清华镜像" : "BMCLAPI";
                var route = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head ? mirror : null;
                if (route is not null && !request.Headers.Contains("X-IKun-Metadata") && _nodes.TryGetValue(route, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                {
                    try
                    {
                        var result = await Send(cached.Node, provider: provider);
                        if (result.IsSuccessStatusCode) return result;
                        result.Dispose();
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested) { }
                    _nodes.TryRemove(route, out _);
                }
                try
                {
                    var result = await Send(new Uri(mirror), provider: provider, route: route);
                    if (result.IsSuccessStatusCode) return result;
                    result.Dispose();
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cts.IsCancellationRequested) { }
            }
            return await Send(original);
        }
    }
}

public static class DownloadHash
{
    public static async Task<bool> MatchesAsync(string file, string? sha1, string? sha512, CancellationToken token)
    {
        if (!File.Exists(file)) return false;
        if (string.IsNullOrWhiteSpace(sha1) && string.IsNullOrWhiteSpace(sha512)) return true;
        await using var input = File.OpenRead(file);
        var bytes = sha512 is not null ? await SHA512.HashDataAsync(input, token) : await SHA1.HashDataAsync(input, token);
        return Convert.ToHexString(bytes).Equals(sha512 ?? sha1, StringComparison.OrdinalIgnoreCase);
    }
}
