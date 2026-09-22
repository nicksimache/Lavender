using ModelContextProtocol.Client;
using System.Text.Json;

namespace Lavender.Infrastructure.Mcp;

public sealed record McpToolDefinition(
    string Name,
    string Description,
    BinaryData JsonSchema);

public sealed class LavenderMcpClient : IAsyncDisposable
{
    private readonly string _workingDirectory;
    private McpClient? _client;
    private IList<McpClientTool>? _tools;

    public LavenderMcpClient(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            return;
        }

        string project = Path.Combine(
            _workingDirectory, "Lavender.McpServer", "Lavender.McpServer.csproj");
        if (!File.Exists(project))
        {
            throw new FileNotFoundException(
                "Could not locate the Lavender MCP server project.", project);
        }

        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = "Lavender C# Analysis",
            Command = "dotnet",
            Arguments = ["run", "--no-build", "--project", project],
            WorkingDirectory = _workingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables()
        });

        _client = await McpClient.CreateAsync(
            transport, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        _tools = await _client!.ListToolsAsync(cancellationToken: cancellationToken);

        return _tools.Select(tool => new McpToolDefinition(
            tool.Name,
            tool.Description ?? string.Empty,
            BinaryData.FromString(tool.JsonSchema.GetRawText())))
            .ToArray();
    }

    public async Task<string> CallToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        _tools ??= await _client!.ListToolsAsync(cancellationToken: cancellationToken);

        McpClientTool tool = _tools.FirstOrDefault(
            candidate => candidate.Name.Equals(toolName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown MCP tool: {toolName}");

        Dictionary<string, object?> arguments =
            JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];

        object result = await tool.CallAsync(
            arguments, cancellationToken: cancellationToken);
        return JsonSerializer.Serialize(result);
    }

    public async Task IndexProjectAsync(
        string projectPath,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        string resultJson = await CallToolAsync(
            "lavender_index_project",
            JsonSerializer.Serialize(new { projectPath, solutionPath }),
            cancellationToken);

        using JsonDocument result = JsonDocument.Parse(resultJson);
        JsonElement root = result.RootElement;
        if (root.TryGetProperty("isError", out JsonElement isError) &&
            isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException($"The indexing tool failed: {resultJson}");
        }

        if (root.TryGetProperty("structuredContent", out JsonElement structured) &&
            ValidateIndexResponse(structured))
        {
            return;
        }

        if (root.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("text", out JsonElement text) ||
                    text.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                try
                {
                    using JsonDocument payload = JsonDocument.Parse(text.GetString()!);
                    if (ValidateIndexResponse(payload.RootElement))
                    {
                        return;
                    }
                }
                catch (JsonException)
                {
                    // A text block may contain an explanation instead of JSON.
                }
            }
        }

        throw new InvalidOperationException(
            "The indexing tool did not confirm successful indexing.");
    }

    private static bool ValidateIndexResponse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !(payload.TryGetProperty("success", out JsonElement success) ||
              payload.TryGetProperty("Success", out success)))
        {
            return false;
        }

        if (success.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        string? message = null;
        if ((payload.TryGetProperty("message", out JsonElement detail) ||
             payload.TryGetProperty("Message", out detail)) &&
            detail.ValueKind == JsonValueKind.String)
        {
            message = detail.GetString();
        }

        throw new InvalidOperationException(message ?? "Project indexing failed.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
            _tools = null;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            await ConnectAsync(cancellationToken);
        }
    }
}
