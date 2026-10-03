#pragma warning disable OPENAI001, SCME0001
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Launcher.AI;
using Launcher.Core;
using OpenAI.Responses;
using Xunit;

namespace Launcher.Tests;

public sealed class AgentModeTests
{
    [Fact] public async Task Login_Retry_Uses_Logical_Account_Id_And_Rejects_Password_Arguments()
    {
        var ops = new Ops(); var registry = new AgentToolRegistry(ops);
        var context = new AgentExecutionContext(Guid.NewGuid(), new Interaction(), _ => { }, default);
        Assert.Contains(registry.Definitions.OfType<JsonObject>(), x => x["name"]!.GetValue<string>() == "retry_account_login");
        Assert.True(registry.IsMutation("retry_account_login"));
        Assert.False((await registry.ExecuteAsync(new("bad", "retry_account_login", "{\"account_id\":\"actual\",\"password\":\"forbidden\"}"), context)).Success);
        Assert.Empty(ops.Calls);
        Assert.True((await registry.ExecuteAsync(new("retry", "retry_account_login", "{\"account_id\":\"actual\"}"), context)).Success);
        Assert.Equal("retry_account_login", Assert.Single(ops.Calls));
        Assert.Contains("retry_account_login", AgentPrompt.System);
    }
    [Fact] public async Task Auth_Recovery_Allows_Failed_Launch_Retry_But_Still_Deduplicates_Success()
    {
        int attempts = 0;
        var ops = new Ops { Result = name => name == "launch_game" && ++attempts == 1
            ? new ToolResult(false, "登录失效", new { status = "reauthentication_required" }) : ToolResult.Ok("已完成") };
        var fake = new FakeClient([
            DeepSeekClient.ParseCompleted(Complete(Call("launch_game", "{}", "launch1"))),
            DeepSeekClient.ParseCompleted(Complete(Call("retry_account_login", "{\"account_id\":\"actual\"}", "renew"))),
            DeepSeekClient.ParseCompleted(Complete(Call("launch_game", "{}", "launch2"), Call("launch_game", "{}", "duplicate"))),
            DeepSeekClient.ParseCompleted(Complete())]);
        await new AgentSessionService(fake, new AgentToolRegistry(ops), ops, new Interaction()).SendAsync("启动游戏", new("fixture", "fixture"), "high");
        Assert.Equal(2, attempts); Assert.Equal(2, ops.Calls.Count(c => c == "launch_game"));
    }
    private static JsonObject Complete(params JsonObject[] output) => new() { ["type"] = "response.completed", ["sequence_number"] = 4, ["response"] = new JsonObject {
        ["id"] = "resp-fixture", ["object"] = "response", ["created_at"] = 1, ["status"] = "completed", ["model"] = "deepseek-flash",
        ["output"] = new JsonArray(output.Select(x => (JsonNode)x).ToArray()), ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 20, ["total_tokens"] = 30 } } };
    private static JsonObject Call(string name, string args, string id = "call1") => new() { ["type"] = "function_call", ["id"] = "fc_" + id, ["call_id"] = id, ["name"] = name, ["arguments"] = args, ["status"] = "completed" };
    [Fact] public async Task Sdk_Stream_Preserves_DeepSeek_Reasoning_And_Complete_Tool_Arguments()
    {
        string? requestBody = null;
        var reasoning = JsonNode.Parse("""{"type":"reasoning","id":"rs_fixture","content":[{"type":"reasoning_text","text":"protocol-only"}],"summary":[]}""")!.AsObject();
        var final = Complete(reasoning, Call("get_launcher_state", "{}"));
        using var http = new HttpClient(new Handler(async r => { requestBody = await r.Content!.ReadAsStringAsync(); return Sse("response.reasoning_text.delta", "{\"type\":\"response.reasoning_text.delta\",\"sequence_number\":1,\"delta\":\"private\"}", final); }));
        var client = new DeepSeekClient(() => "fixture-key", sdkOptions: new ResponsesClientOptions { Endpoint = new Uri("https://api.deepseek.com"), Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) });
        var history = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "fixture" }, reasoning.DeepClone());
        var text = new StringBuilder();
        var response = await client.RespondAsync(new("deepseek-flash", "Flash"), "high", history, new AgentToolRegistry(new Ops()).Definitions, s => text.Append(s), default);
        Assert.Single(response.Calls); Assert.Equal("{}", response.Calls[0].Arguments);
        Assert.Equal("protocol-only", response.Output[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Empty(text.ToString());
        var sent = JsonNode.Parse(requestBody!)!; Assert.Equal("protocol-only", sent["input"]![1]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.True(sent["stream"]!.GetValue<bool>()); Assert.Equal("high", sent["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Null(sent["previous_response_id"]);
    }
    [Fact] public async Task Disconnected_Stream_Does_Not_Return_A_Tool_Response()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("event: response.function_call_arguments.delta\ndata: {\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":1,\"delta\":\"{}\"}\n\n", Encoding.UTF8, "text/event-stream") })));
        var client = new DeepSeekClient(() => "fixture", sdkOptions: new ResponsesClientOptions { Endpoint = new("https://api.deepseek.com"), Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.RespondAsync(new("deepseek-flash", "Flash"), "high", [], [], _ => { }, default));
    }
    [Fact] public void Partial_Response_And_Malformed_Tool_Arguments_Are_Rejected()
    {
        var final = Complete(Call("install_game", "{")); Assert.ThrowsAny<Exception>(() => DeepSeekClient.ParseCompleted(final));
        final = Complete(Call("install_game", "{}")); final["response"]!["status"] = "incomplete";
        Assert.Throws<InvalidDataException>(() => DeepSeekClient.ParseCompleted(final));
    }
    [Fact] public async Task Unknown_Fields_And_Shell_Never_Reach_Operations()
    {
        var ops = new Ops(); var registry = new AgentToolRegistry(ops); var context = new AgentExecutionContext(Guid.NewGuid(), new Interaction(), _ => { }, default);
        var result = await registry.ExecuteAsync(new("x", "select_game", "{\"game_id\":\"id\",\"path\":\"C:/Windows\"}"), context);
        Assert.False(result.Success); Assert.False((await registry.ExecuteAsync(new("y", "shell", "{}"), context)).Success); Assert.Empty(ops.Calls);
    }
    [Fact] public async Task Delete_Waits_For_Bound_Human_Approval_And_Denial_Does_Not_Execute()
    {
        var ops = new Ops(); var interaction = new Interaction { Approval = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var registry = new AgentToolRegistry(ops); var context = new AgentExecutionContext(Guid.NewGuid(), interaction, _ => { }, default);
        var task = registry.ExecuteAsync(new("x", "delete_game", "{\"game_id\":\"actual\"}"), context);
        Assert.False(task.IsCompleted); Assert.Equal("actual", ops.Described); Assert.Empty(ops.Calls);
        interaction.Approval.SetResult(false); Assert.False((await task).Success); Assert.Empty(ops.Calls);
        interaction.Approval = new(); task = registry.ExecuteAsync(new("y", "delete_game", "{\"game_id\":\"other\"}"), context);
        interaction.Approval.SetResult(true); Assert.True((await task).Success); Assert.Single(ops.Calls);
    }
    [Fact] public async Task Question_Resume_Duplicate_Write_And_Protocol_Replay()
    {
        var ops = new Ops(); var interaction = new Interaction { Answers = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var question = """{"questions":[{"id":"flavor","prompt":"玩法？","options":[{"label":"探索","description":"推荐","recommended":true},{"label":"科技","description":"自动化"}]}]}""";
        var fake = new FakeClient([DeepSeekClient.ParseCompleted(Complete(Call("get_launcher_state", "{}", "state"), Call("ask_user_question", question, "question"))),
            DeepSeekClient.ParseCompleted(Complete(Call("select_game", "{\"game_id\":\"actual\"}", "pick1"), Call("select_game", "{\"game_id\":\"actual\"}", "pick2"))), DeepSeekClient.ParseCompleted(Complete())]);
        var session = new AgentSessionService(fake, new AgentToolRegistry(ops), ops, interaction);
        var run = session.SendAsync("玩整合包", new("fixture", "fixture"), "high");
        Assert.False(run.IsCompleted); Assert.DoesNotContain("select_game", ops.Calls);
        interaction.Answers.SetResult(new Dictionary<string, string> { ["flavor"] = "探索" }); await run;
        Assert.Equal(1, ops.Calls.Count(x => x == "select_game"));
        Assert.Contains(fake.Histories[1].OfType<JsonObject>(), x => x["type"]?.GetValue<string>() == "function_call_output" && JsonNode.Parse(x["output"]!.GetValue<string>())?["Data"]?["flavor"]?.GetValue<string>() == "探索");
        await session.SendAsync("继续", new("fixture", "fixture"), "high"); Assert.True(fake.Histories[^1].Count > fake.Histories[0].Count);
    }
    [Fact] public async Task Stop_Cancels_Pending_Question_And_Only_Current_Group()
    {
        var ops = new Ops(); var interaction = new Interaction { Approval = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var fake = new FakeClient([DeepSeekClient.ParseCompleted(Complete(Call("delete_game", "{\"game_id\":\"actual\"}")))]);
        var session = new AgentSessionService(fake, new AgentToolRegistry(ops), ops, interaction);
        var run = session.SendAsync("删除", new("fixture", "fixture"), "high"); Assert.False(run.IsCompleted);
        await session.StopAsync(); Assert.False(session.IsRunning); Assert.Empty(ops.Calls); Assert.Single(ops.Cancelled);
        await session.SendAsync("继续", new("fixture", "fixture"), "high");
        Assert.Contains(fake.Histories[^1].OfType<JsonObject>(), x => x["type"]?.GetValue<string>() == "function_call_output" && JsonNode.Parse(x["output"]!.GetValue<string>())!["Summary"]!.GetValue<string>().Contains("中止"));
        Assert.NotEqual(ops.Cancelled[0], ops.Cancelled[1]);
    }
    [Fact] public void Game_Path_Escape_And_Link_Are_Denied()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikun-agent-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try { Assert.Throws<UnauthorizedAccessException>(() => AgentPathGuard.Within(root, Path.Combine(root, "..", "outside"))); Assert.Equal(Path.Combine(root, "logs", "latest.log"), AgentPathGuard.Within(root, Path.Combine(root, "logs", "latest.log"))); }
        finally { Directory.Delete(root); }
    }
    [Fact] public async Task Queue_Group_Cancel_Does_Not_Cancel_Manual_Job_And_Cleans_Staging()
    {
        var root = Path.Combine(Path.GetTempPath(), "ikun-agent-queue-" + Guid.NewGuid()); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var blocker = new SemaphoreSlim(0); using var http = new HttpClient(); using var log = new LogService(Path.Combine(root, "logs")); int attempts = 0;
        var installer = new DownloadInstallService(new JavaService(http, log), new VersionService(), log, sourceFactory: (p, token) => { if (Interlocked.Increment(ref attempts) == 1) { started.TrySetResult(); blocker.Wait(token); } return new(p, token); });
        var queue = new DownloadTaskService(installer, _ => false); var group = Guid.NewGuid();
        InstallPlan Plan(string name) => new(name, root, null, null, null, null, null, null, new(false, false), root, [], LocalArchive: Path.Combine(root, "missing.zip"));
        var ai = queue.Enqueue(Plan("AI"), group); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = queue.Enqueue(Plan("Manual")); var wait = queue.WaitAsync(ai);
        await queue.CancelGroupAsync(group).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadTaskState.Cancelled, (await wait).State);
        Assert.Equal(DownloadTaskState.Failed, (await queue.WaitAsync(manual).WaitAsync(TimeSpan.FromSeconds(5))).State);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, ".ikun-downloads")));
        Assert.True(queue.Remove(ai)); Assert.Equal(DownloadTaskState.Cancelled, (await wait).State);
        await queue.CancelAndWaitAsync();
    }
    private static HttpResponseMessage Sse(string name, string delta, JsonObject final) => new(HttpStatusCode.OK) { Content = new StringContent($"event: {name}\ndata: {delta}\n\nevent: response.completed\ndata: {final.ToJsonString()}\n\n", Encoding.UTF8, "text/event-stream") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> f) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => f(r); }
    private sealed class Ops : ILauncherOperations
    {
        public Func<string, ToolResult>? Result;
        public List<string> Calls { get; } = []; public List<Guid> Cancelled { get; } = []; public string? Described;
        public Task<ToolResult> ExecuteAsync(string name, JsonObject a, AgentExecutionContext c) { Calls.Add(name); return Task.FromResult(Result?.Invoke(name) ?? ToolResult.Ok("完成")); }
        public Task<AgentApproval> DescribeSensitiveAsync(string n, JsonObject a, CancellationToken c) { Described = a["game_id"]!.GetValue<string>(); return Task.FromResult(new AgentApproval("删除", Described)); }
        public Task CancelGroupAsync(Guid g) { Cancelled.Add(g); return Task.CompletedTask; }
    }
    private sealed class Interaction : IAgentInteraction
    {
        public TaskCompletionSource<bool>? Approval;
        public TaskCompletionSource<IReadOnlyDictionary<string, string>>? Answers;
        public Task<bool> ApproveAsync(AgentApproval a, CancellationToken c) => Approval?.Task.WaitAsync(c) ?? Task.FromResult(false);
        public Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<AgentQuestion> q, CancellationToken c) => Answers!.Task.WaitAsync(c);
    }
    private sealed class FakeClient(IEnumerable<AgentResponse> replies) : IDeepSeekClient
    {
        private readonly Queue<AgentResponse> _replies = new(replies); public List<JsonArray> Histories { get; } = [];
        public Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool r, CancellationToken c) => Task.FromResult<IReadOnlyList<AgentModel>>([new("fixture", "fixture")]);
        public Task<AgentResponse> RespondAsync(AgentModel m, string e, JsonArray h, JsonArray t, Action<string> d, CancellationToken c) { Histories.Add(h.DeepClone().AsArray()); return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : DeepSeekClient.ParseCompleted(Complete())); }
    }
}
