using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Launcher.App.Services;
using Launcher.Core;
using SkiaSharp;
using Xunit;

namespace Launcher.Tests;

public sealed class AccountSkinTests
{
    private static byte[] Texture(int height = 64)
    {
        using var bitmap = new SKBitmap(64, height);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
        using var face = new SKPaint { Color = SKColors.Red }; canvas.DrawRect(8, 8, 8, 8, face);
        using var hat = new SKPaint { Color = SKColors.Blue }; canvas.DrawRect(40, 8, 4, 8, hat);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100); return png.ToArray();
    }
    [Fact] public void PngValidation_And_HatLayer_And_LegacyModel()
    {
        var texture = Texture(); AccountSkinService.Validate(texture);
        using var head = SKBitmap.Decode(AccountSkinService.CreateHead(texture));
        Assert.Equal(SKColors.Blue, head.GetPixel(1, 1)); Assert.Equal(SKColors.Red, head.GetPixel(6, 1));
        AccountSkinService.Validate(Texture(32));
        Assert.Throws<InvalidDataException>(() => AccountSkinService.Validate(Encoding.UTF8.GetBytes("broken")));
        using var oversized = new SKBitmap(128, 128); using var encoded = oversized.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Throws<InvalidDataException>(() => AccountSkinService.Validate(encoded.ToArray()));
    }
    [Fact] public void Profiles_ReadTexturesByName_And_OptionalModel_And_ActiveAuthenticatedSkin()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"textures\":{\"SKIN\":{\"url\":\"https://textures.minecraft.net/a\"}}}"));
        var json = JsonSerializer.SerializeToUtf8Bytes(new { properties = new[] { new { name = "unrelated", value = "notbase64" }, new { name = "textures", value = encoded } } });
        Assert.Equal(("https://textures.minecraft.net/a", SkinModel.Classic), AccountSkinService.ParseProfile(json, false));
        Assert.Equal(("active", SkinModel.Slim), AccountSkinService.ParseProfile(Encoding.UTF8.GetBytes("{\"skins\":[{\"state\":\"INACTIVE\",\"url\":\"old\"},{\"state\":\"ACTIVE\",\"url\":\"active\",\"variant\":\"SLIM\"}]}"), true));
        Assert.Equal("角色名", new LittleSkinProfile("角色名", "id").ToString());
    }
    [Fact] public async Task Offline_Apply_IsPersistent_And_MetadataUsesRelativePaths()
    {
        using var fixture = new Fixture(); var account = AccountService.Offline("SkinTest");
        using (var service = fixture.Service())
        {
            Assert.True((await service.GetCachedAsync(account)).IsDefault);
            var imported = await service.ImportAsync(fixture.Image, default);
            await service.ApplyOfflineAsync(account, imported with { Model = SkinModel.Slim }, default);
        }
        using (var restarted = fixture.Service())
        {
            var cached = await restarted.GetCachedAsync(account);
            Assert.False(cached.IsDefault); Assert.Equal(SkinModel.Slim, cached.Model);
            Assert.False(Path.IsPathRooted(cached.Texture)); Assert.False(Path.IsPathRooted(cached.Head));
            Assert.Equal(Texture(), await File.ReadAllBytesAsync(restarted.TextureFile(cached)));
            var legacy = Path.Combine(fixture.Root, "legacy.png"); await File.WriteAllBytesAsync(legacy, Texture(32));
            Assert.Equal(SkinModel.Classic, (await restarted.ImportAsync(legacy, default)).Model);
        }
    }
    [Fact] public async Task Requests_Merge_And_Cancel_And_CorruptCacheRedownloads()
    {
        using var fixture = new Fixture();
        var account = new AccountProfile { Kind = AccountKind.LittleSkin, Uuid = "069a79f444e94726a5befca90e38aaf5" };
        var requests = 0;
        using (var service = fixture.Service(new Handler(async (request, token) =>
        {
            Assert.Null(request.Headers.Authorization);
            Interlocked.Increment(ref requests); await Task.Delay(80, token);
            if (request.RequestUri!.Host == "littleskin.cn") return Profile();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Texture()) };
        })))
        {
            var results = await Task.WhenAll(service.RefreshAsync(account, true, default), service.RefreshAsync(account, true, default));
            Assert.Equal(2, requests); Assert.Equal(results[0], results[1]);
            await service.RefreshAsync(account, false, default); Assert.Equal(2, requests);
            await File.WriteAllTextAsync(service.TextureFile(results[0]), "corrupt");
        }
        using (var service = fixture.Service(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host == "littleskin.cn" ? Profile() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Texture()) }))))
        {
            Assert.True((await service.GetCachedAsync(account)).IsDefault);
            Assert.False((await service.RefreshAsync(account, true, default)).IsDefault);
        }
        var began = new TaskCompletionSource(); var cancelled = new TaskCompletionSource();
        using (var service = fixture.Service(new Handler(async (_, token) =>
        {
            began.SetResult(); try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { cancelled.SetResult(); throw; } return Profile();
        })))
        {
            using var cts = new CancellationTokenSource(); var pending = service.RefreshAsync(account, true, cts.Token);
            await began.Task; cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); await service.CancelAndWaitAsync();
            Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        }
    }
    private static HttpResponseMessage Profile()
    {
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"textures\":{\"SKIN\":{\"url\":\"https://textures.minecraft.net/test\",\"metadata\":{\"model\":\"slim\"}}}}"));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { properties = new[] { new { name = "textures", value } } })) };
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token); }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ikun-skins-" + Guid.NewGuid());
        public string Image => Path.Combine(Root, "skin.png");
        private readonly HttpClient _http = new(); private readonly LogService _log;
        private readonly AccountService _accounts;
        public Fixture() { Directory.CreateDirectory(Root); File.WriteAllBytes(Image, Texture()); _log = new LogService(Root); _accounts = new AccountService(_http, new SecretStore(Root), _log); }
        public AccountSkinService Service(HttpMessageHandler? handler = null) => new(_accounts, Path.Combine(Root, "skins"), handler, Image);
        public void Dispose() { _log.Dispose(); _http.Dispose(); Directory.Delete(Root, true); }
    }
}
