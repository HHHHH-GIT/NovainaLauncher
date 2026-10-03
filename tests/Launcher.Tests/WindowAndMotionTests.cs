using System.Windows;
using Launcher.App.Controls;
using Xunit;

namespace Launcher.Tests;

public sealed class WindowAndMotionTests
{
    [Theory]
    [InlineData(2, 2, 13)] [InlineData(998, 2, 14)] [InlineData(2, 598, 16)] [InlineData(998, 598, 17)]
    [InlineData(2, 300, 10)] [InlineData(998, 300, 11)] [InlineData(500, 2, 12)] [InlineData(500, 598, 15)]
    public void Frameless_Window_Still_Resizes_From_Every_Edge(double x, double y, int hit)
        => Assert.Equal(hit, NativeWindowFrame.HitTest(new Point(x, y), new Size(1000, 600), false, true));
    [Fact] public void Caption_Drag_Does_Not_Intercept_Titlebar_Buttons()
    {
        var point = new Point(500, 24); var size = new Size(1000, 600);
        Assert.Equal(2, NativeWindowFrame.HitTest(point, size, false, true));
        Assert.Equal(1, NativeWindowFrame.HitTest(point, size, true, true));
        Assert.Equal(1, NativeWindowFrame.HitTest(new Point(500, 80), size, false, true));
    }
    [Theory] [InlineData(0, 0)] [InlineData(1, 148)] [InlineData(2, 296)]
    public void Slider_Fill_Reaches_Under_The_Thumb_And_Covers_The_End(double value, double width)
        => Assert.Equal(width, CapsuleSlider.FilledWidth(296, 32, 0, 2, value));
}
