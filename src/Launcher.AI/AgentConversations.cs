using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.Core;

namespace Launcher.AI;

public enum AgentMode { Basic, Workbench }
public sealed record AgentModeChoice(AgentMode Mode, string Name, string Description);
public sealed record AgentSessionSnapshot(JsonArray History, string Goal, IReadOnlyList<AgentReference> References, int Compactions)
{ public IReadOnlyList<JsonObject>? Checkpoints { get; init; } }
public sealed record AgentConversationEntry(Guid Id, AgentMode Mode, string Title, DateTimeOffset Updated);
public sealed record AgentTranscriptEntry(AgentUiEvent Event, IReadOnlyList<AgentAnswerRecord>? Answers = null, IReadOnlyList<AgentTranscriptEntry>? Tools = null);
public sealed record AgentAnswerRecord(string Prompt, string Answer, string Description);
public sealed record AgentConversation(AgentConversationEntry Entry, AgentSessionSnapshot Session, IReadOnlyList<AgentTranscriptEntry> Transcript, string Workspace = "");

/// <summary>Encrypted, atomically replaced snapshots. Protocol history is never written to application logs.</summary>
public sealed class AgentConversationStore(string? data = null)
{
    private string Root => Path.Combine(data ?? AppPaths.Data, "ai", "conversations");
    private string DirectoryFor(AgentMode mode) => Path.Combine(Root, mode == AgentMode.Workbench ? "workbench" : "basic");
    public IReadOnlyList<AgentConversationEntry> List(AgentMode mode)
    {
        var directory = DirectoryFor(mode); if (!Directory.Exists(directory)) return [];
        var entries = new List<AgentConversationEntry>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.protected"))
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id) && Load(mode, id) is { } conversation) entries.Add(conversation.Entry);
        return entries.OrderByDescending(x => x.Updated).ToArray();
    }
    public AgentConversation? Load(AgentMode mode, Guid id)
    {
        try
        {
            var path = Path.Combine(DirectoryFor(mode), id + ".protected");
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return null;
            var saved = new SecretStore(DirectoryFor(mode)).ReadJson<AgentConversation>(id.ToString());
            return saved?.Entry?.Id == id && saved.Entry.Mode == mode && saved.Session?.History is not null && saved.Session.References is not null && saved.Transcript is not null ? saved : null;
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException or UnauthorizedAccessException) { return null; }
    }
    public void Save(AgentConversation conversation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(conversation);
        if (bytes.Length > 32 * 1024 * 1024 - 4096) throw new IOException("会话过大，请新建对话；先前保存的记录仍保留");
        new SecretStore(DirectoryFor(conversation.Entry.Mode)).Write(conversation.Entry.Id.ToString(), bytes);
    }
    public void Delete(AgentMode mode, Guid id) => new SecretStore(DirectoryFor(mode)).Delete(id.ToString());
}

public sealed class AiConfiguration
{
    public string ModelId { get; set; } = "";
    public string ReasoningEffort { get; set; } = "high";
    public bool SetupCompleted { get; set; }
    public bool SidebarExpanded { get; set; } = true;
    public AgentMode LastMode { get; set; }
}
public sealed class AiConfigurationStore(string? data = null)
{
    private string FilePath => Path.Combine(data ?? AppPaths.Data, "ai", "settings.json");
    public AiConfiguration Load(LauncherSettings legacy)
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AiConfiguration>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new() { ModelId = legacy.AiModelId, ReasoningEffort = legacy.AiReasoningEffort, SetupCompleted = legacy.AiSetupCompleted };
    }
    public void Save(AiConfiguration configuration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(configuration)); File.Move(FilePath + ".tmp", FilePath, true);
    }
}
