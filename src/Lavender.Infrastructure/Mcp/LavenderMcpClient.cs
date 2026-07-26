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
        _tools ??= await _client!.ListToolsAsync(cancellationToken: cancellationToken);

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
        await CallToolAsync(
            "lavender_index_project",
            JsonSerializer.Serialize(new { projectPath, solutionPath }),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
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
