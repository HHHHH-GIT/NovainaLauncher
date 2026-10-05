using System.Globalization;
using System.Text.Json.Nodes;

namespace Launcher.AI;

public sealed record AgentContextCategory(string Name, long Tokens)
{ public string TokenLabel => AgentContextUsage.FormatTokens(Tokens); }
public sealed record AgentContextUsage(long Used, int Capacity, IReadOnlyList<AgentContextCategory> Categories)
{
    public bool IsEstimate { get; init; } = true;
    public bool CapacityVerified { get; init; }
    public bool IsCompacting { get; init; }
    public bool CompactionPending { get; init; }
    public int CompactionCount { get; init; }
    public long LastSavedTokens { get; init; }
    public int LastInputTokens { get; init; }
    public int LastOutputTokens { get; init; }
    public double Percent => Capacity <= 0 ? 0 : 100d * Used / Capacity;
    public string PercentLabel => $"{Percent:F0}% 已用";
    public string TotalLabel => $"已用{(IsEstimate ? "约" : "")} {FormatTokens(Used)} / {FormatTokens(Capacity)} tokens";
    public string MeasurementNote => "分类为互不重叠的本地估算；总量按最近一次 API usage 校准。";
    public string ApiUsageLabel => LastInputTokens > 0 ? $"最近请求：输入 {FormatTokens(LastInputTokens)} · 输出 {FormatTokens(LastOutputTokens)}" : "尚无 API 用量，当前为本地估算";
    public string CompactionLabel => IsCompacting ? "正在压缩，原上下文暂时保留" : CompactionPending ? "将在当前操作完整结束后压缩" : $"超过 45% 自动压缩 · 已压缩 {CompactionCount} 次";
    public string SavingsLabel => LastSavedTokens > 0 ? $"上次释放约 {FormatTokens(LastSavedTokens)} tokens" : "";
    public string CapacityNote => CapacityVerified ? "容量来自所选模型的官方元数据" : "容量为保守默认值；连接后刷新模型元数据";
    public static string FormatTokens(long value) => value >= 1000 ? (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "K" : value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Local attribution only. Provider usage calibrates the total, never pretends to expose exact per-source billing.</summary>
public static class AgentContextAccounting
{
    public const string SummaryPrefix = "【压缩的会话摘要】";
    public const string PinnedPrefix = "【当前会话目标与引用】";
    public static long Estimate(string text) => (long)Math.Ceiling(text.Count(c => c > 127) * 1.5 + text.Count(c => c <= 127) / 3d) + 16;
    public static long[] Measure(JsonArray history, JsonArray tools, string instructions = AgentPrompt.System)
    {
        var totals = new long[7];
        totals[0] = Estimate(instructions) + Estimate(tools.ToJsonString());
        var calls = history.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "function_call")
            .GroupBy(x => x["call_id"]!.ToString()).ToDictionary(g => g.Key, g => g.Last()["name"]?.ToString());
        foreach (var item in history.OfType<JsonObject>())
        {
            var size = Estimate(item.ToJsonString());
            var type = item["type"]?.ToString(); var content = item["content"]?.ToString() ?? "";
            int category = type is "function_call" or "function_call_output" ? 3 : item["role"]?.ToString() == "user" ? 1 : 2;
            if (content.StartsWith(SummaryPrefix, StringComparison.Ordinal)) category = 5;
            if (content.StartsWith(PinnedPrefix, StringComparison.Ordinal)) category = 6;
            if (type == "function_call_output" && calls.GetValueOrDefault(item["call_id"]!.ToString()) == "read_game_logs")
            {
                try
                {
                    var log = JsonNode.Parse(item["output"]!.ToString())?["Data"]?["log"]?.ToString();
                    if (log is not null) { var logSize = Math.Min(size, Estimate(log)); totals[4] += logSize; size -= logSize; }
                }
                catch (System.Text.Json.JsonException) { }
            }
            totals[category] += size;
        }
        return totals;
    }
    public static IReadOnlyList<AgentContextCategory> Categories(long[] sizes, long used)
    {
        string[] names = ["系统提示与工具定义", "用户输入", "模型输出与推理", "工具调用与结果", "日志内容", "压缩摘要", "目标与引用"];
        var total = sizes.Sum(); var counts = sizes.Select(size => total == 0 ? 0 : (long)Math.Floor(used * ((double)size / total))).ToArray();
        // Put rounding remainder in an actually occupied category, never invent usage for absent goals/logs.
        var largest = Array.IndexOf(sizes, sizes.Max()); counts[largest] += used - counts.Sum();
        return names.Select((name, index) => new AgentContextCategory(name, Math.Max(0, counts[index]))).ToArray();
    }
}
