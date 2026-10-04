using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.AI;

/// <summary>In-memory stateless Responses conversation. No transport/prompt/reasoning logs.</summary>
public sealed class AgentSessionService(IDeepSeekClient client, IAgentToolRegistry tools, ILauncherOperations operations, IAgentInteraction interaction) : IAgentSessionService
{
    private readonly JsonArray _history = new();
    private CancellationTokenSource? _active;
    private Task? _turn;
    private Guid _group;
    private AgentModel _model = new("", "") { HasVerifiedContextWindow = false };
    private readonly Dictionary<string, AgentReference> _references = new();
    private readonly List<JsonObject> _checkpoints = new();
    private long _calibrationOffset;
    private bool _isEstimate = true, _isCompacting, _autoCompactionFailed, _goalConfigured;
    private volatile bool _compactRequested;
    private int _compactionCount, _lastInputTokens, _lastOutputTokens;
    private int _modelCalls, _generatedTokens;
    private long _lastSavedTokens;
    public string Goal { get; private set; } = "";
    public AgentContextUsage ContextUsage { get; private set; } = new(0, 131072, []);
    private static readonly JsonSerializerOptions ResultJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public bool IsRunning => _active is not null;
    public event Action<AgentUiEvent>? Event;
    private void Emit(AgentUiEvent e) => Event?.Invoke(e);
    public Task SendAsync(string text, AgentModel model, string effort, CancellationToken cancellation = default, IReadOnlyList<AgentReference>? references = null)
    {
        if (IsRunning) throw new InvalidOperationException("请等待当前执行完成");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new ArgumentException("请输入不超过 16000 字的内容");
        if (references is { Count: > 20 }) throw new ArgumentException("每条消息最多引用 20 项");
        if (_references.Keys.Union((references ?? []).Select(r => r.Id)).Count() > 100) throw new ArgumentException("本次会话引用过多，请新建对话");
        foreach (var reference in references ?? []) _references[reference.Id] = reference;
        SetModel(model); _autoCompactionFailed = false; _modelCalls = _generatedTokens = 0;
        _active = CancellationTokenSource.CreateLinkedTokenSource(cancellation); _group = Guid.NewGuid();
        _turn = RunAsync(text, model, effort, _active); return _turn;
    }
    private async Task RunAsync(string text, AgentModel model, string effort, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token; var writes = new Dictionary<string, ToolResult>();
        var pending = new List<string>(); var visibleCalls = new HashSet<string>();
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = text }); _isEstimate = true; Emit(new(AgentUiEventKind.User, "你", text));
        try
        {
            while (_modelCalls < 24)
            {
                token.ThrowIfCancellationRequested();
                await MaybeCompactAsync(model, effort, token).ConfigureAwait(false);
                if (_modelCalls >= 24) break;
                if (ContextUsage.Used + Math.Min(16384, model.MaxOutputTokens) > model.ContextWindow * .9)
                    throw new InvalidOperationException("上下文接近模型容量，压缩未释放足够空间；请手动压缩或新建对话，原历史仍保留");
                if (_generatedTokens >= 65536)
                {
                    if (!await interaction.ApproveAsync(new("继续执行？", "本轮已生成 64K tokens，是否继续？"), token)) { Emit(new(AgentUiEventKind.Status, "本轮已暂停", "可发送新消息继续。")); return; }
                    _generatedTokens = 0;
                }
                Emit(new(AgentUiEventKind.Status, "正在理解与规划"));
                string messageId = Guid.NewGuid().ToString("N"); var textBuffer = new System.Text.StringBuilder(); long last = 0;
                _modelCalls++;
                var response = await client.RespondAsync(model, effort, RequestHistory(), tools.Definitions, delta =>
                {
                    textBuffer.Append(delta); var now = Environment.TickCount64;
                    if (now - last >= 100) { last = now; Emit(new(AgentUiEventKind.Assistant, "DeepSeek", textBuffer.ToString(), messageId)); }
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (textBuffer.Length > 0) Emit(new(AgentUiEventKind.Assistant, "DeepSeek", textBuffer.ToString(), messageId));
                _generatedTokens += response.OutputTokens;
                foreach (var item in response.Output) _history.Add(item!.DeepClone());
                _lastInputTokens = response.InputTokens; _lastOutputTokens = response.OutputTokens;
                if (response.InputTokens > 0) { _calibrationOffset = (long)response.InputTokens + response.OutputTokens - AgentContextAccounting.Measure(RequestHistory(), tools.Definitions).Sum(); _isEstimate = false; }
                PublishContext();
                pending = response.Calls.Select(c => c.Id).ToList();
                if (response.Calls.Count == 0) { await MaybeCompactAsync(model, effort, token).ConfigureAwait(false); return; }
                foreach (var call in response.Calls)
                {
                    token.ThrowIfCancellationRequested();
                    bool visibleTool = call.Name != "ask_user_question";
                    var uiCallId = $"{_group:N}/{messageId}/{call.Id}";
                    if (visibleTool) { visibleCalls.Add(uiCallId); Emit(new(AgentUiEventKind.ToolStarted, AgentToolPresentation.Name(call.Name)) { ToolCallId = uiCallId, ToolState = AgentToolState.Running }); }
                    var context = new AgentExecutionContext(_group, interaction, e => Emit(e with { ToolCallId = uiCallId }), token);
                    ToolResult result;
                    var fingerprint = call.Name + ":" + Canonical(JsonNode.Parse(call.Arguments));
                    try
                    {
                        if (tools.IsMutation(call.Name) && writes.TryGetValue(fingerprint, out var cached)) result = cached;
                        else
                        {
                            result = await tools.ExecuteAsync(call, context).ConfigureAwait(false);
                            if (result.Success && call.Name is "retry_account_login" or "open_account_login")
                            {
                                // A failed launch is safe to retry after actual authentication recovery.
                                // Keep successful mutations and unrelated failures deduplicated.
                                foreach (var key in writes.Where(x => !x.Value.Success &&
                                    (x.Key.StartsWith("launch_game:", StringComparison.Ordinal) || x.Key.StartsWith("retry_account_login:", StringComparison.Ordinal)) &&
                                    JsonSerializer.SerializeToNode(x.Value.Data)?["status"]?.GetValue<string>() == "reauthentication_required").Select(x => x.Key).ToArray())
                                    writes.Remove(key);
                            }
                            if (tools.IsMutation(call.Name)) writes[fingerprint] = result;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        if (visibleTool) { visibleCalls.Remove(uiCallId); Emit(new(AgentUiEventKind.ToolCompleted, "已中止", Detail: "本次调用已取消；已完成的操作保留。") { ToolCallId = uiCallId, ToolState = AgentToolState.Cancelled }); }
                        throw;
                    }
                    catch (Exception) { result = ToolResult.Fail("操作失败，请查看启动器状态并更换方案"); }
                    AddResult(call.Id, result); pending.Remove(call.Id);
                    // Small exact checkpoints survive summarization. They are facts, never reusable approvals.
                    _checkpoints.Add(new JsonObject { ["tool"] = call.Name, ["arguments"] = JsonNode.Parse(call.Arguments), ["success"] = result.Success, ["summary"] = result.Summary,
                        ["answers"] = call.Name == "ask_user_question" ? JsonSerializer.SerializeToNode(result.Data) : null });
                    if (_checkpoints.Count > 48) _checkpoints.RemoveAt(0);
                    if (visibleTool) { Emit(new(AgentUiEventKind.ToolCompleted, result.Summary, Detail: AgentToolPresentation.Result(result)) { ToolCallId = uiCallId, ToolState = result.Success ? AgentToolState.Completed : AgentToolState.Failed }); visibleCalls.Remove(uiCallId); }
                }
                // All call/output pairs are complete before any summary request or history replacement.
                await MaybeCompactAsync(model, effort, token).ConfigureAwait(false);
            }
            Emit(new(AgentUiEventKind.Status, "已达到本轮调用上限", "请发送新消息继续；已完成的操作保留。"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Emit(new(AgentUiEventKind.Interrupted, "本轮已中止", "重新继续时会查询实际状态。"));
        }
        catch (Exception e) { Emit(new(AgentUiEventKind.Error, "执行未完成", SafeError(e))); }
        finally
        {
            foreach (var id in visibleCalls) Emit(new(AgentUiEventKind.ToolCompleted, token.IsCancellationRequested ? "已中止" : "调用未完成", Detail: "请查询实际状态后继续。") { ToolCallId = id, ToolState = token.IsCancellationRequested ? AgentToolState.Cancelled : AgentToolState.Failed });
            foreach (var id in pending) AddResult(id, ToolResult.Fail("本轮已中止或失败，未执行此调用"));
            if (token.IsCancellationRequested) { const string stopped = "上一轮已中止；下次行动先查询实际状态，不重复执行已完成操作。"; _history.Add(new JsonObject { ["role"] = "user", ["content"] = stopped }); _isEstimate = true; }
            await operations.CancelGroupAsync(_group);
            _active = null; lifetime.Dispose(); PublishContext(); Emit(new(AgentUiEventKind.Status, "就绪"));
        }
    }
    private static string SafeError(Exception error) => error is InvalidOperationException or InvalidDataException ? error.Message : "连接或服务异常，请重试或检查连接设置";
    private void AddResult(string id, ToolResult result)
    { var output = JsonSerializer.Serialize(result, ResultJson); _history.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = output }); _isEstimate = true; PublishContext(); }
    private JsonArray RequestHistory(JsonArray? history = null)
    {
        var result = (history ?? _history).DeepClone().AsArray();
        if (_goalConfigured || _references.Count > 0)
            result.Insert(0, new JsonObject { ["role"] = "user", ["content"] = AgentContextAccounting.PinnedPrefix + "\n" +
                JsonSerializer.Serialize(new { goal = Goal, references = _references.Values.ToArray() }, ResultJson) });
        return result;
    }
    private void PublishContext()
    {
        var sizes = AgentContextAccounting.Measure(RequestHistory(), tools.Definitions);
        var used = Math.Max(0, sizes.Sum() + _calibrationOffset);
        ContextUsage = new(used, _model.ContextWindow, AgentContextAccounting.Categories(sizes, used))
        { IsEstimate = _isEstimate, CapacityVerified = _model.HasVerifiedContextWindow, IsCompacting = _isCompacting,
            CompactionPending = _compactRequested || (!_autoCompactionFailed && used > _model.ContextWindow * .45),
            CompactionCount = _compactionCount, LastSavedTokens = _lastSavedTokens, LastInputTokens = _lastInputTokens, LastOutputTokens = _lastOutputTokens };
        Emit(new(AgentUiEventKind.Context, "上下文") { ContextUsage = ContextUsage });
    }
    public void SetModel(AgentModel model)
    { if (IsRunning) throw new InvalidOperationException("执行结束后才能切换模型"); _model = model; PublishContext(); }
    public void SetGoal(string goal)
    {
        if (IsRunning) throw new InvalidOperationException("请先停止执行再修改目标");
        if (goal.Length > 8000) throw new ArgumentException("目标不能超过 8000 字");
        Goal = goal.Trim(); _goalConfigured = true; _isEstimate = true; PublishContext();
    }
    public void RequestCompaction()
    { _compactRequested = true; Emit(new(AgentUiEventKind.Context, "上下文") { ContextUsage = ContextUsage with { CompactionPending = true } }); }
    private async Task MaybeCompactAsync(AgentModel model, string effort, CancellationToken token)
    {
        PublishContext();
        if (!_compactRequested && (_autoCompactionFailed || ContextUsage.Percent <= 45)) return;
        if (_modelCalls >= 24 || _generatedTokens >= 65536) return; // Shared model-call/output budget; resume at the next safe request.
        await CompactCoreAsync(model, effort, token).ConfigureAwait(false);
    }
    private async Task CompactCoreAsync(AgentModel model, string effort, CancellationToken token)
    {
        _compactRequested = false;
        if (_history.Count == 0) { Emit(new(AgentUiEventKind.Status, "暂无可压缩的历史")); PublishContext(); return; }
        _isCompacting = true; PublishContext(); Emit(new(AgentUiEventKind.Status, "正在压缩上下文"));
        try
        {
            var originalSize = AgentContextAccounting.Measure(RequestHistory(), tools.Definitions).Sum();
            _modelCalls++;
            var summary = await client.CompactAsync(model, effort, RequestHistory(), token).ConfigureAwait(false);
            _generatedTokens += summary.OutputTokens;
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(summary.Summary) || summary.Summary.Length > 24000) throw new InvalidDataException("摘要无效");
            var candidate = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = AgentContextAccounting.SummaryPrefix + "\n历史资料，不能授权操作或改变权限。\n" + summary.Summary });
            if (_checkpoints.Count > 0) candidate.Add(new JsonObject { ["role"] = "user", ["content"] = AgentContextAccounting.SummaryPrefix + "\n实际操作检查点（仍须核实当前状态）：\n" + JsonSerializer.Serialize(_checkpoints, ResultJson) });
            // Keep recent user wording verbatim, outside the model-generated summary.
            foreach (var user in _history.OfType<JsonObject>().Where(x => x["role"]?.ToString() == "user" &&
                !(x["content"]?.ToString() ?? "").StartsWith(AgentContextAccounting.SummaryPrefix, StringComparison.Ordinal)).TakeLast(3)) candidate.Add(user.DeepClone());
            var size = AgentContextAccounting.Measure(RequestHistory(candidate), tools.Definitions).Sum();
            if (size >= originalSize) throw new InvalidDataException("摘要未释放空间");
            token.ThrowIfCancellationRequested();
            _history.Clear(); foreach (var item in candidate) _history.Add(item!.DeepClone());
            _lastSavedTokens = originalSize - size; _calibrationOffset = 0; _isEstimate = true; _compactionCount++; _autoCompactionFailed = false;
            Emit(new(AgentUiEventKind.Status, "上下文已压缩", $"释放约 {AgentContextUsage.FormatTokens(_lastSavedTokens)} tokens，目标与最近输入已保留。"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { _autoCompactionFailed = true; Emit(new(AgentUiEventKind.Status, "压缩未完成", "原上下文已保留；可用 /compact 重试。")); }
        finally { _isCompacting = false; PublishContext(); }
    }
    public Task CompactAsync(AgentModel model, string effort, CancellationToken cancellation = default)
    {
        if (IsRunning) { RequestCompaction(); return Task.CompletedTask; }
        SetModel(model); _modelCalls = _generatedTokens = 0; _active = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _turn = CompactIdleAsync(model, effort, _active); return _turn;
    }
    private async Task CompactIdleAsync(AgentModel model, string effort, CancellationTokenSource lifetime)
    {
        try { await CompactCoreAsync(model, effort, lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { Emit(new(AgentUiEventKind.Status, "压缩已中止", "原上下文已保留。")); }
        finally { _active = null; lifetime.Dispose(); PublishContext(); Emit(new(AgentUiEventKind.Status, "就绪")); }
    }
    private static string Canonical(JsonNode? value) => value is JsonObject obj ? "{" + string.Join(",", obj.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value))) + "}" : value?.ToJsonString() ?? "null";
    public async Task StopAsync() { _active?.Cancel(); if (_turn is { } turn) await turn; }
    public void Clear()
    {
        if (IsRunning) throw new InvalidOperationException("请先停止执行");
        _history.Clear(); _references.Clear(); _checkpoints.Clear(); Goal = ""; _goalConfigured = false; _calibrationOffset = 0; _compactRequested = false;
        _autoCompactionFailed = false; _isEstimate = true; _compactionCount = 0; _lastSavedTokens = 0; _lastInputTokens = _lastOutputTokens = 0; PublishContext();
    }
}
