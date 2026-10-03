using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.App.ViewModels;

public sealed partial class LogsViewModel : ViewModelBase, IDisposable
{
    public MainViewModel Main { get; }
    public ObservableCollection<LauncherLogEvent> Logs { get; } = new();
    [ObservableProperty] private string _filterLevel = "All";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _searchText = "";
    private readonly Queue<LauncherLogEvent> _allLogs = new();
    private readonly ConcurrentQueue<LauncherLogEvent> _pending = new();
    private readonly DispatcherTimer _timer;
    public event Action? BatchUpdated;
    public LogsViewModel(MainViewModel main)
    {
        Main = main; main.Log.Emitted += OnLog;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += Tick; _timer.Start();
    }
    private void OnLog(LauncherLogEvent entry)
    {
        _pending.Enqueue(entry);
        // Bound the preview only. LogService independently writes every event to disk.
        while (_pending.Count > 4000) _pending.TryDequeue(out _);
    }
    private void Tick(object? sender, EventArgs e)
    {
        bool changed = false;
        for (int i = 0; i < 200 && _pending.TryDequeue(out var entry); i++)
        {
            _allLogs.Enqueue(entry); if (_allLogs.Count > 2000) _allLogs.Dequeue();
            if (!Matches(entry)) continue;
            Logs.Add(entry); if (Logs.Count > 1000) Logs.RemoveAt(0);
            changed = true;
        }
        if (changed) BatchUpdated?.Invoke();
    }
    private bool Matches(LauncherLogEvent entry) =>
        (FilterLevel != "Warning" || entry.Level is LogLevel.Warning or LogLevel.Error) &&
        (FilterLevel != "Error" || entry.Level == LogLevel.Error) &&
        (SearchText.Length == 0 || entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    private void Refilter()
    {
        Logs.Clear(); foreach (var item in _allLogs.Where(Matches).TakeLast(1000)) Logs.Add(item);
        BatchUpdated?.Invoke();
    }
    partial void OnFilterLevelChanged(string value) => Refilter();
    partial void OnSearchTextChanged(string value) => Refilter();
    [RelayCommand] public void SetFilter(string level) => FilterLevel = level;
    [RelayCommand] public void ClearLogs() { _pending.Clear(); _allLogs.Clear(); Logs.Clear(); }
    [RelayCommand] public async Task CopyAllLogsAsync()
    {
        var text = File.Exists(Main.Log.FilePath) ? await File.ReadAllTextAsync(Main.Log.FilePath) : string.Join(Environment.NewLine, _allLogs.Select(x => x.Text));
        if (text.Length > 0) Clipboard.SetText(text);
    }
    [RelayCommand] public void OpenLogFolder()
    {
        var dir = Path.GetDirectoryName(Main.Log.FilePath);
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir!) { UseShellExecute = true });
    }
    [RelayCommand] public async Task ExportLogsAsync()
    {
        var dialog = new SaveFileDialog { Filter = "日志文件 (*.log)|*.log", FileName = $"Novaina-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (dialog.ShowDialog() != true) return;
        if (Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(Main.Log.FilePath), StringComparison.OrdinalIgnoreCase)) return;
        var text = File.Exists(Main.Log.FilePath) ? await File.ReadAllTextAsync(Main.Log.FilePath) : string.Join(Environment.NewLine, _allLogs.Select(x => x.Text));
        await File.WriteAllTextAsync(dialog.FileName, text, Encoding.UTF8);
    }
    public void Dispose() { _timer.Stop(); _timer.Tick -= Tick; Main.Log.Emitted -= OnLog; }
}
