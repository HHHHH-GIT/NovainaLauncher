using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;
using Microsoft.Win32;
using Launcher.App.Controls;

namespace Launcher.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    public MainViewModel Main { get; }
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }
    private CancellationTokenSource? _downloadCts;
    public async Task CancelAndWaitAsync()
    {
        CancelJavaDownload();
        if (DownloadAdoptiumJavaCommand.ExecutionTask is { } task) await task;
    }
    public void Dispose() { _memoryLifetime.Cancel(); _downloadCts?.Cancel(); if (_saveTimer.IsEnabled) { _saveTimer.Stop(); Main.SettingsStore.Save(Main.Settings); } }
    public static IReadOnlyList<SettingsSection> Sections { get; } = [new("Appearance", "外观与动画"), new("Danmaku", "弹幕"), new("Launch", "启动"), new("Java", "Java"), new("Downloads", "下载"), new("Data", "数据"), new("AI", "AI"), new("Advanced", "高级")];
    [ObservableProperty] private string _selectedSection = "Appearance";
    public double ScrollOffset { get; set; }
    public bool HasPendingSection { get; set; }
    public event Action? SectionRequested;
    [RelayCommand] public void NavigateSection(string id)
    {
        if (!Sections.Any(x => x.Id == id)) return;
        Main.Navigate("Settings"); SelectedSection = id; HasPendingSection = true; SectionRequested?.Invoke();
    }
    [ObservableProperty] private int _animationIndex = 2;
    public string AnimationLabel => AnimationIndex switch { 0 => "关闭", 1 => "性能", _ => "舒缓" };
    partial void OnAnimationIndexChanged(int value)
    {
        var mode = (UiAnimationMode)Math.Clamp(value, 0, 2);
        Main.Settings.UiAnimationMode = mode; MotionPolicy.Set(mode); ScheduleSave(); OnPropertyChanged(nameof(AnimationLabel));
    }
    [ObservableProperty] private bool _showDownloadDetails;
    [ObservableProperty] private bool _isCancellingDownload;
    [ObservableProperty] private string _downloadSpeed = "";
    [ObservableProperty] private string _downloadError = "";
    [ObservableProperty] private bool _downloadIndeterminate;
    public ObservableCollection<DownloadConnectionProgress> DownloadConnections { get; } = new();
    [RelayCommand] private void ToggleDownloadDetails() => ShowDownloadDetails = !ShowDownloadDetails;
    [RelayCommand] private void CancelJavaDownload()
    {
        if (_downloadCts is null || !IsDownloadingJava || IsCancellingDownload) return;
        IsCancellingDownload = true; JavaDownloadStatus = "正在取消"; _downloadCts.Cancel();
    }



    [ObservableProperty]
    private int _memoryMb;

    [ObservableProperty]
    private string _microsoftClientId;

    [ObservableProperty] private string _theme = "System";
    public string ThemeLabel => Theme switch { "Dark" => "深色", "Light" => "浅色", _ => "跟随系统" };
    partial void OnThemeChanged(string value)
    {
        Main.Settings.Theme = value;
        Main.SettingsStore.Save(Main.Settings);
        OnPropertyChanged(nameof(ThemeLabel));
    }
    [RelayCommand] private void SelectTheme(string theme)
    {
        if (theme is "System" or "Light" or "Dark") Theme = theme;
    }

    [ObservableProperty]
    private bool _isScanningJava = false;

    [ObservableProperty]
    private bool _isDownloadingJava = false;

    [ObservableProperty]
    private double _javaDownloadProgress = 0;

    [ObservableProperty]
    private string _javaDownloadStatus = "";

    public ObservableCollection<JavaRuntimeInfo> InstalledJavas { get; } = new();

    public string MemoryLabel => $"{MemoryMb} MB ({(MemoryMb / 1024.0):F1} GB)";

    public SettingsViewModel(MainViewModel main)
    {
        Main = main;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Main.SettingsStore.Save(Main.Settings); };
        _memoryMb = main.Settings.MemoryMb;
        _availableMemoryMb = Math.Max(256, _memoryMb);
        _smartMemory = main.Settings.MemoryAllocationMode != MemoryAllocationMode.Manual;
        _javaDownloadDirectory = string.IsNullOrWhiteSpace(main.Settings.JavaDownloadDirectory) ? AppPaths.Runtime : main.Settings.JavaDownloadDirectory;
        _windowTransparencyPercent = Math.Clamp(main.Settings.WindowTransparencyPercent, 0, 50);
        _microsoftClientId = main.Settings.MicrosoftClientId;
        _theme = main.Settings.Theme;
        _animationIndex = Enum.IsDefined(main.Settings.UiAnimationMode) ? (int)main.Settings.UiAnimationMode : 2;
        MotionPolicy.Set((UiAnimationMode)_animationIndex);
        _preferGameMirror = main.Settings.PreferGameMirror;
        _preferContentMirror = main.Settings.PreferContentMirror;
        _curseForgeKeyStatus = main.Secrets.ReadJson<string>("curseforge-key") is { Length: > 0 } ? "已配置" : "未配置";
        InitializeDanmaku();
    }

    partial void OnMemoryMbChanged(int value)
    {
        if (!_applyingMemory) SmartMemory = false;
        Main.Settings.MemoryMb = value;
        ScheduleSave();
        OnPropertyChanged(nameof(MemoryLabel));
    }

    partial void OnMicrosoftClientIdChanged(string value)
    {
        Main.Settings.MicrosoftClientId = value.Trim();
        ScheduleSave();
    }

    [RelayCommand]
    public async Task ScanJavaAsync()
    {
        if (IsScanningJava) return;
        IsScanningJava = true;

        try
        {
            Main.Log.Write("正在扫描本地 Java 运行时...", LogLevel.Info, true);
            var overrides = Main.Settings.JavaOverrides.Values;
            var list = await Main.Java.ScanAsync(Main.Settings.GameRoot, overrides, downloadDirectory: JavaDownloadDirectory);

            InstalledJavas.Clear();
            foreach (var j in list)
            {
                InstalledJavas.Add(j);
            }

            Main.AutoSelectJava();
            Main.Log.Write($"Java 扫描完成，发现 {InstalledJavas.Count} 个可用环境", LogLevel.Info, true);
        }
        catch (Exception ex)
        {
            Main.Log.Write($"扫描 Java 出错: {ex.Message}", LogLevel.Error, true);
        }
        finally
        {
            IsScanningJava = false;
        }
    }

    [RelayCommand]
    public void SelectManualJava()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 java.exe 或 javaw.exe",
            Filter = "Java 可执行文件 (java.exe;javaw.exe)|java.exe;javaw.exe|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            _ = AddCustomJavaAsync(dialog.FileName);
        }
    }

    private async Task AddCustomJavaAsync(string path)
    {
        try
        {
            var info = await JavaService.ProbeAsync(path);
            if (info is null)
            {
                Launcher.App.Controls.AppDialog.Show("所选文件不是有效的 Java 运行时！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var existing = InstalledJavas.FirstOrDefault(j => j.Path.Equals(info.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                InstalledJavas.Insert(0, info);
            }

            Main.CurrentJava = info;
            Main.Log.Write($"已添加自定义 Java: {info.Label}", LogLevel.Info, true);
        }
        catch (Exception ex)
        {
            Launcher.App.Controls.AppDialog.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public async Task DownloadAdoptiumJavaAsync(string majorString)
    {
        if (IsDownloadingJava || !int.TryParse(majorString, out var major)) return;

        IsDownloadingJava = true; IsCancellingDownload = false;
        JavaDownloadProgress = 0; DownloadSpeed = ""; DownloadError = ""; DownloadConnections.Clear();
        JavaDownloadStatus = $"准备 Java {major}";
        using var cts = new CancellationTokenSource(); _downloadCts = cts;
        var downloadDirectory = JavaDownloadDirectory;
        try
        {
            var progress = new Progress<JavaDownloadProgress>(p =>
            {
                if (_downloadCts != cts || IsCancellingDownload || cts.IsCancellationRequested) return;
                JavaDownloadProgress = p.Percent;
                DownloadIndeterminate = p.Stage != JavaDownloadStage.Downloading || p.TotalBytes is null;
                DownloadSpeed = p.Stage == JavaDownloadStage.Downloading ? $"{p.BytesPerSecond / 1048576d:F2} MB/s" : "";
                JavaDownloadStatus = p.Stage switch
                {
                    JavaDownloadStage.Downloading => p.TotalBytes is null ? $"Java {major} · 下载中" : $"Java {major} · {p.Percent:F0}%",
                    JavaDownloadStage.Verifying => "下载完成 · 正在校验",
                    JavaDownloadStage.Extracting => "正在解压",
                    JavaDownloadStage.Completed => $"Java {major} 已就绪", _ => $"准备 Java {major}"
                };
                for (int i = 0; i < p.Connections.Count; i++)
                    if (i < DownloadConnections.Count) DownloadConnections[i] = p.Connections[i]; else DownloadConnections.Add(p.Connections[i]);
            });
            var info = await Main.Java.DownloadAsync(major, progress, cts.Token, downloadDirectory);
            if (!InstalledJavas.Any(j => j.Path.Equals(info.Path, StringComparison.OrdinalIgnoreCase))) InstalledJavas.Insert(0, info);
            Main.AutoSelectJava();
            JavaDownloadProgress = 100; JavaDownloadStatus = $"Java {major} 已就绪";
            for (int i = 0; i < DownloadConnections.Count; i++) DownloadConnections[i] = DownloadConnections[i] with { State = "完成", BytesPerSecond = 0 };
            Main.Log.Write($"Java {major} 已就绪", LogLevel.Info, true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            JavaDownloadStatus = "已取消"; Main.Log.Write("已取消 Java 下载", LogLevel.Info, true);
            for (int i = 0; i < DownloadConnections.Count; i++) DownloadConnections[i] = DownloadConnections[i] with { State = "已取消", BytesPerSecond = 0 };
        }
        catch (Exception ex)
        {
            JavaDownloadStatus = "下载失败"; DownloadError = ex.Message; ShowDownloadDetails = true;
            Main.Log.Write($"下载 Java {major} 失败: {ex.Message}", LogLevel.Error, true);
            for (int i = 0; i < DownloadConnections.Count; i++) DownloadConnections[i] = DownloadConnections[i] with { State = "已停止", BytesPerSecond = 0 };
        }
        finally
        {
            _downloadCts = null; IsDownloadingJava = false; IsCancellingDownload = false; DownloadIndeterminate = false; DownloadSpeed = "";
        }
    }

    [RelayCommand]
    public void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.Data);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Data}\"") { UseShellExecute = true });
    }
}

public sealed record SettingsSection(string Id, string Name);

