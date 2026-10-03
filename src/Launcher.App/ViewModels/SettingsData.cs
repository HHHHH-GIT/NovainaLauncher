using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Controls;
using Launcher.App.Views;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.App.ViewModels;

public sealed partial class SettingsViewModel
{
    public string DataDirectory => AppPaths.Data;
    [ObservableProperty] private bool _isChangingData;
    [ObservableProperty] private string _dataStatus = "";
    [RelayCommand] private async Task ChangeDataDirectoryAsync()
    {
        if (IsChangingData) return;
        var picker = new OpenFolderDialog { Title = "选择空的数据目录", InitialDirectory = AppPaths.Data };
        if (picker.ShowDialog() != true) return;
        var destination = Path.GetFullPath(picker.FolderName);
        if (string.Equals(destination.TrimEnd(Path.DirectorySeparatorChar), AppPaths.Data.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.EnumerateFileSystemEntries(destination).Any()) { DataStatus = "请选择空目录"; return; }
        var app = (App)Application.Current;
        await RunDataChangeAsync(async () =>
        {
            DataStatus = "正在复制";
            await Task.Run(() => app.DataDirectories.MigrateAsync(AppPaths.Data, destination, Main.Settings));
            DataStatus = "正在重启";
            try { app.Restart(); }
            catch { app.DataDirectories.WriteLocation(AppPaths.Data); throw; }
        });
    }
    [RelayCommand] private async Task ImportSettingsAsync()
    {
        if (IsChangingData) return;
        var picker = new OpenFileDialog { Title = "导入 settings.json", Filter = "设置文件 (settings.json)|settings.json|JSON 文件 (*.json)|*.json" };
        if (picker.ShowDialog() != true) return;
        LauncherSettings imported;
        try { imported = await Task.Run(() => DataDirectoryService.ReadImport(picker.FileName)); }
        catch (Exception error) { DataStatus = "导入失败：" + error.Message; return; }
        await RunDataChangeAsync(async () =>
        {
            DataStatus = "正在导入";
            var backup = await Task.Run(() => DataDirectoryService.ImportSettings(AppPaths.Data, imported));
            DataStatus = "正在重启";
            try { ((App)Application.Current).Restart(); }
            catch { if (File.Exists(backup)) File.Copy(backup, Path.Combine(AppPaths.Data, "settings.json"), true); throw; }
        });
    }
    private async Task RunDataChangeAsync(Func<Task> action)
    {
        IsChangingData = true; DataStatus = "正在保存";
        var window = Application.Current.MainWindow;
        if (window is not null) window.IsEnabled = false;
        try
        {
            if (window is MainWindow mainWindow) await mainWindow.InitializationTask;
            Main.CancelLaunch();
            await Main.AgentVM.CancelAndWaitAsync();
            await Task.WhenAll(Main.AccountsVM.CancelAndWaitAsync(), CancelAndWaitAsync(), Main.ContentVM.CancelAndWaitAsync(), Main.DownloadsVM.CancelAndWaitAsync());
            if (Main.LaunchGameCommand.ExecutionTask is { } launch) await launch;
            if (ScanJavaCommand.ExecutionTask is { } scan) await scan;
            if (Main.VersionsVM.ScanVersionsCommand.ExecutionTask is { } versions) await versions;
            await StopMemoryMonitoringAsync();
            _saveTimer.Stop(); Main.SettingsStore.Save(Main.Settings);
            await Main.Log.PauseFileAsync();
            await action();
        }
        catch (Exception error)
        {
            Main.Log.ResumeFile(); DataStatus = "操作失败：" + error.Message;
            Main.Log.Write(DataStatus, LogLevel.Error);
            await ResumeMemoryMonitoringAsync();
            Main.AccountsVM.StartSessionMaintenance();
        }
        finally { IsChangingData = false; if (window is not null) window.IsEnabled = true; }
    }
}
