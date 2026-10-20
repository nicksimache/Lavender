using Lavender.Infrastructure.Mcp;
using Lavender.Application.Agent;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;

namespace Lavender.Infrastructure.AI;

public sealed class OpenAIService
{
    private ChatClient? _client;
    private AgentModelOption _model;

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
        if (tools.Count > 0 && _model.ToolCompatibilityError is string error)
            throw new InvalidOperationException(error);
        ChatCompletionOptions options = new();
        foreach (McpToolDefinition tool in tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name, tool.Description, tool.JsonSchema));
        }
        return options;
    }

    public async Task<ChatCompletion> CompleteAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
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
        ChatClient client = GetClient();
        ConfigureReasoning(options);
        StringBuilder answer = new();
        await foreach (StreamingChatCompletionUpdate update in
            client.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            foreach (ChatMessageContentPart part in update.ContentUpdate)
            {
                if (string.IsNullOrEmpty(part.Text))
                    continue;
                answer.Append(part.Text);
                onText(part.Text);
            }
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
            _client = new ChatClient(_model.Id, apiKey);
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
