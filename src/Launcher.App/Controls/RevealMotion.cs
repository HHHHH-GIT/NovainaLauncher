using System.Windows;
using System.Windows.Media.Animation;

namespace Launcher.App.Controls;

public static class RevealMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(RevealMotion), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)e.NewValue) element.IsVisibleChanged += Reveal;
        else element.IsVisibleChanged -= Reveal;
    }
    private static void Reveal(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if ((bool)e.NewValue && MotionPolicy.Menu > TimeSpan.Zero)
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, MotionPolicy.Menu) { FillBehavior = FillBehavior.Stop });
    }
}
