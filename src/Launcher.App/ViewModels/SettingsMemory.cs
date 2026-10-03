using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;
using Microsoft.Win32;
using System.IO;

namespace Launcher.App.ViewModels;

public sealed partial class SettingsViewModel
{
    private CancellationTokenSource _memoryLifetime = new();
    private readonly SemaphoreSlim _memoryGate = new(1, 1);
    private Task? _memoryLoop;
    private bool _applyingMemory;
    [ObservableProperty] private bool _smartMemory = true;
    [ObservableProperty] private int _availableMemoryMb = 4096;
    private bool _hasMemorySample;
    [ObservableProperty] private string _memoryStatus = "";
    [ObservableProperty] private string _javaDownloadDirectory = AppPaths.Runtime;
    [ObservableProperty] private int _windowTransparencyPercent;
    public string AvailableMemoryLabel => _hasMemorySample ? $"可用 {AvailableMemoryMb / 1024d:F1} GB" : "读取中";
    public string TransparencyLabel => $"{WindowTransparencyPercent}%";
    partial void OnAvailableMemoryMbChanged(int value) => OnPropertyChanged(nameof(AvailableMemoryLabel));
    partial void OnSmartMemoryChanged(bool value)
    {
        Main.Settings.MemoryAllocationMode = value ? MemoryAllocationMode.Smart : MemoryAllocationMode.Manual;
        ScheduleSave();
        if (value) _ = RefreshMemoryAsync();
    }
    partial void OnJavaDownloadDirectoryChanged(string value) { Main.Settings.JavaDownloadDirectory = value; ScheduleSave(); }
    partial void OnWindowTransparencyPercentChanged(int value)
    {
        Main.Settings.WindowTransparencyPercent = Math.Clamp(value, 0, 50); ScheduleSave(); OnPropertyChanged(nameof(TransparencyLabel));
    }
    public async Task StartMemoryMonitoringAsync()
    {
        if (_memoryLoop is not null) return;
        await RefreshMemoryAsync();
        _memoryLoop = PollMemoryAsync(_memoryLifetime.Token);
    }
    private async Task StopMemoryMonitoringAsync()
    {
        _memoryLifetime.Cancel();
        if (_memoryLoop is { } loop) await loop;
        await _memoryGate.WaitAsync(); _memoryGate.Release();
        _memoryLoop = null;
    }
    private async Task ResumeMemoryMonitoringAsync()
    {
        if (!_memoryLifetime.IsCancellationRequested) return;
        _memoryLifetime.Dispose(); _memoryLifetime = new(); await StartMemoryMonitoringAsync();
    }
    private async Task PollMemoryAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try { while (await timer.WaitForNextTickAsync(token)) await RefreshMemoryAsync(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public async Task<int> RefreshMemoryAsync(VersionInfo? version = null)
    {
        var token = _memoryLifetime.Token;
        try
        {
            await _memoryGate.WaitAsync(token);
            try
            {
                version ??= Main.CurrentVersion;
                var directory = version is null ? null : VersionService.ResolveGameDirectory(version, Main.Settings);
                var sample = await Task.Run(() => (Available: MemoryService.GetAvailableMemoryMb(), Mods: directory is null ? 0 : MemoryService.CountEnabledMods(directory)), token);
                token.ThrowIfCancellationRequested();
                var selectedMemory = 0;
                await Main.UiDispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    _hasMemorySample = true; _applyingMemory = true;
                    try
                    {
                        AvailableMemoryMb = sample.Available;
                        MemoryMb = SmartMemory ? MemoryService.Recommend(sample.Available, sample.Mods) : Math.Clamp(MemoryMb, 0, sample.Available);
                        selectedMemory = MemoryMb;
                    }
                    finally { _applyingMemory = false; }
                    OnPropertyChanged(nameof(AvailableMemoryLabel));
                    MemoryStatus = sample.Available < 256 ? "可用内存不足" : "";
                });
                return selectedMemory;
            }
            finally { _memoryGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return MemoryMb; }
        catch (Exception e) { MemoryStatus = "无法读取可用内存"; Main.Log.Write(e.Message, LogLevel.Warning); return 0; }
    }
    [RelayCommand] private void BrowseJavaDownloadDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Java 下载目录", InitialDirectory = Directory.Exists(JavaDownloadDirectory) ? JavaDownloadDirectory : AppPaths.Data };
        if (dialog.ShowDialog() == true) JavaDownloadDirectory = Path.GetFullPath(dialog.FolderName);
    }
    [RelayCommand] private void ResetJavaDownloadDirectory() => JavaDownloadDirectory = AppPaths.Runtime;
}
