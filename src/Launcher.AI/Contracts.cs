using System.Text.Json.Nodes;
using Launcher.Core;

namespace Launcher.AI;

public sealed record AgentModel(string Id, string Name, int ContextWindow = 131072, int MaxOutputTokens = 16384, IReadOnlyList<string>? Efforts = null)
{ public bool HasVerifiedContextWindow { get; init; } = true; public override string ToString() => Name; }
public sealed record AgentReference(string Kind, string Id, string Name, string Detail)
{
    public string KindLabel => Kind switch { "game" => "游戏", "java" => "Java", "account" => "账户", _ => "引用" };
}
public sealed record AgentCompaction(string Summary, int OutputTokens = 0, int InputTokens = 0);
public sealed record AgentToolCall(string Id, string Name, string Arguments);
public sealed record AgentResponse(JsonArray Output, IReadOnlyList<AgentToolCall> Calls, int OutputTokens, int InputTokens);
public sealed record ToolResult(bool Success, string Summary, object? Data = null)
{ public static ToolResult Ok(string summary, object? data = null) => new(true, summary, data); public static ToolResult Fail(string reason) => new(false, reason); }
public sealed record AgentOption(string Label, string Description, bool Recommended = false);
public sealed record AgentQuestion(string Id, string Prompt, IReadOnlyList<AgentOption> Options);
public sealed record AgentApproval(string Title, string Detail);
public enum AgentUiEventKind { User, Assistant, Status, Operation, Question, Approval, Error, Interrupted, ToolStarted, ToolCompleted, Context, Compaction }
public enum AgentToolState { Running, Completed, Failed, Cancelled }
public sealed record AgentUiEvent(AgentUiEventKind Kind, string Title, string Text = "", string? Id = null, double? Percent = null, string? Detail = null)
{
    public DownloadTaskInfo? TaskProgress { get; init; }
    public string? ToolCallId { get; init; }
    public AgentToolState? ToolState { get; init; }
    public AgentContextUsage? ContextUsage { get; init; }
}

public interface IDeepSeekClient
{
    Task<AgentResponse> RespondWithInstructionsAsync(AgentModel model, string effort, JsonArray history, JsonArray tools, string instructions, Action<string> textDelta, CancellationToken cancellation)
        => RespondAsync(model, effort, history, tools, textDelta, cancellation);
    Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool refresh, CancellationToken cancellation);
    Task<AgentResponse> RespondAsync(AgentModel model, string effort, JsonArray history, JsonArray tools, Action<string> textDelta, CancellationToken cancellation);
    Task<AgentCompaction> CompactAsync(AgentModel model, string effort, JsonArray history, CancellationToken cancellation) => throw new NotSupportedException("此客户端不支持上下文压缩");
}
public interface IAgentInteraction
{
    bool FullAccess => false;
    Task<IReadOnlyDictionary<string, string>> AskAsync(IReadOnlyList<AgentQuestion> questions, CancellationToken cancellation);
    Task<bool> ApproveAsync(AgentApproval approval, CancellationToken cancellation);
}
public sealed record AgentExecutionContext(Guid GroupId, IAgentInteraction Interaction, Action<AgentUiEvent> Emit, CancellationToken Cancellation)
{ public AgentModelBudget? ModelBudget { get; init; } }
public interface ILauncherOperations
{
    IReadOnlyList<AgentReference> GetReferences() => [];
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
    string Goal { get; }
    AgentContextUsage ContextUsage { get; }
    event Action<AgentUiEvent>? Event;
    Task SendAsync(string text, AgentModel model, string effort, CancellationToken cancellation = default, IReadOnlyList<AgentReference>? references = null);
    Task CompactAsync(AgentModel model, string effort, CancellationToken cancellation = default);
    void RequestCompaction();
    void SetGoal(string goal);
    void SetModel(AgentModel model);
    Task StopAsync();
    void Clear();
}
