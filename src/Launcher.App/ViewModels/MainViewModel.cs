using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly SecretStore _secretStore;
    private readonly AccountService _accountService;
    private readonly JavaService _javaService;
    private readonly VersionService _versionService;
    private readonly LaunchService _launchService;
    private readonly LogService _logService;
    private readonly HttpClient _http;
    private CancellationTokenSource? _launchCts;

    [ObservableProperty]
    private string _currentPage = "Launch";

    [ObservableProperty]
    private bool _sidebarExpanded = true;

    [ObservableProperty]
    private DanmakuMode _danmakuMode = DanmakuMode.Selected;

    [ObservableProperty]
    private AccountProfile? _currentAccount;

    [ObservableProperty]
    private VersionInfo? _currentVersion;

    [ObservableProperty]
    private JavaRuntimeInfo? _currentJava;

    [ObservableProperty]
    private LaunchState _launchState = LaunchState.Idle;

    [ObservableProperty]
    private string _launchStatusText = "就绪";

    [ObservableProperty]
    private double _launchProgress = 0;

    [ObservableProperty]
    private bool _isLaunching = false;

    public LauncherSettings Settings { get; private set; }
    public System.Windows.Threading.Dispatcher UiDispatcher { get; } = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    public LaunchViewModel LaunchVM { get; }
    public VersionsViewModel VersionsVM { get; }
    public AccountsViewModel AccountsVM { get; }
    public LogsViewModel LogsVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public VersionContentViewModel ContentVM { get; }
    public DownloadsViewModel DownloadsVM { get; }
    public Services.LauncherOperations Operations { get; }
    public AgentViewModel AgentVM { get; }
    public string? ActiveGameDirectory { get; private set; }

    public ViewModelBase CurrentViewModel => CurrentPage switch
    {
        "Versions" => VersionsVM.InContentPage ? ContentVM : VersionsVM, "Accounts" => AccountsVM,
        "Downloads" => DownloadsVM, "Logs" => LogsVM, "Settings" => SettingsVM, _ => LaunchVM
    };
    public bool CanCancelLaunch => LaunchState is LaunchState.Preparing or LaunchState.Installing or LaunchState.Starting;
    partial void OnCurrentPageChanged(string value) { OnPropertyChanged(nameof(CurrentViewModel)); if (value == "Downloads") DownloadsVM.EnsureLoaded(); }
    partial void OnLaunchStateChanged(LaunchState value) => OnPropertyChanged(nameof(CanCancelLaunch));

    public LogService Log => _logService;
    public JavaService Java => _javaService;
    public VersionService Versions => _versionService;
    public AccountService Accounts => _accountService;
    public IAccountSkinService Skins { get; }
    public LaunchService Launch => _launchService;
    public SettingsStore SettingsStore => _settingsStore;
    public SecretStore Secrets => _secretStore;

    public string DanmakuModeLabel => DanmakuMode switch
    {
        DanmakuMode.Off => "弹幕: 关",
        DanmakuMode.Selected => "弹幕: 精选",
        DanmakuMode.All => "弹幕: 全开",
        _ => "弹幕"
    };

    public MainViewModel(
        SettingsStore settingsStore,
        SecretStore secretStore,
        AccountService accountService,
        JavaService javaService,
        VersionService versionService,
        LaunchService launchService,
        LogService logService,
        HttpClient http,
        bool autoLoadDownloads = true)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _accountService = accountService;
        _javaService = javaService;
        _versionService = versionService;
        _launchService = launchService;
        _logService = logService;
        _http = http;
        Skins = new Services.AccountSkinService(accountService);

        Settings = _settingsStore.Load();
        _sidebarExpanded = Settings.SidebarExpanded;
        _danmakuMode = Settings.Danmaku;

        _launchService.StateChanged += OnLaunchStateChanged;
        _launchService.ProgressChanged += HandleLaunchProgress;

        LaunchVM = new LaunchViewModel(this);
        VersionsVM = new VersionsViewModel(this);
        AccountsVM = new AccountsViewModel(this);
        LogsVM = new LogsViewModel(this);
        SettingsVM = new SettingsViewModel(this);
        ContentVM = new VersionContentViewModel(this);
        DownloadsVM = new DownloadsViewModel(this, autoLoadDownloads);
        Operations = new Services.LauncherOperations(this);
        AgentVM = new AgentViewModel(this);
        if (Secrets.ReadJson<string>("curseforge-key") is { Length: > 0 } key) Log.RegisterSecret(key);
    }

    public async Task InitializeAsync()
    {
        _logService.Write("Novaina Launcher 启动初始化中...", LogLevel.Info, true);
        await SettingsVM.StartMemoryMonitoringAsync();

        // Load accounts
        AccountsVM.LoadAccounts();
        if (AccountsVM.Accounts.Count > 0)
        {
            var matched = AccountsVM.Accounts.FirstOrDefault(a => a.Id == Settings.SelectedAccountId);
            CurrentAccount = matched ?? AccountsVM.Accounts[0];
        }

        // Scan versions
        await VersionsVM.ScanVersionsAsync();
        if (VersionsVM.Versions.Count > 0)
        {
            var matched = VersionsVM.Versions.FirstOrDefault(v => v.Id == Settings.SelectedVersionId);
            CurrentVersion = matched ?? VersionsVM.Versions.FirstOrDefault(v => v.IsValid);
        }

        // Scan Javas
        await SettingsVM.ScanJavaAsync();
        AutoSelectJava();
        if (SettingsVM is not null) _ = SettingsVM.RefreshMemoryAsync();

        _logService.Write("启动器就绪", LogLevel.Info, true);
        AccountsVM.StartSessionMaintenance();
        DownloadsVM.EnsureLoaded();
    }

    public void AutoSelectJava()
    {
        if (CurrentVersion is null) { CurrentJava = null; return; }
        var manual = Settings.JavaOverrides.TryGetValue(AppPaths.VersionKey(CurrentVersion), out var p) ? p : null;
        var selected = JavaService.Select(SettingsVM.InstalledJavas, CurrentVersion.RequiredJava, manual);
        CurrentJava = selected;
    }

    partial void OnCurrentVersionChanged(VersionInfo? value)
    {
        Settings.SelectedVersionId = value?.Id;
        _settingsStore.Save(Settings);
        AutoSelectJava();
        if (SettingsVM is not null) _ = SettingsVM.RefreshMemoryAsync();
    }

    partial void OnCurrentAccountChanged(AccountProfile? value)
    {
        Settings.SelectedAccountId = value?.Id;
        _settingsStore.Save(Settings);
        AccountsVM.AccountChanged(value);
    }

    partial void OnSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(EffectiveSidebarExpanded));
        OnPropertyChanged(nameof(TopNavigationVisible));
        Settings.SidebarExpanded = value;
        _settingsStore.Save(Settings);
    }

    partial void OnDanmakuModeChanged(DanmakuMode value)
    {
        Settings.Danmaku = value;
        _settingsStore.Save(Settings);
        OnPropertyChanged(nameof(DanmakuModeLabel));
        SettingsVM?.SynchronizeDanmakuMode();
    }

    [RelayCommand]
    public void Navigate(string page)
    {
        if (page is not ("Launch" or "Versions" or "Downloads" or "Accounts" or "Logs" or "Settings")) return;
        if (CurrentPage != page) AccountsVM.CloseAllModals();
        CurrentPage = page;
    }
    public void RefreshPage() => OnPropertyChanged(nameof(CurrentViewModel));
    public bool IsGameOperationBusy(string directory) => IsLaunching && string.Equals(ActiveGameDirectory, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) || ContentVM.IsBusy && string.Equals(ContentVM.GameDirectory, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) || Operations?.IsDirectoryBusy(directory) == true;
    public bool IsDirectoryBusy(string directory) => IsGameOperationBusy(directory) || DownloadsVM.Queue.IsDirectoryBusy(directory);

    [RelayCommand]
    public void ToggleSidebar()
    {
        SidebarExpanded = !SidebarExpanded;
    }

    [RelayCommand]
    public void CycleDanmakuMode()
    {
        DanmakuMode = DanmakuMode switch
        {
            DanmakuMode.Off => DanmakuMode.Selected,
            DanmakuMode.Selected => DanmakuMode.All,
            DanmakuMode.All => DanmakuMode.Off,
            _ => DanmakuMode.Selected
        };
    }

    [RelayCommand]
    public async Task LaunchGameAsync()
    {
        if (IsLaunching) return;
        if (ContentVM.IsRenaming) { LaunchStatusText = "请等待游戏改名完成"; return; }

        if (CurrentAccount is null)
        {
            Launcher.App.Controls.AppDialog.Show("请先选择或添加一个账户！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            Navigate("Accounts");
            return;
        }

        if (CurrentVersion is null || !CurrentVersion.IsValid)
        {
            Launcher.App.Controls.AppDialog.Show("请选择一个可用的 Minecraft 版本！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            Navigate("Versions");
            return;
        }

        if (CurrentJava is null)
        {
            Launcher.App.Controls.AppDialog.Show("未找到匹配的 Java 运行时，请在设置中下载或选择 Java！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            Navigate("Settings");
            return;
        }

        try { await LaunchPreparedAsync(CancellationToken.None); }
        catch (OperationCanceledException) { LaunchStatusText = "已取消启动"; }
        catch (Exception ex) { LaunchStatusText = "启动失败: " + ex.Message; Controls.AppDialog.Show(ex.Message, "启动出错", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // Both normal UI and Agent use the same fixed request and lifecycle.
    public async Task LaunchPreparedAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await AccountsVM.WaitForSessionMaintenanceAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (IsLaunching || ContentVM.IsRenaming) throw new InvalidOperationException("游戏正在运行或处理文件");
        if (CurrentAccount is null || CurrentVersion is not { IsValid: true } || CurrentJava is null) throw new InvalidOperationException("请准备账户、有效游戏和匹配的 Java");
        var account = CurrentAccount; var version = CurrentVersion; var java = CurrentJava;
        var isolation = Settings.IsolationOverrides.TryGetValue(AppPaths.VersionKey(version), out var iso) ? iso : IsolationMode.Auto;
        var directory = VersionService.ResolveGameDirectory(version, Settings);
        if (IsDirectoryBusy(directory)) throw new InvalidOperationException("请等待目标游戏的任务完成");
        _launchCts?.Dispose();
        _launchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        IsLaunching = true;
        ActiveGameDirectory = directory;
        ContentVM.NotifyAvailability();
        LaunchProgress = 0;

        try
        {
            var memory = await SettingsVM.RefreshMemoryAsync(version);
            _launchCts.Token.ThrowIfCancellationRequested();
            if (memory < 256) throw new InvalidOperationException("可用内存不足或内存信息不可用");
            if (ContentVM.IsBusy && string.Equals(ContentVM.GameDirectory, directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请等待文件操作完成");
            var request = new LaunchRequest(account, version, java, memory, isolation, directory);
            await _launchService.LaunchAsync(request, Settings.MicrosoftClientId, _launchCts.Token, DownloadsVM.SourcePolicy());
        }
        finally
        {
            if (LaunchState != LaunchState.Running)
            {
                IsLaunching = false;
                ActiveGameDirectory = null;
                ContentVM.NotifyAvailability();
            }
        }
    }

    [RelayCommand]
    public void CancelLaunch()
    {
        _launchCts?.Cancel();
    }

    private void OnLaunchStateChanged(LaunchState state, string message)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            LaunchState = state;
            LaunchStatusText = message;
            if (state == LaunchState.Running)
            {
                IsLaunching = true;
            }
            else if (state is LaunchState.Exited or LaunchState.Failed or LaunchState.Idle)
            {
                IsLaunching = false;
                ActiveGameDirectory = null;
            }
            ContentVM.NotifyAvailability();
        });
    }

    private void HandleLaunchProgress(double progress)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            LaunchProgress = progress;
        });
    }
}
