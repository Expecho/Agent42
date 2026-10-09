using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson.Exceptions;

namespace Agent42.Agents;

/// <summary>
/// The shared-state rules (ADR 0089), with no grain: apply a JSON Patch (RFC 6902) the model wrote, or accept a
/// whole object the user edited, and enforce the caps either way (a JSON object at the top, at most
/// <see cref="SharedStateLimits.MaxDepth"/> deep and <see cref="SharedStateLimits.MaxBytes"/> serialized).
///
/// <para><b>A patch is all-or-nothing, and the library does not give that by itself.</b> Measured on
/// <c>Microsoft.AspNetCore.JsonPatch.SystemTextJson</c> 10.0.12: a <c>test</c> op that fails AFTER a
/// <c>replace</c> leaves the replace applied to the target. So the ops run against a copy freshly parsed from the
/// stored text, and the copy is adopted only when every op and every cap passed.</para>
///
/// <para>An empty object is stored as <c>null</c>, so "the conversation has no shared state" has one spelling.</para>
/// </summary>
public static class SharedStatePatch
{
    /// <summary>The new state (null = empty), or an error the caller hands back as-is.</summary>
    public sealed record Outcome(string? Json, string? Error)
    {
        public bool Succeeded => Error is null;
    }

    /// <summary>Applies <paramref name="operations"/> (a JSON array of RFC 6902 ops) to <paramref name="currentJson"/>.</summary>
    public static Outcome Apply(string? currentJson, JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array)
        {
            return Fail("operations must be a JSON array of JSON Patch operations.");
        }

        if (operations.GetArrayLength() > SharedStateLimits.MaxOperations)
        {
            return Fail($"At most {SharedStateLimits.MaxOperations} operations per call.");
        }

        // Checked here because the library does not: an op it does not know is silently SKIPPED (measured, 10.0.12),
        // which would report success for a patch that did nothing.
        if (operations.EnumerateArray().FirstOrDefault(op => !IsKnownOperation(op)) is { ValueKind: not JsonValueKind.Undefined } unknown)
        {
            return Fail($"Not a supported operation (nothing changed): {unknown.GetRawText()}. Use one of: "
                + string.Join(", ", KnownOperations) + ".");
        }

        var target = Parse(currentJson);
        try
        {
            var patch = operations.Deserialize<JsonPatchDocument>()
                ?? throw new JsonException("no operations");
            patch.ApplyTo(target);
        }
        catch (Exception ex) when (ex is JsonPatchException or JsonException or InvalidOperationException
            or ArgumentException or NotSupportedException)
        {
            return Fail($"The patch was not applied (nothing changed): {ex.Message}");
        }

        return Validate(target);
    }

    /// <summary>Accepts a whole new state from the user, if it is a JSON object within the caps.</summary>
    public static Outcome Replace(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return Fail($"Not valid JSON: {ex.Message}");
        }

        return node is JsonObject root ? Validate(root) : Fail("The state must be a JSON object.");
    }

    private static Outcome Validate(JsonObject root)
    {
        if (Depth(root) > SharedStateLimits.MaxDepth)
        {
            return Fail($"The state may be at most {SharedStateLimits.MaxDepth} levels deep.");
        }

        var json = root.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > SharedStateLimits.MaxBytes)
        {
            return Fail($"The state may be at most {SharedStateLimits.MaxBytes / 1024} KB; remove something first.");
        }

        return new Outcome(root.Count == 0 ? null : json, null);
    }

    // A fresh, detached object to patch: the stored text parsed anew, so the caller's state is never the target.
    private static JsonObject Parse(string? json)
        => json is { Length: > 0 } && JsonNode.Parse(json) is JsonObject root ? root : [];

    private static int Depth(JsonNode? node) => node switch
    {
        JsonObject obj => 1 + obj.Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max(),
        JsonArray array => 1 + array.Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    private static Outcome Fail(string error) => new(null, error);

    private static readonly string[] KnownOperations = ["add", "remove", "replace", "move", "test"];

    private static bool IsKnownOperation(JsonElement op)
        => op.ValueKind == JsonValueKind.Object
            && op.TryGetProperty("op", out var name) && name.ValueKind == JsonValueKind.String
            && KnownOperations.Contains(name.GetString(), StringComparer.Ordinal)
            && op.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String;
}
