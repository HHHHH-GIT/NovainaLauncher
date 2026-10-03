using System.IO.Compression;
using Microsoft.VisualBasic.FileIO;

namespace Launcher.Core;

public sealed class VersionContentService
{
    public static string ContentDirectory(string gameDirectory, VersionContentKind kind) => Path.Combine(gameDirectory, kind switch
    { VersionContentKind.Mod => "mods", VersionContentKind.Save => "saves", VersionContentKind.ResourcePack => "resourcepacks", _ => "shaderpacks" });

    public Task<IReadOnlyList<VersionContentItem>> ScanAsync(string gameDirectory, VersionContentKind kind, CancellationToken token = default) => Task.Run<IReadOnlyList<VersionContentItem>>(() =>
    {
        var root = ContentDirectory(gameDirectory, kind);
        var list = new List<VersionContentItem>();
        Read(root, true);
        if (kind is VersionContentKind.ResourcePack or VersionContentKind.ShaderPack) Read(Path.Combine(root, ".disabled"), false);
        return list.OrderByDescending(x => x.Enabled).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        void Read(string directory, bool enabled)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name.StartsWith('.') || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var isDirectory = Directory.Exists(path);
                if (kind == VersionContentKind.Save && (!isDirectory || !File.Exists(Path.Combine(path, "level.dat")))) continue;
                if (kind == VersionContentKind.Mod && (isDirectory || !(name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))) continue;
                if (kind is VersionContentKind.ResourcePack or VersionContentKind.ShaderPack && !isDirectory && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var active = kind == VersionContentKind.Mod ? !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) : enabled;
                list.Add(new(path, kind == VersionContentKind.Mod && !active ? name[..^9] : name, kind, active, isDirectory, isDirectory ? 0 : new FileInfo(path).Length, File.GetLastWriteTime(path)));
            }
        }
    }, token);

    public Task ToggleAsync(string gameDirectory, VersionContentItem item, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); ValidateItem(gameDirectory, item);
        if (item.Kind == VersionContentKind.Save) throw new InvalidOperationException("存档不支持禁用");
        string target;
        if (item.Kind == VersionContentKind.Mod) target = item.Enabled ? item.Path + ".disabled" : item.Path[..^9];
        else
        {
            var root = ContentDirectory(gameDirectory, item.Kind);
            target = Path.Combine(item.Enabled ? Path.Combine(root, ".disabled") : root, Path.GetFileName(item.Path));
        }
        EnsureNew(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (item.IsDirectory) Directory.Move(item.Path, target); else File.Move(item.Path, target);
    }, token);

    public Task DeleteAsync(string gameDirectory, VersionContentItem item, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); ValidateItem(gameDirectory, item);
        // Never fall back to permanent deletion when the shell cannot recycle an item.
        if (item.IsDirectory) FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        else FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }, token);

    public Task ImportAsync(string gameDirectory, VersionContentKind kind, string source, CancellationToken token = default) => Task.Run(async () =>
    {
        source = Path.GetFullPath(source);
        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source)) throw new FileNotFoundException("文件不存在", source);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持导入链接");
        if (kind == VersionContentKind.Mod && (isDirectory || !source.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("请选择 .jar 文件");
        if (kind != VersionContentKind.Mod && !isDirectory && !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 ZIP 文件或文件夹");
        var root = ContentDirectory(gameDirectory, kind);
        var stage = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        // Destination must not be nested in the imported folder.
        if (isDirectory && IsInside(source, root)) throw new IOException("导入目录不能包含目标目录");
        Directory.CreateDirectory(stage);
        try
        {
            string staged, name;
            if (kind == VersionContentKind.Save && !isDirectory)
            {
                var unpacked = Path.Combine(stage, "unpacked");
                await JavaService.ExtractArchiveAsync(source, unpacked, token).ConfigureAwait(false);
                if (File.Exists(Path.Combine(unpacked, "level.dat"))) { staged = unpacked; name = Path.GetFileNameWithoutExtension(source); }
                else
                {
                    var worlds = Directory.EnumerateDirectories(unpacked).Where(x => File.Exists(Path.Combine(x, "level.dat"))).ToArray();
                    if (worlds.Length != 1) throw new InvalidDataException("ZIP 中需要包含一个有效存档");
                    staged = worlds[0]; name = Path.GetFileName(staged);
                }
            }
            else
            {
                name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                staged = Path.Combine(stage, name);
                if (isDirectory) await CopyDirectoryAsync(source, staged, token).ConfigureAwait(false);
                else await CopyFileAsync(source, staged, token).ConfigureAwait(false);
            }
            if (kind == VersionContentKind.Save && !File.Exists(Path.Combine(staged, "level.dat"))) throw new InvalidDataException("文件夹不是有效存档");
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) throw new InvalidDataException("无效的文件名称");
            var target = Path.Combine(root, name);
            EnsureNew(target);
            if (kind is VersionContentKind.ResourcePack or VersionContentKind.ShaderPack) EnsureNew(Path.Combine(root, ".disabled", name));
            if (kind == VersionContentKind.Mod) EnsureNew(target + ".disabled");
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(staged)) Directory.Move(staged, target); else File.Move(staged, target);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }, token);

    public Task ExportSaveAsync(string gameDirectory, VersionContentItem item, string destination, CancellationToken token = default) => Task.Run(async () =>
    {
        ValidateItem(gameDirectory, item);
        if (item.Kind != VersionContentKind.Save) throw new InvalidOperationException("请选择存档");
        destination = Path.GetFullPath(destination);
        if (IsInside(item.Path, destination)) throw new IOException("请将 ZIP 保存到存档目录之外");
        var temporary = destination + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var file in EnumerateFiles(item.Path, token))
                {
                    token.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(item.Name + "/" + Path.GetRelativePath(item.Path, file).Replace('\\', '/'), CompressionLevel.Fastest);
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    await using var stream = entry.Open();
                    await input.CopyToAsync(stream, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    private static IEnumerable<string> EnumerateFiles(string directory, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("内容中包含链接");
            if (Directory.Exists(path)) { foreach (var file in EnumerateFiles(path, token)) yield return file; }
            else yield return path;
        }
    }
    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("内容中包含链接");
            var target = Path.Combine(destination, Path.GetFileName(path));
            if (Directory.Exists(path)) await CopyDirectoryAsync(path, target, token).ConfigureAwait(false);
            else await CopyFileAsync(path, target, token).ConfigureAwait(false);
        }
    }
    private static async Task CopyFileAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
    }
    private static bool IsInside(string root, string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
    private static void EnsureNew(string path) { if (File.Exists(path) || Directory.Exists(path)) throw new IOException("已存在同名项目"); }
    private static void ValidateItem(string gameDirectory, VersionContentItem item)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(item.Path));
        var root = Path.GetFullPath(ContentDirectory(gameDirectory, item.Kind));
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) && !(item.Kind is VersionContentKind.ResourcePack or VersionContentKind.ShaderPack && string.Equals(parent, Path.Combine(root, ".disabled"), StringComparison.OrdinalIgnoreCase))) throw new IOException("内容不属于当前版本目录");
        if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持操作链接");
    }
}
