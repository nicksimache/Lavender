using System.Text.Json;
using System.Text.Json.Serialization;
using Effort = Lavender.Application.Agent.ReasoningEffort;

namespace Lavender.Application.Agent;

public sealed class AgentSettings
{
    public string Model { get; init; } = "gpt-6-sol";
    public List<AgentModelOption> Models { get; init; } =
    [
        new() { Label = "GPT-4.1 mini", Id = "gpt-4.1-mini" },
        new() { Label = "GPT-5", Id = "gpt-5" },
        new() { Label = "GPT-6 Sol", Id = "gpt-6-sol", ReasoningEffort = ReasoningEffort.None },
        new() { Label = "GPT-6.1 Sol", Id = "gpt-6.1-sol" },
        new() { Label = "GPT-6 Luna", Id = "gpt-6-luna", ReasoningEffort = ReasoningEffort.None },
        new() { Label = "GPT-6 Astra", Id = "gpt-6-astra" },
    ];
    public int MaxIterations { get; init; } = 100;
    public int MaxToolCalls { get; init; } = 300;
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

        if (Models is null || Models.Count == 0 || Models.Any(model =>
            string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Label) ||
            string.IsNullOrWhiteSpace(model.ApiKeyEnvironmentVariable)) ||
            Models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() != Models.Count ||
            !Models.Any(model => model.Id == Model))
        {
            throw new InvalidOperationException("Agent:Models must contain unique model IDs, labels, key environment-variable names, and the default Agent:Model.");
        }

        if (MaxIterations < 1 || MaxToolCalls < 1 || MaxConversationTurns < 1)
        {
            throw new InvalidOperationException("Agent limits must be greater than zero.");
        }

        foreach (AgentModelOption model in Models)
            model.ValidateReasoning();

        if (ToolTimeoutSeconds < 1 || MaxToolResultCharacters < 1000)
        {
            throw new InvalidOperationException("Agent timeout and tool-result limits are invalid.");
        }
    }
}

public sealed class AgentModelOption
{
    public string Label { get; init; } = "";
    public string Id { get; init; } = "";
    public string ApiKeyEnvironmentVariable { get; init; } = "OPENAI_API_KEY";
    public ReasoningEffort? ReasoningEffort { get; set; }

    // Null omits the parameter and uses the model default.
    [JsonIgnore]
    public IReadOnlyList<ReasoningEffort?> AllowedReasoningLevels => Id switch
    {
        "gpt-5" => [null, Effort.Minimal, Effort.Low, Effort.Medium, Effort.High],
        "gpt-6-sol" or "gpt-6-luna" =>
            [null, Effort.None, Effort.Low, Effort.Medium, Effort.High, Effort.XHigh, Effort.Max],
        "gpt-6-astra" or "gpt-6.1-sol" =>
            [null, Effort.Low, Effort.Medium, Effort.High, Effort.XHigh, Effort.Max],
        _ => [null]
    };

    [JsonIgnore]
    public string ReasoningHint => Id switch
    {
        "gpt-5" => "Higher reasoning effort can take longer and use more tokens. Default lets the model decide.",
        "gpt-6-sol" or "gpt-6-luna" => "Default uses the model's medium effort. None is fastest; higher levels use more time and tokens.",
        "gpt-6-astra" or "gpt-6.1-sol" => "Default uses the model's reasoning setting. Higher levels use more time and tokens.",
        "gpt-4.1-mini" => "This model does not support adjustable reasoning effort.",
        _ => "Reasoning controls are not configured for this model. The API default is used."
    };

    public void ValidateReasoning()
    {
        if (!AllowedReasoningLevels.Contains(ReasoningEffort))
            throw new InvalidOperationException($"Invalid reasoning effort for {Label}. {ReasoningHint}");
    }
}

[JsonConverter(typeof(ReasoningEffortJsonConverter))]
public enum ReasoningEffort
{
    None,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max
}

public sealed class ReasoningEffortJsonConverter : JsonConverter<ReasoningEffort>
{
    public override ReasoningEffort Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? name = reader.GetString();
            if (Enum.GetNames<ReasoningEffort>().Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)))
                return Enum.Parse<ReasoningEffort>(name!, ignoreCase: true);
        }
        throw new JsonException("ReasoningEffort must be a named level: none, minimal, low, medium, high, xhigh, or max.");
    }

    public override void Write(Utf8JsonWriter writer, ReasoningEffort value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value))
            throw new JsonException("Unknown reasoning effort.");
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}

