using Lavender.Application.Agent;
using Lavender.Infrastructure.Mcp;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Lavender.Infrastructure.AI;

public sealed record ResponseToolCall(string CallId, string Name, string ArgumentsJson);
public sealed record ResponseTurn(
    string Text,
    IReadOnlyList<ResponseToolCall> ToolCalls,
    IReadOnlyList<JsonNode> OutputItems);

/// <summary>Stateless Responses requests for reasoning models with function tools.</summary>
public sealed class ResponsesApiClient
{
    private static readonly HttpClient SharedHttpClient = new(new ApiTrackingHandler(new HttpClientHandler()))
    {
        Timeout = TimeSpan.FromMinutes(10)
    };
    private readonly HttpClient _httpClient;

    public ResponsesApiClient(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? SharedHttpClient;

    public async Task<ResponseTurn> CompleteAsync(
        AgentModelOption model,
        IReadOnlyList<JsonNode> input,
        IReadOnlyList<McpToolDefinition> tools,
        string instructions,
        CancellationToken cancellationToken = default)
    {
        using var tracking = ApiUsageTracker.BeginCall();
        model.ValidateReasoning();
        string? apiKey = Environment.GetEnvironmentVariable(model.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"Set {model.ApiKeyEnvironmentVariable} and restart Lavender to use {model.Label}.");

        JsonObject requestBody = BuildRequest(model, input, tools, instructions);
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>()
                ?? response.ReasonPhrase ?? "Unknown API error";
            throw new InvalidOperationException(
                $"OpenAI Responses request failed ({(int)response.StatusCode}): {detail}");
        }
        return ParseResponse(body);
    }

    public static JsonObject BuildRequest(
        AgentModelOption model,
        IReadOnlyList<JsonNode> input,
        IReadOnlyList<McpToolDefinition> tools,
        string instructions)
    {
        JsonObject body = new()
        {
            ["model"] = model.Id,
            ["instructions"] = instructions,
            ["store"] = false,
            ["input"] = JsonSerializerNode(input)
        };
        if (model.ReasoningEffort is ReasoningEffort effort)
            body["reasoning"] = new JsonObject { ["effort"] = effort.ToString().ToLowerInvariant() };
        if (tools.Count > 0)
        {
            JsonArray definitions = new();
            foreach (McpToolDefinition tool in tools)
            {
                definitions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.JsonSchema.ToString()),
                    ["strict"] = false
                });
            }
            body["tools"] = definitions;
        }
        return body;
    }

    private static JsonArray JsonSerializerNode(IReadOnlyList<JsonNode> items)
    {
        JsonArray array = new();
        foreach (JsonNode item in items)
            array.Add(item.DeepClone());
        return array;
    }

    public static ResponseTurn ParseResponse(string json)
    {
        JsonNode root = JsonNode.Parse(json) ?? throw new InvalidOperationException("Empty Responses result.");
        string? status = root["status"]?.GetValue<string>();
        if (status is "failed" or "incomplete")
        {
            string reason = root["incomplete_details"]?["reason"]?.GetValue<string>()
                ?? root["error"]?["message"]?.GetValue<string>() ?? status;
            throw new InvalidOperationException($"OpenAI response {status}: {reason}");
        }

        List<JsonNode> output = [];
        List<ResponseToolCall> calls = [];
        List<string> text = [];
        foreach (JsonNode? item in root["output"]?.AsArray() ?? [])
        {
            if (item is null)
                continue;
            output.Add(item.DeepClone());
            string? type = item["type"]?.GetValue<string>();
            if (type == "function_call")
            {
                calls.Add(new ResponseToolCall(
                    item["call_id"]?.GetValue<string>() ?? throw new InvalidOperationException("Function call has no call_id."),
                    item["name"]?.GetValue<string>() ?? throw new InvalidOperationException("Function call has no name."),
                    item["arguments"]?.GetValue<string>() ?? "{}"));
            }
            else if (type == "message" && item["content"] is JsonArray content)
            {
                foreach (JsonNode? part in content)
                    if (part?["type"]?.GetValue<string>() == "output_text")
                        text.Add(part["text"]?.GetValue<string>() ?? "");
            }
        }
        return new ResponseTurn(string.Join("\n", text), calls, output);
    }

    public static JsonObject Message(string role, string content) =>
        new() { ["role"] = role, ["content"] = content };

    public static JsonObject FunctionOutput(string callId, string output) =>
        new() { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output };
}
