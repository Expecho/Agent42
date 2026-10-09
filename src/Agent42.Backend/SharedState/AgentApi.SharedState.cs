using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The shared-state routes (ADR 0089): the UI reads a conversation's state when it opens it, and writes the user's
/// edits back. Behind Easy Auth and keyed through <see cref="AgentKey"/>, like every conversation route.
///
/// <para>The wire shape is <c>{"state": {...} | null, "version": n}</c> out, and
/// <c>{"state": {...}, "baseVersion": n}</c> in. The state travels as a real JSON object, not a string inside
/// one; the grain stores it as text.</para>
/// </summary>
public static partial class AgentApi
{
    // A whole state is at most SharedStateLimits.MaxBytes; the envelope and escaping get the same again.
    private const int MaxSharedStateRequestBytes = 2 * SharedStateLimits.MaxBytes;

    private static void MapSharedStateEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/conversations/{conversationId}/state", async (string conversationId, IGrainFactory grains, HttpContext http) =>
        {
            var snapshot = await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetSharedStateAsync();
            return SharedStateJsonResult(snapshot.Json, snapshot.Version);
        });

        // 200 with the new version; 409 when the edit was based on a stale version (the UI re-fetches) or the
        // conversation has no saved record (incognito, or not yet saved); 400 for a state the caps refuse.
        app.MapPut("/api/conversations/{conversationId}/state", PutSharedStateAsync);
    }

    private static async Task<IResult> PutSharedStateAsync(string conversationId, IGrainFactory grains, HttpContext http)
    {
        var body = await ReadCappedBodyAsync(http.Request, MaxSharedStateRequestBytes);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (ParseStateEdit(body) is not var (state, baseVersion))
        {
            return Results.BadRequest("Expected {\"state\": {...}, \"baseVersion\": n}.");
        }

        var result = await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId))
            .SetSharedStateAsync(state, baseVersion);
        return result.Status switch
        {
            SharedStateWriteStatus.Ok => Results.Json(new { version = result.Version }),
            SharedStateWriteStatus.VersionConflict => Results.Conflict(new { error = "version conflict", version = result.Version }),
            SharedStateWriteStatus.NoSavedRecord => Results.Conflict(new { error = "conversation has no saved state" }),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    }

    private static (string State, long BaseVersion)? ParseStateEdit(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("state", out var state)
                && root.TryGetProperty("baseVersion", out var version)
                && version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out var baseVersion)
                ? (state.GetRawText(), baseVersion)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The stored text is spliced in as JSON rather than re-serialized as a string, so the client gets an object.
    private static IResult SharedStateJsonResult(string? json, long version)
        => Results.Text($$"""{"state":{{json ?? "null"}},"version":{{version}}}""", "application/json");
}
