using System.IO;
using System.Text.Json.Nodes;
using Launcher.AI;
using Xunit;

namespace Launcher.Tests;

public sealed class AgentContextTests
{
    [Fact] public async Task Automatic_Compaction_Waits_For_Complete_Call_Batch_And_Preserves_Goal_References()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ops = new Operations(async (name, token) =>
        {
            if (name == "get_tasks") { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return name == "read_game_logs" ? ToolResult.Ok("已读取日志", new { log = new string('x', 400000) }) : ToolResult.Ok("已查任务");
        });
        var client = new Client([Calls(("read_game_logs", "{\"game_id\":\"local-real\"}", "logs"), ("get_tasks", "{}", "tasks")), new([], [], 0, 0)]);
        var session = Session(client, ops); var events = new List<AgentUiEvent>(); session.Event += events.Add; session.SetGoal("分析并修复当前游戏，删除必须先询问");
        var run = session.SendAsync("先检查日志", Model(), "high", references: [new("game", "local-real", "冒险世界", "1.20.1 · Forge")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(client.Compactions); Assert.True(session.ContextUsage.Percent > 45);
        Assert.True(session.ContextUsage.Categories.Single(x => x.Name == "日志内容").Tokens > 100000);
        session.RequestCompaction(); Assert.Empty(client.Compactions);
        Assert.Contains(events, e => e.Kind == AgentUiEventKind.Compaction && e.Title == "压缩已安排");
        release.TrySetResult(); await run;
        Assert.Single(client.Compactions);
        Assert.Equal(2, client.Compactions[0].OfType<JsonObject>().Count(x => x["type"]?.ToString() == "function_call_output"));
        Assert.Equal(1, session.ContextUsage.CompactionCount); Assert.True(session.ContextUsage.LastSavedTokens > 100000);
        Assert.Contains(events, e => e.Kind == AgentUiEventKind.Compaction && e.Title == "正在压缩上下文");
        Assert.Contains(events, e => e.Kind == AgentUiEventKind.Compaction && e.Title == "上下文已压缩" && e.Text.Contains("释放约"));
        Assert.Contains("删除必须先询问", session.Goal);
        Assert.Contains("local-real", client.Histories[^1].ToJsonString()); Assert.Contains("先检查日志", client.Histories[^1].ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.DoesNotContain(new string('x', 1000), client.Histories[^1].ToJsonString());
        var saved = session.Capture(); Assert.Equal(2, saved.Checkpoints!.Count);
        Assert.Equal(2, saved.History.OfType<JsonObject>().Count(x => x["type"]?.ToString() == "function_call"));
        Assert.Equal(2, saved.History.OfType<JsonObject>().Count(x => x["type"]?.ToString() == "function_call_output"));
        var restored = Session(new Client([]), ops); restored.Restore(saved);
        Assert.Equal(saved.Checkpoints.Count, restored.Capture().Checkpoints!.Count);
        session.Clear(); Assert.Empty(session.Goal); Assert.Equal(0, session.ContextUsage.CompactionCount);
    }
    [Fact] public async Task Cancelled_Or_Failed_Compaction_Retains_Original_Protocol_And_History()
    {
        var ops = new Operations((_, _) => Task.FromResult(ToolResult.Ok("结果", new { log = new string('x', 30000) })));
        var client = new Client([Calls(("read_game_logs", "{\"game_id\":\"real\"}", "logs")), new([], [], 0, 0)]);
        var session = Session(client, ops); var events = new List<AgentUiEvent>(); session.Event += events.Add; var model = Model() with { ContextWindow = 1000000 };
        await session.SendAsync("记录用户目标", model, "high");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Compact = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new("不会提交"); };
        var compact = session.CompactAsync(model, "high"); await entered.Task; await session.StopAsync(); await compact;
        Assert.Contains(events, e => e.Kind == AgentUiEventKind.Compaction && e.Title == "压缩已中止");
        Assert.Equal(0, session.ContextUsage.CompactionCount);
        client.Compact = (_, _) => throw new InvalidDataException("断流"); await session.CompactAsync(model, "high");
        Assert.Contains(events, e => e.Kind == AgentUiEventKind.Compaction && e.Title == "压缩未完成" && e.Text.Contains("已保留"));
        Assert.Equal(0, session.ContextUsage.CompactionCount);
        Assert.Single(ops.Cancelled); // Manual compaction never cancels an installation group.
        await session.SendAsync("继续", model, "high");
        Assert.Contains(new string('x', 1000), client.Histories[^1].ToJsonString());
        Assert.Single(client.Histories[^1].OfType<JsonObject>(), x => x["type"]?.ToString() == "function_call_output");
    }
    [Fact] public async Task Provider_Usage_Calibrates_Total_And_Categories_Do_Not_Double_Count()
    {
        var client = new Client([new(new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "已读取" }) }), [], 400, 1234)]);
        var session = Session(client, new Operations((_, _) => Task.FromResult(ToolResult.Ok("完成"))));
        await session.SendAsync("查询", Model(), "high");
        Assert.Equal(1634, session.ContextUsage.Used); Assert.False(session.ContextUsage.IsEstimate);
        Assert.Equal(session.ContextUsage.Used, session.ContextUsage.Categories.Sum(x => x.Tokens));
        Assert.Equal(1234, session.ContextUsage.LastInputTokens);
    }
    [Fact] public void Commands_And_References_Only_Match_Standalone_Tokens_At_Caret()
    {
        Assert.Equal("comp", AgentComposerParser.AtCaret("/comp", 5)!.Query);
        Assert.Equal("中文世界", AgentComposerParser.AtCaret("安装到 @中文世界", 9)!.Query);
        Assert.Null(AgentComposerParser.AtCaret("", 0)); Assert.Null(AgentComposerParser.AtCaret("https://site.test", 16));
        Assert.Null(AgentComposerParser.AtCaret("name@example.test", 17)); Assert.Null(AgentComposerParser.AtCaret("@「游戏：世界」 ", 9));
        Assert.Null(AgentComposerParser.AtCaret("/goal 我的目标", 10));
        Assert.True(AgentComposerParser.Matches(new("java", "logical", "Java 21", "x64"), "21"));
        Assert.True(AgentComposerParser.Matches(new("account", "logical", "玩家", "LittleSkin"), "账户"));
    }
    private static AgentModel Model() => new("fixture", "Fixture", 200000, 2000);
    private static AgentSessionService Session(Client client, Operations ops) => new(client, new AgentToolRegistry(ops), ops, new Interaction());
    private static AgentResponse Calls(params (string Name, string Args, string Id)[] calls) => new(new JsonArray(calls.Select(c => (JsonNode)new JsonObject { ["type"] = "function_call", ["name"] = c.Name, ["arguments"] = c.Args, ["call_id"] = c.Id, ["status"] = "completed" }).ToArray()), calls.Select(c => new AgentToolCall(c.Id, c.Name, c.Args)).ToArray(), 0, 0);
    private sealed class Client(IEnumerable<AgentResponse> responses) : IDeepSeekClient
    {
        private readonly Queue<AgentResponse> _responses = new(responses);
        public List<JsonArray> Histories { get; } = []; public List<JsonArray> Compactions { get; } = [];
        public Func<JsonArray, CancellationToken, Task<AgentCompaction>>? Compact;
        public Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool refresh, CancellationToken token) => Task.FromResult<IReadOnlyList<AgentModel>>([Model()]);
        public Task<AgentResponse> RespondAsync(AgentModel model, string effort, JsonArray history, JsonArray tools, Action<string> delta, CancellationToken token)
        { Histories.Add(history.DeepClone().AsArray()); return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new AgentResponse([], [], 0, 0)); }
        public Task<AgentCompaction> CompactAsync(AgentModel model, string effort, JsonArray history, CancellationToken token)
        { Compactions.Add(history.DeepClone().AsArray()); return Compact?.Invoke(history, token) ?? Task.FromResult(new AgentCompaction("已读取日志与任务。当前目标是检查问题，尚未修改游戏。")); }
    }
    private sealed class Operations(Func<string, CancellationToken, Task<ToolResult>> run) : ILauncherOperations
    {
        public List<Guid> Cancelled { get; } = [];
        public Task<ToolResult> ExecuteAsync(string name, JsonObject args, AgentExecutionContext context) => run(name, context.Cancellation);
        public Task<AgentApproval> DescribeSensitiveAsync(string name, JsonObject args, CancellationToken token) => throw new NotSupportedException();
        public Task CancelGroupAsync(Guid groupId) { Cancelled.Add(groupId); return Task.CompletedTask; }
    }
    private sealed class Interaction : IAgentInteraction
    {
        public Task<bool> ApproveAsync(AgentApproval a, CancellationToken token) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<AgentQuestion> questions, CancellationToken token) => throw new NotSupportedException();
    }
}
