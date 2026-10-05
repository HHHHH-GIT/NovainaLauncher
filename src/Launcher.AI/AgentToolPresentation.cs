using System.Text;
using System.Text.Json;

namespace Launcher.AI;

/// <summary>Human-readable, bounded tool results. Arguments, reasoning and credentials are never shown.</summary>
public static class AgentToolPresentation
{
    public static string Name(string name) => name switch
    {
        "get_launcher_state" => "查询启动器状态", "list_game_versions" => "查询游戏版本",
        "list_loaders" => "查询加载器", "list_optifine" => "查询 OptiFine",
        "search_projects" => "搜索模组与整合包", "list_project_files" => "查询文件版本",
        "install_game" => "安装游戏", "install_content" => "安装所选内容",
        "import_modpack" => "导入整合包", "export_modpack" => "导出整合包",
        "select_game" => "选择游戏", "select_account" => "选择账户",
        "retry_account_login" => "恢复账户登录", "open_account_login" => "打开账户登录",
        "list_content" => "查询游戏内容", "import_content" => "导入游戏内容",
        "export_save" => "导出存档", "toggle_content" => "调整内容状态",
        "delete_content" => "删除内容", "delete_game" => "删除游戏", "rename_game" => "更改游戏名称",
        "configure_memory" => "配置内存", "configure_java" => "选择 Java", "download_java" => "准备 Java",
        "read_game_logs" => "读取游戏日志", "get_tasks" => "查询下载任务", "launch_game" => "启动游戏",
        "get_development_guide" => "读取开发工作流", "inspect_workspace" => "检查项目与 JDK",
        "list_mod_templates" => "查询官方 Mod 模板", "create_mod_project" => "初始化 Mod 项目",
        "read_project_file" => "读取项目文件", "search_project" => "搜索项目源码",
        "list_workspace_files" => "列出项目文件",
        "list_dependency_sources" => "查询当前版本 API 源码", "read_dependency_source" => "读取依赖源码",
        "write_project_file" => "保存项目文件", "delete_project_file" => "删除项目文件",
        "run_terminal" => "运行开发终端", "list_build_artifacts" => "检查构建产物", "delegate_basic_agent" => "委托基础模式子代理",
        "execute_terminal" => "执行终端命令", "set_working_directory" => "设置工作目录",
        "list_files" => "查询文件目录", "read_file" => "读取文件", "write_file" => "保存文件",
        "delete_path" => "删除文件或目录", "create_directory" => "创建目录", "move_path" => "移动路径", "copy_file" => "复制文件",
        "import_modpack_file" => "导入整合包", "export_modpack_file" => "导出整合包",
        _ => "调用工具"
    };

    public static string Result(ToolResult result)
    {
        var text = new StringBuilder(result.Summary);
        if (result.Data is not null)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
            Append(document.RootElement, "", 0);
        }
        const int limit = 16000;
        return text.Length > limit ? text.ToString(0, limit) + "\n…结果已截短" : text.ToString();

        void Append(JsonElement value, string key, int depth)
        {
            if (depth > 6 || text.Length > limit || Hidden(key)) return;
            var label = Label(key);
            var prefix = new string(' ', depth * 2);
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (label.Length > 0) text.Append('\n').Append(prefix).Append(label).Append('：');
                    foreach (var property in value.EnumerateObject()) Append(property.Value, property.Name, depth + 1);
                    break;
                case JsonValueKind.Array:
                    if (label.Length > 0) text.Append('\n').Append(prefix).Append(label).Append('：');
                    int index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (++index > 30) { text.Append('\n').Append(prefix).Append("…另有 ").Append(value.GetArrayLength() - 30).Append(" 项"); break; }
                        if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) text.Append('\n').Append(prefix).Append("• ").Append(index);
                        Append(item, "", depth + 1);
                    }
                    break;
                case JsonValueKind.Null: break;
                default:
                    var content = value.ValueKind == JsonValueKind.True ? "是" : value.ValueKind == JsonValueKind.False ? "否" : value.ToString();
                    text.Append('\n').Append(prefix);
                    if (label.Length > 0) text.Append(label).Append('：');
                    text.Append(content);
                    break;
            }
        }
    }

    private static bool Hidden(string key)
    {
        var normalized = key.Replace("_", "").ToLowerInvariant();
        return normalized is "id" or "gameid" or "accountid" or "projectid" or "releaseid" or "javaid" or "taskid" or "nexttool"
            || normalized.Contains("token") || normalized.Contains("password") || normalized.Contains("secret") || normalized.Contains("apikey") || normalized.Contains("authorization");
    }
    private static string Label(string key) => key.ToLowerInvariant() switch
    {
        "name" => "名称", "minecraft" or "game_version" => "Minecraft", "loader" or "loaders" => "加载器",
        "loaderversion" => "加载器版本", "version" => "版本", "java" or "major" => "Java",
        "architecture" => "架构", "file" or "filename" => "文件", "files" => "文件版本",
        "games" => "本地游戏", "versions" => "版本列表", "accounts" => "账户", "javas" => "Java 环境",
        "memorymb" => "分配内存 (MB)", "availablememorymb" => "可用内存 (MB)", "smartmemory" => "智能内存",
        "type" => "类型", "total" => "总数", "current" => "当前选择", "valid" => "可用",
        "status" or "state" => "状态", "launchstate" => "游戏状态", "busy" => "任务进行中",
        "description" => "简介", "enabled" => "启用", "client" => "支持客户端",
        "text" or "logs" => "日志内容", "game" => "游戏", "directory" => "目录", "message" => "说明",
        _ => key
    };
}
