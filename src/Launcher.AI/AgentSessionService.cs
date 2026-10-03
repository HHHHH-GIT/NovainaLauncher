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
    private long _contextTokens;
    private static readonly JsonSerializerOptions ResultJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public bool IsRunning => _active is not null;
    public event Action<AgentUiEvent>? Event;
    private void Emit(AgentUiEvent e) => Event?.Invoke(e);
    public Task SendAsync(string text, AgentModel model, string effort, CancellationToken cancellation = default)
    {
        if (IsRunning) throw new InvalidOperationException("请等待当前执行完成");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new ArgumentException("请输入不超过 16000 字的内容");
        _active = CancellationTokenSource.CreateLinkedTokenSource(cancellation); _group = Guid.NewGuid();
        _turn = RunAsync(text, model, effort, _active); return _turn;
    }
    private async Task RunAsync(string text, AgentModel model, string effort, CancellationTokenSource lifetime)
    {
        var token = lifetime.Token; var writes = new Dictionary<string, ToolResult>();
        var pending = new List<string>(); int generated = 0;
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = text }); _contextTokens += Estimate(text); Emit(new(AgentUiEventKind.User, "你", text));
        try
        {
            for (int round = 0; round < 24; round++)
            {
                token.ThrowIfCancellationRequested();
                if (_contextTokens + Estimate(AgentPrompt.System) + Estimate(tools.Definitions.ToJsonString(ResultJson)) + Math.Min(16384, model.MaxOutputTokens) > model.ContextWindow * .9)
                    throw new InvalidOperationException("上下文接近模型容量，请新建对话后继续；本轮未自动丢弃历史");
                if (generated >= 65536)
                {
                    if (!await interaction.ApproveAsync(new("继续执行？", "本轮已生成 64K tokens，是否继续？"), token)) { Emit(new(AgentUiEventKind.Status, "本轮已暂停", "可发送新消息继续。")); return; }
                    generated = 0;
                }
                Emit(new(AgentUiEventKind.Status, "正在理解与规划"));
                string messageId = Guid.NewGuid().ToString("N"); var textBuffer = new System.Text.StringBuilder(); long last = 0;
                var response = await client.RespondAsync(model, effort, _history, tools.Definitions, delta =>
                {
                    textBuffer.Append(delta); var now = Environment.TickCount64;
                    if (now - last >= 100) { last = now; Emit(new(AgentUiEventKind.Assistant, "DeepSeek", textBuffer.ToString(), messageId)); }
                }, token);
                token.ThrowIfCancellationRequested();
                if (textBuffer.Length > 0) Emit(new(AgentUiEventKind.Assistant, "DeepSeek", textBuffer.ToString(), messageId));
                generated += response.OutputTokens;
                _contextTokens = response.InputTokens > 0 ? response.InputTokens + response.OutputTokens : _contextTokens + Estimate(response.Output.ToJsonString(ResultJson));
                foreach (var item in response.Output) _history.Add(item!.DeepClone());
                pending = response.Calls.Select(c => c.Id).ToList();
                if (response.Calls.Count == 0) return;
                var context = new AgentExecutionContext(_group, interaction, Emit, token);
                foreach (var call in response.Calls)
                {
                    token.ThrowIfCancellationRequested();
                    ToolResult result;
                    var fingerprint = call.Name + ":" + Canonical(JsonNode.Parse(call.Arguments));
                    try
                    {
                        if (tools.IsMutation(call.Name) && writes.TryGetValue(fingerprint, out var cached)) result = cached;
                        else
                        {
                            result = await tools.ExecuteAsync(call, context);
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
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { result = ToolResult.Fail("操作失败，请查看启动器状态并更换方案"); }
                    AddResult(call.Id, result); pending.Remove(call.Id);
                    if (call.Name != "ask_user_question") Emit(new(result.Success ? AgentUiEventKind.Operation : AgentUiEventKind.Error, result.Summary));
                }
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
            foreach (var id in pending) AddResult(id, ToolResult.Fail("本轮已中止或失败，未执行此调用"));
            if (token.IsCancellationRequested) { const string stopped = "上一轮已中止；下次行动先查询实际状态，不重复执行已完成操作。"; _history.Add(new JsonObject { ["role"] = "user", ["content"] = stopped }); _contextTokens += Estimate(stopped); }
            await operations.CancelGroupAsync(_group);
            _active = null; lifetime.Dispose(); Emit(new(AgentUiEventKind.Status, "就绪"));
        }
    }
    private static string SafeError(Exception error) => error is InvalidOperationException or InvalidDataException ? error.Message : "连接或服务异常，请重试或检查连接设置";
    private void AddResult(string id, ToolResult result)
    { var output = JsonSerializer.Serialize(result, ResultJson); _history.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = output }); _contextTokens += Estimate(output) + 24; }
    private static int Estimate(string text) => (int)Math.Ceiling(text.Count(c => c > 127) * 1.5 + text.Count(c => c <= 127) / 3d) + 16;
    private static string Canonical(JsonNode? value) => value is JsonObject obj ? "{" + string.Join(",", obj.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => JsonSerializer.Serialize(x.Key) + ":" + Canonical(x.Value))) + "}" : value?.ToJsonString() ?? "null";
    public async Task StopAsync() { _active?.Cancel(); if (_turn is { } turn) await turn; }
    public void Clear() { if (IsRunning) throw new InvalidOperationException("请先停止执行"); _history.Clear(); _contextTokens = 0; }
}
