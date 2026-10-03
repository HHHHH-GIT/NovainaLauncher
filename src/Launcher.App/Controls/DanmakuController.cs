using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launcher.App.ViewModels;
using Launcher.Core;

namespace Launcher.App.Controls;

public sealed class DanmakuController : IDisposable
{
    private readonly Canvas _canvas;
    private readonly MainViewModel _main;
    private readonly DanmakuQueue _queue = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime[] _freeAt = new DateTime[6];
    private bool _suspended;
    private int _mode;
    private DanmakuFilter _filter;
    private static readonly LinearGradientBrush RainbowBrush = CreateRainbow();
    public DanmakuController(Canvas canvas, MainViewModel main)
    {
        _canvas = canvas; _main = main; _mode = (int)main.DanmakuMode; _filter = main.SettingsVM.DanmakuFilter;
        main.SettingsVM.DanmakuChanged += SettingsChanged;
        main.Log.Emitted += OnLog;
        main.PropertyChanged += OnPropertyChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background, canvas.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += Tick; _timer.Start();
    }
    private void OnLog(LauncherLogEvent entry)
    {
        if (!Volatile.Read(ref _filter).IsBlocked(entry)) _queue.Add(entry, (DanmakuMode)Volatile.Read(ref _mode));
    }
    private void SettingsChanged()
    {
        Volatile.Write(ref _filter, _main.SettingsVM.DanmakuFilter);
        _queue.Clear(); ClearVisuals();
    }
    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.DanmakuMode)) return;
        Volatile.Write(ref _mode, (int)_main.DanmakuMode);
        _queue.Clear(); ClearVisuals();
    }
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (suspended) ClearVisuals();
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (_main.DanmakuMode == DanmakuMode.Off || _main.IsAiMode) return;
        bool blocked = _main.AccountsVM.ShowOfflineModal || _main.AccountsVM.ShowMicrosoftModal || _main.AccountsVM.ShowLittleSkinModal;
        if (blocked || _suspended || !SystemParameters.ClientAreaAnimation || !_canvas.IsVisible || Window.GetWindow(_canvas)?.WindowState == WindowState.Minimized)
        { ClearVisuals(); return; }
        if (_canvas.Children.Count >= 12 || _canvas.ActualWidth < 50 || _canvas.ActualHeight < 50) return;
        int track = Array.FindIndex(_freeAt, time => time <= DateTime.UtcNow);
        if (track < 0 || _queue.Take() is not { } entry) return;
        if (Volatile.Read(ref _filter).IsBlocked(entry)) return;
        Spawn(entry, track);
    }
    private void Spawn(LauncherLogEvent entry, int track)
    {
        var foreground = entry.Level == LogLevel.Error ? "#FFB3AD" : entry.Level == LogLevel.Warning ? "#FFE08A" : entry.Featured ? "#99DCFF" : "#E5EAF2";
        var move = new TranslateTransform(_canvas.ActualWidth + 10, 0);
        var style = _main.Settings.DanmakuStyle;
        bool glass = style == DanmakuStyle.Glass;
        bool dark = _canvas.TryFindResource("TextPrimaryBrush") is SolidColorBrush primary && primary.Color.R > 128;
        if (!glass && !dark) foreground = entry.Level == LogLevel.Error ? "#BB302A" : entry.Level == LogLevel.Warning ? "#8D5A00" : entry.Featured ? "#0067AD" : "#252933";
        var text = new TextBlock { Text = entry.Message.Replace('\n', ' ').Replace('\r', ' '), Foreground = style == DanmakuStyle.Rainbow ? RainbowBrush : Brush(foreground),
            FontSize = glass ? 12 : 14, FontWeight = glass ? FontWeights.Normal : FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Min(420, _canvas.ActualWidth - 40) };
        if (!glass && style != DanmakuStyle.Rainbow && entry.Level == LogLevel.Info && !entry.Featured)
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var pill = new Border
        {
            CornerRadius = new(12), Background = glass ? Brush("#E52A303A") : Brushes.Transparent,
            BorderBrush = glass ? Brush("#3091B5DF") : Brushes.Transparent, BorderThickness = new(glass ? 1 : 0),
            Padding = glass ? new(12, 5, 12, 5) : new Thickness(3, 5, 3, 5), RenderTransform = move, Child = text
        };
        Canvas.SetLeft(pill, 0); Canvas.SetTop(pill, 18 + track * 34);
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = pill.DesiredSize.Width;
        // Fixed velocity and per-track spacing prevent a later pill catching the earlier one.
        const double velocity = 140;
        _freeAt[track] = DateTime.UtcNow.AddSeconds((width + 30) / velocity);
        _canvas.Children.Add(pill);
        var animation = new DoubleAnimation(_canvas.ActualWidth + 10, -width - 20, TimeSpan.FromSeconds((_canvas.ActualWidth + width + 30) / velocity)) { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { move.BeginAnimation(TranslateTransform.XProperty, null); _canvas.Children.Remove(pill); };
        move.BeginAnimation(TranslateTransform.XProperty, animation);
    }
    private static SolidColorBrush Brush(string value) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; }
    private static LinearGradientBrush CreateRainbow()
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
        string[] colors = ["#DB4564", "#CF7325", "#8F941A", "#20925C", "#238CBB", "#6364DB", "#AF50B8"];
        for (int i = 0; i < colors.Length; i++) brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(colors[i]), i / (double)(colors.Length - 1)));
        brush.Freeze(); return brush;
    }
    private void ClearVisuals()
    {
        foreach (UIElement child in _canvas.Children)
            if (child.RenderTransform is TranslateTransform move) move.BeginAnimation(TranslateTransform.XProperty, null);
        _canvas.Children.Clear(); Array.Fill(_freeAt, DateTime.MinValue);
    }
    public void Dispose()
    {
        _timer.Stop(); _timer.Tick -= Tick; _main.Log.Emitted -= OnLog; _main.PropertyChanged -= OnPropertyChanged;
        _main.SettingsVM.DanmakuChanged -= SettingsChanged;
        _queue.Clear(); ClearVisuals();
    }
}
