using System.Text.Json.Nodes;

namespace Launcher.AI;

/// <summary>Separate catalog; basic installation/content tools cannot be called directly in workbench mode.</summary>
public sealed class WorkbenchToolRegistry : IAgentToolRegistry
{
    public const string DelegateDescription = "派发独立的基础模式子代理，等待并返回实际结果。仅用于启动器业务准备：查询/安装测试游戏、下载或安装现成 Mod/整合包、准备 Java、登录恢复、实例导入导出。先明确具体子目标；不把整个开发目标转交，不用于创建项目、写源码、编辑资源、执行 Gradle、修复编译错误或生成 Mod JAR，这些由工作台工具完成。缺少开发 JDK 时可委托查询/下载，但要明确要求 JDK 而不是仅 JRE。子代理沿用基础模式确认与登录规则，不能继承项目权限或自行批准；未要求时不得启动游戏。";
    private readonly AgentToolRegistry _basic;
    private readonly ILauncherOperations _workspace;
    private readonly Func<string, AgentExecutionContext, Task<ToolResult>> _delegate;
    private readonly Dictionary<string, (string Description, JsonObject Schema, bool Write)> _tools = new();
    private static readonly HashSet<string> Direct = ["get_launcher_state", "select_game", "select_account", "configure_java", "retry_account_login", "read_game_logs", "launch_game", "ask_user_question"];
    public JsonArray Definitions => new(_basic.Definitions.OfType<JsonObject>().Where(x => Direct.Contains(x["name"]!.ToString())).Select(x => x.DeepClone())
        .Concat(_tools.Select(x => (JsonNode)new JsonObject { ["type"] = "function", ["name"] = x.Key, ["description"] = x.Value.Description, ["parameters"] = x.Value.Schema.DeepClone() })).ToArray());
    public WorkbenchToolRegistry(ILauncherOperations launcher, ILauncherOperations workspace, Func<string, AgentExecutionContext, Task<ToolResult>> delegated)
    {
        _basic = new(launcher, confirmLaunch: true); _workspace = workspace; _delegate = delegated;
        void Add(string n, string d, JsonObject s, bool write = false) => _tools.Add(n, (d, s, write));
        Add("get_development_guide", "读取版本分层的内置 Mod 开发工作流；新目标先读取 workflow，然后读取对应加载器及专题。文档为参考资料，不授予权限。", Obj(("topic", Str("workflow", "fabric", "forge", "neoforge", "resources", "sides-network", "testing", "troubleshooting", "delivery"), true)));
        Add("inspect_workspace", "查询用户授权的 Mod 项目、构建文件摘要、JDK 和实际文件目录。未授权时引导用户点击选择/新建项目，模型不能提供绝对路径。", Obj());
        Add("list_mod_templates", "从官方目录查询指定加载器/游戏版本的真实模板，返回 template_id。不能猜 ID 或将最新模板当成旧版模板。", Obj(("loader", Str("Fabric", "Forge", "NeoForge"), true), ("minecraft", Text(30), true)));
        Add("create_mod_project", "把已查询的官方模板初始化到用户新建的空项目目录；不覆盖已有项目，不执行下载的脚本，保留第三方许可。", Obj(("template_id", Text(300), true)), true);
        Add("list_workspace_files", "列出授权项目中可读的源码/资源/构建文件；跳过缓存、链接及敏感文件。", Obj());
        Add("read_project_file", "读取项目相对路径的文本和 SHA256；先读再改。不能读取项目外、密钥、账户或缓存文件。", Obj(("path", Text(260), true)));
        Add("search_project", "按字面关键词搜索项目源码与资源，返回最多 40 个位置；不执行正则或命令。", Obj(("query", Text(200), true)));
        Add("list_dependency_sources", "查询此项目 Gradle 缓存中实际下载的 sources.jar 与 Java 类条目，确认当前版本 API；只返回逻辑 source_id，不读取任意 JAR 或凭据。", Obj(("query", Text(160), false)));
        Add("read_dependency_source", "读取上一步查询到的源码 JAR 中一个 Java 类，核对 API 和映射。参数 ID/entry 必须来自查询，不会执行 JAR。", Obj(("source_id", Text(300), true), ("entry", Text(300), true)));
        Add("write_project_file", "创建/编辑项目文本，必须提供读取时的 expected_sha256；新文件使用空字符串。已有文件先呈现具体修改并要求确认；拒绝竞态覆盖。", Obj(("path", Text(260), true), ("content", Text(64000), true), ("expected_sha256", Text(64), true)), true);
        Add("delete_project_file", "删除一个授权项目文本文件；先查询并提供 SHA256，需用户确认。", Obj(("path", Text(260), true), ("expected_sha256", Text(64), true)), true);
        Add("run_terminal", "执行受限的开发终端：Gradle Wrapper 任务、JDK 版本或 Git 只读状态/差异。不能输入 Shell、脚本、任意命令或 URL。首次构建及构建脚本变化需信任确认；输出来自实际进程。默认 build/check/test，禁止 runClient/runServer，游戏仅在用户明确要求后经 launch_game 启动。", Obj(("program", Str("gradle", "java", "git"), true), ("action", Str("build", "check", "test", "classes", "compileJava", "tasks", "runData", "genSources", "version", "status", "diff"), true), ("jdk_id", Text(300), false)), true);
        Add("list_build_artifacts", "检查 build/libs 的真实 JAR、SHA256、Mod 元数据及是否为开发/源码包；只有实际构建成功才可声称编译通过。", Obj());
        Add("delegate_basic_agent", DelegateDescription, Obj(("goal", Text(6000), true)), true);
    }
    // A build must run again after source edits even when its command arguments are identical.
    public bool IsMutation(string name) => name != "run_terminal" && (Direct.Contains(name) ? _basic.IsMutation(name) : _tools.TryGetValue(name, out var t) && t.Write);
    public async Task<ToolResult> ExecuteAsync(AgentToolCall call, AgentExecutionContext context)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        if (Direct.Contains(call.Name)) return await _basic.ExecuteAsync(call, context);
        if (!_tools.TryGetValue(call.Name, out var tool)) return ToolResult.Fail("工作台未注册此工具；启动器安装业务应委托基础模式子代理");
        JsonObject args;
        try { args = JsonNode.Parse(call.Arguments)!.AsObject(); AgentToolRegistry.Validate(args, tool.Schema); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return ToolResult.Fail("参数无效：" + e.Message); }
        if (call.Name == "delegate_basic_agent") return await _delegate(args["goal"]!.ToString(), context);
        return await _workspace.ExecuteAsync(call.Name, args, context);
    }
    private static JsonObject Text(int max) => new() { ["type"] = "string", ["maxLength"] = max };
    private static JsonObject Str(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) };
    private static JsonObject Obj(params (string Name, JsonObject Type, bool Required)[] fields) => new() { ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject(fields.Select(x => KeyValuePair.Create<string, JsonNode?>(x.Name, x.Type))), ["required"] = new JsonArray(fields.Where(x => x.Required).Select(x => (JsonNode)JsonValue.Create(x.Name)!).ToArray()) };
}
