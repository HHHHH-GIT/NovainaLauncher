using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using Microsoft.Identity.Client;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.Msal.OAuth;

namespace Launcher.Core;

public sealed partial class AccountService(HttpClient http, SecretStore secrets, LogService log)
{
    private const string SkinApi = "https://littleskin.cn/api/yggdrasil/";
    private readonly object _profilesGate = new();
    public event Action<AccountProfile>? ProfileUpdated;
    public IReadOnlyList<AccountProfile> Load() { lock (_profilesGate) return secrets.ReadJson<List<AccountProfile>>("profiles") ?? []; }
    public void Save(IEnumerable<AccountProfile> profiles) { lock (_profilesGate) secrets.WriteJson("profiles", profiles.ToList()); }
    public static AccountProfile Offline(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{1,16}$")) throw new InvalidOperationException("玩家名需为 1–16 位字母、数字或下划线");
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new() { Kind = AccountKind.Offline, Name = name, Uuid = Convert.ToHexString(bytes).ToLowerInvariant() };
    }
    private (JELoginHandler Handler, IPublicClientApplication App) MicrosoftHandler(string clientId, Func<DeviceLoginInfo, Task> callback)
    {
        if (!Guid.TryParse(clientId, out _)) throw new InvalidOperationException("微软 Client ID 无效");
        var app = MsalClientHelper.BuildApplication(clientId);
        var cacheKey = "msal-" + clientId;
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            if (secrets.Read(cacheKey) is { } data) args.TokenCache.DeserializeMsalV3(data);
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged) secrets.Write(cacheKey, args.TokenCache.SerializeMsalV3());
        });
        var manager = new JsonXboxGameAccountManager(new ProtectedJsonStorage(secrets, "microsoft-" + clientId), JEGameAccount.FromSessionStorage, JsonXboxGameAccountManager.DefaultSerializerOption);
        var provider = new MsalDeviceCodeProvider(app, result => callback(new(result.UserCode, result.VerificationUrl, result.ExpiresOn)));
        return (new JELoginHandlerBuilder().WithAccountManager(manager).WithOAuthProvider(provider).Build(), app);
    }
    public async Task<AccountProfile> LoginMicrosoftAsync(string clientId, Func<DeviceLoginInfo, Task> callback, CancellationToken token)
    {
        var (handler, _) = MicrosoftHandler(clientId, callback);
        var session = await handler.AuthenticateInteractively(token);
        log.RegisterSecret(session.AccessToken);
        return new() { Kind = AccountKind.Microsoft, Name = session.Username!, Uuid = session.UUID!, LoginIdentifier = clientId };
    }
    public async Task<LittleSkinLogin> LoginLittleSkinAsync(string email, string password, CancellationToken token)
    {
        log.RegisterSecret(password);
        using var response = await PostSkinAsync("authenticate", new { agent = new { name = "Minecraft", version = 1 }, username = email, password, clientToken = Guid.NewGuid().ToString("N"), requestUser = true }, false, token);
        var json = await ReadSkinResponse(response, token, passwordLogin: true);
        var access = json.GetProperty("accessToken").GetString()!; var client = json.GetProperty("clientToken").GetString()!;
        log.RegisterSecret(access); log.RegisterSecret(client);
        var profiles = json.TryGetProperty("availableProfiles", out var available) ? available.EnumerateArray().Select(p => new LittleSkinProfile(p.GetProperty("name").GetString()!, p.GetProperty("id").GetString()!)).ToList() : new List<LittleSkinProfile>();
        var selected = ReadSelectedProfile(json);
        if (profiles.Count == 0 && selected is not null) profiles.Add(selected);
        if (profiles.Count == 0) throw new InvalidOperationException("LittleSkin 账户还没有角色");
        return new(access, client, profiles) { SelectedProfile = selected };
    }
    public async Task<AccountProfile> SelectLittleSkinAsync(string email, LittleSkinLogin login, LittleSkinProfile profile, CancellationToken token)
    {
        if (!login.Profiles.Any(p => SameUuid(p.Id, profile.Id))) throw new InvalidOperationException("请选择本次登录返回的角色");
        if (login.SelectedProfile is { } bound && !SameUuid(bound.Id, profile.Id)) throw new InvalidOperationException("令牌已绑定其他角色，请重新登录后选择");
        var existing = Load().FirstOrDefault(a => a.Kind == AccountKind.LittleSkin && SameUuid(a.Uuid, profile.Id));
        var account = new AccountProfile { Id = existing?.Id ?? Guid.NewGuid().ToString("N"), Kind = AccountKind.LittleSkin, Name = profile.Name, Uuid = profile.Id, LoginIdentifier = email };
        var gate = SkinGate(account); await gate.WaitAsync(token);
        try
        {
            // authenticate can already bind a single profile. Only an unbound token needs selection.
            var credentials = new LittleSkinTokens(login.AccessToken, login.ClientToken) { LastRenewedUtc = DateTimeOffset.UtcNow, ProfileName = profile.Name };
            if (login.SelectedProfile is null)
                credentials = await RefreshSkinAsync(account, credentials, profile, token);
            StoreSkinTokens(account, credentials);
            token.ThrowIfCancellationRequested();
            return account with { Name = credentials.ProfileName ?? account.Name };
        }
        finally { gate.Release(); }
    }
    public async Task<MSession> SessionAsync(AccountProfile account, string configuredClientId, CancellationToken token)
    {
        if (account.Kind == AccountKind.Offline)
            return new() { Username = account.Name, UUID = account.Uuid, AccessToken = "0", UserType = "mojang" };
        if (account.Kind == AccountKind.Microsoft)
        {
            // A cached profile belongs to the application that authenticated it.
            var id = string.IsNullOrEmpty(account.LoginIdentifier) ? configuredClientId : account.LoginIdentifier;
            var (handler, _) = MicrosoftHandler(id, _ => throw new AccountLoginRequiredException("微软登录已失效，请在账户页重新登录"));
            if (!handler.AccountManager.GetAccounts().TryGetAccount(account.Uuid, out var cached)) throw new AccountLoginRequiredException("请重新登录 Microsoft 账户");
            var session = await handler.AuthenticateSilently(cached!, token);
            log.RegisterSecret(session.AccessToken); return session;
        }
        return await SkinSessionAsync(account, false, token);
    }
    public async Task RemoveAsync(AccountProfile account, CancellationToken token)
    {
        if (account.Kind == AccountKind.LittleSkin)
        {
            var gate = SkinGate(account); await gate.WaitAsync(token);
            try
            {
                if (secrets.ReadJson<LittleSkinTokens>("skin-" + account.Id) is { } credentials)
                {
                    try { using var response = await PostSkinAsync("invalidate", new { accessToken = credentials.AccessToken, clientToken = credentials.ClientToken }, false, token); }
                    catch (HttpRequestException) { }
                }
                secrets.Delete("skin-" + account.Id);
            }
            finally { gate.Release(); }
        }
        if (account.Kind == AccountKind.Microsoft)
        {
            var (handler, app) = MicrosoftHandler(account.LoginIdentifier, _ => Task.CompletedTask);
            if (handler.AccountManager.GetAccounts().TryGetAccount(account.Uuid, out var cached)) await handler.Signout(cached!, token);
            var storage = new ProtectedJsonStorage(secrets, "microsoft-" + account.LoginIdentifier);
            var node = storage.ReadAsJsonNode() as JsonObject;
            if (node is not null) { node.Remove(account.Uuid); storage.Write(node, null); }
            // Clear the OAuth cache as well, so a removed account cannot silently sign back in.
            foreach (var msalAccount in await app.GetAccountsAsync()) await app.RemoveAsync(msalAccount);
        }
    }
}
