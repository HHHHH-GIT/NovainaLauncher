#pragma warning disable OPENAI001, SCME0001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OpenAI.Responses;

namespace Launcher.AI;

/// <summary>Official endpoint only. SDK handles HTTP/SSE; JsonPatch preserves provider reasoning fields.</summary>
public sealed class DeepSeekClient(Func<string?> keyProvider, HttpClient? modelHttp = null, ResponsesClientOptions? sdkOptions = null) : IDeepSeekClient
{
    private readonly HttpClient _http = modelHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    private IReadOnlyList<AgentModel>? _models;
    private DateTimeOffset _modelsAt;
    private readonly SemaphoreSlim _modelGate = new(1);
    private string Key => keyProvider() is { Length: > 0 } key ? key : throw new InvalidOperationException("请先配置 DeepSeek API Key");
    public async Task<IReadOnlyList<AgentModel>> GetModelsAsync(bool refresh, CancellationToken cancellation)
    {
        await _modelGate.WaitAsync(cancellation);
        try
        {
            if (!refresh && _models is not null && DateTimeOffset.UtcNow - _modelsAt < TimeSpan.FromHours(1)) return _models;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            using var response = await _http.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"DeepSeek 连接失败（{(int)response.StatusCode}）");
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var result = (json?["data"]?.AsArray() ?? throw new InvalidDataException("模型列表格式异常"))
                .Select(x => new AgentModel(x!["id"]!.GetValue<string>(), x["name"]?.GetValue<string>() ?? x["id"]!.GetValue<string>(),
                    x["context_window"]?.GetValue<int>() ?? 131072, x["max_output_tokens"]?.GetValue<int>() ?? 16384,
                    x["effort"]?["supported_levels"]?.AsArray().Select(e => e!.GetValue<string>()).ToArray())
                    { HasVerifiedContextWindow = x["context_window"] is not null }).ToArray();
            if (result.Length == 0) throw new InvalidOperationException("未获取到可用模型");
            _models = result; _modelsAt = DateTimeOffset.UtcNow; return result;
        }
        finally { _modelGate.Release(); }
    }
    public Task<AgentResponse> RespondAsync(AgentModel model, string effort, JsonArray history, JsonArray tools, Action<string> textDelta, CancellationToken cancellation)
        => RespondCoreAsync(model, effort, history, tools, AgentPrompt.System, 16384, textDelta, cancellation);
    public Task<AgentResponse> RespondWithInstructionsAsync(AgentModel model, string effort, JsonArray history, JsonArray tools, string instructions, Action<string> textDelta, CancellationToken cancellation)
        => RespondCoreAsync(model, effort, history, tools, instructions, 16384, textDelta, cancellation);
    public async Task<AgentCompaction> CompactAsync(AgentModel model, string effort, JsonArray history, CancellationToken cancellation)
    {
        var response = await RespondCoreAsync(model, effort, history, [], AgentPrompt.Compaction, 8192, _ => { }, cancellation).ConfigureAwait(false);
        if (response.Calls.Count != 0) throw new InvalidDataException("压缩响应包含工具调用，未替换上下文");
        var text = string.Join("\n", response.Output.OfType<JsonObject>().Where(x => x["type"]?.ToString() == "message")
            .SelectMany(x => x["content"]?.AsArray() ?? []).Where(x => x?["type"]?.ToString() == "output_text").Select(x => x!["text"]?.ToString()));
        if (string.IsNullOrWhiteSpace(text) || text.Length > 24000) throw new InvalidDataException("压缩摘要无效，保留原上下文");
        return new(text, response.OutputTokens, response.InputTokens);
    }
    private async Task<AgentResponse> RespondCoreAsync(AgentModel model, string effort, JsonArray history, JsonArray tools, string instructions, int maxOutput, Action<string> textDelta, CancellationToken cancellation)
    {
        var options = sdkOptions ?? new ResponsesClientOptions { Endpoint = new Uri("https://api.deepseek.com"), RetryPolicy = new ClientRetryPolicy(0), NetworkTimeout = TimeSpan.FromMinutes(5) };
        var client = new ResponsesClient(new ApiKeyCredential(Key), options);
        var request = new CreateResponseOptions { Model = model.Id, Instructions = instructions,
            MaxOutputTokenCount = Math.Min(maxOutput, model.MaxOutputTokens), StoredOutputEnabled = false, StreamingEnabled = true };
        request.Patch.Set("$.input"u8, BinaryData.FromString(history.ToJsonString()));
        request.Patch.Set("$.tools"u8, BinaryData.FromString(tools.ToJsonString()));
        if (tools.Count == 0) request.Patch.Set("$.tool_choice"u8, BinaryData.FromString("\"none\""));
        if (model.Efforts is null || model.Efforts.Contains(effort))
            request.Patch.Set("$.reasoning"u8, BinaryData.FromString(new JsonObject { ["effort"] = effort }.ToJsonString()));
        AgentResponse? completed = null;
        await foreach (var update in client.CreateResponseStreamingAsync(request, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            var raw = JsonNode.Parse(ModelReaderWriter.Write(update).ToString())!;
            var type = raw["type"]?.GetValue<string>();
            if (type == "response.output_text.delta") textDelta(raw["delta"]?.GetValue<string>() ?? "");
            if (type == "response.completed") completed = ParseCompleted(raw);
            if (type is "response.incomplete" or "response.failed" or "error") throw new InvalidDataException("模型响应未完成，未执行本次工具调用");
        }
        return completed ?? throw new InvalidDataException("连接中断，未收到 response.completed；未执行本次工具调用");
    }
    public static AgentResponse ParseCompleted(JsonNode raw)
    {
        if (raw["type"]?.GetValue<string>() != "response.completed" || raw["response"]?["status"]?.GetValue<string>() != "completed")
            throw new InvalidDataException("响应未完成");
        var response = raw["response"]!;
        var output = response["output"]?.DeepClone().AsArray() ?? throw new InvalidDataException("缺少完整输出");
        var calls = new List<AgentToolCall>();
        foreach (var item in output.Where(x => x?["type"]?.GetValue<string>() == "function_call"))
        {
            if (item?["status"] is JsonValue status && status.GetValue<string>() != "completed") throw new InvalidDataException("工具参数生成未完成");
            var call = new AgentToolCall(item!["call_id"]?.GetValue<string>() ?? "", item["name"]?.GetValue<string>() ?? "", item["arguments"]?.GetValue<string>() ?? "");
            if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) || JsonNode.Parse(call.Arguments) is not JsonObject || calls.Any(x => x.Id == call.Id))
                throw new InvalidDataException("工具调用不完整或重复，未执行");
            calls.Add(call);
        }
        if (calls.Count > 32) throw new InvalidDataException("单次工具调用过多");
        return new(output, calls, response["usage"]?["output_tokens"]?.GetValue<int>() ?? 0, response["usage"]?["input_tokens"]?.GetValue<int>() ?? 0);
    }
}
