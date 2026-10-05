using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Launcher.AI;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class WorkbenchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novaina-workbench-test-" + Guid.NewGuid().ToString("N"));
    public WorkbenchTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root) && _root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true); }
    private WorkbenchOperations Workspace() { var ops = new WorkbenchOperations(() => [], s => s); ops.Authorize(_root); return ops; }
    private static AgentExecutionContext Context(Interaction? interaction = null, CancellationToken token = default) => new(Guid.NewGuid(), interaction ?? new(), _ => { }, token);
    private static JsonObject Args(string path, string content, string hash) => new() { ["path"] = path, ["content"] = content, ["expected_sha256"] = hash };
    [Fact] public async Task Catalogs_Are_Separate_And_Delegation_Does_Not_Forward_Project_Permissions()
    {
        var basic = new StubOperations(); var workspace = Workspace(); string? goal = null;
        var registry = new WorkbenchToolRegistry(basic, workspace, (g, _) => { goal = g; return Task.FromResult(ToolResult.Ok("子代理实际结果")); });
        var names = registry.Definitions.OfType<JsonObject>().Select(x => x["name"]!.ToString()).ToArray();
        Assert.Contains("run_terminal", names); Assert.Contains("write_project_file", names); Assert.Contains("delegate_basic_agent", names);
        foreach (var forbidden in new[] { "install_game", "install_content", "delete_game", "import_modpack", "download_java", "export_modpack" })
        { Assert.DoesNotContain(forbidden, names); Assert.False((await registry.ExecuteAsync(new("a", forbidden, "{}"), Context())).Success); }
        var basicNames = new AgentToolRegistry(basic).Definitions.ToJsonString(); Assert.DoesNotContain("run_terminal", basicNames); Assert.DoesNotContain("write_project_file", basicNames); Assert.DoesNotContain("delegate_basic_agent", basicNames);
        Assert.True((await registry.ExecuteAsync(new("b", "delegate_basic_agent", "{\"goal\":\"准备 Java 21 JDK，不启动游戏\"}"), Context())).Success);
        Assert.Contains("JDK", goal); Assert.Equal(0, basic.Executed);
        Assert.False((await registry.ExecuteAsync(new("c", "run_terminal", "{\"program\":\"gradle\",\"action\":\"build\",\"shell\":\"cmd\"}"), Context())).Success);
        Assert.False((await registry.ExecuteAsync(new("d", "run_terminal", "{\"program\":\"gradle\",\"action\":\"runClient\"}"), Context())).Success);
        Assert.False(registry.IsMutation("run_terminal"));
    }
    [Theory]
    [InlineData("../outside.java")][InlineData("C:/Windows/test.java")][InlineData("src/.GIT/config.txt")][InlineData(".gradle/secret.properties")]
    [InlineData("src/.env")][InlineData("account.protected")][InlineData("gradlew.bat")][InlineData("file.txt:stream")]
    public void Project_Paths_Reject_Escapes_Caches_Secrets_And_Scripts(string path) => Assert.Throws<UnauthorizedAccessException>(() => WorkbenchOperations.SourcePath(_root, path));
    [Fact] public void Reparse_Points_Are_Rejected()
    {
        var target = Path.Combine(_root, "real"); Directory.CreateDirectory(target); var link = Path.Combine(_root, "link");
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(arg);
        using (var process = System.Diagnostics.Process.Start(info)!) { process.WaitForExit(); Assert.Equal(0, process.ExitCode); }
        try { Assert.Throws<UnauthorizedAccessException>(() => WorkbenchOperations.SourcePath(_root, "link/Hello.java")); }
        finally { Directory.Delete(link); }
    }
    [Fact] public async Task Writes_Require_Read_Hash_Confirmation_And_Reject_Concurrent_Edits()
    {
        var ops = Workspace(); var create = await ops.ExecuteAsync("write_project_file", Args("src/main/java/Hello.java", "class Hello {}", ""), Context()); Assert.True(create.Success);
        var read = await ops.ExecuteAsync("read_project_file", new() { ["path"] = "src/main/java/Hello.java" }, Context()); var hash = System.Text.Json.JsonSerializer.SerializeToNode(read.Data)!["sha256"]!.ToString();
        Assert.False((await ops.ExecuteAsync("write_project_file", Args("src/main/java/Hello.java", "class Hello { int x; }", ""), Context())).Success);
        Assert.False((await ops.ExecuteAsync("write_project_file", Args("src/main/java/Hello.java", "class Hello { int x; }", hash), Context(new() { Approve = false }))).Success);
        var path = Path.Combine(_root, "src/main/java/Hello.java");
        Assert.False((await ops.ExecuteAsync("write_project_file", Args("src/main/java/Hello.java", "class Hello { int x; }", hash), Context(new() { OnApprove = () => File.WriteAllText(path, "user edit") }))).Success);
        Assert.Equal("user edit", await File.ReadAllTextAsync(path));
    }
    [Fact] public async Task Unselected_Workspace_And_Cancelled_Operations_Cannot_Write()
    {
        var ops = new WorkbenchOperations(() => [], x => x); Assert.False((await ops.ExecuteAsync("write_project_file", Args("x.java", "hello", ""), Context())).Success);
        ops.Authorize(_root); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ops.ExecuteAsync("write_project_file", Args("x.java", "hello", ""), Context(token: cancellation.Token)));
        Assert.False(File.Exists(Path.Combine(_root, "x.java"))); ops.Revoke(); Assert.Null(ops.Root);
    }
    [Fact] public void Conversation_Storage_Is_Encrypted_Mode_Isolated_And_Corruption_Is_Tolerated()
    {
        var store = new AgentConversationStore(_root); var id = Guid.NewGuid(); var snapshot = new AgentSessionSnapshot(new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "PRIVATE-CONVERSATION" }), "目标", [], 2);
        store.Save(new(new(id, AgentMode.Basic, "基础会话", DateTimeOffset.Now), snapshot, []));
        Assert.Null(store.Load(AgentMode.Workbench, id)); Assert.Single(store.List(AgentMode.Basic)); Assert.Empty(store.List(AgentMode.Workbench));
        Assert.Equal("目标", store.Load(AgentMode.Basic, id)!.Session.Goal);
        var file = Directory.GetFiles(_root, "*.protected", SearchOption.AllDirectories).Single(); Assert.DoesNotContain("PRIVATE-CONVERSATION", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        File.WriteAllText(file, "damaged"); Assert.Null(store.Load(AgentMode.Basic, id)); Assert.Empty(store.List(AgentMode.Basic));
        store.Delete(AgentMode.Basic, id); Assert.False(File.Exists(file));
    }
    [Fact] public async Task Restoration_Repairs_Incomplete_Calls_Without_Replaying_And_Uses_Workbench_Prompt()
    {
        var client = new RecordingClient(); var ops = new StubOperations(); var registry = new WorkbenchToolRegistry(ops, Workspace(), (_, _) => Task.FromResult(ToolResult.Ok("实际结果")));
        var session = new AgentSessionService(client, registry, ops, new Interaction(), WorkbenchPrompt.System);
        session.Restore(new(new JsonArray(new JsonObject { ["type"] = "function_call", ["name"] = "write_project_file", ["call_id"] = "past", ["arguments"] = "{}" }), "不要启动游戏", [], 1));
        Assert.Equal(0, ops.Executed); await session.SendAsync("继续检查真实状态", new("test", "test", 100000), "high");
        Assert.Contains("工作台", client.Instructions); Assert.Contains("past", client.History!.ToJsonString()); Assert.Single(client.History.OfType<JsonObject>(), x => x["type"]?.ToString() == "function_call_output");
        Assert.Equal(0, ops.Executed); Assert.Equal("不要启动游戏", session.Capture().Goal);
    }
    [Fact] public void All_Embedded_Workflows_Exist_And_AI_Configuration_Migrates_Independently()
    {
        foreach (var topic in new[] { "workflow", "fabric", "forge", "neoforge", "resources", "sides-network", "testing", "troubleshooting", "delivery" }) Assert.True(ModDevelopmentGuides.Read(topic).Length > 400);
        var legacy = new LauncherSettings { AiModelId = "old-model", AiReasoningEffort = "medium" }; var store = new AiConfigurationStore(_root); var configuration = store.Load(legacy);
        Assert.Equal("old-model", configuration.ModelId); configuration.ModelId = "new-model"; store.Save(configuration);
        Assert.Equal("old-model", legacy.AiModelId); Assert.Equal("new-model", store.Load(legacy).ModelId);
        Assert.False(File.Exists(Path.Combine(_root, "settings.json"))); Assert.DoesNotContain("Key", File.ReadAllText(Path.Combine(_root, "ai/settings.json")));
    }
    [Fact] public async Task Dependency_Sources_Are_Queried_By_Logical_ID_And_Revoked_With_The_Project()
    {
        var cache = Path.Combine(_root, ".novaina/gradle/caches"); Directory.CreateDirectory(cache);
        using (var zip = ZipFile.Open(Path.Combine(cache, "fixture-sources.jar"), ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("fixture/Widget.java").Open()); writer.Write("class Widget {}"); }
        var ops = Workspace();
        Assert.False((await ops.ExecuteAsync("read_dependency_source", new() { ["source_id"] = "guessed", ["entry"] = "fixture/Widget.java" }, Context())).Success);
        var sources = await ops.ExecuteAsync("list_dependency_sources", new() { ["query"] = "Widget" }, Context());
        var id = System.Text.Json.JsonSerializer.SerializeToNode(sources.Data)!["sources"]![0]!["source_id"]!.ToString();
        var read = await ops.ExecuteAsync("read_dependency_source", new() { ["source_id"] = id, ["entry"] = "fixture/Widget.java" }, Context());
        Assert.True(read.Success); Assert.Contains("class Widget", System.Text.Json.JsonSerializer.Serialize(read.Data));
        ops.Revoke(); Assert.False((await ops.ExecuteAsync("read_dependency_source", new() { ["source_id"] = id, ["entry"] = "fixture/Widget.java" }, Context())).Success);
    }
    [Fact] public async Task Build_Trust_Is_Bound_To_The_Confirmed_Configuration()
    {
        var bin = Path.Combine(_root, "jdk/bin"); Directory.CreateDirectory(bin); File.WriteAllText(Path.Combine(bin, "javac.exe"), "fixture");
        var java = new JavaRuntimeInfo(Path.Combine(bin, "java.exe"), 21, new Version(21, 0), "x64", "fixture");
        var wrapper = Path.Combine(_root, "gradle/wrapper"); Directory.CreateDirectory(wrapper); File.WriteAllText(Path.Combine(wrapper, "gradle-wrapper.jar"), "fixture");
        var config = Path.Combine(_root, "build.gradle"); File.WriteAllText(config, "plugins {}");
        var ops = new WorkbenchOperations(() => [("jdk", java)], x => x); ops.Authorize(_root);
        var result = await ops.ExecuteAsync("run_terminal", new() { ["program"] = "gradle", ["action"] = "build", ["jdk_id"] = "jdk" }, Context(new() { OnApprove = () => File.WriteAllText(config, "changed during approval") }));
        Assert.False(result.Success); Assert.Contains("确认期间", result.Summary); Assert.False(File.Exists(Path.Combine(_root, ".novaina/tmp")));
    }
    [Fact] public async Task Basic_Delegate_Uses_Only_Basic_Tools_A_Separate_Task_Group_And_Human_Approval()
    {
        var operations = new StubOperations(); var client = new DelegateClient(); var parent = Guid.NewGuid();
        var result = await new BasicAgentDelegate(client, operations).RunAsync("查询状态，未要求时不要启动", new("fixture", "fixture", 100000), "high", new(parent, new Interaction(), _ => { }, default));
        Assert.True(result.Success); Assert.DoesNotContain("write_project_file", client.Tools); Assert.DoesNotContain("run_terminal", client.Tools);
        Assert.Contains("基础代理", client.Input); Assert.NotEqual(parent, Assert.Single(operations.Groups));
        Assert.Contains(operations.Groups[0], operations.Cancelled); Assert.DoesNotContain(parent, operations.Cancelled);
        var denied = await new BasicAgentDelegate(new DelegateClient("launch_game"), new StubOperations()).RunAsync("明确要求启动测试", new("fixture", "fixture", 100000), "high", Context(new() { Approve = false }));
        Assert.False(denied.Success);
        Assert.Contains("Failed", System.Text.Json.JsonSerializer.Serialize(denied.Data));
        var authorizedClient = new DelegateClient("launch_game");
        var authorized = await new BasicAgentDelegate(authorizedClient, new StubOperations()).RunAsync("启动已委托实例", new("fixture", "fixture", 100000), "high", Context(new() { FullAccess = true }));
        Assert.True(authorized.Success); Assert.Contains("授权模式", authorizedClient.Instructions); Assert.Contains("无需再次询问操作确认", authorizedClient.Instructions);
        Assert.DoesNotContain("execute_terminal", authorizedClient.Tools);
    }
    [Fact] public async Task Delegation_Uses_The_Parent_Model_Budget_And_Does_Not_Claim_An_Unfinished_Goal()
    {
        var budget = new AgentModelBudget(1); var operations = new StubOperations();
        var result = await new BasicAgentDelegate(new DelegateClient(), operations).RunAsync("查询", new("fixture", "fixture", 100000), "high", Context() with { ModelBudget = budget });
        Assert.Equal(0, budget.Remaining); Assert.Equal(10, budget.OutputTokens); Assert.Equal(1, operations.Executed); Assert.False(result.Success); Assert.False(budget.TryStartCall());
    }
    [Fact] public async Task Checkpoints_Are_Complete_And_Do_Not_Reexecute_Historical_Tools()
    {
        var operations = new StubOperations(); var client = new DelegateClient(); var session = new AgentSessionService(client, new AgentToolRegistry(operations), operations, new Interaction());
        var snapshots = new List<AgentSessionSnapshot>(); session.Checkpoint += snapshots.Add;
        await session.SendAsync("检查", new("fixture", "fixture", 100000), "high");
        Assert.True(snapshots.Count >= 3);
        foreach (var snapshot in snapshots)
        {
            var calls = snapshot.History.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "function_call").Select(x => x["call_id"]!.ToString());
            var outputs = snapshot.History.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "function_call_output").Select(x => x["call_id"]!.ToString());
            Assert.All(calls, x => Assert.Contains(x, outputs));
        }
        var restored = new AgentSessionService(new RecordingClient(), new AgentToolRegistry(operations), operations, new Interaction());
        restored.Restore(snapshots[^1]); Assert.Equal(1, operations.Executed);
    }
    [Fact] public async Task Explicit_Development_JDK_Download_Does_Not_Request_A_JRE()
    {
        var urls = new List<string>(); using var http = new HttpClient(new MetadataHandler(urls));
        using var log = new LogService(_root); var service = new JavaService(http, log);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(21, new InlineProgress<JavaDownloadProgress>(_ => { }), default, _root, requireJdk: true));
        Assert.NotEmpty(urls); Assert.All(urls, x => Assert.Contains("image_type=jdk", x));
    }
    private sealed class MetadataHandler(List<string> urls) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { urls.Add(request.RequestUri!.ToString()); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }); } }
    private sealed class DelegateClient(string tool = "get_launcher_state") : IDeepSeekClient
    {
        private int _call; public string Tools = "", Input = "", Instructions = "";
        public Task<AgentResponse> RespondWithInstructionsAsync(AgentModel m, string e, JsonArray h, JsonArray tools, string instructions, Action<string> d, CancellationToken t)
        { Instructions = instructions; return RespondAsync(m, e, h, tools, d, t); }
        public Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool r, CancellationToken t) => throw new NotSupportedException();
        public Task<AgentResponse> RespondAsync(AgentModel m, string e, JsonArray history, JsonArray tools, Action<string> delta, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Tools = tools.ToJsonString(); Input = history.OfType<JsonObject>().First()["content"]!.ToString();
            if (_call++ == 0) return Task.FromResult(new AgentResponse(new JsonArray(new JsonObject { ["type"] = "function_call", ["name"] = tool, ["call_id"] = "query", ["arguments"] = "{}" }), [new("query", tool, "{}")], 10, 200));
            delta("已查询实际状态"); return Task.FromResult(new AgentResponse(new JsonArray(new JsonObject { ["type"] = "message", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "已查询实际状态" }) }), [], 10, 300));
        }
    }
    private sealed class Interaction : IAgentInteraction
    {
        public bool FullAccess { get; init; }
        public bool Approve = true; public Action? OnApprove;
        public Task<bool> ApproveAsync(AgentApproval approval, CancellationToken token) { token.ThrowIfCancellationRequested(); OnApprove?.Invoke(); return Task.FromResult(Approve); }
        public Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<AgentQuestion> q, CancellationToken t) => throw new NotSupportedException();
    }
    private sealed class StubOperations : ILauncherOperations
    {
        public int Executed; public List<Guid> Groups = [], Cancelled = [];
        public Task<ToolResult> ExecuteAsync(string n, JsonObject a, AgentExecutionContext c) { Executed++; Groups.Add(c.GroupId); return Task.FromResult(ToolResult.Ok("真实状态")); }
        public Task<AgentApproval> DescribeSensitiveAsync(string n, JsonObject a, CancellationToken t) => Task.FromResult(new AgentApproval("确认", "对象"));
        public Task CancelGroupAsync(Guid group) { Cancelled.Add(group); return Task.CompletedTask; }
    }
    private sealed class RecordingClient : IDeepSeekClient
    {
        public string Instructions = ""; public JsonArray? History;
        public Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool r, CancellationToken t) => throw new NotSupportedException();
        public Task<AgentResponse> RespondAsync(AgentModel m, string e, JsonArray h, JsonArray tools, Action<string> d, CancellationToken t) => throw new NotSupportedException();
        public Task<AgentResponse> RespondWithInstructionsAsync(AgentModel m, string e, JsonArray h, JsonArray tools, string instructions, Action<string> d, CancellationToken t) { Instructions = instructions; History = h.DeepClone().AsArray(); return Task.FromResult(new AgentResponse([], [], 0, 0)); }
    }
}
