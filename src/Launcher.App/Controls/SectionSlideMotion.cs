using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.App.Controls;

public static class SectionSlideMotion
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached("Key", typeof(string), typeof(SectionSlideMotion), new PropertyMetadata(null, Changed));
    private static readonly DependencyProperty ResetCallbackProperty = DependencyProperty.RegisterAttached("ResetCallback", typeof(Action), typeof(SectionSlideMotion));
    private static readonly string[] Sections = ["Overview", "Mods", "Saves", "Packs", "Shaders"];
    public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);
    public static string GetKey(DependencyObject element) => (string)element.GetValue(KeyProperty);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.OldValue is null)
        {
            element.RenderTransform = new TranslateTransform();
            element.SetValue(ResetCallbackProperty, new Action(() => Reset(element)));
            element.Loaded += Loaded; element.Unloaded += Unloaded;
            return;
        }
        Reset(element);
        if (!element.IsLoaded || MotionPolicy.Page == TimeSpan.Zero) return;
        var direction = Array.IndexOf(Sections, e.NewValue as string) >= Array.IndexOf(Sections, e.OldValue as string) ? 1 : -1;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ((TranslateTransform)element.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(direction * 24, 0, MotionPolicy.Page) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.7, 1, MotionPolicy.Page) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).GetValue(ResetCallbackProperty) is Action callback) MotionPolicy.Changed += callback;
    }
    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (element.GetValue(ResetCallbackProperty) is Action callback) MotionPolicy.Changed -= callback;
        Reset(element);
    }
    private static void Reset(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1;
        if (element.RenderTransform is TranslateTransform offset) { offset.BeginAnimation(TranslateTransform.XProperty, null); offset.X = 0; }
    }
}
