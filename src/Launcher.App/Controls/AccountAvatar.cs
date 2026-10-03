using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core;

namespace Launcher.App.Controls;

public sealed class AccountAvatar : Grid
{
    public static readonly DependencyProperty AccountProperty = DependencyProperty.Register(nameof(Account), typeof(AccountProfile), typeof(AccountAvatar), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ServiceProperty = DependencyProperty.Register(nameof(Service), typeof(IAccountSkinService), typeof(AccountAvatar), new PropertyMetadata(null, Changed));
    public AccountProfile? Account { get => (AccountProfile?)GetValue(AccountProperty); set => SetValue(AccountProperty, value); }
    public IAccountSkinService? Service { get => (IAccountSkinService?)GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private CancellationTokenSource? _cancellation;
    private IAccountSkinService? _listening;
    public AccountAvatar()
    {
        Background = Brushes.Transparent; Children.Add(_image);
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        Loaded += (_, _) => Restart(); Unloaded += (_, _) => { _cancellation?.Cancel(); Subscribe(null); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Restart(); else _cancellation?.Cancel(); };
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); Clip = new RectangleGeometry(new Rect(RenderSize), 8, 8); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AccountAvatar)d).Restart();
    private void Subscribe(IAccountSkinService? service)
    {
        if (_listening == service) return;
        if (_listening is not null) _listening.SkinChanged -= Updated;
        _listening = service; if (_listening is not null) _listening.SkinChanged += Updated;
    }
    private void Updated(string id) => Dispatcher.InvokeAsync(() => { if (Account?.Id == id && IsLoaded) Restart(); });
    private void Restart()
    {
        _cancellation?.Cancel(); Subscribe(IsLoaded ? Service : null);
        if (!IsLoaded || !IsVisible || Service is null) return;
        var cts = new CancellationTokenSource(); _cancellation = cts; _ = LoadAsync(Service, Account, cts);
    }
    private async Task LoadAsync(IAccountSkinService service, AccountProfile? account, CancellationTokenSource cts)
    {
        try
        {
            var cached = await service.GetCachedAsync(account, cts.Token); await DisplayAsync(service, cached, cts.Token);
            if (account is not null)
            {
                var updated = await service.RefreshAsync(account, false, cts.Token);
                if (cached.Head != updated.Head) await DisplayAsync(service, updated, cts.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException or System.Net.Http.HttpRequestException or TimeoutException or System.Text.Json.JsonException or FormatException) { }
        finally { if (_cancellation == cts) _cancellation = null; cts.Dispose(); }
    }
    private async Task DisplayAsync(IAccountSkinService service, AccountSkinInfo skin, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(service.HeadFile(skin), token); token.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        _image.Source = image;
    }
}
