using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Launcher.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Launcher.App.Controls;

public sealed class SkinPreview : Grid
{
    public static readonly DependencyProperty TexturePathProperty = DependencyProperty.Register(nameof(TexturePath), typeof(string), typeof(SkinPreview), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(SkinModel), typeof(SkinPreview), new PropertyMetadata(SkinModel.Classic, Changed));
    public static readonly DependencyProperty PausedProperty = DependencyProperty.Register(nameof(Paused), typeof(bool), typeof(SkinPreview), new PropertyMetadata(false, Changed));
    public string? TexturePath { get => (string?)GetValue(TexturePathProperty); set => SetValue(TexturePathProperty, value); }
    public SkinModel Model { get => (SkinModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public bool Paused { get => (bool)GetValue(PausedProperty); set => SetValue(PausedProperty, value); }
    private readonly TextBlock _status = new() { Text = "正在准备预览", TextAlignment = TextAlignment.Center };
    private readonly Button _install = new() { Content = "安装 WebView2", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private WebView2CompositionControl? _browser;
    private Window? _window;
    private Task? _initialization;
    private bool _ready;
    private int _update;
    public SkinPreview()
    {
        MinHeight = 240; ClipToBounds = true;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _install.SetResourceReference(StyleProperty, "AppleSecondaryButton");
        var placeholder = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        placeholder.Children.Add(_status); placeholder.Children.Add(_install); Children.Add(placeholder);
        _install.Click += (_, _) => Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/#download-section") { UseShellExecute = true });
        Loaded += (_, _) =>
        {
            MotionPolicy.Changed += MotionChanged;
            SystemParameters.StaticPropertyChanged += SystemChanged;
            _window = Window.GetWindow(this);
            if (_window is not null) { _window.StateChanged += WindowChanged; _window.Closed -= WindowClosed; _window.Closed += WindowClosed; }
            Update();
        };
        Unloaded += (_, _) => { Unsubscribe(); _ = SendAsync(); };
        IsVisibleChanged += (_, _) => Update();
    }
    private void Unsubscribe()
    {
        MotionPolicy.Changed -= MotionChanged; SystemParameters.StaticPropertyChanged -= SystemChanged;
        if (_window is not null) { _window.StateChanged -= WindowChanged; /* Keep Closed until disposal, including when the cached page is hidden. */ }
    }
    private void WindowClosed(object? sender, EventArgs e) { Unsubscribe(); if (_window is not null) _window.Closed -= WindowClosed; _browser?.Dispose(); _ready = false; }
    private void WindowChanged(object? sender, EventArgs e) => Update();
    private void MotionChanged() => Update();
    private void SystemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Update(); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SkinPreview)d).Update();
    private void Update()
    {
        if (IsLoaded && IsVisible && _window?.IsVisible == true && _initialization is null) _initialization = InitializeAsync();
        _ = SendAsync();
    }
    private async Task InitializeAsync()
    {
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Data, "skins", "webview"));
            _browser = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent };
            Children.Insert(0, _browser);
            await _browser.EnsureCoreWebView2Async(environment);
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _browser.CoreWebView2.SetVirtualHostNameToFolderMapping("skins.ikun.local", Services.BundledAssets.SkinViewerDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            _browser.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://skins.ikun.local/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
            _browser.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            _browser.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var state = JsonDocument.Parse(e.WebMessageAsJson);
                    if (state.RootElement.TryGetProperty("ready", out var ready)) { _ready = true; _status.Visibility = Visibility.Collapsed; Update(); }
                    else if (state.RootElement.TryGetProperty("error", out var error)) { _status.Text = "皮肤渲染失败"; _status.Visibility = Visibility.Visible; }
                }
                catch (JsonException) { }
            };
            _browser.CoreWebView2.Navigate("https://skins.ikun.local/index.html");
        }
        catch (WebView2RuntimeNotFoundException) { _status.Text = "预览需要 WebView2"; _install.Visibility = Visibility.Visible; }
        catch (Exception) { _status.Text = "暂时无法初始化 3D 预览"; }
    }
    private async Task SendAsync()
    {
        var id = ++_update;
        if (!_ready || _browser?.CoreWebView2 is null) return;
        try
        {
            var path = TexturePath;
            var texture = path is null ? null : "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path));
            if (id != _update || !_ready) return;
            _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                texture, model = Model == SkinModel.Slim ? "slim" : "default",
                active = IsLoaded && IsVisible && !Paused && _window?.WindowState != WindowState.Minimized,
                animated = MotionPolicy.Effective != UiAnimationMode.Off, calm = MotionPolicy.Effective == UiAnimationMode.Calm
            }));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
    public void ResetCamera() { if (_ready) _browser?.CoreWebView2.PostWebMessageAsJson("{\"reset\":true}"); }
    public async Task StopAsync()
    {
        if (_initialization is { } initializing) await initializing;
        _ready = false;
        if (_browser is not null)
        {
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var environment = _browser.CoreWebView2?.Environment;
            void BrowserExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e) => exited.TrySetResult();
            if (environment is not null) environment.BrowserProcessExited += BrowserExited;
            Children.Remove(_browser); _browser.Dispose(); _browser = null;
            _initialization = null;
            try { if (environment is not null) await exited.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
            finally { if (environment is not null) environment.BrowserProcessExited -= BrowserExited; }
        }
        _initialization = null; _status.Text = "正在准备预览"; _status.Visibility = Visibility.Visible;
    }
}
