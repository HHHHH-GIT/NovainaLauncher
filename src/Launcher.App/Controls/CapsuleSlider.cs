using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Launcher.App.Controls;

public sealed class CapsuleSlider : Slider
{
    private FrameworkElement? _surface;
    private Border? _fill;
    private Track? _track;
    public CapsuleSlider() => SizeChanged += (_, _) => UpdateSurface();
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _surface = GetTemplateChild("PART_Surface") as FrameworkElement;
        _fill = GetTemplateChild("PART_Fill") as Border;
        _track = GetTemplateChild("PART_Track") as Track;
        UpdateSurface();
    }
    protected override void OnValueChanged(double oldValue, double newValue) { base.OnValueChanged(oldValue, newValue); UpdateSurface(); }
    private void UpdateSurface()
    {
        if (_surface is null || _fill is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var geometry = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), ActualHeight / 2, ActualHeight / 2); geometry.Freeze(); _surface.Clip = geometry;
        var innerWidth = Math.Max(0, ActualWidth - 4);
        var thumbWidth = _track?.Thumb?.Width ?? 32;
        _fill.Width = FilledWidth(innerWidth, thumbWidth, Minimum, Maximum, Value);
        var radius = Math.Max(0, (ActualHeight - 4) / 2);
        _fill.CornerRadius = Value >= Maximum ? new CornerRadius(radius) : new CornerRadius(radius, 0, 0, radius);
    }
    public static double FilledWidth(double width, double thumbWidth, double minimum, double maximum, double value)
    {
        if (maximum <= minimum || value <= minimum) return 0;
        if (value >= maximum) return width;
        return Math.Clamp((value - minimum) / (maximum - minimum) * Math.Max(0, width - thumbWidth) + thumbWidth / 2, 0, width);
    }
}
