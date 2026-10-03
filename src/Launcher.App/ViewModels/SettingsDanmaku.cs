using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty] private string _danmakuModeKey = "Selected";
    [ObservableProperty] private string _danmakuStyleKey = "Glass";
    [ObservableProperty] private bool _blockLog4j;
    [ObservableProperty] private bool _blockAuthlibInjector;
    [ObservableProperty] private bool _blockWarn;
    [ObservableProperty] private bool _blockError;
    public ObservableCollection<DanmakuRuleViewModel> DanmakuRules { get; } = new();
    public DanmakuFilter DanmakuFilter { get; private set; } = new(DanmakuBlockedPresets.None, []);
    public event Action? DanmakuChanged;
    private bool _initializingDanmaku;
    private void InitializeDanmaku()
    {
        _initializingDanmaku = true;
        DanmakuModeKey = Main.DanmakuMode.ToString();
        DanmakuStyleKey = Main.Settings.DanmakuStyle.ToString();
        var presets = Main.Settings.DanmakuBlockedPresets;
        BlockLog4j = presets.HasFlag(DanmakuBlockedPresets.Log4j);
        BlockAuthlibInjector = presets.HasFlag(DanmakuBlockedPresets.AuthlibInjector);
        BlockWarn = presets.HasFlag(DanmakuBlockedPresets.Warn);
        BlockError = presets.HasFlag(DanmakuBlockedPresets.Error);
        foreach (var rule in Main.Settings.DanmakuBlockRules ?? []) DanmakuRules.Add(new(rule, PublishDanmaku));
        DanmakuFilter = new(presets, Main.Settings.DanmakuBlockRules ?? []);
        _initializingDanmaku = false;
    }
    public void SynchronizeDanmakuMode() => DanmakuModeKey = Main.DanmakuMode.ToString();
    partial void OnDanmakuModeKeyChanged(string value) { if (!_initializingDanmaku && Enum.TryParse<DanmakuMode>(value, out var mode)) Main.DanmakuMode = mode; }
    partial void OnDanmakuStyleKeyChanged(string value)
    {
        if (_initializingDanmaku || !Enum.TryParse<DanmakuStyle>(value, out var style)) return;
        Main.Settings.DanmakuStyle = style; ScheduleSave(); DanmakuChanged?.Invoke();
    }
    partial void OnBlockLog4jChanged(bool value) => PublishDanmaku();
    partial void OnBlockAuthlibInjectorChanged(bool value) => PublishDanmaku();
    partial void OnBlockWarnChanged(bool value) => PublishDanmaku();
    partial void OnBlockErrorChanged(bool value) => PublishDanmaku();
    [RelayCommand] private void SelectDanmakuMode(string key) => DanmakuModeKey = key;
    [RelayCommand] private void SelectDanmakuStyle(string key) => DanmakuStyleKey = key;
    [RelayCommand] private void AddDanmakuRule()
    {
        DanmakuRules.Add(new(new(false, DanmakuMatchKind.Exact, ""), PublishDanmaku)); PublishDanmaku();
    }
    [RelayCommand] private void RemoveDanmakuRule(DanmakuRuleViewModel? rule)
    { if (rule is not null && DanmakuRules.Remove(rule)) PublishDanmaku(); }
    private void PublishDanmaku()
    {
        if (_initializingDanmaku) return;
        var presets = (BlockLog4j ? DanmakuBlockedPresets.Log4j : 0) | (BlockAuthlibInjector ? DanmakuBlockedPresets.AuthlibInjector : 0)
            | (BlockWarn ? DanmakuBlockedPresets.Warn : 0) | (BlockError ? DanmakuBlockedPresets.Error : 0);
        Main.Settings.DanmakuBlockedPresets = presets;
        Main.Settings.DanmakuBlockRules = DanmakuRules.Select(x => new DanmakuBlockRule(x.Enabled && string.IsNullOrEmpty(x.Error), x.Kind, x.Pattern)).ToList();
        DanmakuFilter = new(presets, Main.Settings.DanmakuBlockRules);
        ScheduleSave(); DanmakuChanged?.Invoke();
    }
}

public sealed partial class DanmakuRuleViewModel : ObservableObject
{
    private readonly Action _changed;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _pattern;
    [ObservableProperty] private int _matchIndex;
    [ObservableProperty] private string _error = "";
    public DanmakuMatchKind Kind => MatchIndex == 1 ? DanmakuMatchKind.Regex : DanmakuMatchKind.Exact;
    public DanmakuRuleViewModel(DanmakuBlockRule rule, Action changed)
    {
        _changed = changed; _pattern = rule.Pattern; _matchIndex = (int)rule.Kind;
        _error = DanmakuFilter.Validate(Kind, Pattern) ?? ""; _enabled = rule.Enabled && _error.Length == 0;
    }
    partial void OnEnabledChanged(bool value) => Refresh();
    partial void OnPatternChanged(string value) => Refresh();
    partial void OnMatchIndexChanged(int value) => Refresh();
    private void Refresh()
    {
        Error = DanmakuFilter.Validate(Kind, Pattern) ?? "";
        if (Error.Length > 0 && Enabled) { Enabled = false; return; }
        _changed();
    }
}
