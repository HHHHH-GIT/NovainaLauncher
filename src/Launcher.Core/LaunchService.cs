using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.ProcessBuilder;

namespace Launcher.Core;

public sealed class LaunchService(HttpClient http, AccountService accounts, JavaService java, LogService log)
{
    private int _busy;
    public event Action<LaunchState, string>? StateChanged;
    public event Action<double>? ProgressChanged;
    public Process? GameProcess { get; private set; }
    private void SetState(LaunchState state, string message) { log.Write(message, state == LaunchState.Failed ? LogLevel.Error : LogLevel.Info, true); StateChanged?.Invoke(state, message); }
    public async Task LaunchAsync(LaunchRequest request, string clientId, CancellationToken token, SourcePolicy? sourcePolicy = null)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("游戏已在启动或运行");
        try
        {
            using var sources = new DownloadSources(sourcePolicy ?? new(true, true), token, diagnostic: message => log.Write(message));
            SetState(LaunchState.Preparing, "准备启动");
            if (!request.Version.IsValid) throw new InvalidOperationException(request.Version.Error);
            if (!File.Exists(request.Java.Path)) throw new InvalidOperationException("Java 文件不存在，请重新扫描");
            if (request.Version.RequiredJava is int required && request.Java.Major != required) throw new InvalidOperationException($"这个版本需要 Java {required}");
            var session = await accounts.SessionAsync(request.Account, clientId, token);
            string? injector = null; string? metadata = null;
            if (request.Account.Kind == AccountKind.LittleSkin)
            {
                injector = await GetInjectorAsync(sources, token);
                metadata = Convert.ToBase64String(Encoding.UTF8.GetBytes(await http.GetStringAsync("https://littleskin.cn/api/yggdrasil", token)));
            }
            var launcher = VersionService.CreateLocalLauncher(request.Version.Root, sources.Http);
            var version = await launcher.GetVersionAsync(request.Version.Id, token);
            SetState(LaunchState.Installing, "检查游戏文件");
            launcher.FileProgressChanged += (_, e) => { ProgressChanged?.Invoke(e.TotalTasks == 0 ? 0 : 100.0 * e.ProgressedTasks / e.TotalTasks); };
            // Local file extractors read cached indexes; the installer only downloads missing or damaged files.
            await launcher.InstallAsync(version, token);
            token.ThrowIfCancellationRequested();
            SetState(LaunchState.Starting, "正在启动 Minecraft");
            var gameDirectory = request.ResolvedGameDirectory ?? VersionService.GameDirectory(request.Version, request.Isolation);
            var option = new MLaunchOption
            {
                Session = session, JavaPath = request.Java.Path, MaximumRamMb = request.MemoryMb,
                MinimumRamMb = Math.Min(1024, request.MemoryMb), GameLauncherName = "NovainaLauncher", GameLauncherVersion = "1.0.0",
                ArgumentDictionary = new Dictionary<string, string> { ["game_directory"] = gameDirectory }, ClientId = clientId
            };
            if (injector is not null)
                option.ExtraJvmArguments = MLaunchOption.DefaultExtraJvmArguments.Concat(new MArgument[] { new($"-javaagent:{injector}=https://littleskin.cn/api/yggdrasil"), new($"-Dauthlibinjector.yggdrasil.prefetched={metadata}") });
            var process = await Task.Run(() => launcher.BuildProcess(version, option), token);
            process.StartInfo.WorkingDirectory = gameDirectory;
            process.StartInfo.UseShellExecute = false; process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true; process.StartInfo.RedirectStandardError = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8; process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            token.ThrowIfCancellationRequested();
            process.Start(); GameProcess = process;
            SetState(LaunchState.Running, "Minecraft 正在运行"); ProgressChanged?.Invoke(100);
            _ = ObserveAsync(process); // Cancellation now affects preparation only, never the running game.
        }
        catch (OperationCanceledException) { Interlocked.Exchange(ref _busy, 0); SetState(LaunchState.Idle, "已取消启动"); throw; }
        catch { Interlocked.Exchange(ref _busy, 0); SetState(LaunchState.Failed, "启动失败"); throw; }
    }
    private async Task ObserveAsync(Process process)
    {
        try
        {
            await Task.WhenAll(ReadAsync(process.StandardOutput, false), ReadAsync(process.StandardError, true), process.WaitForExitAsync());
            SetState(process.ExitCode == 0 ? LaunchState.Exited : LaunchState.Failed, process.ExitCode == 0 ? "游戏已退出" : $"游戏异常退出 · {process.ExitCode}");
        }
        catch (Exception e) { log.Write(e.Message, LogLevel.Warning, true); }
        finally { GameProcess = null; process.Dispose(); Interlocked.Exchange(ref _busy, 0); }
    }
    private async Task ReadAsync(StreamReader reader, bool stderr)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            var level = line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("Exception") ? LogLevel.Error
                : line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warning : LogLevel.Info;
            log.Write(line, level);
        }
    }
    private async Task<string> GetInjectorAsync(DownloadSources sources, CancellationToken token)
    {
        var directory = Path.Combine(AppPaths.Data, "authlib"); Directory.CreateDirectory(directory);
        var index = Path.Combine(directory, "verified.json");
        if (File.Exists(index))
        {
            var cached = JsonSerializer.Deserialize<InjectorCache>(await File.ReadAllTextAsync(index, token));
            if (cached is not null && File.Exists(cached.Path) && await HashAsync(cached.Path, token) == cached.Hash) return cached.Path;
        }
        var root = await sources.JsonAsync("https://authlib-injector.yushi.moe/artifact/latest.json", token);
        var hash = root.GetProperty("checksums").GetProperty("sha256").GetString()!.ToLowerInvariant();
        var path = Path.Combine(directory, "authlib-injector-" + root.GetProperty("build_number").GetInt32() + ".jar");
        await java.DownloadFileAsync(root.GetProperty("download_url").GetString()!, path + ".part", null, token, sources.Http);
        if (await HashAsync(path + ".part", token) != hash) throw new InvalidDataException("authlib-injector 校验失败");
        File.Move(path + ".part", path, true);
        await File.WriteAllTextAsync(index, JsonSerializer.Serialize(new InjectorCache(path, hash)), token);
        return path;
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant(); }
    private sealed record InjectorCache(string Path, string Hash);
}
