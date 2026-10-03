using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Launcher.App.Controls;

// Windows 10 has one owner for the HWND region. WindowChrome is deliberately detached.
public sealed class NativeWindowFrame : IDisposable
{
    private readonly Window _window;
    private readonly HwndSource _source;
    private const long Caption = 0x00C00000, ThickFrame = 0x00040000, MaximizeBox = 0x00010000;
    public NativeWindowFrame(Window window, HwndSource source)
    {
        _window = window; _source = source;
        source.AddHook(Hook);
        var style = GetWindowLongPtr(source.Handle, -16).ToInt64();
        SetWindowLongPtr(source.Handle, -16, new IntPtr((style & ~(Caption | MaximizeBox)) | ThickFrame));
        var extended = GetWindowLongPtr(source.Handle, -20).ToInt64();
        SetWindowLongPtr(source.Handle, -20, new IntPtr(extended & ~0x20300L)); // Remove window/client/static edges.
        int disabled = 1;
        DwmSetWindowAttribute(source.Handle, 2, ref disabled, sizeof(int));
        var margins = new Margins(); DwmExtendFrameIntoClientArea(source.Handle, ref margins);
        SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37); // Refresh NC layout, without moving/activating.
    }
    private IntPtr Hook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0083: // WM_NCCALCSIZE: the entire HWND is the client area.
            case 0x0085: // WM_NCPAINT: no Windows-painted frame.
                handled = true; return IntPtr.Zero;
            case 0x0086: // WM_NCACTIVATE: preserve the custom surface on activation.
                handled = true; return new IntPtr(1);
            case 0x007C when wParam.ToInt64() == -16:
                var style = Marshal.PtrToStructure<StyleChange>(lParam);
                style.New &= ~(int)(Caption | MaximizeBox);
                Marshal.StructureToPtr(style, lParam, false);
                break;
            case 0x0084: // WM_NCHITTEST; OS still owns the normal move/resize loops.
                if (!GetWindowRect(handle, out var bounds)) break;
                var dpi = VisualTreeHelper.GetDpi(_window);
                var packed = lParam.ToInt64();
                var point = new Point(((short)(packed & 0xFFFF) - bounds.Left) / dpi.DpiScaleX,
                    ((short)((packed >> 16) & 0xFFFF) - bounds.Top) / dpi.DpiScaleY);
                var size = new Size((bounds.Right - bounds.Left) / dpi.DpiScaleX, (bounds.Bottom - bounds.Top) / dpi.DpiScaleY);
                var interactive = IsInteractive(_window.InputHitTest(point) as DependencyObject);
                handled = true;
                return new IntPtr(HitTest(point, size, interactive, _window.WindowState == WindowState.Normal && _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip));
        }
        return IntPtr.Zero;
    }
    public static int HitTest(Point point, Size size, bool interactive, bool canResize)
    {
        const double edge = 6;
        if (canResize)
        {
            bool left = point.X < edge, right = point.X >= size.Width - edge;
            bool top = point.Y < edge, bottom = point.Y >= size.Height - edge;
            if (top) return left ? 13 : right ? 14 : 12;
            if (bottom) return left ? 16 : right ? 17 : 15;
            if (left) return 10; if (right) return 11;
        }
        return !interactive && point.Y >= 0 && point.Y < 46 ? 2 : 1;
    }
    private static bool IsInteractive(DependencyObject? target)
    {
        while (target is not null)
        {
            if (target is ButtonBase or System.Windows.Controls.Primitives.Thumb || target is IInputElement input && WindowChrome.GetIsHitTestVisibleInChrome(input)) return true;
            target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        return false;
    }
    public void Dispose() => _source.RemoveHook(Hook);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleChange { public int Old, New; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
}
