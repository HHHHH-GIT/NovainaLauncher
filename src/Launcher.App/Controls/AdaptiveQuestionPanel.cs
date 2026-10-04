using System.Windows;
using System.Windows.Controls;

namespace Launcher.App.Controls;

/// <summary>Two equal question columns when there is room; a single column in compact windows.</summary>
public sealed class AdaptiveQuestionPanel : Panel
{
    private const double Gap = 16;
    private int Columns(double width) => InternalChildren.Count > 1 && width >= 680 ? 2 : 1;
    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = Columns(availableSize.Width);
        double width = (availableSize.Width - Gap * (columns - 1)) / columns;
        double height = 0;
        for (int row = 0; row < InternalChildren.Count; row += columns)
        {
            double rowHeight = 0;
            for (int col = 0; col < columns && row + col < InternalChildren.Count; col++)
            {
                var child = InternalChildren[row + col];
                child.Measure(new(width, double.PositiveInfinity)); rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight + (row == 0 ? 0 : Gap);
        }
        return new(double.IsInfinity(availableSize.Width) ? InternalChildren.Cast<UIElement>().Select(c => c.DesiredSize.Width).DefaultIfEmpty().Max() : availableSize.Width, height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = Columns(finalSize.Width);
        double width = (finalSize.Width - Gap * (columns - 1)) / columns;
        double top = 0;
        for (int row = 0; row < InternalChildren.Count; row += columns)
        {
            double height = InternalChildren.Cast<UIElement>().Skip(row).Take(columns).Max(c => c.DesiredSize.Height);
            for (int col = 0; col < columns && row + col < InternalChildren.Count; col++)
                InternalChildren[row + col].Arrange(new(col * (width + Gap), top, width, height));
            top += height + Gap;
        }
        return finalSize;
    }
}
