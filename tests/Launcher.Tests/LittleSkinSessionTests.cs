using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class LittleSkinSessionTests
{
    private const string Uuid = "069a79f444e94726a5befca90e38aaf5";
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Renew(string name = "NewName", string access = "new-token", string uuid = Uuid) => Json(new { accessToken = access, clientToken = "client-token", selectedProfile = new { id = uuid, name } });

    [Fact]
    public async Task Bound_Login_Does_Not_Select_Profile_Again_And_Preserves_Account_Id()
    {
        using var fixture = new Fixture((r, t) => Task.FromResult(Json(new { accessToken = "first-token", clientToken = "client-token", availableProfiles = new[] { new { id = Uuid, name = "Player" } }, selectedProfile = new { id = Uuid, name = "Player" } })));
        fixture.Service.Save([fixture.Account]);
        var login = await fixture.Service.LoginLittleSkinAsync("email", "fixture-password", default);
        var account = await fixture.Service.SelectLittleSkinAsync("email", login, login.Profiles[0], default);
        Assert.Equal(fixture.Account.Id, account.Id);
        Assert.Equal(new[] { "authenticate" }, fixture.Calls.ToArray());
        Assert.Equal("first-token", fixture.Tokens!.AccessToken);
        Assert.True(fixture.Tokens.LastRenewedUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Unbound_Login_Selects_Once_And_Cancellation_After_Rotation_Still_Persists()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture((r, t) => Task.FromResult(Renew()));
        fixture.Service.Save([fixture.Account]);
        fixture.Service.ProfileUpdated += _ => cancellation.Cancel();
        var login = new LittleSkinLogin("old-token", "client-token", [new("Player", Uuid)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SelectLittleSkinAsync("email", login, login.Profiles[0], cancellation.Token));
        Assert.Equal("new-token", fixture.Tokens!.AccessToken);
        Assert.True(fixture.Bodies.Single().TryGetProperty("selectedProfile", out _));
    }

    [Fact]
    public async Task Invalid_Token_Refresh_Omits_Profile_And_Updates_Renamed_Role()
    {
        using var fixture = new Fixture((r, t) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("validate") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Renew()));
        fixture.Service.Save([fixture.Account]); fixture.Seed();
        var session = await fixture.Service.SessionAsync(fixture.Account, "", default);
        Assert.Equal("new-token", session.AccessToken); Assert.Equal("NewName", session.Username);
        Assert.False(fixture.Bodies.Last().TryGetProperty("selectedProfile", out _));
        Assert.Equal("NewName", fixture.Service.Load().Single().Name);
        Assert.Equal(fixture.Account.Id, fixture.Service.Load().Single().Id);
    }

    [Fact]
    public async Task Proactive_Renewal_And_Concurrent_Retries_Do_Not_Revoke_Each_Others_Tokens()
    {
        int refreshes = 0;
        using var fixture = new Fixture(async (r, t) => { await Task.Delay(20, t); if (r.RequestUri!.AbsolutePath.EndsWith("validate")) return new(HttpStatusCode.NoContent); Interlocked.Increment(ref refreshes); return Renew("Player"); });
        fixture.Seed(DateTimeOffset.UtcNow.AddHours(-13));
        var sessions = await Task.WhenAll(fixture.Service.SessionAsync(fixture.Account, "", default), fixture.Service.RetryLoginAsync(fixture.Account, "", default), fixture.Service.RetryLoginAsync(fixture.Account, "", default));
        Assert.Equal(1, refreshes); Assert.All(sessions, s => Assert.Equal("new-token", s.AccessToken));
        Assert.False(fixture.Service.IsLittleSkinRenewalDue(fixture.Account));
        Assert.Equal("new-token", fixture.Bodies.Last().GetProperty("accessToken").GetString());
    }

    [Fact]
    public async Task Transient_Verification_Recovers_Without_Claiming_Password_Expired()
    {
        int requests = 0;
        using var fixture = new Fixture((r, t) => Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref requests) < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent)));
        fixture.Seed(DateTimeOffset.UtcNow);
        Assert.Equal("old-token", (await fixture.Service.SessionAsync(fixture.Account, "", default)).AccessToken);
        Assert.Equal(3, requests); Assert.All(fixture.Calls, c => Assert.Equal("validate", c));
    }

    [Fact]
    public async Task Revoked_Token_Requires_Human_Login_Without_Deleting_Saved_Credentials()
    {
        using var fixture = new Fixture((r, t) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        fixture.Seed();
        await Assert.ThrowsAsync<AccountLoginRequiredException>(() => fixture.Service.RetryLoginAsync(fixture.Account, "", default));
        Assert.Equal(new[] { "validate", "refresh" }, fixture.Calls.ToArray());
        Assert.Equal("old-token", fixture.Tokens!.AccessToken);
    }

    [Fact]
    public async Task Rate_Limit_With_Long_Retry_After_Does_Not_Hammer_Or_Report_Expired()
    {
        using var fixture = new Fixture((r, t) => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromMinutes(1)); return Task.FromResult(response); });
        fixture.Seed();
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.SessionAsync(fixture.Account, "", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode); Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task Cancellation_Stops_Verification_And_Does_Not_Refresh()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (r, t) => { started.SetResult(); await Task.Delay(Timeout.Infinite, t); return new(HttpStatusCode.NoContent); });
        fixture.Seed(); using var cancellation = new CancellationTokenSource();
        var operation = fixture.Service.SessionAsync(fixture.Account, "", cancellation.Token); await started.Task;
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Single(fixture.Calls); Assert.Equal("old-token", fixture.Tokens!.AccessToken);
    }

    [Fact]
    public async Task Lost_Rotation_Response_Is_Not_Replayed_And_Cross_Role_Response_Is_Rejected()
    {
        using var fixture = new Fixture((r, t) => r.RequestUri!.AbsolutePath.EndsWith("validate") ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)) : throw new HttpRequestException("connection lost"));
        fixture.Seed(); await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.SessionAsync(fixture.Account, "", default));
        Assert.Equal(new[] { "validate", "refresh" }, fixture.Calls.ToArray());
        using var mismatch = new Fixture((r, t) => Task.FromResult(r.RequestUri!.AbsolutePath.EndsWith("validate") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Renew(uuid: "another-role")));
        mismatch.Seed(); await Assert.ThrowsAsync<AccountLoginRequiredException>(() => mismatch.Service.RetryLoginAsync(mismatch.Account, "", default));
        Assert.Equal("old-token", mismatch.Tokens!.AccessToken);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ikun-ls-auth-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly SecretStore _secrets;
        private readonly LogService _log;
        public AccountService Service { get; }
        public AccountProfile Account { get; } = new() { Kind = AccountKind.LittleSkin, Name = "Player", Uuid = Uuid, LoginIdentifier = "email" };
        public List<string> Calls { get; } = [];
        public List<JsonElement> Bodies { get; } = [];
        public LittleSkinTokens? Tokens => _secrets.ReadJson<LittleSkinTokens>("skin-" + Account.Id);
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _secrets = new(_root); _log = new(_root);
            _http = new(new Handler(async (r, t) => { Calls.Add(r.RequestUri!.Segments.Last()); using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(t)); Bodies.Add(body.RootElement.Clone()); return await respond(r, t); }));
            Service = new(_http, _secrets, _log);
        }
        public void Seed(DateTimeOffset time = default) => _secrets.WriteJson("skin-" + Account.Id, new LittleSkinTokens("old-token", "client-token") { LastRenewedUtc = time, ProfileName = "Player" });
        public void Dispose()
        {
            _http.Dispose(); _log.Dispose();
            if (Directory.Exists(_root) && Path.GetFullPath(_root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "ikun-ls-auth-", StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
        }
    }
}
