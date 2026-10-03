using System.Windows.Controls;
using Launcher.App.ViewModels;

namespace Launcher.App.Views.Pages;

public partial class LogsPage : UserControl
{
    public LogsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is LogsViewModel vm) { vm.BatchUpdated += FollowTail; FollowTail(); } };
        Unloaded += (_, _) => { if (DataContext is LogsViewModel vm) vm.BatchUpdated -= FollowTail; };
    }
    private void FollowTail()
    {
        if (IsVisible && DataContext is LogsViewModel { AutoScroll: true } vm && vm.Logs.Count > 0)
            LogList.ScrollIntoView(vm.Logs[^1]);
    }
    private void LogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.VerticalChange != 0 && DataContext is LogsViewModel vm)
            vm.AutoScroll = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1;
    }
}
