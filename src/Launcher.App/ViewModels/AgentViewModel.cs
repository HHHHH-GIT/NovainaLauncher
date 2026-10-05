using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.AI;
using Launcher.Core;

namespace Launcher.App.ViewModels;

public sealed partial class AgentViewModel : ViewModelBase, IAgentInteraction
{
    public MainViewModel Main { get; }
    private AgentSessionService _session;
    private IDeepSeekClient _client;
    private string? _key;
    private CancellationTokenSource? _connection;
    private Task? _connectionTask;
    private Task? _stopTask;
    private readonly Dictionary<string, AgentTimelineItem> _toolItems = new();
    public string PendingKey { private get; set; } = "";
    public ObservableCollection<AgentTimelineItem> Timeline { get; } = new();
    public ObservableCollection<AgentModel> Models { get; } = new();
    public ObservableCollection<AgentComposerSuggestion> Suggestions { get; } = new();
    public ObservableCollection<AgentComposerReference> DraftReferences { get; } = new();
    [ObservableProperty] private AgentComposerSuggestion? _selectedSuggestion;
    [ObservableProperty] private AgentContextUsage _contextUsage = new(0, 131072, []);
    [ObservableProperty] private string _compactionNotice = "";
    public bool HasCompactionNotice => CompactionNotice.Length > 0;
    public bool CanDismissCompactionNotice => !ContextUsage.IsCompacting && !(IsRunning && ContextUsage.CompactionPending);
    public bool CanCompact => !IsStopping && !IsConnecting && IsConfigured && SelectedModel is not null && !ContextUsage.IsCompacting && !(IsRunning && ContextUsage.CompactionPending);
    public IReadOnlyList<AgentContextCategoryView> ContextCategories => AgentContextCategoryView.Create(ContextUsage);
    public long ContextRemainingTokens => Math.Max(0, (long)ContextUsage.Capacity - Math.Max(0, ContextUsage.Used));
    public double ContextRemainingPercent => ContextUsage.Capacity > 0 ? 100d * ContextRemainingTokens / ContextUsage.Capacity : 0;
    public string ContextRemainingTokenLabel => AgentContextUsage.FormatTokens(ContextRemainingTokens);
    public string ContextRemainingPercentLabel => AgentContextCategoryView.FormatPercent(ContextRemainingPercent);
    public string ContextUsageSummary => $"{(ContextUsage.IsEstimate ? "约 " : "")}{AgentContextUsage.FormatTokens(ContextUsage.Used)} / {AgentContextUsage.FormatTokens(ContextUsage.Capacity)} ({ContextUsage.Percent:F0}%)";
    [ObservableProperty] private bool _showSuggestions;
    [ObservableProperty] private bool _showGoalEditor;
    [ObservableProperty] private string _goalDraft = "";
    [ObservableProperty] private string _sessionGoal = "";
    private AgentComposerToken? _composerToken;
    public bool HasGoal => SessionGoal.Length > 0;
    public bool HasDraftReferences => DraftReferences.Count > 0;
    public event Action<int>? FocusComposerRequested;
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
    public bool ShowWelcome => !ShowAiSettings && !IsManaging && (!IsConfigured || ShowConnection);
    public bool CanConfigure => !IsRunning && !IsStopping && !IsConnecting && !IsManaging;
    public bool CanSend => !IsRunning && !IsStopping && !IsConnecting && !IsManaging && IsConfigured && SelectedModel is not null && !string.IsNullOrWhiteSpace(Input);
    public event Action? TimelineChanged;
    public AgentViewModel(MainViewModel main)
    {
        Main = main;
        _aiConfiguration = _aiConfigurationStore.Load(main.Settings);
        _reasoningEffort = _aiConfiguration.ReasoningEffort;
        _sidebarExpanded = _aiConfiguration.SidebarExpanded;
        try { _key = main.Secrets.ReadJson<string>("deepseek-api-key"); } catch { ConnectionStatus = "密钥读取失败，请重新配置"; }
        if (_key is { Length: > 0 }) { main.Log.RegisterSecret(_key); IsConfigured = true; ConnectionStatus = "已配置"; }
        _client = new DeepSeekClient(() => _key);
        _session = CreateSession(AgentMode.Basic);
        if (_aiConfiguration.ModelId.Length > 0) { var saved = new AgentModel(_aiConfiguration.ModelId, _aiConfiguration.ModelId) { HasVerifiedContextWindow = false }; Models.Add(saved); _selectedModel = saved; _session.SetModel(saved); }
        ContextUsage = _session.ContextUsage;
        DraftReferences.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDraftReferences));
    }
    private sealed class ClientProxy(AgentViewModel vm) : IDeepSeekClient
    {
        public Task<AgentResponse> RespondWithInstructionsAsync(AgentModel m, string effort, System.Text.Json.Nodes.JsonArray h, System.Text.Json.Nodes.JsonArray t, string instructions, Action<string> delta, CancellationToken c) => vm._client.RespondWithInstructionsAsync(m, effort, h, t, instructions, delta, c);
        public Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool refresh, CancellationToken cancellation) => vm._client.GetModelsAsync(refresh, cancellation);
        public Task<AgentResponse> RespondAsync(AgentModel m, string effort, System.Text.Json.Nodes.JsonArray h, System.Text.Json.Nodes.JsonArray t, Action<string> delta, CancellationToken c) => vm._client.RespondAsync(m, effort, h, t, delta, c);
        public Task<AgentCompaction> CompactAsync(AgentModel m, string effort, System.Text.Json.Nodes.JsonArray h, CancellationToken c) => vm._client.CompactAsync(m, effort, h, c);
    }
    partial void OnSelectedModelChanged(AgentModel? value)
    { if (value is null || IsRunning) return; _session.SetModel(value); _aiConfiguration.ModelId = value.Id; _aiConfigurationStore.Save(_aiConfiguration); OnPropertyChanged(nameof(ReasoningLevels)); if (!ReasoningLevels.Contains(ReasoningEffort)) ReasoningEffort = ReasoningLevels.Contains("high") ? "high" : ReasoningLevels[0]; Changed(); }
    partial void OnReasoningEffortChanged(string value) { _aiConfiguration.ReasoningEffort = value; _aiConfigurationStore.Save(_aiConfiguration); }
    partial void OnInputChanged(string value) { foreach (var item in DraftReferences.Where(x => !value.Contains(x.Token, StringComparison.Ordinal)).ToArray()) DraftReferences.Remove(item); Changed(); }
    partial void OnSessionGoalChanged(string value) => OnPropertyChanged(nameof(HasGoal));
    partial void OnCompactionNoticeChanged(string value) => OnPropertyChanged(nameof(HasCompactionNotice));
    partial void OnContextUsageChanged(AgentContextUsage value)
    {
        OnPropertyChanged(nameof(ContextCategories)); OnPropertyChanged(nameof(ContextRemainingTokens)); OnPropertyChanged(nameof(ContextRemainingPercent));
        OnPropertyChanged(nameof(ContextRemainingTokenLabel)); OnPropertyChanged(nameof(ContextRemainingPercentLabel)); OnPropertyChanged(nameof(ContextUsageSummary));
        OnPropertyChanged(nameof(CanCompact)); OnPropertyChanged(nameof(CanDismissCompactionNotice));
    }
    partial void OnIsConfiguredChanged(bool value) { OnPropertyChanged(nameof(ShowWelcome)); Changed(); }
    partial void OnShowConnectionChanged(bool value) => OnPropertyChanged(nameof(ShowWelcome));
    partial void OnIsRunningChanged(bool value) => Changed();
    partial void OnIsStoppingChanged(bool value) => Changed();
    partial void OnIsConnectingChanged(bool value) => Changed();
    private void Changed() { OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanManageConversations)); OnPropertyChanged(nameof(CanCompact)); OnPropertyChanged(nameof(CanDismissCompactionNotice)); SendCommand.NotifyCanExecuteChanged(); }
    public async Task ModeChangedAsync(bool enabled)
    {
        if (!enabled) { ShowSuggestions = false; ShowGoalEditor = false; ShowFullAccessWarning = false; await StopAsync(); await SaveCurrentConversationAsync(); _permissions.Reset(); _workbench?.Revoke(); PermissionChanged(); Main.CompleteAccountLogin(); }
        else { await InitializeConversationsAsync(); if (IsConfigured) { _connectionTask = RefreshModelsCoreAsync(false); await _connectionTask; } }
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
            ApplyModels(models); IsConfigured = true; ShowConnection = false; _aiConfiguration.SetupCompleted = true; _aiConfigurationStore.Save(_aiConfiguration); ConnectionStatus = "连接成功";
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
        var prior = _aiConfiguration.ModelId; Models.Clear(); foreach (var m in models) Models.Add(m);
        SelectedModel = models.FirstOrDefault(m => m.Id == prior) ?? models.FirstOrDefault(m => m.Id.Contains("pro", StringComparison.OrdinalIgnoreCase)) ?? models.FirstOrDefault(m => m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase)) ?? models[0];
    }
    [RelayCommand] private void RemoveKey()
    { if (!CanConfigure) return; Main.Secrets.Delete("deepseek-api-key"); _key = null; PendingKey = ""; _client = new DeepSeekClient(() => _key); IsConfigured = false; Models.Clear(); SelectedModel = null; _aiConfiguration.SetupCompleted = false; _aiConfigurationStore.Save(_aiConfiguration); ConnectionStatus = "未配置"; }
    [RelayCommand(CanExecute = nameof(CanSend))] private async Task SendAsync()
    {
        if (await TryExecuteLocalInputAsync()) return;
        if (!CanSend) return;
        var text = Input.Trim(); var references = DraftReferences.Select(x => x.Reference).ToArray();
        var current = Main.Operations.GetReferences();
        if (references.Any(r => !current.Any(x => x.Id == r.Id && x.Kind == r.Kind))) { Status = "引用已失效，请重新选择"; return; }
        Input = ""; ShowSuggestions = false;
        await RunMessageAsync(text, references);
    }
    private async Task RunMessageAsync(string text, IReadOnlyList<AgentReference>? references = null)
    {
        if (SelectedModel is not { } model || !CanConfigure || !IsConfigured) return;
        IsRunning = true;
        try { await _session.SendAsync(text, model, _aiConfiguration.ReasoningEffort, references: references); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Status = e.Message; }
        finally { IsRunning = false; await SaveCurrentConversationAsync(); }
    }
    public void UpdateSuggestions(int caret)
    {
        _composerToken = AgentComposerParser.AtCaret(Input, caret); Suggestions.Clear();
        if (_composerToken is { Prefix: '/' } slash)
        {
            foreach (var command in new[] { new AgentComposerSuggestion("/compact", "压缩上下文，保留目标与关键状态", "◔", "compact"), new AgentComposerSuggestion("/goal", "设置本次会话持续目标", "◎", "goal") })
                if (command.Title[1..].StartsWith(slash.Query, StringComparison.OrdinalIgnoreCase)) Suggestions.Add(command);
        }
        else if (_composerToken is { Prefix: '@' } mention)
            foreach (var reference in Main.Operations.GetReferences().Where(x => AgentComposerParser.Matches(x, mention.Query)))
                Suggestions.Add(new(reference.Name, reference.KindLabel + " · " + reference.Detail, reference.Kind switch { "game" => "◇", "java" => "⚙", _ => "○" }, Reference: reference));
        SelectedSuggestion = Suggestions.FirstOrDefault(); ShowSuggestions = Suggestions.Count > 0;
    }
    public async Task AcceptSuggestionAsync(AgentComposerSuggestion suggestion)
    {
        ShowSuggestions = false;
        if (suggestion.Reference is { } reference && _composerToken is { } token)
        {
            var label = $"@「{reference.KindLabel}：{reference.Name}」";
            if (Main.Operations.GetReferences().Count(x => x.Kind == reference.Kind && x.Name == reference.Name) > 1) label = $"@「{reference.KindLabel}：{reference.Name} · {reference.Id[^6..]}」";
            Input = Input.Remove(token.Start, token.Length).Insert(token.Start, label + " ");
            if (!DraftReferences.Any(x => x.Reference.Id == reference.Id)) DraftReferences.Add(new(reference, label));
            FocusComposerRequested?.Invoke(token.Start + label.Length + 1);
        }
        else if (suggestion.Command == "compact") { Input = ""; await CompactAsync(); }
        else if (suggestion.Command == "goal") { Input = ""; OpenGoal(); }
    }
    public async Task<bool> TryExecuteLocalInputAsync()
    {
        var text = Input.Trim();
        if (text.Equals("/compact", StringComparison.OrdinalIgnoreCase)) { Input = ""; ShowSuggestions = false; await CompactAsync(); return true; }
        if (text.Equals("/goal", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/goal ", StringComparison.OrdinalIgnoreCase))
        { Input = ""; ShowSuggestions = false; OpenGoal(); if (text.Length > 5 && ShowGoalEditor) GoalDraft = text[5..].Trim(); return true; }
        return false;
    }
    [RelayCommand] private async Task CompactAsync()
    {
        if (SelectedModel is not { } model || !IsConfigured) { Status = CompactionNotice = "请先连接模型，再压缩上下文。"; return; }
        if (ContextUsage.IsCompacting || (IsRunning && ContextUsage.CompactionPending)) return;
        if (IsRunning) { _session.RequestCompaction(); Status = "将在当前操作完整结束后压缩"; return; }
        if (!CanConfigure) return;
        IsRunning = true;
        try { await _session.CompactAsync(model, ReasoningEffort); } finally { IsRunning = false; await SaveCurrentConversationAsync(); }
    }
    [RelayCommand] private void DismissCompactionNotice() { if (CanDismissCompactionNotice) CompactionNotice = ""; }
    [RelayCommand] private void OpenGoal()
    { if (!CanConfigure) { Status = "请先停止执行再修改目标"; return; } GoalDraft = SessionGoal; ShowGoalEditor = true; }
    [RelayCommand] private void CloseGoal() => ShowGoalEditor = false;
    [RelayCommand] private async Task SaveGoalAsync()
    {
        if (!CanConfigure || !IsConfigured || SelectedModel is null || string.IsNullOrWhiteSpace(GoalDraft)) return;
        _session.SetGoal(GoalDraft); SessionGoal = _session.Goal; ShowGoalEditor = false;
        await RunMessageAsync("本次会话目标：" + SessionGoal);
    }
    [RelayCommand] private void ClearGoal()
    { if (!CanConfigure) return; _session.SetGoal(""); SessionGoal = ""; ShowGoalEditor = false; }
    [RelayCommand] private Task ContinueGoalAsync() => HasGoal ? RunMessageAsync("继续本次会话目标；请先核实实际状态，避免重复已完成的操作。") : Task.CompletedTask;
    [RelayCommand] private Task StopAsync() => _stopTask is { IsCompleted: false } existing ? existing : _stopTask = StopCoreAsync();
    private async Task StopCoreAsync()
    {
        IsStopping = true; _connection?.Cancel(); _managementInteraction?.Cancel();
        try { await _session.StopAsync(); if (_connectionTask is { } connect) await connect; if (_managementInteractionTask is { IsCompleted: false } ui) await ui; }
        finally { IsStopping = false; IsRunning = false; Status = "就绪"; }
    }
    public async Task CancelAndWaitAsync() { await StopAsync(); await SaveCurrentConversationAsync(); }
    [RelayCommand] private Task NewConversationAsync() => CreateConversationAsync();
    [RelayCommand] private void Suggest(string text) => Input = text;
    private void Receive(AgentUiEvent e)
    {
        if (e.Kind == AgentUiEventKind.Context) { if (e.ContextUsage is not null) ContextUsage = e.ContextUsage; return; }
        if (e.Kind == AgentUiEventKind.Compaction) { CompactionNotice = e.Title + (e.Text.Length > 0 ? "\n" + e.Text : ""); Status = e.Title; return; }
        if (e.ToolCallId is { } callId)
        {
            if (e.Kind == AgentUiEventKind.ToolStarted)
            {
                if (!_toolItems.ContainsKey(callId))
                {
                    var group = Timeline.LastOrDefault() as AgentToolGroupItem;
                    if (group is null) { group = new(); Timeline.Add(group); }
                    var item = new AgentTimelineItem(e); _toolItems[callId] = item; group.Tools.Add(item);
                }
            }
            else if (_toolItems.TryGetValue(callId, out var item))
            {
                if (e.Kind == AgentUiEventKind.ToolCompleted) item.Update(e);
                else item.UpdateOperation(e);
            }
            else { Timeline.Add(new(e)); }
            if (e.Kind == AgentUiEventKind.Operation) Status = e.Text.Length > 0 ? e.Text : e.Title;
            TimelineChanged?.Invoke(); return;
        }
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
            card.Submit = () =>
            {
                if (!card.CanRespond || cancellation.IsCancellationRequested) return;
                if (card.Questions.Any(x => string.IsNullOrWhiteSpace(x.Answer))) { card.Text = "请回答每一个问题"; return; }
                var answers = card.Questions.ToDictionary(x => x.Id, x => x.Answer);
                if (source.TrySetResult(answers)) { card.ResolveQuestions(answers); TimelineChanged?.Invoke(); }
            };
            Timeline.Add(card); TimelineChanged?.Invoke();
        });
        try { return await source.Task.WaitAsync(cancellation); }
        finally { await Main.UiDispatcher.InvokeAsync(() => { if (card is not null) { card.ResolveQuestions(source.Task.IsCompletedSuccessfully ? source.Task.Result : null); TimelineChanged?.Invoke(); } }); }
    }
    public async Task<bool> ApproveAsync(AgentApproval approval, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (IsFullAccess) return true;
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); AgentTimelineItem? card = null;
        await Main.UiDispatcher.InvokeAsync(() => { Status = "等待确认"; card = new(new(AgentUiEventKind.Approval, approval.Title, approval.Detail)); card.Confirm = approved => source.TrySetResult(approved); Timeline.Add(card); TimelineChanged?.Invoke(); });
        try { return await source.Task.WaitAsync(cancellation); }
        finally { await Main.UiDispatcher.InvokeAsync(() => { if (card is not null) { card.CanRespond = false; card.Text += source.Task.IsCompletedSuccessfully ? source.Task.Result ? "\n已确认" : "\n已拒绝" : "\n已中止"; } }); }
    }
}

public sealed record AgentComposerSuggestion(string Title, string Description, string Symbol, string? Command = null, AgentReference? Reference = null);
public sealed record AgentComposerReference(AgentReference Reference, string Token);

public partial class AgentTimelineItem : ObservableObject
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
    [ObservableProperty] private bool _isDetailExpanded;
    [ObservableProperty] private bool _isQuestionExpanded;
    [ObservableProperty] private string _questionSummaryTitle = "问题已中止";
    [ObservableProperty] private AgentToolState? _toolState;
    public bool HasTaskProgress => TaskProgress is not null;
    public bool HasActiveTaskProgress => TaskProgress?.IsActive == true;
    public bool HasPlainDetail => TaskProgress is null && Detail.Length > 0;
    public bool IsAssistant => Kind == AgentUiEventKind.Assistant;
    public bool IsUser => Kind == AgentUiEventKind.User;
    public bool IsError => Kind is AgentUiEventKind.Error or AgentUiEventKind.Interrupted || ToolState is AgentToolState.Failed or AgentToolState.Cancelled;
    public bool IsQuestion => Kind == AgentUiEventKind.Question;
    public bool IsQuestionPending => IsQuestion && CanRespond;
    public bool IsQuestionResolved => IsQuestion && !CanRespond;
    public bool IsApproval => Kind == AgentUiEventKind.Approval;
    public bool IsOperation => Kind == AgentUiEventKind.Operation;
    public bool IsTool => Kind is AgentUiEventKind.ToolStarted or AgentUiEventKind.ToolCompleted;
    public bool IsToolRunning => ToolState == AgentToolState.Running;
    public string ToolMarker => ToolState switch { AgentToolState.Running => "·", AgentToolState.Failed => "!", AgentToolState.Cancelled => "–", _ => "✓" };
    public string ToolCaption => IsToolRunning ? Text.Length > 0 ? Text : "执行中" : ToolState switch { AgentToolState.Failed => "未完成", AgentToolState.Cancelled => "已中止", _ => "已完成" };
    public ObservableCollection<AgentQuestionViewModel> Questions { get; } = new();
    public ObservableCollection<AgentAnswerSummary> Answers { get; } = new();
    public Action? Submit { get; set; }
    public Action<bool>? Confirm { get; set; }
    public AgentTimelineItem(AgentUiEvent e) { Kind = e.Kind; Id = e.Id; Update(e); }
    public void Update(AgentUiEvent e) { Title = e.Title; Text = e.Text; HasProgress = e.TaskProgress is null && e.Percent.HasValue; Progress = e.Percent ?? 0; if (e.Detail is not null) Detail = e.Detail; if (e.TaskProgress is not null) TaskProgress = e.TaskProgress; if (e.ToolState is { } state) ToolState = state; OnPropertyChanged(nameof(HasPlainDetail)); OnPropertyChanged(nameof(ToolCaption)); }
    public void UpdateOperation(AgentUiEvent e)
    {
        Update(e with { Title = Title, Text = e.TaskProgress?.Detail ?? (e.Text.Length > 0 ? e.Text : e.Title) });
    }
    partial void OnToolStateChanged(AgentToolState? value) { OnPropertyChanged(nameof(IsToolRunning)); OnPropertyChanged(nameof(IsError)); OnPropertyChanged(nameof(ToolMarker)); OnPropertyChanged(nameof(ToolCaption)); }
    partial void OnTaskProgressChanged(DownloadTaskInfo? value) { OnPropertyChanged(nameof(HasTaskProgress)); OnPropertyChanged(nameof(HasActiveTaskProgress)); OnPropertyChanged(nameof(HasPlainDetail)); }
    partial void OnCanRespondChanged(bool value) { OnPropertyChanged(nameof(IsQuestionPending)); OnPropertyChanged(nameof(IsQuestionResolved)); }
    public void ResolveQuestions(IReadOnlyDictionary<string, string>? answers)
    {
        if (!IsQuestion || !CanRespond) return;
        if (answers is not null)
            foreach (var question in Questions)
                if (answers.TryGetValue(question.Id, out var answer))
                    Answers.Add(new(question.Prompt, answer, question.CustomInput.Trim().Length == 0 && question.SelectedOption?.Label == answer ? question.SelectedOption.Description : ""));
        Questions.Clear();
        QuestionSummaryTitle = answers is null ? "问题已中止" : $"已回答 {Answers.Count} 个问题";
        Text = ""; IsQuestionExpanded = false; CanRespond = false;
    }
    [RelayCommand] private void SubmitAnswers() => Submit?.Invoke();
    [RelayCommand] private void Approve() { if (CanRespond) Confirm?.Invoke(true); }
    [RelayCommand] private void Decline() { if (CanRespond) Confirm?.Invoke(false); }
}
public sealed record AgentAnswerSummary(string Prompt, string Answer, string Description);
public sealed partial class AgentToolGroupItem : AgentTimelineItem
{
    [ObservableProperty] private bool _isExpanded = true;
    private bool _wasRunning;
    public ObservableCollection<AgentTimelineItem> Tools { get; } = new();
    public AgentToolGroupItem() : base(new(AgentUiEventKind.Operation, "正在调用工具"))
    {
        Tools.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (AgentTimelineItem item in e.NewItems) item.PropertyChanged += (_, _) => Refresh();
            Refresh();
        };
    }
    private void Refresh()
    {
        var running = Tools.Any(t => t.IsToolRunning);
        if (running != _wasRunning) IsExpanded = running;
        _wasRunning = running;
        Title = running ? "正在调用工具" : "调用了工具"; Text = Tools.Count + " 项";
    }
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
