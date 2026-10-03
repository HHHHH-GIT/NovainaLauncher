using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Interop;
using Launcher.App.Views;

namespace Launcher.App.Controls;

public static class AppDialog
{
    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (Application.Current.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
        if (Environment.OSVersion.Version.Build >= 22000)
            WindowChrome.SetWindowChrome(dialog, new WindowChrome { CaptionHeight = 42, ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8), UseAeroCaptionButtons = false });
        dialog.SetResourceReference(Window.BackgroundProperty, "DialogSurfaceBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold });
        var scroll = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 18, 0, 24), Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 } };
        panel.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var result = MessageBoxResult.Cancel;
        if (buttons == MessageBoxButton.YesNo)
        {
            var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 10, 0) };
            cancel.SetResourceReference(FrameworkElement.StyleProperty, "AppleSecondaryButton");
            cancel.Click += (_, _) => { result = MessageBoxResult.No; dialog.Close(); };
            actions.Children.Add(cancel);
        }
        var confirm = new Button { Content = "确定", IsDefault = true, MinWidth = 80 };
        confirm.SetResourceReference(FrameworkElement.StyleProperty, "ApplePrimaryButton");
        confirm.Click += (_, _) => { result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK; dialog.Close(); };
        actions.Children.Add(confirm);
        panel.Children.Add(actions);
        var surface = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20) };
        surface.SetResourceReference(Border.BackgroundProperty, "DialogSurfaceBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "DialogEdgeBrush");
        dialog.Content = surface;
        NativeWindowFrame? frame = null;
        WindowOutline? outline = null;
        dialog.SourceInitialized += (_, _) =>
        {
            if (HwndSource.FromHwnd(new WindowInteropHelper(dialog).Handle) is not { } source) return;
            if (Environment.OSVersion.Version.Build < 22000) frame = new NativeWindowFrame(dialog, source);
            outline = new WindowOutline(dialog, surface, source);
        };
        using var shade = (dialog.Owner as MainWindow)?.ShowDialogShade();
        try { dialog.ShowDialog(); }
        finally { outline?.Dispose(); frame?.Dispose(); }
        return result;
    }
}
