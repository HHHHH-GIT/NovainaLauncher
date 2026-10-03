using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.AI;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class AgentViewModel : ViewModelBase, IAgentInteraction
{
    public MainViewModel Main { get; }
    private readonly IAgentSessionService _session;
    private IDeepSeekClient _client;
    private string? _key;
    private CancellationTokenSource? _connection;
    private Task? _connectionTask;
    private Task? _stopTask;
    public string PendingKey { private get; set; } = "";
    public ObservableCollection<AgentTimelineItem> Timeline { get; } = new();
    public ObservableCollection<AgentModel> Models { get; } = new();
    [ObservableProperty] private AgentModel? _selectedModel;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private string _connectionStatus = "未配置";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStopping;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _showConnection;
    [ObservableProperty] private string _reasoningEffort = "high";
    public IReadOnlyList<string> ReasoningLevels => SelectedModel?.Efforts is { Count: > 0 } levels ? levels : ["high", "medium", "low"];
    public bool ShowWelcome => !IsConfigured || ShowConnection;
    public bool CanConfigure => !IsRunning && !IsStopping && !IsConnecting;
    public bool CanSend => !IsRunning && !IsStopping && !IsConnecting && IsConfigured && SelectedModel is not null && !string.IsNullOrWhiteSpace(Input);
    public event Action? TimelineChanged;
    public AgentViewModel(MainViewModel main)
    {
        Main = main;
        _reasoningEffort = main.Settings.AiReasoningEffort;
        try { _key = main.Secrets.ReadJson<string>("deepseek-api-key"); } catch { ConnectionStatus = "密钥读取失败，请重新配置"; }
        if (_key is { Length: > 0 }) { main.Log.RegisterSecret(_key); IsConfigured = true; ConnectionStatus = "已配置"; }
        _client = new DeepSeekClient(() => _key);
        _session = new AgentSessionService(new ClientProxy(this), new AgentToolRegistry(main.Operations), main.Operations, this);
        _session.Event += e => main.UiDispatcher.InvokeAsync(() => Receive(e));
        if (main.Settings.AiModelId.Length > 0) { var saved = new AgentModel(main.Settings.AiModelId, main.Settings.AiModelId); Models.Add(saved); _selectedModel = saved; }
    }
    private sealed class ClientProxy(AgentViewModel vm) : IDeepSeekClient
    {
        public Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool refresh, CancellationToken cancellation) => vm._client.GetModelsAsync(refresh, cancellation);
        public Task<AgentResponse> RespondAsync(AgentModel m, string effort, System.Text.Json.Nodes.JsonArray h, System.Text.Json.Nodes.JsonArray t, Action<string> delta, CancellationToken c) => vm._client.RespondAsync(m, effort, h, t, delta, c);
    }
    partial void OnSelectedModelChanged(AgentModel? value)
    { if (value is null || IsRunning) return; Main.Settings.AiModelId = value.Id; Main.SettingsStore.Save(Main.Settings); OnPropertyChanged(nameof(ReasoningLevels)); if (!ReasoningLevels.Contains(ReasoningEffort)) ReasoningEffort = ReasoningLevels.Contains("high") ? "high" : ReasoningLevels[0]; Changed(); }
    partial void OnReasoningEffortChanged(string value) { Main.Settings.AiReasoningEffort = value; Main.SettingsStore.Save(Main.Settings); }
    partial void OnInputChanged(string value) => Changed();
    partial void OnIsConfiguredChanged(bool value) { OnPropertyChanged(nameof(ShowWelcome)); Changed(); }
    partial void OnShowConnectionChanged(bool value) => OnPropertyChanged(nameof(ShowWelcome));
    partial void OnIsRunningChanged(bool value) => Changed();
    partial void OnIsStoppingChanged(bool value) => Changed();
    partial void OnIsConnectingChanged(bool value) => Changed();
    private void Changed() { OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanSend)); SendCommand.NotifyCanExecuteChanged(); }
    public async Task ModeChangedAsync(bool enabled)
    {
        if (!enabled) { await StopAsync(); Main.CompleteAccountLogin(); }
        else if (IsConfigured) { _connectionTask = RefreshModelsCoreAsync(false); await _connectionTask; }
    }
    [RelayCommand] private void OpenConnection() => ShowConnection = true;
    [RelayCommand] private void CloseConnection() { if (IsConfigured) ShowConnection = false; }
    [RelayCommand] private Task ConnectAsync() => _connectionTask = ConnectCoreAsync();
    private async Task ConnectCoreAsync()
    {
        if (!CanConfigure) return;
        var key = PendingKey.Trim(); PendingKey = "";
        if (key.Length == 0) { await RefreshModelsAsync(); return; }
        IsConnecting = true; ConnectionStatus = "验证连接"; _connection?.Cancel(); using var cts = new CancellationTokenSource(); _connection = cts;
        try
        {
            var candidate = new DeepSeekClient(() => key);
            var models = await candidate.GetModelsAsync(true, cts.Token); cts.Token.ThrowIfCancellationRequested();
            Main.Secrets.WriteJson("deepseek-api-key", key); Main.Log.RegisterSecret(key); _key = key; _client = candidate;
            ApplyModels(models); IsConfigured = true; ShowConnection = false; Main.Settings.AiSetupCompleted = true; Main.SettingsStore.Save(Main.Settings); ConnectionStatus = "连接成功";
        }
        catch (OperationCanceledException) { ConnectionStatus = "已取消"; }
        catch (Exception) { ConnectionStatus = "连接失败，请检查密钥与网络"; }
        finally { _connection = null; IsConnecting = false; }
    }
    [RelayCommand] private Task RefreshModelsAsync() => _connectionTask = RefreshModelsCoreAsync();
    private async Task RefreshModelsCoreAsync(bool refresh = true)
    {
        if (!CanConfigure || !IsConfigured) return;
        IsConnecting = true; ConnectionStatus = "获取模型"; using var cts = new CancellationTokenSource(); _connection = cts;
        try { ApplyModels(await _client.GetModelsAsync(refresh, cts.Token)); ConnectionStatus = "连接正常"; }
        catch (OperationCanceledException) { ConnectionStatus = "已取消"; }
        catch { ConnectionStatus = "连接失败，保留当前模型"; }
        finally { _connection = null; IsConnecting = false; }
    }
    private void ApplyModels(IReadOnlyList<AgentModel> models)
    {
        var prior = Main.Settings.AiModelId; Models.Clear(); foreach (var m in models) Models.Add(m);
        SelectedModel = models.FirstOrDefault(m => m.Id == prior) ?? models.FirstOrDefault(m => m.Id.Contains("pro", StringComparison.OrdinalIgnoreCase)) ?? models.FirstOrDefault(m => m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase)) ?? models[0];
    }
    [RelayCommand] private void RemoveKey()
    { if (!CanConfigure) return; Main.Secrets.Delete("deepseek-api-key"); _key = null; PendingKey = ""; _client = new DeepSeekClient(() => _key); IsConfigured = false; Models.Clear(); SelectedModel = null; Main.Settings.AiSetupCompleted = false; Main.SettingsStore.Save(Main.Settings); ConnectionStatus = "未配置"; }
    [RelayCommand(CanExecute = nameof(CanSend))] private async Task SendAsync()
    {
        if (!CanSend) return; var text = Input.Trim(); Input = ""; var model = SelectedModel!;
        IsRunning = true;
        try { await _session.SendAsync(text, model, Main.Settings.AiReasoningEffort); }
        finally { IsRunning = false; }
    }
    [RelayCommand] private Task StopAsync() => _stopTask is { IsCompleted: false } existing ? existing : _stopTask = StopCoreAsync();
    private async Task StopCoreAsync()
    {
        IsStopping = true; _connection?.Cancel();
        try { await _session.StopAsync(); if (_connectionTask is { } connect) await connect; }
        finally { IsStopping = false; IsRunning = false; Status = "就绪"; }
    }
    public Task CancelAndWaitAsync() => StopAsync();
    [RelayCommand] private async Task NewConversationAsync() { await StopAsync(); _session.Clear(); Main.Operations.ResetHandles(); Timeline.Clear(); TimelineChanged?.Invoke(); }
    [RelayCommand] private void Suggest(string text) => Input = text;
    private void Receive(AgentUiEvent e)
    {
        if (e.Kind == AgentUiEventKind.Status) { Status = e.Title; if (e.Text.Length > 0) Timeline.Add(new(e)); return; }
        var existing = e.Id is null ? null : Timeline.FirstOrDefault(x => x.Id == e.Id);
        if (e.Kind == AgentUiEventKind.Operation && e.Id is not null) Status = e.Text;
        if (existing is not null) existing.Update(e); else Timeline.Add(new(e)); TimelineChanged?.Invoke();
    }
    public async Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<AgentQuestion> questions, CancellationToken cancellation)
    {
        var source = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentTimelineItem? card = null;
        await Main.UiDispatcher.InvokeAsync(() =>
        {
            Status = "等待你的选择";
            card = new(new(AgentUiEventKind.Question, "需要你的选择"));
            foreach (var question in questions) card.Questions.Add(new(question));
            card.Submit = () => { if (card.Questions.Any(x => string.IsNullOrWhiteSpace(x.Answer))) { card.Text = "请回答每一个问题"; return; } source.TrySetResult(card.Questions.ToDictionary(x => x.Id, x => x.Answer)); };
            Timeline.Add(card); TimelineChanged?.Invoke();
        });
        try { return await source.Task.WaitAsync(cancellation); }
        finally { await Main.UiDispatcher.InvokeAsync(() => { if (card is not null) { card.CanRespond = false; card.Text = source.Task.IsCompletedSuccessfully ? "已回答" : "已中止"; } }); }
    }
    public async Task<bool> ApproveAsync(AgentApproval approval, CancellationToken cancellation)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); AgentTimelineItem? card = null;
        await Main.UiDispatcher.InvokeAsync(() => { Status = "等待确认"; card = new(new(AgentUiEventKind.Approval, approval.Title, approval.Detail)); card.Confirm = approved => source.TrySetResult(approved); Timeline.Add(card); TimelineChanged?.Invoke(); });
        try { return await source.Task.WaitAsync(cancellation); }
        finally { await Main.UiDispatcher.InvokeAsync(() => { if (card is not null) { card.CanRespond = false; card.Text += source.Task.IsCompletedSuccessfully ? source.Task.Result ? "\n已确认" : "\n已拒绝" : "\n已中止"; } }); }
    }
}

public sealed partial class AgentTimelineItem : ObservableObject
{
    public string? Id { get; }
    public AgentUiEventKind Kind { get; }
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private bool _canRespond = true;
    [ObservableProperty] private DownloadTaskInfo? _taskProgress;
    [ObservableProperty] private bool _isProgressExpanded;
    public bool HasTaskProgress => TaskProgress is not null;
    public bool HasPlainDetail => TaskProgress is null && Detail.Length > 0;
    public bool IsAssistant => Kind == AgentUiEventKind.Assistant;
    public bool IsUser => Kind == AgentUiEventKind.User;
    public bool IsError => Kind is AgentUiEventKind.Error or AgentUiEventKind.Interrupted;
    public bool IsQuestion => Kind == AgentUiEventKind.Question;
    public bool IsApproval => Kind == AgentUiEventKind.Approval;
    public bool IsOperation => Kind == AgentUiEventKind.Operation;
    public ObservableCollection<AgentQuestionViewModel> Questions { get; } = new();
    public Action? Submit { get; set; }
    public Action<bool>? Confirm { get; set; }
    public AgentTimelineItem(AgentUiEvent e) { Kind = e.Kind; Id = e.Id; Update(e); }
    public void Update(AgentUiEvent e) { Title = e.Title; Text = e.Text; HasProgress = e.TaskProgress is null && e.Percent.HasValue; Progress = e.Percent ?? 0; if (e.Detail is not null) Detail = e.Detail; if (e.TaskProgress is not null) TaskProgress = e.TaskProgress; OnPropertyChanged(nameof(HasPlainDetail)); }
    partial void OnTaskProgressChanged(DownloadTaskInfo? value) { OnPropertyChanged(nameof(HasTaskProgress)); OnPropertyChanged(nameof(HasPlainDetail)); }
    [RelayCommand] private void SubmitAnswers() => Submit?.Invoke();
    [RelayCommand] private void Approve() { if (CanRespond) Confirm?.Invoke(true); }
    [RelayCommand] private void Decline() { if (CanRespond) Confirm?.Invoke(false); }
}
public sealed partial class AgentQuestionViewModel(AgentQuestion question) : ObservableObject
{
    public string Id => question.Id;
    public string Prompt => question.Prompt;
    public IReadOnlyList<AgentOption> Options => question.Options;
    [ObservableProperty] private AgentOption? _selectedOption;
    [ObservableProperty] private string _customInput = "";
    public string Answer => CustomInput.Trim().Length > 0 ? CustomInput.Trim() : SelectedOption?.Label ?? "";
}
