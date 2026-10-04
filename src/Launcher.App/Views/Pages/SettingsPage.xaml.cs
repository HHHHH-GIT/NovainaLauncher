using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using Launcher.App.Controls;
using Launcher.App.ViewModels;
using Launcher.Core;

namespace Launcher.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    private void AiKeyChanged(object sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) vm.Main.AgentVM.PendingKey = ((PasswordBox)sender).Password; }
    private async void SaveAiKeyClick(object sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) { await vm.Main.AgentVM.ConnectCommand.ExecuteAsync(null); SettingsAiKey.Clear(); } }
    private void CurseForgeKeyChanged(object sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) vm.PendingCurseForgeKey = ((PasswordBox)sender).Password; }
    private void SaveCurseForgeKeyClick(object sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) { vm.SaveCurseForgeKeyCommand.Execute(null); CurseForgeKeyBox.Clear(); } }
    private SettingsViewModel? _vm;
    private readonly DispatcherTimer _scrollTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _watch = new();
    private double _from, _to;
    private TimeSpan _duration;
    private string? _jumpSection;
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _vm = DataContext as SettingsViewModel;
            if (_vm is null) return;
            _vm.SectionRequested += Jump; MotionPolicy.Changed += CompleteScroll;
            if (_vm.HasPendingSection) Jump(); else SettingsScroll.ScrollToVerticalOffset(_vm.ScrollOffset);
        };
        Unloaded += (_, _) =>
        {
            StopScroll(); MotionPolicy.Changed -= CompleteScroll;
            if (_vm is not null) _vm.SectionRequested -= Jump;
            _vm = null;
        };
        _scrollTimer.Tick += (_, _) =>
        {
            var ratio = Math.Clamp(_watch.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - ratio, 3);
            SettingsScroll.ScrollToVerticalOffset(_from + (_to - _from) * eased);
            if (ratio >= 1) StopScroll(true);
        };
        SettingsScroll.PreviewMouseWheel += (_, _) => StopScroll();
        SettingsScroll.PreviewMouseDown += (_, _) => StopScroll();
        SettingsScroll.PreviewKeyDown += (_, _) => StopScroll();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _vm?.ShowDownloadDetails == true) { _vm.ShowDownloadDetails = false; e.Handled = true; } };
    }
    private void DownloadOverlayVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue) DownloadOverlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private IEnumerable<FrameworkElement> Anchors => SectionsPanel.Children.OfType<FrameworkElement>().Where(x => x.Tag is string);
    private void StopScroll(bool preserveSelection = false) { _scrollTimer.Stop(); if (!preserveSelection) _jumpSection = null; }
    private void CompleteScroll() { if (_scrollTimer.IsEnabled) SettingsScroll.ScrollToVerticalOffset(_to); StopScroll(true); }
    private void Jump()
    {
        if (_vm is null) return;
        var requested = _vm.SelectedSection;
        StopScroll(); SettingsScroll.UpdateLayout();
        var anchor = Anchors.First(x => (string)x.Tag == requested);
        _vm.SelectedSection = requested;
        _from = SettingsScroll.VerticalOffset;
        _to = Math.Clamp(anchor.TranslatePoint(new Point(), SectionsPanel).Y + SectionsPanel.Margin.Top - 16, 0, SettingsScroll.ScrollableHeight);
        _vm.HasPendingSection = false;
        _jumpSection = (string)anchor.Tag;
        _duration = MotionPolicy.Scroll;
        if (_duration == TimeSpan.Zero) SettingsScroll.ScrollToVerticalOffset(_to);
        else { _watch.Restart(); _scrollTimer.Start(); }
    }
    private void SettingsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ScrollOffset = SettingsScroll.VerticalOffset;
        if (_scrollTimer.IsEnabled || _vm.HasPendingSection) return;
        if (_jumpSection is not null && Math.Abs(SettingsScroll.VerticalOffset - _to) < 1) { _vm.SelectedSection = _jumpSection; return; }
        _jumpSection = null;
        var selected = Anchors.LastOrDefault(x => x.TranslatePoint(new Point(), SettingsScroll).Y <= 64) ?? AppearanceSection;
        if (SettingsScroll.ScrollableHeight > 0 && SettingsScroll.VerticalOffset >= SettingsScroll.ScrollableHeight - 1) selected = Anchors.Last();
        _vm.SelectedSection = (string)selected.Tag;
    }
    private void SelectJava_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JavaRuntimeInfo info } || DataContext is not SettingsViewModel vm) return;
        vm.Main.CurrentJava = info;
        if (vm.Main.CurrentVersion is not null)
        {
            vm.Main.Settings.JavaOverrides[AppPaths.VersionKey(vm.Main.CurrentVersion)] = info.Path;
            vm.Main.SettingsStore.Save(vm.Main.Settings);
        }
        vm.Main.Log.Write($"已选择 Java: {info.Label}", LogLevel.Info, true);
    }
}
