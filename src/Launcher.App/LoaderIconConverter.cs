using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Launcher.Core;

namespace Launcher.App;

public sealed class LoaderIconConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var loader = value is VersionInfo version ? version.Loader : value?.ToString();
        var file = loader switch
        {
            "Forge" => "Forge.ico", "NeoForge" => "NeoForge.png", "Fabric" => "Fabric.png",
            "Quilt" => "Quilt.png", "OptiFine" => "OptiFine.ico", _ => "Vanilla.png"
        };
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var cached)) return cached;
            var image = new BitmapImage(); image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/NovainaLauncher;component/Assets/Loaders/{file}");
            image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 64;
            image.EndInit(); image.Freeze(); Cache[file] = image; return image;
        }
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
