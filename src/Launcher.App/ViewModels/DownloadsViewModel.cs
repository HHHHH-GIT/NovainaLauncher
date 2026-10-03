using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class DownloadsViewModel : ViewModelBase
{
    public MainViewModel Main { get; }
    public DownloadTaskService Queue { get; }
    private readonly DownloadCatalogService _catalog = new();
    private readonly ModReleaseCache _releaseCache = new();
    private CancellationTokenSource? _catalogCancellation;
    private CancellationTokenSource? _componentsCancellation;
    private CancellationTokenSource? _releaseCancellation;
    private Task? _catalogTask, _componentsTask, _releaseTask;
    private IReadOnlyList<GameCatalogVersion> _allGames = [];
    private IReadOnlyList<LoaderCatalogVersion> _forge = [];
    private bool _loaded;
    private int _offset;
    private readonly HashSet<Task> _pendingCatalog = new();
    private readonly HashSet<Task> _pendingUpdates = new();
    private readonly HashSet<Task> _pendingImports = new();
    private readonly HashSet<Guid> _removedTasks = new();
    private CancellationTokenSource _importCancellation = new();
    private bool _stopping;
    public RangeObservableCollection<GameCatalogVersion> Games { get; } = new();
    public RangeObservableCollection<DownloadCatalogItem> Projects { get; } = new();
    public RangeObservableCollection<LoaderCatalogVersion> Loaders { get; } = new();
    public RangeObservableCollection<OptiFineCatalogVersion> OptiFineVersions { get; } = new();
    public RangeObservableCollection<ContentRelease> Releases { get; } = new();
    public RangeObservableCollection<string> ModGameVersions { get; } = new();
    public RangeObservableCollection<string> ModLoaders { get; } = new();
    private ModReleaseIndex _releaseIndex = new([]);
    private bool _applyingReleaseFacets;
    [ObservableProperty] private string? _modGameVersion;
    [ObservableProperty] private string? _modLoader;
    public bool HasModReleaseOptions => ModGameVersions.Count > 0 && !IsLoadingReleases;
    public ObservableCollection<DownloadTaskRow> Tasks { get; } = new();
    public string[] LoaderKinds { get; } = ["原版", "Forge", "Fabric", "NeoForge"];
    public string[] Platforms { get; } = ["Modrinth", "CurseForge"];
    [ObservableProperty] private string _tab = "Welcome";
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _snapshots;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingComponents;
    [ObservableProperty] private bool _isLoadingReleases;
    [ObservableProperty] private bool _isRefreshingReleases;
    public bool IsFetchingReleases => IsLoadingReleases || IsRefreshingReleases;
    public string ReleaseLoadingLabel => IsRefreshingReleases ? "更新版本…" : "获取版本…";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private GameCatalogVersion? _selectedGame;
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private string _loaderKind = "原版";
    [ObservableProperty] private LoaderCatalogVersion? _selectedLoader;
    [ObservableProperty] private bool _useOptiFine;
    [ObservableProperty] private OptiFineCatalogVersion? _selectedOptiFine;
    [ObservableProperty] private string _compatibilityText = "";
    [ObservableProperty] private string _platform = "Modrinth";
    [ObservableProperty] private VersionInfo? _target;
    [ObservableProperty] private DownloadCatalogItem? _selectedProject;
    [ObservableProperty] private ContentRelease? _selectedRelease;
    [ObservableProperty] private VersionInfo? _lastInstalled;
    private CancellationTokenSource? _searchDelay, _preloadCancellation;
    private Task? _preloadTask, _debounceTask, _namesTask;
    private long _searchId;
    private string _gameQuery = "", _modQuery = "", _packQuery = "";
    private bool _localizingProjects;
    private string _browseKind = "Mod";
    private readonly Dictionary<string, DownloadCatalogItem[]> _browseCache = new();
    [ObservableProperty] private string _packName = "";
    private readonly Dictionary<string, (IReadOnlyList<LoaderCatalogVersion> Loaders, IReadOnlyList<OptiFineCatalogVersion> Extras)> _componentCache = new();
    [ObservableProperty] private int _step;
    [ObservableProperty] private int _direction = 1;
    [ObservableProperty] private bool _showTasks;
    public bool IsWelcome => Tab == "Welcome";
    public bool IsFlow => IsGame || IsContent || IsModpack;
    public bool IsBrowse => (IsContent || IsModpack) && Step == 0;
    public string Screen => Tab + Step;
    public string StepTitle => IsGame ? new[] { "你的下一段冒险。", "选择一种玩法。", "再添一点精彩。", "准备好出发。" }[Step] : IsModpack ? new[] { "一个完整的新世界。", "选择你的旅程。", "让世界即刻就绪。" }[Math.Min(Step, 2)] : new[] { "发现新的可能。", "为你的世界而选。", "准备加入你的世界。" }[Math.Min(Step, 2)];
    public string StepLabel => IsGame ? new[] { "游戏版本", "加载器", "额外配置", "确认安装" }[Step] : IsModpack ? new[] { "发现整合包", "版本与名称", "确认安装" }[Math.Min(Step, 2)] : new[] { "发现模组", "文件与目标", "确认安装" }[Math.Min(Step, 2)];
    public string StepNumber => $"0{Step + 1} / 0{(IsGame ? 4 : 3)}";
    public double StepProgress => (Step + 1) * 100.0 / (IsGame ? 4 : 3);
    public bool CanContinue => IsGame ? Step switch { 0 => SelectedGame is not null, 1 => !IsLoadingComponents && (LoaderKind == "原版" || SelectedLoader is not null), _ => CanInstallGame } : Step == 0 ? SelectedProject is not null : IsModpack ? CanInstallModpack : CanInstallContent;
    public string ContinueLabel => Step == (IsGame ? 3 : 2) ? "安装" : "继续  →";
    public bool UsesLoader => LoaderKind != "原版";
    public string ReleaseMinecraft => string.Join(", ", SelectedRelease?.GameVersions ?? []);
    public string ReleaseLoaders => string.Join(" / ", (SelectedRelease?.Loaders ?? []).Select(loader => loader.ToLowerInvariant() switch
    { "forge" => "Forge", "fabric" => "Fabric", "neoforge" => "NeoForge", "quilt" => "Quilt", _ => loader }));
    public string ContentDirectory => Target is { IsValid: true } ? Path.Combine(VersionService.ResolveGameDirectory(Target, Main.Settings), "mods") : "";
    public string GameInstallDirectory => Path.Combine(Main.Settings.GameRoot, "versions", GameName.Trim());
    public string PackInstallDirectory => Path.Combine(Main.Settings.GameRoot, "versions", PackName.Trim());
    private void NotifyFlow()
    { foreach (var name in new[] { nameof(Screen), nameof(StepTitle), nameof(StepLabel), nameof(StepNumber), nameof(StepProgress), nameof(CanContinue), nameof(ContinueLabel), nameof(IsWelcome), nameof(IsFlow), nameof(IsBrowse), nameof(ReleaseMinecraft), nameof(ReleaseLoaders), nameof(ContentDirectory), nameof(GameInstallDirectory), nameof(PackInstallDirectory) }) OnPropertyChanged(name); }
    partial void OnStepChanged(int value) => NotifyFlow();
    [RelayCommand] private void BeginFlow(string kind) { Direction = 1; Step = 0; Tab = kind; }
    [RelayCommand] private void Back()
    { Direction = -1; if (Step > 0) Step--; else Tab = "Welcome"; }
    [RelayCommand] private void Continue()
    {
        if (!CanContinue) return;
        if (Step == (IsGame ? 3 : 2)) { if (IsGame) InstallGame(); else if (IsModpack) InstallModpack(); else InstallContent(); return; }
        Direction = 1; Step++;
    }
    [RelayCommand] private void ToggleTasks() => ShowTasks = !ShowTasks;
    [RelayCommand] private void SelectLoader(string kind) { if (LoaderKinds.Contains(kind)) LoaderKind = kind; }
    private void OnTaskQueued()
    {
        Direction = -1;
        Tab = "Welcome"; Step = 0;
        ShowTasks = true;
    }
    public bool IsGame => Tab == "Game";
    public bool IsContent => Tab == "Mod";
    public bool IsModpack => Tab == "Pack";
    public bool CanInstallModpack => SelectedRelease is { Files.Count: > 0, Kind: CatalogKind.Modpack } && !IsLoadingReleases && !string.IsNullOrWhiteSpace(PackName);
    partial void OnPackNameChanged(string value) => NotifyFlow();
    public bool IsTasks => Tab == "Tasks";
    public bool HasPendingOperations => Queue.IsBusy || IsLoading || IsLoadingComponents || IsFetchingReleases || _pendingUpdates.Count > 0 || _pendingImports.Count > 0;
    public bool CanInstallGame => SelectedGame is not null && !IsLoadingComponents && CompatibilityText.Length == 0 && !string.IsNullOrWhiteSpace(GameName);
    public bool CanInstallContent => SelectedRelease is { Files.Count: > 0 } && Target is { IsValid: true } && !IsLoadingReleases;
    public string ContentInstallLabel => Tab == "Shader" && !HasShaderRenderer() ? "下载文件" : "安装";
    private CatalogKind Kind => IsModpack ? CatalogKind.Modpack : CatalogKind.Mod;
    public DownloadsViewModel(MainViewModel main, bool autoLoad = true)
    {
        Main = main;
        _loaded = !autoLoad;
        Queue = new(new DownloadInstallService(main.Java, main.Versions, main.Log), d => main.IsGameOperationBusy(d));
        Queue.Changed += task => _ = Main.UiDispatcher.InvokeAsync(() => TrackUpdate(task));
        Queue.Removed += id => _ = Main.UiDispatcher.InvokeAsync(() => ForgetTask(id));
    }
    private void TrackUpdate(DownloadTaskInfo state)
    {
        var task = ApplyTaskAsync(state); _pendingUpdates.Add(task); _ = ObserveUpdateAsync(task);
    }
    private async Task ObserveUpdateAsync(Task task)
    {
        try { await task; } catch (Exception error) { Main.Log.Write(error.Message, LogLevel.Error); }
        finally { _pendingUpdates.Remove(task); }
    }
    public SourcePolicy SourcePolicy() => new(Main.Settings.PreferGameMirror, Main.Settings.PreferContentMirror, Main.Secrets.ReadJson<string>("curseforge-key"));
    public void EnsureLoaded()
    {
        if (Target is null) Target = Main.CurrentVersion;
        if (!_loaded) { _loaded = true; _preloadTask = PreloadAsync(); }
    }
    private async Task PreloadAsync()
    {
        var cts = new CancellationTokenSource(); _preloadCancellation = cts;
        _namesTask = WarmChineseNamesAsync(cts.Token);
        var cache = Path.Combine(AppPaths.Data, "downloads", "welcome-catalog.json");
        try
        {
            if (File.Exists(cache))
            {
                try { var snapshot = JsonSerializer.Deserialize<CatalogSnapshot>(await File.ReadAllTextAsync(cache, cts.Token)); if (snapshot is not null) { _allGames = snapshot.Games; FilterGames(); Projects.ReplaceAll(snapshot.Projects); Source = "缓存"; } }
                catch (JsonException) { }
            }
            using var gameSource = new DownloadSources(SourcePolicy(), cts.Token);
            using var modSource = new DownloadSources(SourcePolicy(), cts.Token);
            var games = _catalog.GamesAsync(gameSource, cts.Token);
            var mods = _catalog.SearchAsync(CatalogKind.Mod, ContentPlatform.Modrinth, "", null, modSource, cts.Token);
            // Each source can succeed independently; an unavailable content source never hides games.
            try { _allGames = await games; FilterGames(); SelectedGame ??= Games.FirstOrDefault(); } catch (Exception ex) when (!cts.IsCancellationRequested) { Main.Log.Write("版本预加载：" + ex.Message, LogLevel.Warning); }
            try { var result = await mods; if (Query.Length == 0 && Platform == "Modrinth" && _searchId == 0) Projects.ReplaceAll(result); } catch (Exception ex) when (!cts.IsCancellationRequested) { Main.Log.Write("模组预加载：" + ex.Message, LogLevel.Warning); }
            cts.Token.ThrowIfCancellationRequested();
            if (_allGames.Count > 0 && Projects.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                var temp = cache + ".tmp";
                try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new CatalogSnapshot(_allGames.ToArray(), Projects.ToArray())), cts.Token); File.Move(temp, cache, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            if (Source.Length == 0) Source = gameSource.ActualSource;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Main.Log.Write("目录缓存：" + ex.Message, LogLevel.Warning); }
        finally { if (_namesTask is { } names) await names; if (_preloadCancellation == cts) _preloadCancellation = null; cts.Dispose(); }
    }
    private async Task WarmChineseNamesAsync(CancellationToken token)
    {
        try { await _catalog.WarmChineseNamesAsync(token); LocalizeProjects(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Main.Log.Write("中文模组索引：" + error.Message, LogLevel.Warning); }
    }
    private sealed record CatalogSnapshot(GameCatalogVersion[] Games, DownloadCatalogItem[] Projects);
    private void LocalizeProjects()
    {
        var selected = SelectedProject;
        _localizingProjects = true;
        try
        {
            Projects.ReplaceAll(Projects.Select(_catalog.Localize).ToArray());
            SelectedProject = Projects.FirstOrDefault(p => p.Id == selected?.Id && p.Platform == selected?.Platform);
        }
        finally { _localizingProjects = false; }
    }
    partial void OnTabChanged(string value)
    {
        _searchDelay?.Cancel(); _searchDelay = null;
        ++_searchId; _catalogCancellation?.Cancel(); _catalogCancellation = null; IsLoading = false;
        OnPropertyChanged(nameof(IsGame)); OnPropertyChanged(nameof(IsContent)); OnPropertyChanged(nameof(IsModpack)); OnPropertyChanged(nameof(IsTasks)); NotifyFlow();
        if ((IsContent || IsModpack) && _browseKind != value)
        {
            _browseCache[_browseKind] = Projects.ToArray(); _browseKind = value;
            SelectedProject = null; Projects.ReplaceAll(_browseCache.GetValueOrDefault(value) ?? []);
        }
        Query = IsGame ? _gameQuery : IsModpack ? _packQuery : _modQuery; Status = "";
        if (IsGame) FilterGames();
        else if ((IsContent || IsModpack) && Projects.Count == 0 && _searchDelay is null) _ = SearchAsync();
    }
    [RelayCommand] private void ChangeTab(string key) { if (key == "Tasks") ShowTasks = true; else BeginFlow(key); }
    partial void OnQueryChanged(string value)
    {
        if (IsGame) { _gameQuery = value; FilterGames(); }
        else if (IsContent || IsModpack)
        {
            if (IsModpack) _packQuery = value; else _modQuery = value;
            ++_searchId; _catalogCancellation?.Cancel();
            _searchDelay?.Cancel(); _debounceTask = DebounceAsync();
        }
    }
    private async Task DebounceAsync()
    {
        var cts = new CancellationTokenSource(); _searchDelay = cts;
        try { await Task.Delay(350, cts.Token); await SearchAsync(); } catch (OperationCanceledException) { }
        finally { if (_searchDelay == cts) _searchDelay = null; cts.Dispose(); }
    }
    partial void OnSnapshotsChanged(bool value) => FilterGames();
    partial void OnGameNameChanged(string value) { OnPropertyChanged(nameof(CanInstallGame)); NotifyFlow(); }
    partial void OnIsLoadingComponentsChanged(bool value) { OnPropertyChanged(nameof(CanInstallGame)); NotifyFlow(); }
    private void FilterGames() => Games.ReplaceAll(_allGames.Where(g => (Snapshots || g.Type == "release") && g.Id.Contains(Query, StringComparison.OrdinalIgnoreCase)));
    [RelayCommand] private async Task SearchAsync()
    {
        _searchDelay?.Cancel(); var requestId = ++_searchId;
        _catalogCancellation?.Cancel(); var cts = new CancellationTokenSource(); _catalogCancellation = cts;
        var tab = Tab; var query = Query; var platform = Platform;
        IsLoading = true; Status = "";
        _offset = 0;
        var task = Load(); _catalogTask = task; _pendingCatalog.Add(task);
        try { await task; } finally { _pendingCatalog.Remove(task); }
        async Task Load()
        {
            try
            {
                using var sources = CatalogSources(cts.Token);
                if (tab == "Game")
                {
                    var games = await _catalog.GamesAsync(sources, cts.Token);
                    cts.Token.ThrowIfCancellationRequested(); _allGames = games; FilterGames(); SelectedGame ??= Games.FirstOrDefault();
                }
                else if (tab is "Mod" or "Pack")
                {
                    var kind = tab == "Pack" ? CatalogKind.Modpack : CatalogKind.Mod;
                    var results = await _catalog.SearchAsync(kind, platform == "Modrinth" ? ContentPlatform.Modrinth : ContentPlatform.CurseForge, query, null, sources, cts.Token);
                    cts.Token.ThrowIfCancellationRequested(); if (requestId != _searchId) return; Projects.ReplaceAll(results); if (results.Count == 0) Status = "没有搜索结果";
                }
                Source = sources.ActualSource;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception error) { if (!cts.IsCancellationRequested) { Status = SourceError(error); Main.Log.Write("目录请求：" + error.Message, LogLevel.Warning); } }
            finally { if (_catalogCancellation == cts) { IsLoading = false; _catalogCancellation = null; } cts.Dispose(); }
        }
    }
    [RelayCommand] private async Task LoadMoreAsync()
    {
        if (!(IsContent || IsModpack) || IsLoading) return;
        using var cts = new CancellationTokenSource(); _catalogCancellation = cts; IsLoading = true;
        var requestId = ++_searchId; var next = _offset + 30;
        try
        {
            using var sources = CatalogSources(cts.Token);
            var results = await _catalog.SearchAsync(Kind, Platform == "Modrinth" ? ContentPlatform.Modrinth : ContentPlatform.CurseForge, Query, null, sources, cts.Token, next);
            cts.Token.ThrowIfCancellationRequested();
            if (requestId != _searchId) return;
            foreach (var item in results.Where(r => Projects.All(p => p.Id != r.Id))) Projects.Add(item);
            _offset = next; if (results.Count == 0) Status = "已到底";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception error) { if (!cts.IsCancellationRequested) { Status = SourceError(error); Main.Log.Write("目录请求：" + error.Message, LogLevel.Warning); } }
        finally { if (_catalogCancellation == cts) { _catalogCancellation = null; IsLoading = false; } }
    }
    partial void OnSelectedGameChanged(GameCatalogVersion? value)
    {
        if (value is null) return;
        GameName = value.Id; NotifyFlow(); _componentsTask = LoadComponentsAsync();
    }
    partial void OnLoaderKindChanged(string value) { OnPropertyChanged(nameof(UsesLoader)); NotifyFlow(); _componentsTask = LoadComponentsAsync(); }
    partial void OnSelectedLoaderChanged(LoaderCatalogVersion? value) => CheckCompatibility();
    partial void OnUseOptiFineChanged(bool value)
    {
        if (value && SelectedOptiFine is null) SelectedOptiFine = OptiFineVersions.FirstOrDefault();
        if (value && LoaderKind == "原版" && SelectedOptiFine?.ForgeVersion is { Length: > 0 } forge && !forge.Contains("N/A", StringComparison.OrdinalIgnoreCase)) LoaderKind = "Forge";
        AutoForge(); CheckCompatibility();
    }
    partial void OnSelectedOptiFineChanged(OptiFineCatalogVersion? value) { AutoForge(); CheckCompatibility(); }
    private void AutoForge()
    {
        if (UseOptiFine && LoaderKind == "Forge" && SelectedOptiFine is { } of && InstallCompatibility.ExactForge(of, _forge) is { } exact) SelectedLoader = Loaders.FirstOrDefault(l => l.Version == exact);
    }
    private async Task LoadComponentsAsync()
    {
        _componentsCancellation?.Cancel();
        if (SelectedGame is null) return;
        var cts = new CancellationTokenSource(); _componentsCancellation = cts;
        var mc = SelectedGame.Id; var kind = LoaderKind; IsLoadingComponents = true;
        Loaders.Clear(); SelectedLoader = null; OptiFineVersions.Clear(); SelectedOptiFine = null;
        try
        {
            using var sources = CatalogSources(cts.Token);
            var key = mc + "|" + kind;
            if (_componentCache.TryGetValue(key, out var cached))
            { Loaders.ReplaceAll(cached.Loaders); OptiFineVersions.ReplaceAll(cached.Extras); SelectedLoader = cached.Loaders.FirstOrDefault(); SelectedOptiFine = cached.Extras.FirstOrDefault(); _forge = kind == "Forge" ? cached.Loaders : []; AutoForge(); return; }
            var loaders = kind == "原版" ? Array.Empty<LoaderCatalogVersion>() : await _catalog.LoadersAsync(mc, kind, sources, cts.Token);
            _forge = kind == "Forge" ? loaders : [];
            cts.Token.ThrowIfCancellationRequested(); Loaders.ReplaceAll(loaders); SelectedLoader = loaders.FirstOrDefault();
            try { var of = await _catalog.OptiFineAsync(mc, sources, cts.Token); cts.Token.ThrowIfCancellationRequested(); OptiFineVersions.ReplaceAll(of); SelectedOptiFine = of.FirstOrDefault(); }
            catch (System.Net.Http.HttpRequestException) { }
            _componentCache[key] = (loaders, OptiFineVersions.ToArray()); AutoForge();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cts.IsCancellationRequested) { Status = SourceError(error); Main.Log.Write("目录请求：" + error.Message, LogLevel.Warning); } }
        finally { if (_componentsCancellation == cts) { IsLoadingComponents = false; _componentsCancellation = null; CheckCompatibility(); } cts.Dispose(); }
    }
    private void CheckCompatibility()
    {
        CompatibilityText = LoaderKind != "原版" && SelectedLoader is null ? "此版本没有可用的加载器" : UseOptiFine && SelectedOptiFine is null ? "此版本没有可用的 OptiFine" : string.Join("；", InstallCompatibility.Game(GamePlan(), _forge).Select(i => i.Message));
        OnPropertyChanged(nameof(CanInstallGame)); NotifyFlow();
    }
    private InstallPlan GamePlan() => Main.Operations.GamePlan(GameName, SelectedGame!, LoaderKind == "原版" ? null : SelectedLoader, UseOptiFine ? SelectedOptiFine : null);
    [RelayCommand] private void InstallGame()
    {
        if (!CanInstallGame) return;
        try { VersionManagementService.ValidateName(GameName.Trim()); Main.Operations.Enqueue(GamePlan()); OnTaskQueued(); }
        catch (Exception error) { Status = error.Message; }
    }
    partial void OnTargetChanged(VersionInfo? value)
    {
        OnPropertyChanged(nameof(CanInstallContent)); NotifyFlow();
    }
    partial void OnPlatformChanged(string value) { if (IsContent || IsModpack) { SelectedProject = null; Projects.Clear(); _browseCache.Clear(); _ = SearchAsync(); } }
    public bool CanRetryReleases => SelectedProject is not null && !IsFetchingReleases;
    [RelayCommand(CanExecute = nameof(CanRetryReleases))]
    private async Task RetryReleasesAsync() { _releaseTask = TrackReleasesAsync(true); await _releaseTask; }
    partial void OnSelectedProjectChanged(DownloadCatalogItem? value)
    {
        if (_localizingProjects) return;
        if (value?.Kind == CatalogKind.Modpack) PackName = value.Name;
        _releaseTask = TrackReleasesAsync(); RetryReleasesCommand.NotifyCanExecuteChanged(); NotifyFlow();
    }
    private async Task TrackReleasesAsync(bool refresh = false)
    {
        var task = LoadReleasesAsync(refresh); _pendingCatalog.Add(task);
        try { await task; } finally { _pendingCatalog.Remove(task); }
    }
    partial void OnIsLoadingReleasesChanged(bool value)
    { OnPropertyChanged(nameof(HasModReleaseOptions)); OnPropertyChanged(nameof(CanInstallContent)); OnPropertyChanged(nameof(IsFetchingReleases)); OnPropertyChanged(nameof(ReleaseLoadingLabel)); RetryReleasesCommand.NotifyCanExecuteChanged(); NotifyFlow(); }
    partial void OnIsRefreshingReleasesChanged(bool value)
    { OnPropertyChanged(nameof(IsFetchingReleases)); OnPropertyChanged(nameof(ReleaseLoadingLabel)); RetryReleasesCommand.NotifyCanExecuteChanged(); }
    public void ApplyReleaseIndex(ModReleaseIndex index)
    {
        if (IsModpack)
        {
            var previous = SelectedRelease;
            Releases.ReplaceAll(index.AllReleases);
            SelectedRelease = Releases.FirstOrDefault(v => v.Id == previous?.Id) ?? Releases.FirstOrDefault();
            return;
        }
        var previousGame = ModGameVersion;
        _releaseIndex = index;
        _applyingReleaseFacets = true;
        ModGameVersions.ReplaceAll(index.GameVersions);
        ModGameVersion = index.GameVersions.Contains(previousGame, StringComparer.OrdinalIgnoreCase) ? previousGame
            : index.GameVersions.Contains(Target?.MinecraftVersion, StringComparer.OrdinalIgnoreCase) ? Target!.MinecraftVersion : index.GameVersions.FirstOrDefault();
        _applyingReleaseFacets = false;
        FilterModLoaders();
        OnPropertyChanged(nameof(HasModReleaseOptions));
    }
    partial void OnModGameVersionChanged(string? value) { if (!_applyingReleaseFacets) FilterModLoaders(); }
    partial void OnModLoaderChanged(string? value) { if (!_applyingReleaseFacets) FilterModFiles(); }
    private void FilterModLoaders()
    {
        var previous = ModLoader;
        var loaders = _releaseIndex.Loaders(ModGameVersion);
        var preferred = ModReleaseIndex.LoaderLabel(Target?.Loader ?? "");
        _applyingReleaseFacets = true;
        ModLoaders.ReplaceAll(loaders);
        ModLoader = loaders.Contains(previous, StringComparer.OrdinalIgnoreCase) ? previous
            : loaders.Contains(preferred, StringComparer.OrdinalIgnoreCase) ? preferred : loaders.FirstOrDefault();
        _applyingReleaseFacets = false;
        FilterModFiles();
    }
    private void FilterModFiles()
    {
        var previous = SelectedRelease;
        var files = _releaseIndex.Releases(ModGameVersion, ModLoader);
        Releases.ReplaceAll(files);
        SelectedRelease = files.FirstOrDefault(r => r.Platform == previous?.Platform && r.Id == previous?.Id) ?? files.FirstOrDefault();
    }
    partial void OnSelectedReleaseChanged(ContentRelease? value) { OnPropertyChanged(nameof(CanInstallContent)); NotifyFlow(); }
    private async Task LoadReleasesAsync(bool refresh = false)
    {
        _releaseCancellation?.Cancel(); _releaseCancellation = null;
        IsLoadingReleases = IsRefreshingReleases = false;
        Status = "";
        if (SelectedProject is null) { ApplyReleaseIndex(new([])); return; }
        var cts = new CancellationTokenSource(); _releaseCancellation = cts;
        var project = SelectedProject;
        var cached = _releaseCache.ReadMemory(project);
        ApplyReleaseIndex(cached?.Index ?? new([]));
        IsLoadingReleases = cached is null;
        try
        {
            cached ??= await _releaseCache.ReadAsync(project, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (cached is not null)
            {
                if (!ReferenceEquals(_releaseIndex, cached.Index)) ApplyReleaseIndex(cached.Index);
                IsLoadingReleases = false; Source = "缓存";
                if (cached.IsFresh && !refresh) return;
                IsRefreshingReleases = true;
            }
            using var sources = CatalogSources(cts.Token);
            var releases = await _catalog.ReleasesAsync(project, null, sources, cts.Token, refresh);
            var index = await Task.Run(() => new ModReleaseIndex(releases, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ApplyReleaseIndex(index); IsLoadingReleases = false; Source = sources.ActualSource;
            if (index.AllReleases.Count == 0) Status = IsModpack ? "此整合包暂无公开文件" : "此模组暂无公开文件";
            if (sources.ActualSource != "离线缓存")
            {
                try { await _releaseCache.SaveAsync(project, new(releases.ToArray(), index, DateTime.UtcNow), cts.Token); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Main.Log.Write("模组版本缓存：" + error.Message, LogLevel.Warning); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!cts.IsCancellationRequested)
            {
                if (cached is null) { Status = SourceError(error); Main.Log.Write("模组版本获取：" + error.Message, LogLevel.Warning); }
                else { Source = "离线缓存"; Main.Log.Write("模组版本刷新：" + error.Message, LogLevel.Warning); }
            }
        }
        finally { if (_releaseCancellation == cts) { IsLoadingReleases = IsRefreshingReleases = false; _releaseCancellation = null; NotifyFlow(); } cts.Dispose(); }
    }
    private bool HasShaderRenderer() => false;
    private DownloadSources CatalogSources(CancellationToken token) => new(SourcePolicy(), token,
        diagnostic: message => Main.Log.Write("下载来源：" + message, LogLevel.Warning));
    private static string SourceError(Exception error) => error is OperationCanceledException or TimeoutException ? "来源响应超时，请重试" : error is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } ? "来源限流，请稍后重试" : error is System.Net.Http.HttpRequestException ? "来源不可用 · " + error.Message : error.Message;
    [RelayCommand] private void InstallContent()
    {
        if (!CanInstallContent) return;
        try
        {
            var directory = VersionService.ResolveGameDirectory(Target!, Main.Settings);
            Main.Operations.Enqueue(Main.Operations.ContentPlan(SelectedProject!.Name, SelectedRelease!, Target));
            OnTaskQueued();
        }
        catch (Exception error) { Status = error.Message; }
    }
    [RelayCommand] private void InstallModpack()
    {
        if (!CanInstallModpack) return;
        try
        {
            VersionManagementService.ValidateName(PackName.Trim());
            Main.Operations.Enqueue(Main.Operations.ContentPlan(PackName, SelectedRelease!, null));
            OnTaskQueued();
        }
        catch (Exception error) { Status = error.Message; }
    }
    [RelayCommand] private async Task ImportModpackAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入整合包", Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        await ImportModpackFilesAsync(dialog.FileNames);
    }
    public async Task ImportModpackFilesAsync(IEnumerable<string> paths)
    {
        if (_stopping || Main.SettingsVM.IsChangingData) return;
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        Main.Navigate("Downloads");
        var work = ImportArchivesAsync(files, _importCancellation.Token);
        _pendingImports.Add(work);
        try { await work; }
        finally { _pendingImports.Remove(work); }
    }
    private async Task ImportArchivesAsync(string[] paths, CancellationToken token)
    {
        // Capture settings before background inspection; navigation cannot change an accepted target.
        var root = Main.Settings.GameRoot;
        var sources = SourcePolicy();
        var javaDirectory = Main.Settings.JavaDownloadDirectory;
        var javas = Main.SettingsVM.InstalledJavas.ToArray();
        foreach (var path in paths)
        {
            try
            {
                Status = "识别整合包…";
                var format = await Task.Run(() => ModpackService.InspectArchiveAsync(path, token), token);
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                VersionManagementService.ValidateName(name);
                if (Directory.Exists(Path.Combine(root, "versions", name))) throw new IOException("同名游戏已存在");
                Main.Operations.Enqueue(new(name, root, null, null, null, null, null, null, sources, javaDirectory, javas, LocalArchive: path));
                Source = format; Status = ""; OnTaskQueued();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                Status = $"{Path.GetFileName(path)}：{error.Message}";
                Main.Log.Write(Status, LogLevel.Warning);
                Launcher.App.Controls.AppDialog.Show(Status, "整合包导入", image: System.Windows.MessageBoxImage.Warning);
            }
        }
    }
    private async Task ApplyTaskAsync(DownloadTaskInfo task)
    {
        if (_removedTasks.Contains(task.Id)) return;
        int index = Tasks.ToList().FindIndex(t => t.Id == task.Id);
        if (index < 0) Tasks.Insert(0, new(task)); else Tasks[index].Info = task;
        Main.ContentVM.NotifyAvailability();
        if (task.State == DownloadTaskState.Completed && !Main.SettingsVM.IsChangingData)
        {
            if (task.InstalledVersion is { } version)
            {
                LastInstalled = version;
                if (string.Equals(Main.Settings.GameRoot, version.Root, StringComparison.OrdinalIgnoreCase))
                {
                    await Main.VersionsVM.ScanVersionsAsync(); Main.CurrentVersion = Main.VersionsVM.Versions.FirstOrDefault(v => v.Id == version.Id) ?? version;
                    await Main.SettingsVM.ScanJavaAsync();
                }
            }
            Main.ContentVM.InvalidateCache();
            await Main.SettingsVM.RefreshMemoryAsync();
        }
        if (task.State == DownloadTaskState.Failed) Main.Log.Write(task.Error ?? task.Message, LogLevel.Error, true);
    }
    [RelayCommand] private void CancelTask(DownloadTaskInfo task) => Queue.Cancel(task.Id);
    [RelayCommand] private void RemoveTask(DownloadTaskInfo task)
    {
        if (Queue.Remove(task.Id)) ForgetTask(task.Id);
    }
    private void ForgetTask(Guid id)
    {
        _removedTasks.Add(id);
        var row = Tasks.FirstOrDefault(t => t.Id == id);
        if (row is not null) Tasks.Remove(row);
    }
    [RelayCommand] private void RetryTask(DownloadTaskInfo task) { try { Queue.Retry(task.Id); OnTaskQueued(); } catch (Exception error) { Status = error.Message; } }
    [RelayCommand] private void OpenProject() { if (SelectedProject?.WebUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    [RelayCommand] private void GoLaunch() => Main.Navigate("Launch");
    public async Task CancelAndWaitAsync()
    {
        _stopping = true;
        try
        {
            _catalogCancellation?.Cancel(); _componentsCancellation?.Cancel(); _releaseCancellation?.Cancel(); _searchDelay?.Cancel(); _preloadCancellation?.Cancel();
            _importCancellation.Cancel();
            await Task.WhenAll(_pendingImports.ToArray());
            await Queue.CancelAndWaitAsync();
            if (LoadMoreCommand.ExecutionTask is { } more) await more;
            await Task.WhenAll(_pendingCatalog.Concat(new[] { _catalogTask, _componentsTask, _releaseTask, _preloadTask, _debounceTask, _namesTask }.OfType<Task>()).ToArray());
            await Main.UiDispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            while (_pendingUpdates.Count > 0) await Task.WhenAll(_pendingUpdates.ToArray());
        }
        finally
        {
            _importCancellation.Dispose(); _importCancellation = new(); _stopping = false;
        }
    }
}

public sealed partial class DownloadConnectionRow(string key, DownloadConnectionProgress progress) : ObservableObject
{
    public string Key { get; } = key;
    [ObservableProperty] private DownloadConnectionProgress _progress = progress;
}

public sealed partial class DownloadTaskRow(DownloadTaskInfo info) : ObservableObject
{
    public Guid Id { get; } = info.Id;
    [ObservableProperty] private DownloadTaskInfo _info = info;
    [ObservableProperty] private bool _isExpanded;
}

