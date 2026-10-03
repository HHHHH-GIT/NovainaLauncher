using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Launcher.App.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Launcher.App.Controls;

public sealed class WindowAppearance : IDisposable
{
    private readonly Window _window;
    private readonly SettingsViewModel _settings;
    private readonly HwndSource? _source;
    private bool _moving;
    public void SetMoving(bool moving)
    {
        if (_moving == moving) return;
        _moving = moving;
        if (Environment.OSVersion.Version.Build < 22000) Apply();
    }
    public WindowAppearance(Window window, SettingsViewModel settings)
    {
        _window = window; _settings = settings;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(StyleHook);
        _settings.PropertyChanged += SettingsChanged;
        SystemEvents.UserPreferenceChanged += SystemChanged;
        Apply();
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(SettingsViewModel.Theme) or nameof(SettingsViewModel.WindowTransparencyPercent)) Apply(); }
    private void SystemChanged(object sender, UserPreferenceChangedEventArgs e)
    { if (!_window.Dispatcher.HasShutdownStarted) _window.Dispatcher.BeginInvoke(Apply); }

    private void Apply()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool dark = _settings.Theme == "Dark" || _settings.Theme == "System" && key?.GetValue("AppsUseLightTheme") is 0;
        bool transparency = _settings.WindowTransparencyPercent == 0 && !SystemParameters.HighContrast && key?.GetValue("EnableTransparency") is not 0;
        var handle = new WindowInteropHelper(_window).Handle;
        bool legacy = Environment.OSVersion.Version.Build < 22000;
        bool acrylic = transparency && !_moving && (legacy
            ? ApplyLegacy(handle, dark, true)
            : WindowBackdrop.ApplyBackdrop(handle, WindowBackdropType.Acrylic));
        if (acrylic) WindowBackdrop.RemoveBackground(_window);
        else
        {
            if (legacy) ApplyLegacy(handle, dark, false);
            WindowBackdrop.RemoveBackdrop(_window);
        }
        int round = 2, darkValue = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(handle, 20, ref darkValue, sizeof(int));
        int borderColor = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
        ApplyOpacity(handle);
        _window.Background = acrylic ? Brushes.Transparent : new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#202329" : "#F1F4F8"));
        Set("AppleWindowBackgroundBrush", acrylic ? (dark ? "#55202329" : "#55F1F4F8") : (dark ? "#FF202329" : "#FFF1F4F8"));
        Set("AppleWindowBorderBrush", dark ? "#32FFFFFF" : "#20000000");
        Set("TextPrimaryBrush", dark ? "#F2F3F5" : "#1D1D1F");
        Set("TextSecondaryBrush", dark ? "#B8BDC7" : "#656973");
        Set("TextTertiaryBrush", dark ? "#8E96A4" : "#858D99");
        Set("AppleCardBrush", dark ? "#D02C3038" : "#D6FAFBFD");
        Set("AppleCardBorderBrush", dark ? "#26FFFFFF" : "#18000000");
        Set("InputBrush", dark ? "#B022252B" : "#D8F7F9FC");
        Set("InputFocusBrush", dark ? "#FF303640" : "#FFFFFFFF");
        Set("ControlBrush", dark ? "#703A414F" : "#DDE5ECF5");
        Set("ControlHoverBrush", dark ? "#90515C70" : "#A0FFFFFF");
        Set("AiChoiceSelectedBrush", dark ? "#FF263B56" : "#FFE2EEFC");
        Set("AiChoiceHoverBrush", dark ? "#FF363D48" : "#FFF0F5FC");
        Set("ControlPressedBrush", dark ? "#B05C6A80" : "#80D8E7FA");
        Set("GlassEdgeBrush", dark ? "#38FFFFFF" : "#30000000");
        Set("GlassHoverEdgeBrush", dark ? "#60FFFFFF" : "#40828F9F");
        Set("GlassHoverTintBrush", dark ? "#18FFFFFF" : "#14007AFF");
        var highlight = (Color)ColorConverter.ConvertFromString(dark ? "#D0FFFFFF" : "#9A52A4FF");
        if (Application.Current.Resources["GlassHighlightColor"] is not Color previous || previous != highlight) Application.Current.Resources["GlassHighlightColor"] = highlight;
        Set("PopupBrush", dark ? "#FF292E36" : "#FFF9FAFC");
        Set("DialogSurfaceBrush", dark ? "#FF303640" : "#FFFCFDFE");
        Set("DialogEdgeBrush", dark ? "#608D9AAD" : "#507A8799");
        Set("ModalShadeBrush", dark ? "#66080D17" : "#260F1C30");
        Set("PrimaryEdgeBrush", dark ? "#40FFFFFF" : "#24000000");
        Set("PrimaryHoverTintBrush", dark ? "#1AFFFFFF" : "#16FFFFFF");
        Set("ScrollThumbBrush", dark ? "#888D99AA" : "#88798291");
    }
    private void ApplyOpacity(IntPtr handle)
    {
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        if (_settings.WindowTransparencyPercent > 0)
        {
            if ((style & 0x80000) == 0) SetWindowLongPtr(handle, -20, new IntPtr(style | 0x80000));
            if (!SetLayeredWindowAttributes(handle, 0, (byte)Math.Round(255 * (1 - Math.Clamp(_settings.WindowTransparencyPercent, 0, 50) / 100d)), 2))
                _settings.Main.Log.Write("无法应用窗口透明度", Launcher.Core.LogLevel.Warning);
        }
        else if ((style & 0x80000) != 0)
        {
            SetWindowLongPtr(handle, -20, new IntPtr(style & ~0x80000L));
            RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, 0x485);
        }
    }
    private IntPtr StyleHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // HwndTarget otherwise strips WS_EX_LAYERED when AllowsTransparency is false.
        // Keep system-managed constant alpha without opting into WPF per-pixel rendering.
        if (message == 0x007C && wParam.ToInt64() == -20 && _settings.WindowTransparencyPercent > 0)
        {
            var style = Marshal.PtrToStructure<NativeStyle>(lParam);
            style.New |= 0x80000; Marshal.StructureToPtr(style, lParam, false); handled = true;
        }
        return IntPtr.Zero;
    }
    private static void Set(string key, string color)
    {
        var value = (Color)ColorConverter.ConvertFromString(color);
        if (Application.Current.Resources[key] is SolidColorBrush existing && existing.Color == value) return;
        var brush = new SolidColorBrush(value); brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
    public void Dispose() { _settings.PropertyChanged -= SettingsChanged; SystemEvents.UserPreferenceChanged -= SystemChanged; _source?.RemoveHook(StyleHook); }
    // Windows 10 does not implement the Windows 11 DWM system-backdrop attributes.
    private static bool ApplyLegacy(IntPtr handle, bool dark, bool enabled)
    {
        var policy = new AccentPolicy { State = enabled ? 4 : 0, Flags = 2, Color = dark ? 0x99292320u : 0x99F8F4F1u };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionData { Attribute = 19, Data = memory, Size = new IntPtr(Marshal.SizeOf<AccentPolicy>()) };
            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeStyle { public int Old, New; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public IntPtr Size; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
}

