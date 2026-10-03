using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.CompilerServices;

namespace Launcher.App.Controls;

public static class InteractionMotion
{
    private sealed class HighlightState { public long LastFrame; public Border? Border; public RadialGradientBrush? Brush; }
    private static readonly ConditionalWeakTable<ButtonBase, HighlightState> Highlights = new();
    private static readonly DependencyPropertyKey IsHoveredPropertyKey = DependencyProperty.RegisterAttachedReadOnly("IsHovered", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false));
    public static readonly DependencyProperty IsHoveredProperty = IsHoveredPropertyKey.DependencyProperty;
    public static bool GetIsHovered(DependencyObject element) => (bool)element.GetValue(IsHoveredProperty);
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(InteractionMotion), new PropertyMetadata(false, Changed));
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        if ((bool)e.NewValue)
        {
            button.RenderTransformOrigin = new Point(.5, .5);
            button.RenderTransform = new ScaleTransform(1, 1);
            button.PreviewMouseDown += Down; button.PreviewMouseUp += Up; button.MouseLeave += Up; button.LostMouseCapture += Up;
            button.MouseEnter += Enter; button.MouseLeave += Leave; button.MouseMove += Highlight;
        }
        else { button.PreviewMouseDown -= Down; button.PreviewMouseUp -= Up; button.MouseLeave -= Up; button.LostMouseCapture -= Up; button.MouseEnter -= Enter; button.MouseLeave -= Leave; button.MouseMove -= Highlight; }
    }
    private static void Enter(object sender, MouseEventArgs e)
    { var button = (ButtonBase)sender; button.SetValue(IsHoveredPropertyKey, true); Glow(button, .62); }
    private static void Leave(object sender, MouseEventArgs e)
    { var button = (ButtonBase)sender; button.SetValue(IsHoveredPropertyKey, false); Glow(button, 0); }
    private static void Glow(ButtonBase button, double value)
    {
        if (button.Template.FindName("PART_Highlight", button) is not Border border) return;
        border.BeginAnimation(UIElement.OpacityProperty, null);
        if (MotionPolicy.Interaction == TimeSpan.Zero) border.Opacity = value;
        else border.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(value, MotionPolicy.Interaction));
    }
    private static void Highlight(object sender, MouseEventArgs e)
    {
        var button = (ButtonBase)sender;
        var state = Highlights.GetOrCreateValue(button);
        var now = Environment.TickCount64;
        if (now - state.LastFrame < 16) return;
        state.LastFrame = now;
        if (button.Template.FindName("PART_Highlight", button) is not Border border || border.Background is not RadialGradientBrush brush || button.ActualWidth == 0 || button.ActualHeight == 0) return;
        if (!ReferenceEquals(state.Border, border) || !ReferenceEquals(state.Brush, brush))
        { brush = brush.Clone(); border.Background = brush; state.Border = border; state.Brush = brush; }
        var point = e.GetPosition(button); brush.Center = brush.GradientOrigin = new Point(point.X / button.ActualWidth, point.Y / button.ActualHeight);
    }
    private static void Down(object sender, MouseButtonEventArgs e) => Animate((ButtonBase)sender, .97);
    private static void Up(object sender, MouseEventArgs e) => Animate((ButtonBase)sender, 1);
    private static void Animate(ButtonBase button, double target)
    {
        var scale = button.RenderTransform as ScaleTransform ?? (button.RenderTransform as TransformGroup)?.Children.OfType<ScaleTransform>().FirstOrDefault();
        if (scale is null) return;
        var duration = MotionPolicy.Interaction;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, duration));
    }
}
