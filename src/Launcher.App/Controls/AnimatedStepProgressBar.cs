using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Launcher.App.Controls;

/// <summary>Animate the fill without changing the bound value or running layout every frame.</summary>
public sealed class AnimatedStepProgressBar : ProgressBar
{
    private RectangleGeometry? _clip;

    public AnimatedStepProgressBar()
    {
        Loaded += (_, _) => { MotionPolicy.Changed += Snap; Draw(true, true); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= Snap; Snap(); };
        SizeChanged += (_, e) => Draw(e.PreviousSize.Width == 0, e.PreviousSize.Width == 0);
        IsVisibleChanged += (_, _) => { if (IsVisible) Draw(true, true); else Snap(); };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_AnimatedFill") is not FrameworkElement fill) return;
        _clip = new RectangleGeometry { RadiusX = 4, RadiusY = 4 };
        fill.Clip = _clip;
        Draw(false);
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        Draw(true);
    }

    private void Snap() => Draw(false);
    private void Draw(bool animate, bool restart = false)
    {
        if (_clip is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        double fraction = Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        var target = new Rect(0, 0, ActualWidth * fraction, ActualHeight);
        var current = restart ? new Rect(0, 0, 0, ActualHeight) : _clip.Rect;
        _clip.BeginAnimation(RectangleGeometry.RectProperty, null);
        _clip.Rect = target;
        if (animate && IsLoaded && IsVisible && MotionPolicy.Page > TimeSpan.Zero && current != target)
            _clip.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(current, target, MotionPolicy.Page)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
}
