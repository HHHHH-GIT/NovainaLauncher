using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CmlLib.Core.Auth;

namespace Launcher.Core;

public sealed partial class AccountService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _skinGates = new(StringComparer.Ordinal);
    private static readonly TimeSpan SkinRenewalInterval = TimeSpan.FromHours(12);
    private SemaphoreSlim SkinGate(AccountProfile account) => _skinGates.GetOrAdd(account.Id, _ => new(1, 1));
    private static bool SameUuid(string left, string right) => left.Replace("-", "").Equals(right.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

    // Silent recovery only. Never store or replay the user's password.
    public Task<MSession> RetryLoginAsync(AccountProfile account, string clientId, CancellationToken token) =>
        account.Kind == AccountKind.LittleSkin ? SkinSessionAsync(account, true, token) : SessionAsync(account, clientId, token);

    public bool IsLittleSkinRenewalDue(AccountProfile account)
    {
        if (account.Kind != AccountKind.LittleSkin) return false;
        var saved = secrets.ReadJson<LittleSkinTokens>("skin-" + account.Id);
        return saved is not null && DateTimeOffset.UtcNow - saved.LastRenewedUtc >= SkinRenewalInterval;
    }

    private async Task<MSession> SkinSessionAsync(AccountProfile account, bool retryLogin, CancellationToken token)
    {
        var gate = SkinGate(account); await gate.WaitAsync(token);
        try
        {
            // Read after acquiring the lock: another launch/renewal may have rotated the token.
            var credentials = secrets.ReadJson<LittleSkinTokens>("skin-" + account.Id)
                ?? throw new AccountLoginRequiredException("LittleSkin 登录凭据不存在，请在账户页重新登录");
            log.RegisterSecret(credentials.AccessToken); log.RegisterSecret(credentials.ClientToken);
            var valid = await ValidateSkinAsync(credentials, token);
            var age = DateTimeOffset.UtcNow - credentials.LastRenewedUtc;
            // Collapse immediate retries into one renewal, while always recovering invalid tokens.
            if (!valid || age >= SkinRenewalInterval || retryLogin && age >= TimeSpan.FromMinutes(1))
            {
                try
                {
                    // Ordinary renewal preserves the bound profile: selectedProfile MUST be omitted.
                    credentials = await RefreshSkinAsync(account, credentials, null, token);
                    StoreSkinTokens(account, credentials);
                    log.Write($"LittleSkin 登录已自动续期：{credentials.ProfileName ?? account.Name}");
                }
                catch (HttpRequestException e) when (valid && e.StatusCode is { } status && IsTransient(status))
                {
                    // A rejected renewal leaves the old validated token intact. A lost response is
                    // different: never blindly repeat a rotating request or assume its old token works.
                    log.Write("LittleSkin 续期服务暂不可用，保留已验证的登录状态", LogLevel.Warning);
                }
            }
            token.ThrowIfCancellationRequested();
            return new() { Username = credentials.ProfileName ?? account.Name, UUID = account.Uuid, AccessToken = credentials.AccessToken, UserType = "mojang" };
        }
        finally { gate.Release(); }
    }

    private async Task<bool> ValidateSkinAsync(LittleSkinTokens credentials, CancellationToken token)
    {
        using var response = await PostSkinAsync("validate", new { accessToken = credentials.AccessToken, clientToken = credentials.ClientToken }, true, token);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return false;
        ThrowSkinError(response, false);
        return false;
    }

    private async Task<LittleSkinTokens> RefreshSkinAsync(AccountProfile account, LittleSkinTokens credentials, LittleSkinProfile? selection, CancellationToken token)
    {
        object payload = selection is null
            ? new { accessToken = credentials.AccessToken, clientToken = credentials.ClientToken, requestUser = true }
            : new { accessToken = credentials.AccessToken, clientToken = credentials.ClientToken, selectedProfile = new { id = selection.Id, name = selection.Name }, requestUser = true };
        using var response = await PostSkinAsync("refresh", payload, false, token);
        // PostAsJsonAsync already buffered the response. Once rotation succeeded, persist its new
        // credentials even if cancellation arrived in the meantime; the old token is now revoked.
        var json = await ReadSkinResponse(response, CancellationToken.None);
        var selected = ReadSelectedProfile(json);
        var access = json.GetProperty("accessToken").GetString(); var client = json.GetProperty("clientToken").GetString();
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(client) || client != credentials.ClientToken)
            throw new InvalidDataException("LittleSkin 返回的登录凭据不完整或不匹配，请重试登录");
        log.RegisterSecret(access); log.RegisterSecret(client);
        if (selected is null || !SameUuid(selected.Id, account.Uuid))
            throw new AccountLoginRequiredException("LittleSkin 返回的角色与当前账户不符，请在账户页重新登录");
        return new(access, client) { LastRenewedUtc = DateTimeOffset.UtcNow, ProfileName = selected.Name };
    }

    private void StoreSkinTokens(AccountProfile account, LittleSkinTokens credentials)
    {
        log.RegisterSecret(credentials.AccessToken); log.RegisterSecret(credentials.ClientToken);
        secrets.WriteJson("skin-" + account.Id, credentials);
        if (credentials.ProfileName is not { Length: > 0 } name || name == account.Name) return;
        AccountProfile? updated = null;
        lock (_profilesGate)
        {
            var profiles = Load().ToList(); var index = profiles.FindIndex(a => a.Id == account.Id);
            if (index >= 0 && profiles[index].Name != name)
            {
                updated = profiles[index] with { Name = name }; profiles[index] = updated; Save(profiles);
            }
        }
        if (updated is not null) ProfileUpdated?.Invoke(updated);
    }

    private static LittleSkinProfile? ReadSelectedProfile(JsonElement json) =>
        json.TryGetProperty("selectedProfile", out var selected) && selected.ValueKind == JsonValueKind.Object
            ? new(selected.GetProperty("name").GetString()!, selected.GetProperty("id").GetString()!) : null;

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    private async Task<HttpResponseMessage> PostSkinAsync(string endpoint, object body, bool retrySafe, CancellationToken token)
    {
        // Only validate is idempotent. authenticate/refresh are never blindly replayed.
        var attempts = retrySafe ? 3 : 1;
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            TimeSpan delay = TimeSpan.FromMilliseconds(350 * (attempt + 1));
            try
            {
                var response = await http.PostAsJsonAsync(SkinApi + "authserver/" + endpoint, body, timeout.Token);
                if (!IsTransient(response.StatusCode) || attempt + 1 >= attempts) return response;
                var after = response.Headers.RetryAfter;
                var requested = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : delay);
                // Never hammer a rate-limited service sooner than its requested retry time.
                if (requested > TimeSpan.FromSeconds(3)) return response;
                delay = requested > TimeSpan.Zero ? requested : delay;
                response.Dispose();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (attempt + 1 >= attempts) throw new HttpRequestException("LittleSkin 响应超时，请检查网络后重试");
            }
            catch (HttpRequestException)
            {
                if (attempt + 1 >= attempts) throw new HttpRequestException("LittleSkin 网络连接失败，请检查网络后重试");
            }
            await Task.Delay(delay, token);
        }
    }

    private static void ThrowSkinError(HttpResponseMessage response, bool passwordLogin)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            if (passwordLogin) throw new InvalidOperationException("LittleSkin 邮箱或密码错误，或登录暂时受限");
            throw new AccountLoginRequiredException("LittleSkin 登录已过期或被撤销，自动续期失败，请在账户页重新登录");
        }
        if (IsTransient(response.StatusCode))
            throw new HttpRequestException(response.StatusCode == HttpStatusCode.TooManyRequests ? "LittleSkin 请求受限，请稍后重试" : "LittleSkin 服务暂不可用，请稍后重试", null, response.StatusCode);
        throw new InvalidOperationException($"LittleSkin 认证请求失败（HTTP {(int)response.StatusCode}），请重试登录");
    }

    private static async Task<JsonElement> ReadSkinResponse(HttpResponseMessage response, CancellationToken token, bool passwordLogin = false)
    {
        ThrowSkinError(response, passwordLogin);
        try { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); return json.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidDataException("LittleSkin 返回的数据不完整，请稍后重试"); }
    }
}
