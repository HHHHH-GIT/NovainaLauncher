using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Launcher.Core;

namespace Launcher.App;

// Works for booleans and counts. Visibility and IsEnabled require different result types.
public sealed class StateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool active = value switch { bool b => b, int n => n > 0, string s => s.Length > 0, _ => false };
        if (string.Equals(parameter?.ToString(), "Inverse", StringComparison.OrdinalIgnoreCase)) active = !active;
        return targetType == typeof(Visibility) ? (active ? Visibility.Visible : Visibility.Collapsed) : active;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EqualConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool equals = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        if (targetType == typeof(Visibility))
        {
            return equals ? Visibility.Visible : Visibility.Collapsed;
        }
        return equals;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string str) return str;
        return Binding.DoNothing;
    }
}

public sealed class DanmakuActiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DanmakuMode mode && mode != DanmakuMode.Off)
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class CurrentItemConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length >= 2 && (values[0], values[1]) switch
    {
        (AccountProfile a, AccountProfile b) => a.Id == b.Id,
        (VersionInfo a, VersionInfo b) => a.Id == b.Id && string.Equals(System.IO.Path.GetFullPath(a.Root).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b.Root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase),
        _ => false
    };
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
