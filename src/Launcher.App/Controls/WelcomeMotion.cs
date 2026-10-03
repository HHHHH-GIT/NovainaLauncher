using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Launcher.App.Controls;

public static class WelcomeMotion
{
    private sealed class State { public int Generation; public bool PolicyConnected; public Action? Reset; }
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(WelcomeMotion));
    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached("Delay", typeof(int), typeof(WelcomeMotion), new PropertyMetadata(-1, Configure));
    public static void SetDelay(DependencyObject d, int value) => d.SetValue(DelayProperty, value);
    public static int GetDelay(DependencyObject d) => (int)d.GetValue(DelayProperty);
    private static void Configure(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || (int)e.NewValue < 0 || element.GetValue(StateProperty) is State) return;
        var state = new State(); element.SetValue(StateProperty, state);
        var group = new TransformGroup(); group.Children.Add(new ScaleTransform()); group.Children.Add(new TranslateTransform()); element.RenderTransform = group;
        state.Reset = () => Finish(element, state);
        // Hide before the first layout/render, including the stagger delay.
        Prepare(element);
        element.Loaded += (_, _) => { if (!state.PolicyConnected) { MotionPolicy.Changed += state.Reset; state.PolicyConnected = true; } Reveal(element, state); };
        element.Unloaded += (_, _) => { MotionPolicy.Changed -= state.Reset; state.PolicyConnected = false; state.Generation++; Prepare(element); };
        element.IsVisibleChanged += (_, _) => Reveal(element, state);
        if (element is Button)
        {
            element.MouseEnter += (_, _) => Lift(element, -3);
            element.MouseLeave += (_, _) => Lift(element, 0);
        }
    }
    private static TranslateTransform Offset(FrameworkElement element) => ((TransformGroup)element.RenderTransform).Children.OfType<TranslateTransform>().First();
    private static void Prepare(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        var offset = Offset(element);
        offset.BeginAnimation(TranslateTransform.XProperty, null); offset.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = MotionPolicy.Page == TimeSpan.Zero ? 1 : 0;
        offset.X = MotionPolicy.Page == TimeSpan.Zero || element is not Button ? 0 : -4096;
        offset.Y = 0;
    }
    private static void Finish(FrameworkElement element, State state)
    {
        state.Generation++;
        var offset = Offset(element);
        element.Opacity = 1; offset.X = offset.Y = 0;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        offset.BeginAnimation(TranslateTransform.XProperty, null); offset.BeginAnimation(TranslateTransform.YProperty, null);
    }
    private static void Reveal(FrameworkElement element, State state)
    {
        var generation = ++state.Generation;
        Prepare(element);
        if (!element.IsVisible || !element.IsLoaded) return;
        if (MotionPolicy.Page == TimeSpan.Zero) { Finish(element, state); return; }
        element.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (state.Generation != generation || !element.IsVisible || !element.IsLoaded) return;
            var offset = Offset(element);
            double start = 0;
            if (element is Button)
            {
                DependencyObject? ancestor = VisualTreeHelper.GetParent(element);
                while (ancestor is not null && ancestor is not UserControl) ancestor = VisualTreeHelper.GetParent(ancestor);
                var surface = ancestor as FrameworkElement ?? Window.GetWindow(element);
                var left = surface is null ? element.ActualWidth : element.TranslatePoint(new Point(), surface).X - offset.X;
                start = -(Math.Max(0, left) + element.ActualWidth + 48);
            }
            offset.X = start; offset.Y = element is Button ? 0 : 12;
            var delay = TimeSpan.FromMilliseconds(GetDelay(element) * MotionPolicy.Page.TotalMilliseconds / 420);
            DoubleAnimationUsingKeyFrames Animation(double end)
            {
                var animation = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = MotionPolicy.Page };
                animation.KeyFrames.Add(new SplineDoubleKeyFrame(end, KeyTime.FromTimeSpan(MotionPolicy.Page), new KeySpline(.22, .70, .22, 1)));
                return animation;
            }
            // Entrance base values remain in place until BeginTime, never at the destination.
            var opacity = Animation(1);
            opacity.Completed += (_, _) => { if (state.Generation == generation) Finish(element, state); };
            offset.BeginAnimation(element is Button ? TranslateTransform.XProperty : TranslateTransform.YProperty, Animation(0));
            element.BeginAnimation(UIElement.OpacityProperty, opacity);
        }));
    }
    private static void Lift(FrameworkElement element, double target)
    {
        if (element.Opacity < .99) return;
        var offset = Offset(element); var current = offset.Y;
        offset.BeginAnimation(TranslateTransform.YProperty, null);
        offset.Y = MotionPolicy.Interaction == TimeSpan.Zero ? 0 : target;
        if (MotionPolicy.Interaction != TimeSpan.Zero)
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(current, target, MotionPolicy.Interaction) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
}
