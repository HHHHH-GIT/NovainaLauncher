namespace Launcher.AI;

/// <summary>A real isolated ReAct session with the basic catalog, not an alias for a workbench operation.</summary>
public sealed class BasicAgentDelegate(IDeepSeekClient client, ILauncherOperations launcher)
{
    public async Task<ToolResult> RunAsync(string goal, AgentModel model, string effort, AgentExecutionContext context)
    {
        var instructions = AgentPrompt.System + (context.Interaction.FullAccess
            ? "\n当前宿主已由用户启用授权模式。覆盖默认逐项审批要求：本基础子代理处理已委托的启动器业务，无需再次询问操作确认。人工登录仍由用户完成；只使用当前基础工具目录，不修改开发项目或扩大子目标。"
            : "");
        var child = new AgentSessionService(client, new AgentToolRegistry(launcher, confirmLaunch: true), launcher, context.Interaction, instructions: instructions, sharedBudget: context.ModelBudget);
        var results = new List<string>(); string answer = ""; bool failed = false;
        child.Event += e =>
        {
            if (e.Kind == AgentUiEventKind.Assistant) answer = e.Text;
            if (e.Kind == AgentUiEventKind.Error || e.Kind == AgentUiEventKind.ToolCompleted && e.ToolState is AgentToolState.Failed or AgentToolState.Cancelled) failed = true;
            if (e.Kind == AgentUiEventKind.ToolCompleted) results.Add(e.Title + "：" + e.ToolState);
            if (e.Kind is AgentUiEventKind.ToolCompleted or AgentUiEventKind.Operation) context.Emit(new(AgentUiEventKind.Operation, "基础模式子代理", e.Title + (e.Text.Length > 0 ? " · " + e.Text : "")) { TaskProgress = e.TaskProgress });
        };
        try
        {
            await child.SendAsync("你是工作台委托的独立基础代理。只处理以下启动器子目标，不修改开发项目，未明确要求不启动游戏。先查询真实状态，保留人类登录" + (context.Interaction.FullAccess ? "。子目标：\n" : "和确认。子目标：\n") + goal, model, effort, context.Cancellation);
            context.Cancellation.ThrowIfCancellationRequested();
            var success = !failed && answer.Length > 0;
            return new(success, success ? "基础子代理已返回" : "基础子代理未完成，请检查返回状态", new { response = answer, operations = results, last_input_tokens = child.ContextUsage.LastInputTokens, last_output_tokens = child.ContextUsage.LastOutputTokens });
        }
        finally { await child.StopAsync(); }
    }
}
