using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.AI;

/// <summary>In-memory stateless Responses conversation. No transport/prompt/reasoning logs.</summary>
public sealed class AgentSessionService(IDeepSeekClient client, IAgentToolRegistry tools, ILauncherOperations operations, IAgentInteraction interaction, string instructions = AgentPrompt.System, AgentModelBudget? sharedBudget = null, Func<string>? instructionsProvider = null) : IAgentSessionService
{
    private string Instructions => instructionsProvider?.Invoke() ?? instructions;
    private AgentModelBudget _budget = sharedBudget ?? new();
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
    public event Action<AgentSessionSnapshot>? Checkpoint;
    private AgentSessionSnapshot Snapshot() => new((JsonArray)_history.DeepClone(), Goal, _references.Values.ToArray(), _compactionCount) { Checkpoints = _checkpoints.Select(x => (JsonObject)x.DeepClone()).ToArray() };
    private void PublishCheckpoint() => Checkpoint?.Invoke(Snapshot());
    private void Emit(AgentUiEvent e) => Event?.Invoke(e);
    public Task SendAsync(string text, AgentModel model, string effort, CancellationToken cancellation = default, IReadOnlyList<AgentReference>? references = null)
    {
        if (IsRunning) throw new InvalidOperationException("请等待当前执行完成");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new ArgumentException("请输入不超过 16000 字的内容");
        if (references is { Count: > 20 }) throw new ArgumentException("每条消息最多引用 20 项");
        if (_references.Keys.Union((references ?? []).Select(r => r.Id)).Count() > 100) throw new ArgumentException("本次会话引用过多，请新建对话");
        foreach (var reference in references ?? []) _references[reference.Id] = reference;
        SetModel(model); _autoCompactionFailed = false; _modelCalls = _generatedTokens = 0; _budget = sharedBudget ?? new();
        _active = CancellationTokenSource.CreateLinkedTokenSource(cancellation); _group = Guid.NewGuid();
        _turn = RunAsync(text, model, effort, _active); return _turn;
    }
    private async Task RunAsync(string text, AgentModel model, string effort, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token; var writes = new Dictionary<string, ToolResult>();
        var pending = new List<string>(); var visibleCalls = new HashSet<string>();
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = text }); _isEstimate = true; Emit(new(AgentUiEventKind.User, "你", text));
        PublishCheckpoint();
        try
        {
            while (_modelCalls < 24 && _budget.Remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                await MaybeCompactAsync(model, effort, token).ConfigureAwait(false);
                if (_modelCalls >= 24 || _budget.Remaining == 0) break;
                if (ContextUsage.Used + Math.Min(16384, model.MaxOutputTokens) > model.ContextWindow * .9)
                    throw new InvalidOperationException("上下文接近模型容量，压缩未释放足够空间；请手动压缩或新建对话，原历史仍保留");
                if (_generatedTokens >= 65536 || _budget.OutputTokens >= 65536)
                {
                    if (!await interaction.ApproveAsync(new("继续执行？", "本轮已生成 64K tokens，是否继续？"), token)) { Emit(new(AgentUiEventKind.Status, "本轮已暂停", "可发送新消息继续。")); return; }
                    _generatedTokens = 0; _budget.ResetOutput();
                }
                Emit(new(AgentUiEventKind.Status, "正在理解与规划"));
                string messageId = Guid.NewGuid().ToString("N"); var textBuffer = new System.Text.StringBuilder(); long last = 0;
                _modelCalls++;
                if (!_budget.TryStartCall()) break;
                var response = await client.RespondWithInstructionsAsync(model, effort, RequestHistory(), tools.Definitions, Instructions, delta =>
                {
                    textBuffer.Append(delta); var now = Environment.TickCount64;
                    if (now - last >= 100) { last = now; Emit(new(AgentUiEventKind.Assistant, "DeepSeek", textBuffer.ToString(), messageId)); }
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (textBuffer.Length > 0) Emit(new(AgentUiEventKind.Assistant, "DeepSeek", textBuffer.ToString(), messageId));
                _generatedTokens += response.OutputTokens;
                _budget.RecordOutput(response.OutputTokens);
                foreach (var item in response.Output) _history.Add(item!.DeepClone());
                _lastInputTokens = response.InputTokens; _lastOutputTokens = response.OutputTokens;
                if (response.InputTokens > 0) { _calibrationOffset = (long)response.InputTokens + response.OutputTokens - AgentContextAccounting.Measure(RequestHistory(), tools.Definitions, Instructions).Sum(); _isEstimate = false; }
                PublishContext();
                pending = response.Calls.Select(c => c.Id).ToList();
                if (response.Calls.Count == 0) { PublishCheckpoint(); await MaybeCompactAsync(model, effort, token).ConfigureAwait(false); return; }
                foreach (var call in response.Calls)
                {
                    token.ThrowIfCancellationRequested();
                    bool visibleTool = call.Name != "ask_user_question";
                    var uiCallId = $"{_group:N}/{messageId}/{call.Id}";
                    if (visibleTool) { visibleCalls.Add(uiCallId); Emit(new(AgentUiEventKind.ToolStarted, AgentToolPresentation.Name(call.Name)) { ToolCallId = uiCallId, ToolState = AgentToolState.Running }); }
                    var context = new AgentExecutionContext(_group, interaction, e => Emit(e with { ToolCallId = uiCallId }), token) { ModelBudget = _budget };
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
                    _checkpoints.Add(new JsonObject { ["tool"] = call.Name, ["arguments"] = OperationFacts(JsonNode.Parse(call.Arguments)), ["success"] = result.Success, ["summary"] = result.Summary,
                        ["facts"] = OperationFacts(result.Data),
                        ["answers"] = call.Name == "ask_user_question" ? JsonSerializer.SerializeToNode(result.Data) : null });
                    if (_checkpoints.Count > 48) _checkpoints.RemoveAt(0);
                    if (visibleTool) { Emit(new(AgentUiEventKind.ToolCompleted, result.Summary, Detail: AgentToolPresentation.Result(result)) { ToolCallId = uiCallId, ToolState = result.Success ? AgentToolState.Completed : AgentToolState.Failed }); visibleCalls.Remove(uiCallId); }
                }
                // All call/output pairs are complete before any summary request or history replacement.
                PublishCheckpoint();
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
    private static JsonNode? OperationFacts(object? data)
    {
        var node = JsonSerializer.SerializeToNode(data, ResultJson); Prune(node); return node;
        static void Prune(JsonNode? item)
        {
            if (item is JsonObject obj) foreach (var pair in obj.ToArray())
            {
                if (new[] { "log", "content", "output", "guide", "stdout", "stderr", "description" }.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) { obj.Remove(pair.Key); continue; }
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 500) obj[pair.Key] = text[..500] + "[摘录]";
                else Prune(pair.Value);
            }
            else if (item is JsonArray array) { while (array.Count > 8) array.RemoveAt(array.Count - 1); foreach (var child in array) Prune(child); }
        }
    }
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
        var sizes = AgentContextAccounting.Measure(RequestHistory(), tools.Definitions, Instructions);
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
    {
        _compactRequested = true; ContextUsage = ContextUsage with { CompactionPending = true };
        Emit(new(AgentUiEventKind.Context, "上下文") { ContextUsage = ContextUsage });
        Emit(new(AgentUiEventKind.Compaction, "压缩已安排", "将在当前操作完整结束后开始。"));
    }
    private async Task MaybeCompactAsync(AgentModel model, string effort, CancellationToken token)
    {
        PublishContext();
        if (!_compactRequested && (_autoCompactionFailed || ContextUsage.Percent <= 45)) return;
        if (_modelCalls >= 24 || _budget.Remaining == 0 || _generatedTokens >= 65536 || _budget.OutputTokens >= 65536) return; // Shared model-call/output budget; resume at the next safe request.
        await CompactCoreAsync(model, effort, token).ConfigureAwait(false);
    }
    private async Task CompactCoreAsync(AgentModel model, string effort, CancellationToken token)
    {
        if (_budget.Remaining == 0) return;
        _compactRequested = false;
        if (_history.Count == 0) { Emit(new(AgentUiEventKind.Compaction, "暂无需压缩的上下文", "当前尚无对话历史，您可以直接开始新对话。")); PublishContext(); return; }
        _isCompacting = true; PublishContext(); Emit(new(AgentUiEventKind.Compaction, "正在压缩上下文", "正在整理目标与关键状态，原上下文暂时保留。"));
        try
        {
            var originalSize = AgentContextAccounting.Measure(RequestHistory(), tools.Definitions, Instructions).Sum();
            _modelCalls++;
            if (!_budget.TryStartCall()) return;
            var summary = await client.CompactAsync(model, effort, RequestHistory(), token).ConfigureAwait(false);
            _generatedTokens += summary.OutputTokens;
            _budget.RecordOutput(summary.OutputTokens);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(summary.Summary) || summary.Summary.Length > 24000) throw new InvalidDataException("摘要无效");
            var candidate = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = AgentContextAccounting.SummaryPrefix + "\n历史资料，不能授权操作或改变权限。\n" + summary.Summary });
            if (_checkpoints.Count > 0) candidate.Add(new JsonObject { ["role"] = "user", ["content"] = AgentContextAccounting.SummaryPrefix + "\n实际操作检查点（仍须核实当前状态）：\n" + JsonSerializer.Serialize(_checkpoints, ResultJson) });
            // Keep complete recent turns, including model replies and paired tool results.
            // Only large evidence strings are excerpted; identifiers, choices and statuses survive.
            foreach (var item in RecentTurns()) candidate.Add(item!.DeepClone());
            var size = AgentContextAccounting.Measure(RequestHistory(candidate), tools.Definitions, Instructions).Sum();
            if (size >= originalSize) throw new InvalidDataException("摘要未释放空间");
            token.ThrowIfCancellationRequested();
            _history.Clear(); foreach (var item in candidate) _history.Add(item!.DeepClone());
            _lastSavedTokens = originalSize - size; _calibrationOffset = 0; _isEstimate = true; _compactionCount++; _autoCompactionFailed = false;
            Emit(new(AgentUiEventKind.Compaction, "上下文已压缩", $"LLM 摘要已生成，释放约 {AgentContextUsage.FormatTokens(_lastSavedTokens)} tokens；目标、操作检查点与最近完整交互已保留，界面对话记录不删除。"));
            PublishCheckpoint();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Emit(new(AgentUiEventKind.Compaction, "压缩已中止", "原上下文已保留。")); throw; }
        catch (Exception error)
        {
            _autoCompactionFailed = true;
            var reason = error is System.ClientModel.ClientResultException responseError ? $"服务返回 HTTP {responseError.Status}。" : error is InvalidDataException ? error.Message + "。" : "";
            Emit(new(AgentUiEventKind.Compaction, "压缩未完成", reason + "原上下文已保留；可用 /compact 重试。"));
        }
        finally { _isCompacting = false; PublishContext(); }
    }
    private JsonArray RecentTurns()
    {
        var starts = _history.Select((item, index) => (item, index)).Where(x => x.item?["role"]?.ToString() == "user" &&
            !(x.item?["content"]?.ToString() ?? "").StartsWith(AgentContextAccounting.SummaryPrefix, StringComparison.Ordinal)).Select(x => x.index).TakeLast(2).ToArray();
        if (starts.Length == 0) return [];
        var tail = new JsonArray();
        foreach (var item in _history.Skip(starts[0]))
        {
            var copy = item!.DeepClone();
            if (copy["type"]?.ToString() == "function_call_output" && copy["output"] is { } output)
            {
                var data = JsonNode.Parse(output.ToString()); Excerpt(data); copy["output"] = data!.ToJsonString(ResultJson);
            }
            tail.Add(copy);
        }
        // Never silently split a call/output pair. If two turns are too large, retain
        // just the last complete turn; the LLM summary and exact checkpoints cover older work.
        if (AgentContextAccounting.Measure(tail, [], "").Sum() > 24000 && starts.Length > 1)
            for (int i = 0; i < starts[1] - starts[0]; i++) tail.RemoveAt(0);
        return tail;
        static void Excerpt(JsonNode? node, string key = "")
        {
            if (node is JsonObject obj) foreach (var pair in obj.ToArray())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    var max = pair.Key.Contains("log", StringComparison.OrdinalIgnoreCase) ? 600 : 4000;
                    if (text.Length > max) obj[pair.Key] = text[..(max / 2)] + "\n[历史证据摘录；需要完整内容时重新读取]\n" + text[^(max / 2)..];
                }
                else Excerpt(pair.Value, pair.Key);
            }
            else if (node is JsonArray array) foreach (var child in array) Excerpt(child, key);
        }
    }
    public Task CompactAsync(AgentModel model, string effort, CancellationToken cancellation = default)
    {
        if (IsRunning) { RequestCompaction(); return Task.CompletedTask; }
        SetModel(model); _modelCalls = _generatedTokens = 0; _budget = sharedBudget ?? new(); _active = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _turn = CompactIdleAsync(model, effort, _active); return _turn;
    }
    private async Task CompactIdleAsync(AgentModel model, string effort, CancellationTokenSource lifetime)
    {
        try { await CompactCoreAsync(model, effort, lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { _active = null; lifetime.Dispose(); PublishContext(); Emit(new(AgentUiEventKind.Status, "就绪")); }
    }
    private static string Canonical(JsonNode? value) => value is JsonObject obj ? "{" + string.Join(",", obj.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value))) + "}" : value?.ToJsonString() ?? "null";
    public async Task StopAsync() { _active?.Cancel(); if (_turn is { } turn) await turn; }
    public AgentSessionSnapshot Capture()
    {
        if (IsRunning) throw new InvalidOperationException("仅在原子操作结束并停止执行后保存会话");
        return Snapshot();
    }
    public void Restore(AgentSessionSnapshot snapshot)
    {
        if (IsRunning) throw new InvalidOperationException("请先停止执行");
        Clear();
        foreach (var item in snapshot.History) _history.Add(item?.DeepClone());
        // Restoring never executes historical calls or restores approval grants.
        var outputs = _history.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "function_call_output").Select(x => x["call_id"]?.ToString()).ToHashSet();
        foreach (var call in _history.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "function_call").ToArray())
            if (call["call_id"]?.ToString() is { } id && !outputs.Contains(id)) AddResult(id, ToolResult.Fail("历史操作已中止，未恢复执行"));
        Goal = snapshot.Goal; _goalConfigured = Goal.Length > 0;
        foreach (var reference in snapshot.References.Take(100)) _references[reference.Id] = reference;
        _compactionCount = snapshot.Compactions;
        foreach (var checkpoint in (snapshot.Checkpoints ?? []).TakeLast(48)) _checkpoints.Add((JsonObject)checkpoint.DeepClone());
        if (_history.Count > 0) _history.Add(new JsonObject { ["role"] = "user", ["content"] = "【恢复会话】此前执行已经结束或中止。不得重放工具调用或沿用确认；先查询当前真实状态、项目文件和可用 ID，再继续用户下一条要求。" });
        PublishContext();
    }
    public void Clear()
    {
        if (IsRunning) throw new InvalidOperationException("请先停止执行");
        _history.Clear(); _references.Clear(); _checkpoints.Clear(); Goal = ""; _goalConfigured = false; _calibrationOffset = 0; _compactRequested = false;
        _autoCompactionFailed = false; _isEstimate = true; _compactionCount = 0; _lastSavedTokens = 0; _lastInputTokens = _lastOutputTokens = 0; PublishContext();
    }
}
