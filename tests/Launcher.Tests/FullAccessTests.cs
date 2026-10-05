using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.AI;
using Xunit;

namespace Launcher.Tests;

public sealed class FullAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NovainaFullAccess-" + Guid.NewGuid().ToString("N"));
    public FullAccessTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static AgentExecutionContext Context(CancellationToken token = default) => new(Guid.NewGuid(), new Interaction(), _ => { }, token);
    [Fact] public async Task Only_Host_Confirmation_Exposes_Full_Tools_And_Reset_Revokes_Them()
    {
        var policy = new AgentPermissionPolicy(); var registry = new PermissionToolRegistry(new EmptyRegistry(), new FullAccessOperations(x => x), policy);
        var call = new AgentToolCall("write", "write_file", new JsonObject { ["path"] = Path.Combine(_root, "outside.txt"), ["content"] = "allowed" }.ToJsonString());
        Assert.Empty(registry.Definitions); Assert.False((await registry.ExecuteAsync(call, Context())).Success);
        policy.EnableAfterHumanConfirmation(); Assert.Contains(registry.Definitions, x => x!["name"]!.ToString() == "execute_terminal");
        Assert.True((await registry.ExecuteAsync(call, Context())).Success); Assert.Equal("allowed", await File.ReadAllTextAsync(Path.Combine(_root, "outside.txt")));
        Assert.False((await registry.ExecuteAsync(call with { Arguments = "{\"path\":\"a\",\"content\":\"b\",\"unknown\":true}" }, Context())).Success);
        policy.Reset(); Assert.Empty(registry.Definitions); Assert.False((await registry.ExecuteAsync(call, Context())).Success);
    }
    [Fact] public async Task Full_Files_Accept_Absolute_Paths_Binary_And_Recursive_Delete_Without_Approvals()
    {
        var ops = new FullAccessOperations(x => x); var context = Context(); var directory = Path.Combine(_root, "somewhere");
        Assert.True((await ops.ExecuteAsync("create_directory", new() { ["path"] = directory }, context)).Success);
        var path = Path.Combine(directory, "asset.bin");
        Assert.True((await ops.ExecuteAsync("write_file", new() { ["path"] = path, ["content"] = Convert.ToBase64String([0, 12, 255]), ["base64"] = true }, context)).Success);
        var read = await ops.ExecuteAsync("read_file", new() { ["path"] = path, ["base64"] = true }, context);
        Assert.Equal(Convert.ToBase64String([0, 12, 255]), JsonSerializer.SerializeToNode(read.Data)!["content"]!.ToString());
        var policy = new AgentPermissionPolicy(); policy.EnableAfterHumanConfirmation();
        var registry = new PermissionToolRegistry(new EmptyRegistry(), ops, policy);
        var chunk = await registry.ExecuteAsync(new("read", "read_file", new JsonObject { ["path"] = path, ["offset"] = 1, ["length"] = 1, ["base64"] = true }.ToJsonString()), context);
        Assert.True(chunk.Success); Assert.Equal(Convert.ToBase64String([12]), JsonSerializer.SerializeToNode(chunk.Data)!["content"]!.ToString());
        Assert.True((await ops.ExecuteAsync("delete_path", new() { ["path"] = directory, ["recursive"] = true }, context)).Success); Assert.False(Directory.Exists(directory));
    }
    [Fact] public async Task Complete_Terminal_Executes_Real_PowerShell_And_Cancellation_Kills_Children()
    {
        var ops = new FullAccessOperations(x => x.Replace("fixture-secret", "[redacted]")); ops.SetWorkingDirectoryFromHost(_root);
        var result = await ops.ExecuteAsync("execute_terminal", new() { ["shell"] = "powershell", ["command"] = "Write-Output ('fixture-' + 'secret'); [IO.File]::WriteAllText((Join-Path (Get-Location) 'real.txt'),'done')" }, Context());
        Assert.True(result.Success, result.Summary); Assert.Equal("done", await File.ReadAllTextAsync(Path.Combine(_root, "real.txt")));
        var output = JsonSerializer.SerializeToNode(result.Data)!["output"]!.ToString(); Assert.Contains("[redacted]", output); Assert.DoesNotContain("fixture-secret", output);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var cancellation = new CancellationTokenSource();
        var context = Context(cancellation.Token) with { Emit = _ => started.TrySetResult() };
        var run = ops.ExecuteAsync("execute_terminal", new() { ["shell"] = "powershell", ["command"] = "Start-Sleep -Seconds 30; [IO.File]::WriteAllText('late.txt','bad')" }, context);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.False(File.Exists(Path.Combine(_root, "late.txt")));
        var cmd = await ops.ExecuteAsync("execute_terminal", new() { ["shell"] = "cmd", ["command"] = "echo complete-cmd" }, Context());
        Assert.True(cmd.Success); Assert.Contains("complete-cmd", JsonSerializer.SerializeToNode(cmd.Data)!["output"]!.ToString());
    }
    [Fact] public async Task Missing_NeoForge_ModDev_Template_Falls_Back_To_Official_NeoGradle()
    {
        var visited = new List<string>(); using var http = new HttpClient(new Handler(request =>
        {
            visited.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsoluteUri.Contains("ModDevGradle") ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent("{\"sha\":\"" + new string('a', 40) + "\"}") };
        }));
        var ops = new WorkbenchOperations(() => [], x => x, http: http);
        var result = await ops.ExecuteAsync("list_mod_templates", new() { ["loader"] = "NeoForge", ["minecraft"] = "1.20.1" }, Context());
        Assert.True(result.Success); Assert.Equal(2, visited.Count); Assert.Contains("NeoGradle", JsonSerializer.Serialize(result.Data));
    }
    [Fact] public async Task Project_Java_Requirement_Prevents_Accidentally_Selecting_Higher_Installed_Jdk()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "build.gradle"), "java.toolchain.languageVersion = JavaLanguageVersion.of(21)");
        Assert.Equal(21, WorkbenchOperations.RequiredJava(_root));
        var wrong = new Launcher.Core.JavaRuntimeInfo(Path.Combine(_root, "jdk22/bin/java.exe"), 22, new Version(22, 0), "x64", "Fixture");
        var ops = new WorkbenchOperations(() => [("wrong", wrong)], x => x); ops.Authorize(_root);
        var result = await ops.ExecuteAsync("run_terminal", new() { ["program"] = "gradle", ["action"] = "build", ["jdk_id"] = "wrong" }, Context());
        Assert.False(result.Success); Assert.Contains("要求 JDK 21", result.Summary);
        Assert.Contains("网络/TLS", WorkbenchOperations.DiagnoseBuildFailure("Could not GET https://maven.neoforged.net: Read timed out"));
        Assert.Contains("版本不匹配", WorkbenchOperations.DiagnoseBuildFailure("Unsupported class file major version 66"));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request)); }
    private sealed class EmptyRegistry : IAgentToolRegistry
    { public JsonArray Definitions => []; public bool IsMutation(string name) => false; public Task<ToolResult> ExecuteAsync(AgentToolCall call, AgentExecutionContext context) => Task.FromResult(ToolResult.Fail("not registered")); }
    private sealed class Interaction : IAgentInteraction
    {
        public Task<bool> ApproveAsync(AgentApproval approval, CancellationToken cancellation) => throw new InvalidOperationException("Full tools must not request approval");
        public Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<AgentQuestion> questions, CancellationToken cancellation) => throw new NotSupportedException();
    }
}
