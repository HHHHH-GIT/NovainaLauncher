using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.App.Controls;

public sealed class SlidingTabStrip : Grid
{
    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(nameof(SelectedKey), typeof(string), typeof(SlidingTabStrip), new PropertyMetadata("Overview", KeyChanged));
    public string SelectedKey { get => (string)GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }
    private readonly Border _indicator = new() { CornerRadius = new CornerRadius(18), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
    private readonly TranslateTransform _position = new();
    private double _targetX = double.NaN, _targetWidth = double.NaN;
    public SlidingTabStrip()
    {
        _indicator.RenderTransform = _position;
        _indicator.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        _indicator.SetResourceReference(Border.BorderBrushProperty, "GlassEdgeBrush");
        _indicator.BorderThickness = new Thickness(1);
        Children.Add(_indicator);
        Loaded += (_, _) => { MotionPolicy.Changed += Finish; UpdateIndicator(false); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= Finish; Finish(); };
        SizeChanged += (_, _) => UpdateIndicator(false);
    }
    private static void KeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SlidingTabStrip)d).UpdateIndicator(true);
    private RadioButton? SelectedButton() => Children.OfType<Panel>().SelectMany(x => x.Children.OfType<RadioButton>()).FirstOrDefault(x => Equals(x.CommandParameter, SelectedKey));
    private void UpdateIndicator(bool animate)
    {
        var button = SelectedButton();
        if (button is null || button.ActualWidth <= 0) return;
        var point = button.TranslatePoint(new Point(), this);
        var width = button.ActualWidth;
        if (animate && _targetX == point.X && _targetWidth == width) return;
        var fromX = _position.X; var fromWidth = double.IsNaN(_indicator.Width) ? width : _indicator.Width;
        var initialized = _indicator.Visibility == Visibility.Visible;
        _targetX = point.X; _targetWidth = width;
        _indicator.Visibility = Visibility.Visible; _indicator.Height = button.ActualHeight;
        _position.BeginAnimation(TranslateTransform.XProperty, null); _indicator.BeginAnimation(WidthProperty, null);
        _position.X = point.X; _position.Y = point.Y; _indicator.Width = width;
        if (!animate || !initialized || MotionPolicy.Menu == TimeSpan.Zero) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _position.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, point.X, MotionPolicy.Menu) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        _indicator.BeginAnimation(WidthProperty, new DoubleAnimation(fromWidth, width, MotionPolicy.Menu) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }
    private void Finish()
    {
        _position.BeginAnimation(TranslateTransform.XProperty, null); _indicator.BeginAnimation(WidthProperty, null);
        if (!double.IsNaN(_targetX)) { _position.X = _targetX; _indicator.Width = _targetWidth; }
    }
}
