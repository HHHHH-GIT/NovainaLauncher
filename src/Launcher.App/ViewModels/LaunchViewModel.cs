using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class LaunchViewModel : ViewModelBase
{
    public MainViewModel Main { get; }

    public LaunchViewModel(MainViewModel main)
    {
        Main = main;
    }

    [RelayCommand]
    public void GoToVersions() => Main.Navigate("Versions");

    [RelayCommand]
    public void GoToAccounts() => Main.Navigate("Accounts");

    [RelayCommand]
    public void GoToSettings() => Main.Navigate("Settings");

    [RelayCommand]
    public Task LaunchGameAsync() => Main.LaunchGameAsync();

    [RelayCommand]
    public void CancelLaunch() => Main.CancelLaunch();
}
