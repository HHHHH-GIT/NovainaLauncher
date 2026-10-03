using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Launcher.Core;

/// <summary>Only the installer host and its descendants enter this job, never a Minecraft process.</summary>
public sealed class HostedGameInstallEngine : IGameInstallEngine
{
    public static string? Executable { get; set; }
    public static string? ManagedEntryPoint { get; set; }
    public static bool IsConfigured => Executable is not null;
    public async Task<string> InstallAsync(GameInstallRequest request, Action<DownloadTaskState, string, FileDownloadProgress?> update, CancellationToken token)
    {
        using var budget = await DownloadConnectionBudget.ReserveHostAsync(token);
        using var job = new InstallerJob();
        var start = new ProcessStartInfo(Executable!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        if (ManagedEntryPoint is not null) start.ArgumentList.Add(ManagedEntryPoint);
        start.ArgumentList.Add("--install-host"); start.Environment["IKUN_INSTALL_HOST"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("无法启动安装宿主");
        try { job.Assign(process); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        // The host waits for stdin before doing work, so child processes cannot escape assignment.
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)); await process.StandardInput.FlushAsync();
        var errors = process.StandardError.ReadToEndAsync();
        Task? cancellation = null;
        using var registration = token.Register(() => cancellation = StopAsync());
        async Task StopAsync()
        {
            try { await process.StandardInput.WriteLineAsync("cancel"); await process.StandardInput.FlushAsync(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(2500));
            if (!process.HasExited) job.Terminate();
        }
        string? result = null, failure = null;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("IKUN:")) continue;
            var packet = JsonSerializer.Deserialize<InstallHostPacket>(line[5..])!;
            if (packet.Version is not null) result = packet.Version;
            if (packet.Error is not null) failure = packet.Error;
            if (packet.State is { } state) update(state, packet.Message ?? "", packet.Progress);
        }
        await process.WaitForExitAsync(); if (cancellation is not null) await cancellation;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || result is null) throw new IOException(failure ?? "安装宿主异常退出：" + await errors);
        return result;
    }
    private sealed class InstallerJob : IDisposable
    {
        private readonly IntPtr _handle;
        public InstallerJob()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) { Dispose(); throw new System.ComponentModel.Win32Exception(); }
        }
        public void Assign(Process process) { if (!AssignProcessToJobObject(_handle, process.Handle)) throw new System.ComponentModel.Win32Exception(); }
        public void Terminate() => TerminateJobObject(_handle, 1);
        public void Dispose() => CloseHandle(_handle);
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorking, MaxWorking; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits value, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}

public sealed record InstallHostPacket(DownloadTaskState? State = null, string? Message = null, FileDownloadProgress? Progress = null, string? Version = null, string? Error = null);
public static class InstallHost
{
    public static async Task<int> RunAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var output = Console.Out; var gate = new object();
        void Send(InstallHostPacket packet) { lock (gate) { output.WriteLine("IKUN:" + JsonSerializer.Serialize(packet)); output.Flush(); } }
        try
        {
            var request = JsonSerializer.Deserialize<GameInstallRequest>(await Console.In.ReadLineAsync() ?? throw new IOException("安装请求为空"))!;
            AppPaths.ConfigureData(request.DataDirectory);
            _ = Task.Run(async () => { while (await Console.In.ReadLineAsync() is { } line) if (line == "cancel") cancellation.Cancel(); });
            var version = await new NativeGameInstallEngine().InstallAsync(request, (state, message, progress) => Send(new(state, message, progress)), cancellation.Token);
            Send(new(Version: version)); return 0;
        }
        catch (Exception error) { Send(new(Error: error.Message)); return 1; }
    }
}
