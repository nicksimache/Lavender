using Lavender.Infrastructure.Mcp;
using OpenAI.Chat;
using System.ClientModel;

namespace Lavender.Infrastructure.AI;

public sealed class OpenAIService
{
    private readonly ChatClient _client;

    public OpenAIService(string model)
    {
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("Missing OPENAI_API_KEY");
        _client = new ChatClient(model, apiKey);
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

    public async Task<ChatCompletion> CompleteAsync(
        IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ClientResult<ChatCompletion> result = await _client.CompleteChatAsync(
            messages, options, cancellationToken);
        return result.Value;
    }
}
