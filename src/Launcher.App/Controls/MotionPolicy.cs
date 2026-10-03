using System.Windows;
using Launcher.Core;

namespace Launcher.App.Controls;

public static class MotionPolicy
{
    public static UiAnimationMode Mode { get; private set; } = UiAnimationMode.Calm;
    public static event Action? Changed;
    public static UiAnimationMode Effective => SystemParameters.ClientAreaAnimation ? Mode : UiAnimationMode.Off;
    public static void Set(UiAnimationMode mode) { Mode = mode; Changed?.Invoke(); }
    public static TimeSpan Page => Duration(420);
    public static TimeSpan Sidebar => Duration(360);
    public static TimeSpan Interaction => Duration(180);
    public static TimeSpan Menu => Duration(240);
    public static TimeSpan Scroll => Duration(420);
    private static TimeSpan Duration(double previousCalm) => TimeSpan.FromMilliseconds(Effective switch
    { UiAnimationMode.Calm => previousCalm * 4 / 3 * 1.15, UiAnimationMode.Performance => previousCalm * .85 * .75, _ => 0 });
}
