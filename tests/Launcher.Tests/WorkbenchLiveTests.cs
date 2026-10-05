using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.AI;
using Launcher.Core;
using Xunit;

namespace Launcher.Tests;

/// <summary>Explicit live gate; never turns an unexecuted network/build test into a pass.</summary>
public sealed class WorkbenchLiveTests
{
    [ManualDeepSeek]
    public async Task Real_Llm_Compaction_Preserves_Older_Requirements_And_Recent_Complete_Turns()
    {
        var key = new SecretStore(Environment.GetEnvironmentVariable("NOVAINA_AI_KEY_DIRECTORY")!).ReadJson<string>("deepseek-api-key");
        var client = new DeepSeekClient(() => key); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var models = await client.GetModelsAsync(false, timeout.Token); var model = models.FirstOrDefault(x => x.Id.Contains("flash", StringComparison.OrdinalIgnoreCase)) ?? models[0];
        var history = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "验收夹具：制作 campfire_soup 模组，NeoForge 1.21.1，JDK 21，中文名称为营火汤。不启动游戏、不上传、不改已有配置。请保留这些约束。" },
            new JsonObject { ["role"] = "assistant", ["content"] = "已查询基线构建，编译通过；这不是游戏内验证。日志：\n" + string.Concat(Enumerable.Repeat("[INFO] Repeated build diagnostics; no new state.\n", 6000)) });
        for (int i = 0; i < 5; i++) { history.Add(new JsonObject { ["role"] = "user", ["content"] = $"确认第 {i} 步，只编译，不运行游戏。" }); history.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"第 {i} 步记录完毕；先前的模组 ID 和环境要求保持。" }); }
        var launcher = new FixtureLauncher(); var session = new AgentSessionService(client, new AgentToolRegistry(launcher), launcher, new FixtureInteraction(""));
        session.Restore(new(history, "完成营火汤 Mod，编译后交付 JAR", [], 0)); var before = session.ContextUsage.Used;
        await session.CompactAsync(model, "high", timeout.Token);
        Assert.Equal(1, session.ContextUsage.CompactionCount); Assert.True(session.ContextUsage.Used < before);
        var saved = session.Capture(); var summary = saved.History[0]!["content"]!.ToString();
        Assert.Contains("campfire_soup", summary); Assert.Contains("1.21.1", summary); Assert.Contains("21", summary);
        Assert.Contains(saved.History.OfType<JsonObject>(), x => x["role"]?.ToString() == "assistant" && x["content"]?.ToString().Contains("第 4 步记录完毕") == true);
        var continued = await client.RespondWithInstructionsAsync(model, "high", saved.History, [], "验收：不调用工具，不做操作。根据保留的上下文，只回复 JSON 对象，不加 Markdown：mod_id 字符串、minecraft 字符串、jdk 整数、run_game 布尔值、upload 布尔值、edit_existing_configuration 布尔值。后三项表示用户是否允许对应操作。", _ => { }, timeout.Token);
        Assert.Empty(continued.Calls);
        var reply = string.Join("\n", continued.Output.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "message")
            .SelectMany(x => x["content"]?.AsArray() ?? []).Where(x => x?["type"]?.ToString() == "output_text").Select(x => x!["text"]!.ToString()));
        var remembered = JsonNode.Parse(reply)!.AsObject(); Assert.Equal("campfire_soup", remembered["mod_id"]!.ToString());
        Assert.Equal("1.21.1", remembered["minecraft"]!.ToString()); Assert.Equal(21, remembered["jdk"]!.GetValue<int>());
        Assert.False(remembered["run_game"]!.GetValue<bool>()); Assert.False(remembered["upload"]!.GetValue<bool>()); Assert.False(remembered["edit_existing_configuration"]!.GetValue<bool>());
        var evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ai-reliability")); Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "llm-compaction.json"), JsonSerializer.Serialize(new { model = model.Id, before, after = session.ContextUsage.Used, summary_retained_identifiers = true, recent_complete_turn_retained = true, continuation_retained_environment_and_constraints = true }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [ManualDeepSeek]
    public async Task Real_DeepSeek_Workbench_Writes_Compiles_And_Packages_A_Class_In_The_Verification_Project()
    {
        var secrets = Environment.GetEnvironmentVariable("NOVAINA_AI_KEY_DIRECTORY")!;
        var key = new SecretStore(secrets).ReadJson<string>("deepseek-api-key"); Assert.False(string.IsNullOrWhiteSpace(key));
        var client = new DeepSeekClient(() => key); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var models = await client.GetModelsAsync(false, timeout.Token); var model = models.FirstOrDefault(x => x.Id.Contains("flash", StringComparison.OrdinalIgnoreCase)) ?? models[0];
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/agent-workbench-check/live-neoforge"));
        Assert.True(File.Exists(Path.Combine(root, "verification.json")), "请先执行官方模板构建验收");
        var javaPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java/jdk-21/bin/java.exe");
        var ops = new WorkbenchOperations(() => [("jdk-real", new(javaPath, 21, new Version(21, 0), "x64", "Fixture JDK"))], s => s); ops.Authorize(root);
        var launcher = new FixtureLauncher(); var interaction = new FixtureInteraction(root); var events = new List<string>();
        var registry = new WorkbenchToolRegistry(launcher, ops, (goal, context) => new BasicAgentDelegate(client, launcher).RunAsync(goal, model, "high", context));
        var session = new AgentSessionService(client, registry, launcher, interaction, WorkbenchPrompt.System);
        session.Event += e => { if (e.Kind is AgentUiEventKind.ToolCompleted or AgentUiEventKind.Error) events.Add(e.Title); };
        var className = "NovainaVerification" + Guid.NewGuid().ToString("N")[..8]; var path = "src/main/java/com/example/examplemod/" + className + ".java";
        await session.SendAsync($"这是一份专用验收项目，明确目标：NeoForge 1.21.1，Java 21，工作目录已授权。按工作流检查环境，然后只新增 {path}，类名 {className}，包名 com.example.examplemod，添加一个返回字符串 Novaina 的 public static String message() 方法。不要改或删除已有文件，不改 Gradle 配置，不下载或启动游戏，不需要追问。执行实际 build 并核对正式 JAR。", model, "high", timeout.Token);
        var snapshot = session.Capture(); var outputs = snapshot.History.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "function_call_output").Select(x => JsonNode.Parse(x["output"]!.ToString())!).ToArray();
        var built = outputs.Any(x => x["Success"]?.GetValue<bool>() == true && x["Data"]?["program"]?.ToString() == "gradle" && x["Data"]?["action"]?.ToString() == "build" && x["Data"]?["exit_code"]?.GetValue<int>() == 0);
        // Store only result summaries; never persist key, full prompts, reasoning or provider payloads.
        await File.WriteAllTextAsync(Path.Combine(root, "deepseek-verification.json"), JsonSerializer.Serialize(new { model = model.Id, source = path, built, events }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(File.Exists(Path.Combine(root, path)), "模型没有完成新增源文件"); Assert.True(built, "没有成功的实际构建结果");
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(root, "build/libs"), "*.jar"), file => { using var zip = System.IO.Compression.ZipFile.OpenRead(file); return zip.GetEntry("com/example/examplemod/" + className + ".class") is not null; });
        await session.SendAsync("现在只通过基础模式子代理查询启动器状态，委托目标必须明确只查询、不安装、不下载、不启动。根据子代理实际结果回复。", model, "high", timeout.Token);
        Assert.Contains(session.Capture().History.OfType<JsonObject>(), x => x["name"]?.ToString() == "delegate_basic_agent"); Assert.True(launcher.Queried);
    }
    private sealed class FixtureLauncher : ILauncherOperations
    {
        public bool Queried;
        public Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, AgentExecutionContext context)
        { Queried |= name == "get_launcher_state"; return Task.FromResult(name == "get_launcher_state" ? ToolResult.Ok("验收夹具状态：没有账户与游戏", new { games = Array.Empty<object>(), accounts = Array.Empty<object>() }) : ToolResult.Fail("验收仅允许状态查询，不操作用户数据")); }
        public Task<AgentApproval> DescribeSensitiveAsync(string name, JsonObject arguments, CancellationToken token) => throw new NotSupportedException();
        public Task CancelGroupAsync(Guid group) => Task.CompletedTask;
    }
    [ManualModBuild]
    public async Task Official_NeoForge_Template_Initializes_And_Builds_With_A_Real_JDK()
        => await VerifyOfficialBuild("NeoForge", "1.21.1", "live-neoforge");
    [ManualModBuild]
    public async Task Official_Forge_Template_Initializes_And_Builds_With_A_Real_JDK()
        => await VerifyOfficialBuild("Forge", "1.21.1", "live-forge");
    private async Task VerifyOfficialBuild(string loader, string minecraft, string folder)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ai-reliability/" + folder));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) root += "-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root); var javaPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java/jdk-21/bin/java.exe"); Assert.True(File.Exists(javaPath));
        var java = new JavaRuntimeInfo(javaPath, 21, new Version(21, 0), "x64", "Fixture JDK");
        var ops = new WorkbenchOperations(() => [("jdk-real", java)], s => s); ops.Authorize(root);
        var events = new List<string>(); var context = new AgentExecutionContext(Guid.NewGuid(), new FixtureInteraction(root), e => { lock (events) events.Add(e.Title + " · " + e.Text); }, CancellationToken.None);
        var templates = await ops.ExecuteAsync("list_mod_templates", new() { ["loader"] = loader, ["minecraft"] = minecraft }, context); Assert.True(templates.Success, templates.Summary);
        var id = JsonSerializer.SerializeToNode(templates.Data)!["templates"]![0]!["template_id"]!.ToString();
        var create = await ops.ExecuteAsync("create_mod_project", new() { ["template_id"] = id }, context); Assert.True(create.Success, create.Summary);
        Assert.True(File.Exists(Path.Combine(root, "build.gradle"))); Assert.True(File.Exists(Path.Combine(root, "gradle/wrapper/gradle-wrapper.jar")));
        var build = await ops.ExecuteAsync("run_terminal", new() { ["program"] = "gradle", ["action"] = "build", ["jdk_id"] = "jdk-real" }, context);
        await File.WriteAllTextAsync(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { template = templates, create, build, events }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(build.Success, JsonSerializer.Serialize(build));
        var artifacts = await ops.ExecuteAsync("list_build_artifacts", new(), context); Assert.True(artifacts.Success);
        Assert.Contains(JsonSerializer.SerializeToNode(artifacts.Data)!["files"]!.AsArray(), x => x!["production"]!.GetValue<bool>());
    }
    private sealed class FixtureInteraction(string root) : IAgentInteraction
    {
        public Task<bool> ApproveAsync(AgentApproval approval, CancellationToken token) { Assert.Contains(root, approval.Detail); return Task.FromResult(true); }
        public Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<AgentQuestion> questions, CancellationToken token) => throw new NotSupportedException();
    }
}
public sealed class ManualDeepSeekAttribute : FactAttribute
{
    public ManualDeepSeekAttribute() { if (Environment.GetEnvironmentVariable("NOVAINA_VERIFY_DEEPSEEK") != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOVAINA_AI_KEY_DIRECTORY"))) Skip = "需显式启用真实 DeepSeek 验收并指定 DPAPI 密钥目录；不使用真实账户/游戏作为夹具"; }
}
public sealed class ManualModBuildAttribute : FactAttribute
{
    public ManualModBuildAttribute() { if (Environment.GetEnvironmentVariable("NOVAINA_VERIFY_MOD_BUILD") != "1") Skip = "需显式启用 NOVAINA_VERIFY_MOD_BUILD=1；联网获取官方模板并执行真实 Gradle 构建"; }
}
