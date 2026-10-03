using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Launcher.App.ViewModels;
using Launcher.Core;

namespace Launcher.App.Views.Pages;

public partial class VersionsPage : UserControl
{
    private bool _restoring = true;
    public VersionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded) return;
            if (DataContext is VersionsViewModel vm && VersionsList.Template.FindName("PART_ScrollViewer", VersionsList) is ScrollViewer scroll) scroll.ScrollToVerticalOffset(vm.ScrollOffset);
            _restoring = false;
        }));
        Unloaded += (_, _) => _restoring = true;
    }
    private void ScrollChanged(object sender, ScrollChangedEventArgs e) { if (!_restoring && IsLoaded && DataContext is VersionsViewModel vm && e.VerticalChange != 0) vm.ScrollOffset = e.VerticalOffset; }
    private void VersionClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: VersionInfo version } row || DataContext is not VersionsViewModel vm) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source != row)
        {
            if (source is ButtonBase) return;
            source = VisualTreeHelper.GetParent(source);
        }
        row.Focus();
        if (e.ClickCount == 2) vm.SelectAndLaunch(version); else vm.SelectVersion(version);
        e.Handled = true;
    }
    private void VersionKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { DataContext: VersionInfo version } row || DataContext is not VersionsViewModel vm || !row.IsKeyboardFocused) return;
        if (e.Key == Key.Enter) { vm.SelectAndLaunch(version); e.Handled = true; }
        else if (e.Key == Key.Space) { vm.SelectVersion(version); e.Handled = true; }
    }
}
