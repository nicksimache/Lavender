using System.Text.Json;

namespace Lavender.Application.Agent;

public sealed class AgentSettings
{
    public string Model { get; init; } = "gpt-4.1-mini";
    public int MaxIterations { get; init; } = 8;
    public int MaxToolCalls { get; init; } = 16;
    public int MaxParallelToolCalls { get; init; } = 4;
    public int ToolTimeoutSeconds { get; init; } = 30;
    public int MaxToolResultCharacters { get; init; } = 50_000;
    public int MaxConversationTurns { get; init; } = 10;
    public bool PersistHistory { get; init; } = true;
    public string HistoryDirectory { get; init; } = "Data/Conversations";
    public bool StopOnRepeatedToolCall { get; init; } = true;

    public static AgentSettings Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new AgentSettings();
        }

        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("Agent", out JsonElement section))
        {
            return new AgentSettings();
        }

        return section.Deserialize<AgentSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new AgentSettings();
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("Agent:Model must be configured.");
        }

        if (MaxIterations < 1 || MaxToolCalls < 1 || MaxParallelToolCalls < 1)
        {
            throw new InvalidOperationException("Agent limits must be greater than zero.");
        }

        if (ToolTimeoutSeconds < 1 || MaxToolResultCharacters < 1000)
        {
            throw new InvalidOperationException("Agent timeout and tool-result limits are invalid.");
        }
    }
}
