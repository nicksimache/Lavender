using Lavender.Infrastructure.Mcp;
using Lavender.Application.Agent;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Lavender.Infrastructure.AI;

public sealed class OpenAIService
{
    private static readonly HttpClient TrackedHttpClient = new(new ApiTrackingHandler(new HttpClientHandler())) { Timeout = TimeSpan.FromMinutes(10) };
    private ChatClient? _client;
    private AgentModelOption _model;
    private readonly ResponsesApiClient _responses = new();
    public bool UsesResponsesForTools => _model.Id.StartsWith("gpt-6", StringComparison.Ordinal);

    public OpenAIService(string model)
        : this(new AgentModelOption { Id = model, Label = model })
    {
    }

    public OpenAIService(AgentModelOption model) => _model = model;

    // The UI only changes models between runs, preserving in-flight tool calls.
    public void SelectModel(AgentModelOption model)
    {
        model.ValidateReasoning();
        _model = model;
        _client = null;
    }

    public ChatCompletionOptions CreateToolOptions(IReadOnlyList<McpToolDefinition> tools)
    {
        ChatCompletionOptions options = new();
        foreach (McpToolDefinition tool in tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name, tool.Description, tool.JsonSchema));
        }
        return options;
    }

    public Task<ResponseTurn> CompleteResponseAsync(
        IReadOnlyList<JsonNode> input,
        IReadOnlyList<McpToolDefinition> tools,
        string instructions,
        CancellationToken cancellationToken = default) =>
        _responses.CompleteAsync(_model, input, tools, instructions, cancellationToken);

    public async Task<ChatCompletion> CompleteAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        using var tracking = ApiUsageTracker.BeginCall();
        ChatClient client = GetClient();
        ConfigureReasoning(options);
        ClientResult<ChatCompletion> result = await client.CompleteChatAsync(
            messages, options, cancellationToken);
        return result.Value;
    }

    public async Task<string> CompleteTextStreamingAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options,
        Action<string> onText,
        CancellationToken cancellationToken = default)
    {
        using var tracking = ApiUsageTracker.BeginCall();
        ChatClient client = GetClient();
        ConfigureReasoning(options);
        StringBuilder answer = new();
        string streamId = Guid.NewGuid().ToString("N");
        ChatTokenUsage? usage = null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (StreamingChatCompletionUpdate update in
                client.CompleteChatStreamingAsync(messages, options, cancellationToken))
            {
                if (update.Usage is not null) usage = update.Usage;
                foreach (ChatMessageContentPart part in update.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text))
                        continue;
                    answer.Append(part.Text);
                    onText(part.Text);
                }
            }
        }
        finally
        {
            ApiUsageTracker.Write(new { event_type = "stream_usage", stream_id = streamId,
                model = _model.Id, duration_ms = timer.Elapsed.TotalMilliseconds,
                usage = usage is null ? null : new {
                    input_tokens = usage.InputTokenCount, output_tokens = usage.OutputTokenCount,
                    total_tokens = usage.TotalTokenCount,
                    cached_tokens = usage.InputTokenDetails?.CachedTokenCount,
                    reasoning_tokens = usage.OutputTokenDetails?.ReasoningTokenCount } });
        }
        if (answer.Length > 0)
            return answer.ToString();
        const string fallback = "I could not produce a final response.";
        onText(fallback);
        return fallback;
    }

    private ChatClient GetClient()
    {
        if (_client is null)
        {
            string? apiKey = Environment.GetEnvironmentVariable(_model.ApiKeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException($"Set {_model.ApiKeyEnvironmentVariable} and restart Lavender to use {_model.Label}.");
            _client = new ChatClient(_model.Id, new ApiKeyCredential(apiKey), new OpenAI.OpenAIClientOptions { Transport = new HttpClientPipelineTransport(TrackedHttpClient) });
        }
        return _client;
    }

    private void ConfigureReasoning(ChatCompletionOptions options)
    {
        _model.ValidateReasoning();
#pragma warning disable OPENAI001 // SDK 2.11 marks the documented reasoning parameter experimental.
        options.ReasoningEffortLevel = _model.ReasoningEffort is ReasoningEffort effort
            ? new ChatReasoningEffortLevel(effort.ToString().ToLowerInvariant())
            : null;
#pragma warning restore OPENAI001
    }
}
