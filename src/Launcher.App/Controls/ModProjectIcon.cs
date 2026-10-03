using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core;
using SkiaSharp;
using ShapePath = System.Windows.Shapes.Path;

namespace Launcher.App.Controls;

/// <summary>Visible/recycled rows load small cached thumbnails without blocking layout.</summary>
public sealed class ModProjectIcon : Grid
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(nameof(Url), typeof(string), typeof(ModProjectIcon), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty PreferMirrorProperty = DependencyProperty.Register(nameof(PreferMirror), typeof(bool), typeof(ModProjectIcon), new PropertyMetadata(true, Changed));
    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public bool PreferMirror { get => (bool)GetValue(PreferMirrorProperty); set => SetValue(PreferMirrorProperty, value); }
    private static readonly ConcurrentDictionary<string, BitmapSource> Images = new();
    private static readonly SemaphoreSlim Slots = new(2);
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ShapePath _fallback;
    private CancellationTokenSource? _cancellation;
    private string? _displayedUrl;
    public bool HasIcon => _image.Source is not null;
    public ModProjectIcon()
    {
        Width = Height = 36; Background = new SolidColorBrush(Color.FromArgb(24, 0, 122, 255));
        _fallback = new() { Data = Geometry.Parse("M4,4 L10,4 C7,0 17,0 14,4 L20,4 L20,10 C24,7 24,17 20,14 L20,20 L14,20 C17,24 7,24 10,20 L4,20 L4,14 C0,17 0,7 4,10 Z"),
            Stretch = Stretch.Uniform, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _fallback.SetResourceReference(ShapePath.FillProperty, "AccentBrush");
        Children.Add(_fallback); Children.Add(_image);
        Loaded += (_, _) => Restart(); Unloaded += (_, _) => _cancellation?.Cancel();
        IsVisibleChanged += (_, _) => { if (IsVisible) Restart(); else _cancellation?.Cancel(); };
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    { base.OnRenderSizeChanged(info); Clip = new RectangleGeometry(new Rect(RenderSize), 10, 10); }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) => ((ModProjectIcon)target).Restart();
    private void Restart()
    {
        if (_displayedUrl != Url) { _image.Source = null; _displayedUrl = null; _fallback.Visibility = Visibility.Visible; }
        if (!IsLoaded || !IsVisible || string.IsNullOrWhiteSpace(Url) || _displayedUrl == Url) return;
        _cancellation?.Cancel(); var cts = new CancellationTokenSource(); _cancellation = cts;
        _ = LoadAsync(Url, PreferMirror, cts);
    }
    private async Task LoadAsync(string url, bool mirror, CancellationTokenSource cts)
    {
        try
        {
            Images.TryGetValue(url, out var image);
            for (int attempt = 0; image is null && attempt < 2; attempt++)
            {
                await Slots.WaitAsync(cts.Token);
                try
                {
                    if (!Images.TryGetValue(url, out image))
                    {
                        using var sources = new DownloadSources(new(false, mirror), cts.Token);
                        var file = await sources.CacheIconAsync(url, cts.Token, file => { image = Decode(file); return true; });
                        if (file is not null && image is not null)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            Images[url] = image;
                            if (Images.Count > 192 && Images.Keys.FirstOrDefault(key => key != url) is { } oldest) Images.TryRemove(oldest, out _);
                        }
                    }
                }
                finally { Slots.Release(); }
                if (image is null && attempt == 0) await Task.Delay(1200, cts.Token);
            }
            if (image is null) return;
            if (!cts.IsCancellationRequested && Url == url)
            { _image.Source = image; _displayedUrl = url; _fallback.Visibility = Visibility.Collapsed; }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or System.Net.Http.HttpRequestException or NotSupportedException or ArgumentException or InvalidOperationException) { }
        finally { if (_cancellation == cts) _cancellation = null; cts.Dispose(); }
    }
    public static BitmapSource Decode(string file)
    {
        using var original = SKBitmap.Decode(file) ?? throw new InvalidDataException("无法读取模组图标");
        using var thumbnail = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(thumbnail))
        { canvas.Clear(SKColors.Transparent); canvas.DrawBitmap(original, new SKRect(0, 0, 64, 64)); }
        using var png = thumbnail.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = png.AsStream();
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
