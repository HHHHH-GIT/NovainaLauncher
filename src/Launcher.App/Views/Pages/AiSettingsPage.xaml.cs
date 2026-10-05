using System.Windows;
using System.Windows.Controls;
using Launcher.App.ViewModels;

namespace Launcher.App.Views.Pages;

public partial class AiSettingsPage : UserControl
{
    public AiSettingsPage() => InitializeComponent();
    private void KeyChanged(object sender, RoutedEventArgs e) { if (DataContext is AgentViewModel vm) vm.PendingKey = ((PasswordBox)sender).Password; }
    private async void SaveKeyClick(object sender, RoutedEventArgs e) { if (DataContext is AgentViewModel vm) { await vm.ConnectCommand.ExecuteAsync(null); AiSettingsKey.Clear(); } }
}
