using System.Windows;
using System.Windows.Media;
using Launcher.App.ViewModels;

namespace Launcher.App.Controls;

/// <summary>One capacity-scaled strip; the unfilled track represents remaining context.</summary>
public sealed class ContextUsageBar : FrameworkElement
{
    public static readonly DependencyProperty CategoriesProperty = DependencyProperty.Register(nameof(Categories), typeof(IReadOnlyList<AgentContextCategoryView>), typeof(ContextUsageBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(ContextUsageBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly Dictionary<string, SolidColorBrush> _colors = new();
    public IReadOnlyList<AgentContextCategoryView>? Categories { get => (IReadOnlyList<AgentContextCategoryView>?)GetValue(CategoriesProperty); set => SetValue(CategoriesProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var radius = ActualHeight / 2;
        drawing.DrawRoundedRectangle(Track, null, bounds, radius, radius);
        drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
        double offset = 0;
        foreach (var category in Categories ?? [])
        {
            var width = Math.Min(ActualWidth - offset, ActualWidth * Math.Max(0, category.Percent) / 100);
            if (width <= 0) continue;
            if (!_colors.TryGetValue(category.Color, out var color))
            { color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(category.Color)); color.Freeze(); _colors[category.Color] = color; }
            drawing.DrawRectangle(color, null, new Rect(offset, 0, width, ActualHeight));
            offset += width;
            if (offset >= ActualWidth) break;
        }
        drawing.Pop();
    }
}
