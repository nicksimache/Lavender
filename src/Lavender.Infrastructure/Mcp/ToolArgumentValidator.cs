using System.Text.Json;
using Lavender.Infrastructure.FileSystem;

namespace Lavender.Infrastructure.Mcp;

/// <summary>Checks required fields/types before MCP binding can return an opaque invocation failure.</summary>
internal static class ToolArgumentValidator
{
    public static void Validate(JsonElement arguments, JsonElement schema, string location = "arguments")
    {
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            bool valid = type.GetString() switch
            {
                "object" => arguments.ValueKind == JsonValueKind.Object,
                "array" => arguments.ValueKind == JsonValueKind.Array,
                "string" => arguments.ValueKind == JsonValueKind.String,
                "integer" => arguments.ValueKind == JsonValueKind.Number && arguments.TryGetInt64(out _),
                "number" => arguments.ValueKind == JsonValueKind.Number,
                "boolean" => arguments.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => arguments.ValueKind == JsonValueKind.Null,
                _ => true
            };
            if (!valid) throw new ArgumentException($"{location} must be {type.GetString()}. No tool executed.");
        }
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!arguments.TryGetProperty(field.GetString()!, out var value) || value.ValueKind == JsonValueKind.Null)
                        throw new ArgumentException($"Missing required {location}.{field.GetString()}. For edits, use expectedContentHash from a current successful read. No tool executed.");
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in arguments.EnumerateObject())
                    if (properties.TryGetProperty(property.Name, out var child)) Validate(property.Value, child, location + "." + property.Name);
        }
        if (arguments.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            int index = 0;
            foreach (var item in arguments.EnumerateArray()) Validate(item, items, $"{location}[{index++}]");
        }
    }

    public static void ValidateSource(string name, JsonElement arguments)
    {
        if (name is not ("lavender_write_lines" or "lavender_apply_edits" or "lavender_create_file")) return;
        if (arguments.TryGetProperty("replacementCode", out var replacement)) SourceWriteGuard.Validate(replacement.GetString()!);
        if (arguments.TryGetProperty("content", out var content)) SourceWriteGuard.Validate(content.GetString()!);
        if (arguments.TryGetProperty("edits", out var edits))
            foreach (var edit in edits.EnumerateArray())
                if (edit.TryGetProperty("replacementCode", out var code)) SourceWriteGuard.Validate(code.GetString()!);
    }
}
