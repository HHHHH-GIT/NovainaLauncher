using System.Text.Json.Nodes;
using Launcher.Core;

namespace Launcher.AI;

public sealed record AgentModel(string Id, string Name, int ContextWindow = 131072, int MaxOutputTokens = 16384, IReadOnlyList<string>? Efforts = null)
{ public override string ToString() => Name; }
public sealed record AgentToolCall(string Id, string Name, string Arguments);
public sealed record AgentResponse(JsonArray Output, IReadOnlyList<AgentToolCall> Calls, int OutputTokens, int InputTokens);
public sealed record ToolResult(bool Success, string Summary, object? Data = null)
{ public static ToolResult Ok(string summary, object? data = null) => new(true, summary, data); public static ToolResult Fail(string reason) => new(false, reason); }
public sealed record AgentOption(string Label, string Description, bool Recommended = false);
public sealed record AgentQuestion(string Id, string Prompt, IReadOnlyList<AgentOption> Options);
public sealed record AgentApproval(string Title, string Detail);
public enum AgentUiEventKind { User, Assistant, Status, Operation, Question, Approval, Error, Interrupted }
public sealed record AgentUiEvent(AgentUiEventKind Kind, string Title, string Text = "", string? Id = null, double? Percent = null, string? Detail = null)
{ public DownloadTaskInfo? TaskProgress { get; init; } }

public interface IDeepSeekClient
{
    Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool refresh, CancellationToken cancellation);
    Task<AgentResponse> RespondAsync(AgentModel model, string effort, JsonArray history, JsonArray tools, Action<string> textDelta, CancellationToken cancellation);
}
public interface IAgentInteraction
{
    Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<AgentQuestion> questions, CancellationToken cancellation);
    Task<bool> ApproveAsync(AgentApproval approval, CancellationToken cancellation);
}
public sealed record AgentExecutionContext(Guid GroupId, IAgentInteraction Interaction, Action<AgentUiEvent> Emit, CancellationToken Cancellation);
public interface ILauncherOperations
{
    Task<ToolResult> ExecuteAsync(string name, JsonObject arguments, AgentExecutionContext context);
    Task<AgentApproval> DescribeSensitiveAsync(string name, JsonObject arguments, CancellationToken cancellation);
    Task CancelGroupAsync(Guid groupId);
}
public interface IAgentToolRegistry
{
    JsonArray Definitions { get; }
    Task<ToolResult> ExecuteAsync(AgentToolCall call, AgentExecutionContext context);
    bool IsMutation(string name);
}
public interface IAgentSessionService
{
    bool IsRunning { get; }
    event Action<AgentUiEvent>? Event;
    Task SendAsync(string text, AgentModel model, string effort, CancellationToken cancellation = default);
    Task StopAsync();
    void Clear();
}
