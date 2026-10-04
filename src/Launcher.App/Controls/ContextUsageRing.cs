using System.Windows;
using System.Windows.Media;

namespace Launcher.App.Controls;

/// <summary>Static vector ring. Updates only when a complete operation reports usage.</summary>
public sealed class ContextUsageRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(double), typeof(ContextUsageRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(ContextUsageRing), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(ContextUsageRing), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2); var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 2);
        dc.DrawEllipse(null, new Pen(Track, 2.5), center, radius, radius);
        var percent = Math.Clamp(Percent, 0, 100); if (percent <= 0 || radius == 0) return;
        var pen = new Pen(Accent, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (percent >= 100) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = percent / 100 * Math.PI * 2;
        var path = new StreamGeometry();
        using (var drawing = path.Open()) { drawing.BeginFigure(new(center.X, center.Y - radius), false, false); drawing.ArcTo(new(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius), new(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true, false); }
        path.Freeze(); dc.DrawGeometry(null, pen, path);
    }
}
