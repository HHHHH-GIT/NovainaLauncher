using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using Launcher.App.Controls;
using Launcher.App.ViewModels;

namespace Launcher.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private DanmakuController? _danmaku;
    private WindowAppearance? _appearance;
    private WindowOutline? _outline;
    private NativeWindowFrame? _nativeFrame;
    private HwndSource? _source;
    private bool _waitingForDownload;
    private bool _shutdownReady;
    private int _dialogCount;
    private bool _loaded;
    private readonly CancellationTokenSource _startupLifetime = new();
    public Task InitializationTask { get; private set; } = Task.CompletedTask;
    public Task StartupAnimationTask { get; private set; } = Task.CompletedTask;

    public MainWindow(MainViewModel viewModel, bool playStartupAnimation = true)
    {
        InitializeComponent();
        if (Environment.OSVersion.Version.Build < 22000)
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
        _viewModel = viewModel;
        DataContext = viewModel;
        StartupScene.Prepare(LauncherShell, playStartupAnimation);
        SidebarBorder.Width = viewModel.SidebarExpanded ? 200 : 0;
        _viewModel.PropertyChanged += ViewModelChanged;
        MotionPolicy.Changed += ResetSidebar;
        Loaded += MainWindow_Loaded;
        Closing += async (_, e) =>
        {
            if (_viewModel.SettingsVM.IsChangingData && Application.Current is App { IsRestarting: false }) { e.Cancel = true; return; }
            if (_waitingForDownload) { e.Cancel = true; return; }
            if (_shutdownReady) return;
            _startupLifetime.Cancel(); StartupScene.Finish();
            e.Cancel = true; _waitingForDownload = true;
            try { _viewModel.CancelLaunch(); await _viewModel.AgentVM.CancelAndWaitAsync(); await Task.WhenAll(_viewModel.AccountsVM.CancelAndWaitAsync(), _viewModel.SettingsVM.CancelAndWaitAsync(), _viewModel.ContentVM.CancelAndWaitAsync(), _viewModel.DownloadsVM.CancelAndWaitAsync()); if (_viewModel.LaunchGameCommand.ExecutionTask is { } launch) await launch; }
            catch (Exception ex) { _viewModel.Log.Write("关闭任务：" + ex.Message, Launcher.Core.LogLevel.Warning); }
            finally { _shutdownReady = true; _waitingForDownload = false; Close(); }
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(handle);
            if (_source is not null && Environment.OSVersion.Version.Build < 22000)
                _nativeFrame = new NativeWindowFrame(this, _source);
            _source?.AddHook(WindowHook);
            SetWindowLongPtr(handle, -16, new IntPtr(GetWindowLongPtr(handle, -16).ToInt64() & ~0x10000L));
            _appearance = new WindowAppearance(this, _viewModel.SettingsVM);
            if (_source is not null) _outline = new WindowOutline(this, WindowSurface, _source);
        };
        Closed += (_, _) =>
        {
            _startupLifetime.Cancel(); StartupScene.Finish(); _startupLifetime.Dispose();
            _viewModel.PropertyChanged -= ViewModelChanged;
            MotionPolicy.Changed -= ResetSidebar;
            _viewModel.AccountsVM.CloseAllModals();
            (_viewModel.Skins as IDisposable)?.Dispose();
            _viewModel.LogsVM.Dispose();
            _viewModel.SettingsVM.Dispose();
            _danmaku?.Dispose(); _appearance?.Dispose(); _outline?.Dispose(); _nativeFrame?.Dispose();
            _source?.RemoveHook(WindowHook);
        };
    }
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _danmaku = new DanmakuController(DanmakuCanvas, _viewModel);
        _danmaku.SetSuspended(StartupScene.IsActive);
        StartupAnimationTask = RevealStartupAsync();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        if (_startupLifetime.IsCancellationRequested) return;
        try { InitializationTask = _viewModel.InitializeAsync(); await InitializationTask; }
        catch (Exception ex) { _viewModel.LaunchStatusText = "初始化失败，请查看日志"; _viewModel.Log.Write(ex.Message, Launcher.Core.LogLevel.Error, true); }
    }
    private async Task RevealStartupAsync()
    {
        try { await StartupScene.PlayAsync(_startupLifetime.Token); }
        catch (Exception ex) { _viewModel.Log.Write("开场动画：" + ex.Message, Launcher.Core.LogLevel.Warning); }
        finally { StartupScene.Finish(); }
        if (_startupLifetime.IsCancellationRequested) return;
        _danmaku?.SetSuspended(_viewModel.IsAiMode || _dialogCount > 0);
        PageHost.Preload(new object[] { _viewModel.LaunchVM, _viewModel.VersionsVM, _viewModel.DownloadsVM, _viewModel.AccountsVM, _viewModel.LogsVM, _viewModel.SettingsVM, _viewModel.ContentVM });
    }
    private void ResetSidebar() { SidebarBorder.BeginAnimation(WidthProperty, null); SidebarBorder.Width = _viewModel.EffectiveSidebarExpanded ? 200 : 0; }
    public IDisposable ShowDialogShade()
    {
        _dialogCount++;
        DialogShade.Visibility = Visibility.Visible;
        _danmaku?.SetSuspended(true);
        return new DialogShadeScope(this);
    }
    private sealed class DialogShadeScope(MainWindow window) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--window._dialogCount == 0)
            {
                window.DialogShade.Visibility = Visibility.Collapsed;
                window._danmaku?.SetSuspended(false);
            }
        }
    }
    private static string[] DroppedArchives(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop)
        && data.GetData(DataFormats.FileDrop) is string[] paths
        ? paths.Where(path => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".mrpack", StringComparison.OrdinalIgnoreCase)).ToArray() : [];
    private void Files_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = !_waitingForDownload && !_shutdownReady && !_viewModel.SettingsVM.IsChangingData
            && DroppedArchives(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (_waitingForDownload || _shutdownReady || _viewModel.SettingsVM.IsChangingData) return;
        await _viewModel.DownloadsVM.ImportModpackFilesAsync(DroppedArchives(e.Data));
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAiMode)) _danmaku?.SetSuspended(StartupScene.IsActive || _viewModel.IsAiMode || _dialogCount > 0);
        if (e.PropertyName != nameof(MainViewModel.EffectiveSidebarExpanded)) return;
        double from = SidebarBorder.ActualWidth, to = _viewModel.EffectiveSidebarExpanded ? 200 : 0;
        SidebarBorder.BeginAnimation(WidthProperty, null);
        SidebarBorder.Width = to;
        if (MotionPolicy.Sidebar > TimeSpan.Zero)
            SidebarBorder.BeginAnimation(WidthProperty, new DoubleAnimation(from, to, MotionPolicy.Sidebar)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0112 && (wParam.ToInt64() & 0xfff0) == 0xf030 || message == 0x00a3) handled = true;
        if (message == 0x0231) { _danmaku?.SetSuspended(true); _appearance?.SetMoving(true); }
        if (message == 0x0232) { _danmaku?.SetSuspended(StartupScene.IsActive || _viewModel.IsAiMode || _dialogCount > 0); _appearance?.SetMoving(false); }
        return IntPtr.Zero;
    }
    private void SettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var section in SettingsViewModel.Sections)
        {
            var item = new MenuItem { Header = section.Name, Command = _viewModel.SettingsVM.NavigateSectionCommand, CommandParameter = section.Id, IsCheckable = true };
            item.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(SettingsViewModel.SelectedSection)) { Source = _viewModel.SettingsVM, Mode = BindingMode.OneWay, Converter = new EqualConverter(), ConverterParameter = section.Id });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void AccountLoginDone(object sender, RoutedEventArgs e) => _viewModel.CompleteAccountLogin();
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
