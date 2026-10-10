using System.Text.Json;

namespace Agent42.Agents;

/// <summary>
/// Checks a browser's form answer against the schema the MCP server requested (ADR 0095) before anything reaches
/// the server. Elicitation schemas are flat objects of primitives: string (length bounds, enum), number or integer
/// (range), boolean, and an array of enum strings (multi-select). Each key must be a declared property of the right
/// type, every <c>required</c> key must be present, and an enum value must be one of the listed members. Anything
/// else fails, and the caller answers <c>cancel</c>: partly invalid data is never forwarded.
/// </summary>
public static class McpElicitationContent
{
    /// <summary>True when <paramref name="contentJson"/> satisfies <paramref name="schemaJson"/>.</summary>
    public static bool IsValid(string? schemaJson, string? contentJson)
    {
        try
        {
            using var schemaDoc = JsonDocument.Parse(schemaJson ?? "{}");
            using var contentDoc = JsonDocument.Parse(contentJson ?? "{}");
            var schema = schemaDoc.RootElement;
            var content = contentDoc.RootElement;
            if (content.ValueKind != JsonValueKind.Object || schema.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
                ? p
                : default;
            return HasEveryRequired(schema, content)
                && content.EnumerateObject().All(field =>
                    properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty(field.Name, out var definition)
                    && MatchesDefinition(definition, field.Value));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasEveryRequired(JsonElement schema, JsonElement content)
        => !schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array
            || required.EnumerateArray().All(r =>
                r.ValueKind == JsonValueKind.String && content.TryGetProperty(r.GetString()!, out _));

    private static bool MatchesDefinition(JsonElement definition, JsonElement value)
        => ReadString(definition, "type") switch
        {
            "string" => value.ValueKind == JsonValueKind.String && MatchesString(definition, value.GetString()!),
            "number" => value.ValueKind == JsonValueKind.Number && InRange(definition, value.GetDouble()),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var whole) && InRange(definition, whole),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array && MatchesMultiSelect(definition, value),
            _ => false,
        };

    private static bool MatchesString(JsonElement definition, string text)
    {
        if (ReadNumber(definition, "minLength") is { } min && text.Length < min)
        {
            return false;
        }

        if (ReadNumber(definition, "maxLength") is { } max && text.Length > max)
        {
            return false;
        }

        return EnumMembers(definition) is not { } members || members.Contains(text);
    }

    private static bool InRange(JsonElement definition, double number)
        => !(ReadNumber(definition, "minimum") is { } min && number < min)
            && !(ReadNumber(definition, "maximum") is { } max && number > max);

    private static bool MatchesMultiSelect(JsonElement definition, JsonElement value)
    {
        var members = definition.TryGetProperty("items", out var items) ? EnumMembers(items) : null;
        return members is not null && value.EnumerateArray().All(v =>
            v.ValueKind == JsonValueKind.String && members.Contains(v.GetString()!));
    }

    // The allowed values of an enum-shaped definition: untitled or legacy "enum", or titled "oneOf"/"anyOf" consts.
    // Null when the definition is not an enum at all.
    private static HashSet<string>? EnumMembers(JsonElement definition)
    {
        if (definition.TryGetProperty("enum", out var plain) && plain.ValueKind == JsonValueKind.Array)
        {
            return [.. plain.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
        }

        foreach (var key in (string[])["oneOf", "anyOf"])
        {
            if (definition.TryGetProperty(key, out var titled) && titled.ValueKind == JsonValueKind.Array)
            {
                return [.. titled.EnumerateArray().Select(o => ReadString(o, "const")).OfType<string>()];
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? ReadNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
