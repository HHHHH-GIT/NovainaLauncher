using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.App.ViewModels;

public sealed partial class VersionsViewModel : ViewModelBase
{
    public MainViewModel Main { get; }

    [ObservableProperty]
    private string _gameRoot;

    [ObservableProperty]
    private bool _isScanning = false;

    public RangeObservableCollection<VersionInfo> Versions { get; } = new();
    public bool InContentPage { get; set; }
    public double ScrollOffset { get; set; }

    public VersionsViewModel(MainViewModel main)
    {
        Main = main;
        _gameRoot = main.Settings.GameRoot;
    }

    [RelayCommand]
    public async Task ScanVersionsAsync()
    {
        if (IsScanning) return;
        IsScanning = true;

        try
        {
            var normalized = AppPaths.NormalizeRoot(GameRoot);
            GameRoot = normalized;
            Main.Settings.GameRoot = normalized;
            Main.SettingsStore.Save(Main.Settings);

            Main.Log.Write($"正在扫描版本目录: {normalized}", LogLevel.Info, true);
            var scanned = await Main.Versions.ScanAsync(normalized);

            Versions.ReplaceAll(scanned);

            // Even the same ID can belong to a different selected game root.
            var selectedId = Main.CurrentVersion?.Id ?? Main.Settings.SelectedVersionId;
            Main.CurrentVersion = Versions.FirstOrDefault(v => v.Id == selectedId && v.IsValid)
                ?? Versions.FirstOrDefault(v => v.IsValid);

            Main.Log.Write($"扫描完成，找到 {Versions.Count} 个版本", LogLevel.Info, true);
        }
        catch (Exception ex)
        {
            Main.Log.Write($"扫描版本失败: {ex.Message}", LogLevel.Error, true);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    public void SelectVersion(VersionInfo? version)
    {
        if (version is { IsValid: true })
        {
            Main.CurrentVersion = version;
        }
    }

    [RelayCommand] public void SelectAndLaunch(VersionInfo? version)
    {
        if (version is not { IsValid: true }) return;
        SelectVersion(version);
        Main.Navigate("Launch");
    }

    [RelayCommand] private void ManageVersion(VersionInfo? version)
    {
        if (version is not null) Main.ContentVM.OpenOverview(version);
    }

    [RelayCommand]
    public void BrowseGameRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 .minecraft 游戏目录",
            InitialDirectory = Directory.Exists(GameRoot) ? GameRoot : AppPaths.DefaultGameRoot
        };

        if (dialog.ShowDialog() == true)
        {
            GameRoot = dialog.FolderName;
            _ = ScanVersionsAsync();
        }
    }

    [RelayCommand]
    public void OpenVersionsFolder()
    {
        var dir = Path.Combine(GameRoot, "versions");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    public void OpenGameFolder(VersionInfo? version)
    {
        if (version is null) return;
        var dir = VersionService.ResolveGameDirectory(version, Main.Settings);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    public void OpenModsFolder(VersionInfo? version)
    {
        if (version is null) return;
        Main.ContentVM.Open(version, VersionContentKind.Mod);
    }
    [RelayCommand] private void ManageSaves(VersionInfo? version) { if (version is not null) Main.ContentVM.Open(version, VersionContentKind.Save); }
    [RelayCommand] private void ManageResources(VersionInfo? version) { if (version is not null) Main.ContentVM.Open(version, VersionContentKind.ResourcePack); }
}
