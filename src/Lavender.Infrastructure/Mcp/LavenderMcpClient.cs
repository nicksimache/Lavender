using ModelContextProtocol.Client;
using System.Text.Json;
using System.Diagnostics;
using Lavender.Infrastructure.Indexing;

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
    public string? LastIndexingSummary { get; private set; }

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

        // Visual Studio can rebuild the desktop without rebuilding this separate executable.
        // Build into its own output directory to avoid replacing the running desktop's DLLs.
        string outputDirectory = Path.Combine(_workingDirectory, "artifacts", "mcp-runtime");
        var buildInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "build", project, "--no-restore", "--nologo", "--verbosity", "quiet", "--output", outputDirectory })
            buildInfo.ArgumentList.Add(argument);
        using (Process build = Process.Start(buildInfo) ?? throw new InvalidOperationException("Could not build the MCP server."))
        {
            Task<string> stdout = build.StandardOutput.ReadToEndAsync();
            Task<string> stderr = build.StandardError.ReadToEndAsync();
            try { await build.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!build.HasExited) build.Kill(entireProcessTree: true);
                throw;
            }
            string diagnostics = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            if (build.ExitCode != 0)
                throw new InvalidOperationException($"MCP server build failed. Restore/rebuild the solution and retry.\n{diagnostics}");
        }
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["LAVENDER_INDEX_REPORT_DIRECTORY"] = IndexingTimings.ReportDirectory;
        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = "Lavender C# Analysis",
            Command = "dotnet",
            Arguments = [Path.Combine(outputDirectory, "Lavender.McpServer.dll")],
            WorkingDirectory = _workingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment
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

    private bool ValidateIndexResponse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !(payload.TryGetProperty("success", out JsonElement success) ||
              payload.TryGetProperty("Success", out success)))
        {
            return false;
        }

        if (success.ValueKind == JsonValueKind.True)
        {
            LastIndexingSummary = (payload.TryGetProperty("message", out JsonElement summary) ||
                payload.TryGetProperty("Message", out summary)) && summary.ValueKind == JsonValueKind.String
                ? summary.GetString() : null;
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
