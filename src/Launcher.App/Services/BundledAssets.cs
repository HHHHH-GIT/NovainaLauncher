using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Launcher.Core;

namespace Launcher.App.Services;

/// <summary>Pack scripts/textures/licenses in the assembly; never look for loose files beside a single EXE.</summary>
public static class BundledAssets
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Ready = new(StringComparer.OrdinalIgnoreCase);
    public static string SkinViewerDirectory
    {
        get
        {
            var assembly = typeof(BundledAssets).Assembly;
            var root = Path.Combine(AppPaths.Data, "assets", assembly.ManifestModule.ModuleVersionId.ToString("N"));
            lock (Gate)
            {
                if (!Ready.Contains(root))
                {
                    Extract(assembly, "Novaina.Asset/", Path.Combine(root, "skinviewer"));
                    Extract(assembly, "Novaina.License/", Path.Combine(root, "licenses"));
                    Ready.Add(root);
                }
            }
            return Path.Combine(root, "skinviewer");
        }
    }
    private static void Extract(Assembly assembly, string prefix, string root)
    {
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var path = Path.GetFullPath(Path.Combine(root, name[prefix.Length..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("内置资源路径无效");
            using var source = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream(); source.CopyTo(buffer); var bytes = buffer.ToArray();
            if (File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
