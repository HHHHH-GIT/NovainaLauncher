namespace Launcher.Core;

public enum SkinModel { Classic, Slim }

public sealed record AccountSkinInfo(string Texture, string Head, SkinModel Model, DateTimeOffset Updated, bool IsDefault = false);

public interface IAccountSkinService
{
    event Action<string>? SkinChanged;
    Task<AccountSkinInfo> GetCachedAsync(AccountProfile? account, CancellationToken token = default);
    Task<AccountSkinInfo> RefreshAsync(AccountProfile account, bool force, CancellationToken token);
    Task<AccountSkinInfo> ImportAsync(string file, CancellationToken token);
    Task<AccountSkinInfo> ApplyOfflineAsync(AccountProfile account, AccountSkinInfo skin, CancellationToken token);
    Task<AccountSkinInfo> UploadMicrosoftAsync(AccountProfile account, AccountSkinInfo skin, string clientId, CancellationToken token);
    Task ExportAsync(AccountSkinInfo skin, string file, CancellationToken token);
    string TextureFile(AccountSkinInfo skin);
    string HeadFile(AccountSkinInfo skin);
    Task CancelAndWaitAsync();
}
