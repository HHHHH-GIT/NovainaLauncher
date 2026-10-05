using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.AI;
using Launcher.Core;
using Microsoft.Win32;

namespace Launcher.App.ViewModels;

public sealed partial class AgentViewModel
{
    private readonly AiConfigurationStore _aiConfigurationStore = new();
    private readonly AgentConversationStore _conversationsStore = new();
    private AiConfiguration _aiConfiguration = new();
    private AgentConversationEntry _conversation = new(Guid.NewGuid(), AgentMode.Basic, "新对话", DateTimeOffset.Now);
    private WorkbenchOperations? _workbench;
    private readonly AgentPermissionPolicy _permissions = new();
    private FullAccessOperations? _fullOperations;
    public bool FullAccess => IsFullAccess;
    [ObservableProperty] private bool _showFullAccessWarning;
    public bool IsFullAccess => _permissions.FullAccess;
    public string PermissionLabel => IsFullAccess ? "⚠ 授权模式" : "审批模式";
    public string FullAccessWarning => AgentPermissionPolicy.Warning;
    [RelayCommand] private void OpenFullAccessWarning() { if (CanConfigure) ShowFullAccessWarning = true; }
    [RelayCommand] private void CancelFullAccess() => ShowFullAccessWarning = false;
    [RelayCommand] private void ConfirmFullAccess()
    {
        if (!ShowFullAccessWarning || !CanConfigure) return;
        _permissions.EnableAfterHumanConfirmation(); ShowFullAccessWarning = false;
        if (IsWorkbench && WorkspacePath.Length > 0 && Directory.Exists(WorkspacePath)) { _workbench!.Authorize(WorkspacePath); _fullOperations!.SetWorkingDirectoryFromHost(WorkspacePath); }
        PermissionChanged(); Status = "授权模式已启用";
    }
    [RelayCommand] private void UseApprovalMode()
    {
        if (!CanConfigure) return;
        _permissions.Reset(); _workbench?.Revoke(); ShowFullAccessWarning = false; PermissionChanged();
    }
    private void PermissionChanged() { OnPropertyChanged(nameof(IsFullAccess)); OnPropertyChanged(nameof(PermissionLabel)); OnPropertyChanged(nameof(WorkspaceLabel)); _session.SetModel(SelectedModel ?? new("", "")); }
    private bool _conversationsInitialized, _suppressSelection;
    private CancellationTokenSource? _managementInteraction;
    private Task<bool>? _managementInteractionTask;
    private readonly SemaphoreSlim _managementGate = new(1);
    private readonly SemaphoreSlim _saveGate = new(1);
    private readonly HashSet<Guid> _deletedConversations = new();
    [ObservableProperty] private bool _isManaging;
    [ObservableProperty] private bool _showAiSettings;
    [ObservableProperty] private bool _sidebarExpanded = true;
    [ObservableProperty] private string _workspacePath = "";
    [ObservableProperty] private string _sessionStorageStatus = "会话按模式加密保存在本地";
    [ObservableProperty] private AgentConversationEntry? _selectedConversation;
    [ObservableProperty] private AgentModeChoice _selectedModeChoice = new(AgentMode.Basic, "基础模式", "游玩、安装与管理");
    public IReadOnlyList<AgentModeChoice> ModeChoices { get; } = [new(AgentMode.Basic, "基础模式", "游玩、安装与管理"), new(AgentMode.Workbench, "工作台", "创建、编译与交付 Mod")];
    public ObservableCollection<AgentConversationEntry> Conversations { get; } = new();
    public bool IsWorkbench => _conversation.Mode == AgentMode.Workbench;
    public string ConversationTitle => _conversation.Title;
    public string EmptyTitle => IsWorkbench ? "想做一个怎样的模组？" : "今天，想创造什么？";
    public string WorkspaceLabel => WorkspacePath.Length == 0 ? "未选择项目" : _workbench?.Root is null ? "待重新授权 · " + WorkspacePath : WorkspacePath;
    public bool CanManageConversations => !IsManaging && !IsStopping;
    partial void OnIsManagingChanged(bool value) { Changed(); OnPropertyChanged(nameof(CanManageConversations)); OnPropertyChanged(nameof(ShowWelcome)); }
    partial void OnShowAiSettingsChanged(bool value) => OnPropertyChanged(nameof(ShowWelcome));
    partial void OnSidebarExpandedChanged(bool value) { _aiConfiguration.SidebarExpanded = value; _aiConfigurationStore.Save(_aiConfiguration); }
    partial void OnWorkspacePathChanged(string value) => OnPropertyChanged(nameof(WorkspaceLabel));
    partial void OnSelectedModeChoiceChanged(AgentModeChoice value)
    { if (!_suppressSelection && _conversationsInitialized && value.Mode != _conversation.Mode) _ = SwitchModeAsync(value.Mode); }
    partial void OnSelectedConversationChanged(AgentConversationEntry? value)
    { if (!_suppressSelection && value is not null && value.Id != _conversation.Id) _ = OpenConversationAsync(value); }
    [RelayCommand] private void ToggleConversationSidebar() => SidebarExpanded = !SidebarExpanded;
    [RelayCommand] private void OpenAiSettings() { ShowSuggestions = false; ShowGoalEditor = false; ShowAiSettings = true; }
    [RelayCommand] private void CloseAiSettings() => ShowAiSettings = false;
    private AgentSessionService CreateSession(AgentMode mode)
    {
        var proxy = new ClientProxy(this); IAgentToolRegistry registry;
        if (mode == AgentMode.Workbench)
        {
            _workbench = new WorkbenchOperations(Main.Operations.GetDevelopmentJavas, Main.Log.Redact, fullAccess: () => IsFullAccess);
            registry = new WorkbenchToolRegistry(Main.Operations, _workbench, DelegateBasicAsync);
        }
        else { _workbench = null; registry = new AgentToolRegistry(Main.Operations); }
        var fullOperations = _fullOperations = new FullAccessOperations(Main.Log.Redact, root => Main.UiDispatcher.Invoke(() => { if (_workbench is not null) { _workbench.Authorize(root); WorkspacePath = root; OnPropertyChanged(nameof(WorkspaceLabel)); } }), Main.Operations);
        registry = new PermissionToolRegistry(registry, fullOperations, _permissions);
        var session = new AgentSessionService(proxy, registry, Main.Operations, this, mode == AgentMode.Workbench ? WorkbenchPrompt.System : AgentPrompt.System,
            instructionsProvider: () => (mode == AgentMode.Workbench ? WorkbenchPrompt.System : AgentPrompt.System) + (IsFullAccess ? "\n" + AgentPermissionPolicy.Instructions : ""));
        session.Event += e => Main.UiDispatcher.InvokeAsync(() => { if (ReferenceEquals(_session, session)) Receive(e); });
        session.Checkpoint += snapshot => Main.UiDispatcher.InvokeAsync(async () => { if (ReferenceEquals(_session, session)) await SaveSnapshotAsync(snapshot); });
        return session;
    }
    private Task<ToolResult> DelegateBasicAsync(string goal, AgentExecutionContext context)
        => SelectedModel is { } model ? new BasicAgentDelegate(new ClientProxy(this), Main.Operations).RunAsync(goal, model, ReasoningEffort, context) : Task.FromResult(ToolResult.Fail("请先连接模型"));
    private async Task InitializeConversationsAsync()
    {
        if (_conversationsInitialized) return;
        _conversationsInitialized = true;
        await SwitchModeAsync(_aiConfiguration.LastMode, true);
    }
    private async Task RefreshConversationListAsync()
    {
        var entries = await Task.Run(() => _conversationsStore.List(_conversation.Mode));
        _suppressSelection = true;
        try { Conversations.Clear(); foreach (var entry in entries) Conversations.Add(entry); SelectedConversation = Conversations.FirstOrDefault(x => x.Id == _conversation.Id); }
        finally { _suppressSelection = false; }
    }
    private async Task SaveCurrentConversationAsync()
    {
        if (_session.IsRunning) return;
        await SaveSnapshotAsync(_session.Capture());
    }
    private async Task SaveSnapshotAsync(AgentSessionSnapshot snapshot)
    {
        if (Timeline.Count == 0 && snapshot.Goal.Length == 0 && WorkspacePath.Length == 0) return;
        var title = _conversation.Title;
        if (title == "新对话") { var text = Timeline.FirstOrDefault(x => x.IsUser)?.Text.Replace('\n', ' ').Trim() ?? "会话目标"; title = text[..Math.Min(text.Length, 32)]; }
        var entry = _conversation with { Title = title, Updated = DateTimeOffset.Now };
        var saved = new AgentConversation(entry, snapshot, Timeline.Select(SnapshotItem).ToArray(), WorkspacePath);
        await _saveGate.WaitAsync();
        try
        {
            if (_deletedConversations.Contains(entry.Id)) return;
            await Task.Run(() => _conversationsStore.Save(saved));
            if (_conversation.Id == entry.Id)
            {
                _conversation = entry; SessionStorageStatus = "会话已加密保存"; _suppressSelection = true;
                try { var previous = Conversations.FirstOrDefault(x => x.Id == entry.Id); if (previous is not null) Conversations.Remove(previous); Conversations.Insert(0, entry); SelectedConversation = entry; }
                finally { _suppressSelection = false; }
                OnPropertyChanged(nameof(ConversationTitle));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { SessionStorageStatus = "会话保存失败，请检查数据目录权限"; }
        finally { _saveGate.Release(); }
    }
    private static AgentTranscriptEntry SnapshotItem(AgentTimelineItem item)
    {
        var state = item.ToolState == AgentToolState.Running ? AgentToolState.Cancelled : item.ToolState;
        var e = new AgentUiEvent(item.Kind, item.Title, item.Text, item.Id, item.HasProgress ? item.Progress : null, item.Detail) { ToolState = state };
        return new(e, item.Answers.Select(x => new AgentAnswerRecord(x.Prompt, x.Answer, x.Description)).ToArray(), item is AgentToolGroupItem group ? group.Tools.Select(SnapshotItem).ToArray() : null);
    }
    private static AgentTimelineItem RestoreItem(AgentTranscriptEntry saved)
    {
        AgentTimelineItem item;
        if (saved.Tools is { } tools) { var group = new AgentToolGroupItem(); foreach (var tool in tools) group.Tools.Add(RestoreItem(tool)); group.IsExpanded = false; item = group; }
        else item = new(saved.Event);
        if (item.IsQuestion) { item.ResolveQuestions(null); foreach (var answer in saved.Answers ?? []) item.Answers.Add(new(answer.Prompt, answer.Answer, answer.Description)); item.QuestionSummaryTitle = item.Answers.Count > 0 ? $"已回答 {item.Answers.Count} 个问题" : "问题已中止"; }
        item.CanRespond = false; return item;
    }
    private void ResetView(AgentMode mode)
    {
        _permissions.Reset(); ShowFullAccessWarning = false;
        Main.Operations.ResetHandles();
        _toolItems.Clear(); Timeline.Clear(); Input = ""; DraftReferences.Clear(); SessionGoal = ""; GoalDraft = ""; CompactionNotice = ""; ShowGoalEditor = ShowSuggestions = false;
        _session = CreateSession(mode); if (SelectedModel is { } model) _session.SetModel(model); ContextUsage = _session.ContextUsage; WorkspacePath = "";
        PermissionChanged();
        OnPropertyChanged(nameof(IsWorkbench)); OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(ConversationTitle)); OnPropertyChanged(nameof(WorkspaceLabel)); TimelineChanged?.Invoke();
    }
    private async Task SwitchModeAsync(AgentMode mode, bool initial = false)
    {
        await _managementGate.WaitAsync(); IsManaging = true;
        try
        {
            if (!initial) { await StopAsync(); await SaveCurrentConversationAsync(); }
            var entries = await Task.Run(() => _conversationsStore.List(mode));
            _conversation = entries.FirstOrDefault() ?? new(Guid.NewGuid(), mode, "新对话", DateTimeOffset.Now);
            ResetView(mode);
            if (entries.Count > 0) await RestoreConversationAsync(_conversation);
            _suppressSelection = true; SelectedModeChoice = ModeChoices.First(x => x.Mode == mode); _suppressSelection = false;
            _aiConfiguration.LastMode = mode; _aiConfigurationStore.Save(_aiConfiguration); await RefreshConversationListAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { SessionStorageStatus = "读取会话失败，已保留当前会话"; }
        finally { IsManaging = false; _managementGate.Release(); }
    }
    private async Task RestoreConversationAsync(AgentConversationEntry entry)
    {
        var saved = await Task.Run(() => _conversationsStore.Load(entry.Mode, entry.Id)); if (saved is null) { SessionStorageStatus = "会话损坏或无法解密"; return; }
        _conversation = entry; _session.Restore(saved.Session); SessionGoal = _session.Goal; WorkspacePath = saved.Workspace;
        foreach (var item in saved.Transcript) Timeline.Add(RestoreItem(item)); ContextUsage = _session.ContextUsage;
        OnPropertyChanged(nameof(ConversationTitle)); TimelineChanged?.Invoke();
    }
    private async Task OpenConversationAsync(AgentConversationEntry entry)
    {
        await _managementGate.WaitAsync(); IsManaging = true;
        try { await StopAsync(); await SaveCurrentConversationAsync(); _conversation = entry; ResetView(entry.Mode); await RestoreConversationAsync(entry); await RefreshConversationListAsync(); }
        finally { IsManaging = false; _managementGate.Release(); }
    }
    private async Task CreateConversationAsync()
    {
        await _managementGate.WaitAsync(); IsManaging = true;
        try { await StopAsync(); await SaveCurrentConversationAsync(); _conversation = new(Guid.NewGuid(), _conversation.Mode, "新对话", DateTimeOffset.Now); ResetView(_conversation.Mode); await RefreshConversationListAsync(); }
        finally { IsManaging = false; _managementGate.Release(); }
    }
    [RelayCommand] private async Task DeleteConversationAsync(AgentConversationEntry entry)
    {
        if (IsManaging) return;
        if (!await ManagementApproveAsync(new("删除会话", $"删除“{entry.Title}”的本地记录？项目文件、游戏及账户不会删除。"))) return;
        await _managementGate.WaitAsync(); IsManaging = true;
        try
        {
            await StopAsync(); await _saveGate.WaitAsync();
            try
            {
                _deletedConversations.Add(entry.Id); await Task.Run(() => _conversationsStore.Delete(entry.Mode, entry.Id));
                if (entry.Id == _conversation.Id) { _conversation = new(Guid.NewGuid(), entry.Mode, "新对话", DateTimeOffset.Now); ResetView(entry.Mode); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _deletedConversations.Remove(entry.Id); Status = "无法删除会话，请检查数据目录权限"; }
            finally { _saveGate.Release(); }
            await RefreshConversationListAsync();
        }
        finally { IsManaging = false; _managementGate.Release(); }
    }
    [RelayCommand] private async Task ChooseWorkspaceAsync()
    {
        if (!IsWorkbench || !CanConfigure) return;
        var picker = new OpenFolderDialog { Title = "选择 Mod 项目目录", Multiselect = false };
        if (picker.ShowDialog() != true) return;
        if (!File.Exists(Path.Combine(picker.FolderName, "build.gradle")) && !File.Exists(Path.Combine(picker.FolderName, "build.gradle.kts"))) { Status = "请选择含 Gradle 构建文件的 Mod 项目；空项目请使用新建项目"; return; }
        await GrantWorkspaceAsync(picker.FolderName);
    }
    [RelayCommand] private async Task NewWorkspaceAsync()
    {
        if (!IsWorkbench || !CanConfigure) return;
        var picker = new OpenFolderDialog { Title = "选择新 Mod 项目的父目录", Multiselect = false }; if (picker.ShowDialog() != true) return;
        var root = AgentPathGuard.Within(picker.FolderName, Path.Combine(picker.FolderName, "NovainaMod-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root); await GrantWorkspaceAsync(root);
    }
    [RelayCommand] private async Task ReauthorizeWorkspaceAsync() { if (CanConfigure && IsWorkbench && WorkspacePath.Length > 0) await GrantWorkspaceAsync(WorkspacePath); }
    private async Task GrantWorkspaceAsync(string root)
    {
        if (!await ManagementApproveAsync(new("授权工作台项目", $"目录：{root}\n允许读取和创建源码、资源、构建文本。已有文件替换/删除会逐项确认；首次构建及配置变更另外确认。构建脚本可以运行代码，请只授权可信项目。"))) return;
        try { _workbench!.Authorize(root); _fullOperations?.SetWorkingDirectoryFromHost(root); WorkspacePath = root; OnPropertyChanged(nameof(WorkspaceLabel)); Status = "项目已授权"; await SaveCurrentConversationAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = e.Message; }
    }
    [RelayCommand] private void RevokeWorkspace() { _workbench?.Revoke(); OnPropertyChanged(nameof(WorkspaceLabel)); Status = "已撤销项目授权"; }
    private Task<bool> ManagementApproveAsync(AgentApproval approval) => _managementInteractionTask = ManagementApproveCoreAsync(approval);
    private async Task<bool> ManagementApproveCoreAsync(AgentApproval approval)
    {
        ShowAiSettings = false; ShowConnection = false;
        IsManaging = true; using var cancellation = new CancellationTokenSource(); _managementInteraction = cancellation;
        try { return await ApproveAsync(approval, cancellation.Token); }
        catch (OperationCanceledException) { return false; }
        finally { _managementInteraction = null; IsManaging = false; }
    }
}
