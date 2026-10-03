using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.App.Controls;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.App.ViewModels;

public sealed partial class VersionContentViewModel(MainViewModel main) : ViewModelBase
{
    public MainViewModel Main { get; } = main;
    private readonly VersionContentService _service = new();
    private CancellationTokenSource? _operationCts;
    private TaskCompletionSource? _idle;
    private readonly Dictionary<VersionContentKind, (DateTime Time, IReadOnlyList<VersionContentItem> Items)> _contentCache = new();
    public VersionInfo? Version { get; private set; }
    public string GameDirectory { get; private set; } = "";
    [ObservableProperty] private VersionContentKind _kind;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _section = "Overview";
    [ObservableProperty] private string _gameNameDraft = "";
    [ObservableProperty] private string _contentSummary = "";
    [ObservableProperty] private bool _isRenaming;
    public RangeObservableCollection<VersionContentItem> Items { get; } = new();
    public string Title => Version?.GameName ?? "游戏管理";
    public string VersionName => Version?.TagLabel ?? "";
    public string VersionDirectory => Version is null ? "" : Path.Combine(Version.Root, "versions", Version.Id);
    public string MinecraftLabel => Version?.MinecraftVersion is { Length: > 0 } v ? v : "未知";
    public string LoaderLabel => Version is null ? "" : Version.Loader + (Version.LoaderVersion.Length > 0 ? " " + Version.LoaderVersion : "");
    public string JavaLabel => Version?.RequiredJava is int major ? $"Java {major}" : "手动指定";
    public string ParentLabel => Version?.ParentId ?? "无";
    public string TypeLabel => Version?.VersionType switch { "release" => "正式版", "snapshot" => "快照", "old_alpha" => "Alpha", "old_beta" => "Beta", var type => type ?? "未知" };
    public string DirectoryLabel => string.Equals(VersionDirectory, GameDirectory, StringComparison.OrdinalIgnoreCase) ? "独立目录" : "共享目录";
    public bool IsOverview => Section == "Overview";
    public bool IsContents => !IsOverview;
    public bool CanRename => CanWrite && Version is { IsValid: true } && !Main.IsLaunching;
    public bool IsResources => Kind is VersionContentKind.ResourcePack or VersionContentKind.ShaderPack;
    public bool IsSave => Kind == VersionContentKind.Save;
    public bool CanToggle => !IsSave;
    public void InvalidateCache() { _contentCache.Clear(); if (Version is not null && !IsBusy) _ = ReloadAsync(CancellationToken.None); }
    public bool CanWrite => Version is not null && !IsBusy && !Main.IsDirectoryBusy(GameDirectory);
    public bool CanBrowse => !IsBusy;
    public string BusyNotice => Main.IsDirectoryBusy(GameDirectory) ? "游戏使用中" : "";
    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    partial void OnSectionChanged(string value) { OnPropertyChanged(nameof(IsOverview)); OnPropertyChanged(nameof(IsContents)); }
    partial void OnKindChanged(VersionContentKind value) { OnPropertyChanged(nameof(IsResources)); OnPropertyChanged(nameof(IsSave)); OnPropertyChanged(nameof(CanToggle)); }
    public void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanWrite)); OnPropertyChanged(nameof(CanBrowse)); OnPropertyChanged(nameof(BusyNotice)); OnPropertyChanged(nameof(CanRename));
    }
    public void Open(VersionInfo version, VersionContentKind kind)
        => OpenPage(version, kind switch { VersionContentKind.Mod => "Mods", VersionContentKind.Save => "Saves", VersionContentKind.ResourcePack => "Packs", _ => "Shaders" }, kind);
    public void OpenOverview(VersionInfo version) => OpenPage(version, "Overview", VersionContentKind.Mod);
    private void OpenPage(VersionInfo version, string section, VersionContentKind kind)
    {
        if (IsBusy) { AppDialog.Show("请等待当前文件操作完成", "文件操作"); return; }
        _contentCache.Clear();
        Version = version; Section = section; Kind = kind; GameDirectory = VersionService.ResolveGameDirectory(version, Main.Settings);
        GameNameDraft = version.GameName; ContentSummary = ""; NotifyVersion();
        Items.Clear(); Status = ""; NotifyAvailability();
        Main.VersionsVM.InContentPage = true; Main.Navigate("Versions"); Main.RefreshPage();
        _ = RefreshAsync();
    }
    private void NotifyVersion()
    {
        foreach (var property in new[] { nameof(Version), nameof(Title), nameof(VersionName), nameof(VersionDirectory), nameof(GameDirectory), nameof(MinecraftLabel), nameof(LoaderLabel), nameof(JavaLabel), nameof(ParentLabel), nameof(TypeLabel), nameof(DirectoryLabel) }) OnPropertyChanged(property);
    }
    [RelayCommand] public async Task SwitchSectionAsync(string section)
    {
        if (IsBusy || section is not ("Overview" or "Mods" or "Saves" or "Packs" or "Shaders")) return;
        Section = section;
        Kind = section switch { "Saves" => VersionContentKind.Save, "Packs" => VersionContentKind.ResourcePack, "Shaders" => VersionContentKind.ShaderPack, _ => VersionContentKind.Mod };
        if (!IsOverview && (!_contentCache.TryGetValue(Kind, out var cached) || DateTime.UtcNow - cached.Time >= TimeSpan.FromSeconds(10))) Items.Clear();
        await RunAsync(token => ReloadAsync(token), false);
    }
    [RelayCommand] private async Task RenameAsync()
    {
        if (!CanRename || Version is null) return;
        var before = Version; var name = GameNameDraft;
        IsRenaming = true;
        try
        {
            await RunAsync(async token =>
            {
                var renamed = await new VersionManagementService().RenameAsync(before, name, token);
                await Main.UiDispatcher.InvokeAsync(() =>
                {
                    VersionManagementService.MoveSettings(Main.Settings, before, renamed);
                    var selected = Main.CurrentVersion;
                    if (selected is not null && AppPaths.VersionKey(selected) == AppPaths.VersionKey(before)) Main.CurrentVersion = renamed;
                    Main.SettingsStore.Save(Main.Settings);
                    Version = renamed; GameNameDraft = renamed.GameName;
                    GameDirectory = VersionService.ResolveGameDirectory(renamed, Main.Settings);
                    _contentCache.Clear();
                    NotifyVersion();
                });
                await Main.VersionsVM.ScanVersionsAsync();
                await ReloadAsync(CancellationToken.None);
                Main.Log.Write($"游戏已改名：{before.GameName} → {renamed.GameName}");
            }, false);
        }
        finally { IsRenaming = false; }
    }
    [RelayCommand] private void Back()
    {
        Main.VersionsVM.InContentPage = false; Main.Navigate("Versions"); Main.RefreshPage();
    }
    [RelayCommand] private async Task DeleteGameAsync()
    {
        if (!CanWrite || Version is null) return;
        var version = Version;
        if (AppDialog.Show($"将“{version.GameName}”及其独立目录移入回收站？", "删除游戏", MessageBoxButton.YesNo) != MessageBoxResult.Yes || !CanWrite) return;
        await RunAsync(async token =>
        {
            await new VersionManagementService().DeleteAsync(version, token);
            var key = AppPaths.VersionKey(version);
            Main.Settings.JavaOverrides.Remove(key); Main.Settings.IsolationOverrides.Remove(key);
            await Main.VersionsVM.ScanVersionsAsync(); Main.SettingsStore.Save(Main.Settings);
            Version = null; _contentCache.Clear(); Back();
            Main.Log.Write("游戏已移入回收站：" + version.GameName);
        }, false);
    }
    [RelayCommand] private async Task ExportModpackAsync()
    {
        if (!CanWrite || Version is not { IsValid: true } version) return;
        var dialog = new SaveFileDialog { Title = "导出整合包", FileName = version.GameName + ".mrpack", Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack", OverwritePrompt = true };
        if (dialog.ShowDialog() != true || !CanWrite) return;
        var directory = GameDirectory;
        await RunAsync(token => new ModpackService().ExportAsync(version, directory, dialog.FileName,
            new Progress<string>(message => Status = message), token), false);
    }
    [RelayCommand] private async Task SwitchResourcesAsync(string section)
    {
        if (IsBusy || !IsResources) return;
        Kind = section == "Shaders" ? VersionContentKind.ShaderPack : VersionContentKind.ResourcePack;
        await RefreshAsync();
    }
    [RelayCommand] public async Task RefreshAsync()
    {
        if (Version is null || IsBusy) return;
        _contentCache.Clear();
        await RunAsync(async token => await ReloadAsync(token), false);
    }
    private async Task ReloadAsync(CancellationToken token)
    {
        if (IsOverview)
        {
            var groups = await Task.WhenAll(Enum.GetValues<VersionContentKind>().Select(kind => GetEntriesAsync(kind, token)));
            var mods = groups[0]; var saves = groups[1]; var packs = groups[2]; var shaders = groups[3];
            await Main.UiDispatcher.InvokeAsync(() => ContentSummary = $"{mods.Count(x => x.Enabled)} Mod · {saves.Count} 存档 · {packs.Count} 资源包 · {shaders.Count} 光影包");
            return;
        }
        var entries = await GetEntriesAsync(Kind, token);
        await Main.UiDispatcher.InvokeAsync(() => { token.ThrowIfCancellationRequested(); Items.ReplaceAll(entries); });
    }
    private async Task<IReadOnlyList<VersionContentItem>> GetEntriesAsync(VersionContentKind kind, CancellationToken token)
    {
        if (_contentCache.TryGetValue(kind, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromSeconds(10)) return cached.Items;
        var entries = await _service.ScanAsync(GameDirectory, kind, token);
        await Main.UiDispatcher.InvokeAsync(() => { token.ThrowIfCancellationRequested(); _contentCache[kind] = (DateTime.UtcNow, entries); });
        return entries;
    }
    [RelayCommand] private async Task ToggleAsync(VersionContentItem? item)
    {
        if (item is null || !CanWrite) return;
        await RunAsync(token => _service.ToggleAsync(GameDirectory, item, token), true);
    }
    [RelayCommand] private async Task DeleteAsync(VersionContentItem? item)
    {
        if (item is null || !CanWrite) return;
        if (AppDialog.Show($"将“{item.Name}”移入回收站？", "删除", MessageBoxButton.YesNo) != MessageBoxResult.Yes || !CanWrite) return;
        await RunAsync(token => _service.DeleteAsync(GameDirectory, item, token), true);
    }
    [RelayCommand] private async Task ImportFilesAsync()
    {
        if (!CanWrite) return;
        var dialog = new OpenFileDialog { Title = "导入", Multiselect = !IsSave, Filter = Kind == VersionContentKind.Mod ? "Mod (*.jar)|*.jar" : "ZIP (*.zip)|*.zip" };
        if (dialog.ShowDialog() != true || !CanWrite) return;
        await RunAsync(async token => { foreach (var source in dialog.FileNames) await _service.ImportAsync(GameDirectory, Kind, source, token); }, true);
    }
    [RelayCommand] private async Task ImportFolderAsync()
    {
        if (!CanWrite || Kind == VersionContentKind.Mod) return;
        var dialog = new OpenFolderDialog { Title = "导入文件夹" };
        if (dialog.ShowDialog() == true && CanWrite) await RunAsync(token => _service.ImportAsync(GameDirectory, Kind, dialog.FolderName, token), true);
    }
    [RelayCommand] private async Task ExportSaveAsync(VersionContentItem? item)
    {
        if (item is null || !CanWrite) return;
        var dialog = new SaveFileDialog { Title = "导出存档", FileName = item.Name + ".zip", Filter = "ZIP (*.zip)|*.zip", OverwritePrompt = true };
        if (dialog.ShowDialog() == true && CanWrite) await RunAsync(token => _service.ExportSaveAsync(GameDirectory, item, dialog.FileName, token), false);
    }
    [RelayCommand] private void OpenFolder(VersionContentItem? item)
    {
        if (Version is null) return;
        var directory = item is { IsDirectory: true } ? item.Path : IsOverview ? GameDirectory : VersionContentService.ContentDirectory(GameDirectory, Kind);
        // Browsing does not create directories or change Auto isolation.
        if (!Directory.Exists(directory)) directory = GameDirectory;
        if (!Directory.Exists(directory)) { Status = "目录不存在"; return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { directory } }); }
        catch (Exception e) { Status = e.Message; }
    }
    [RelayCommand] private void Cancel() { if (_operationCts is not null) { Status = "正在取消"; _operationCts.Cancel(); } }
    public async Task CancelAndWaitAsync() { Cancel(); if (_idle is { } completion) await completion.Task; }
    private async Task RunAsync(Func<CancellationToken, Task> action, bool refresh)
    {
        if (IsBusy) return;
        using var cts = new CancellationTokenSource(); _operationCts = cts; _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true; Status = "处理中";
        try
        {
            await action(cts.Token);
            if (refresh) { _contentCache.Clear(); await ReloadAsync(cts.Token); }
            await Main.UiDispatcher.InvokeAsync(() => Status = "");
        }
        catch (OperationCanceledException) { await Main.UiDispatcher.InvokeAsync(() => Status = "已取消"); }
        catch (Exception e) { await Main.UiDispatcher.InvokeAsync(() => Status = e.Message); Main.Log.Write(e.Message, LogLevel.Warning); }
        finally
        {
            await Main.UiDispatcher.InvokeAsync(() => { _operationCts = null; IsBusy = false; _idle.TrySetResult(); });
            if (refresh && Kind == VersionContentKind.Mod) await Main.SettingsVM.RefreshMemoryAsync();
        }
    }
}

