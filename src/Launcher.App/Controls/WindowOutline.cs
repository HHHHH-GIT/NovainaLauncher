using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Launcher.App.Controls;

public sealed class WindowOutline : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _surface;
    private readonly HwndSource _source;
    private readonly bool _legacy = Environment.OSVersion.Version.Build < 22000;
    private bool _queued, _disposed;
    private (int Left, int Top, int Width, int Height, int X, int Y)? _lastRegion;
    private double Radius => _legacy ? 20 : 8;
    public WindowOutline(Window window, FrameworkElement surface, HwndSource source)
    {
        _window = window; _surface = surface; _source = source;
        _window.SizeChanged += Changed; _surface.SizeChanged += Changed;
        _window.StateChanged += StateChanged; _surface.Loaded += Loaded;
        _source.AddHook(Hook); QueueUpdate();
    }
    private void Changed(object sender, SizeChangedEventArgs e) => QueueUpdate();
    private void StateChanged(object? sender, EventArgs e) => QueueUpdate();
    private void Loaded(object sender, RoutedEventArgs e) => QueueUpdate();
    private void QueueUpdate()
    {
        if (_queued || _disposed) return;
        _queued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => { _queued = false; if (!_disposed) Update(); }));
    }
    private IntPtr Hook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x02E0 or 0x0005 or 0x0047 or 0x007D or 0x031E) QueueUpdate();
        return IntPtr.Zero;
    }
    private void Update()
    {
        if (_surface.ActualWidth <= 0 || _surface.ActualHeight <= 0 || _window.WindowState == WindowState.Minimized) return;
        var clip = new RectangleGeometry(new Rect(0, 0, _surface.ActualWidth, _surface.ActualHeight), Radius, Radius); clip.Freeze();
        _surface.Clip = clip;
        if (_surface is System.Windows.Controls.Border border) border.CornerRadius = new CornerRadius(Radius);
        if (!_legacy && (GetWindowLongPtr(_source.Handle, -20).ToInt64() & 0x80000) == 0)
        {
            if (_lastRegion is not null) { SetWindowRgn(_source.Handle, IntPtr.Zero, true); _lastRegion = null; }
            return;
        }
        if (!GetWindowRect(_source.Handle, out var bounds)) return;
        var client = new NativePoint(); if (!ClientToScreen(_source.Handle, ref client)) return;
        var dpi = VisualTreeHelper.GetDpi(_surface);
        // EnsureHandle may run before Window's template attaches its content visually.
        var visualRoot = _window.IsAncestorOf(_surface) ? (Visual)_window : _window.Content as Visual;
        if (visualRoot is null || !visualRoot.IsAncestorOf(_surface)) return;
        var origin = _surface.TransformToAncestor(visualRoot).Transform(new Point());
        var regionBounds = (Left: client.X - bounds.Left + (int)Math.Round(origin.X * dpi.DpiScaleX),
            Top: client.Y - bounds.Top + (int)Math.Round(origin.Y * dpi.DpiScaleY),
            Width: (int)Math.Round(_surface.ActualWidth * dpi.DpiScaleX), Height: (int)Math.Round(_surface.ActualHeight * dpi.DpiScaleY),
            X: (int)Math.Round(Radius * 2 * dpi.DpiScaleX), Y: (int)Math.Round(Radius * 2 * dpi.DpiScaleY));
        // GDI's rounded region excludes the final row/column; pad its input bounds by one pixel.
        var region = CreateRoundRectRgn(regionBounds.Left, regionBounds.Top, regionBounds.Left + regionBounds.Width + 1, regionBounds.Top + regionBounds.Height + 1, regionBounds.X, regionBounds.Y);
        if (region == IntPtr.Zero) return;
        if (_lastRegion == regionBounds)
        {
            var current = CreateRectRgn(0, 0, 0, 0);
            try
            {
                if (current != IntPtr.Zero && GetWindowRgn(_source.Handle, current) != 0 && EqualRgn(region, current)) { DeleteObject(region); return; }
            }
            finally { if (current != IntPtr.Zero) DeleteObject(current); }
        }
        if (SetWindowRgn(_source.Handle, region, true) == 0) DeleteObject(region);
        else _lastRegion = regionBounds;
    }
    public void Dispose()
    {
        _disposed = true; _window.SizeChanged -= Changed; _surface.SizeChanged -= Changed;
        _window.StateChanged -= StateChanged; _surface.Loaded -= Loaded; _source.RemoveHook(Hook);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool EqualRgn(IntPtr first, IntPtr second);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
}
