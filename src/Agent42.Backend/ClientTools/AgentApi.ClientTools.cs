using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The client-tool routes (ADR 0089): the browser posts the result of a tool it ran for the model, and a browser
/// whose stream died asks which calls are still waiting for it (ADR 0046 recovery). Both stay behind Easy Auth and
/// reach the grain through <see cref="AgentKey"/>, so a call id from another user's conversation is unreachable.
/// </summary>
public static partial class AgentApi
{
    private static void MapClientToolEndpoints(IEndpointRouteBuilder app)
    {
        // 204 when a waiting call took the result, 404 when none did (unknown id, already answered, timed out or
        // stopped). The browser treats the 404 as "too late" and stays quiet about it.
        app.MapPost("/api/chat/{conversationId}/tool-result", PostClientToolResultAsync);

        app.MapGet("/api/chat/{conversationId}/pending-tool-calls", async (string conversationId, IGrainFactory grains, HttpContext http) =>
            Results.Json(await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId))
                .GetPendingClientToolCallsAsync()));
    }

    // Read by hand rather than bound, so an oversized body is refused (413) before it is buffered whole: the result
    // is capped again in the grain, and nothing larger than that cap is worth reading.
    private static async Task<IResult> PostClientToolResultAsync(string conversationId, IGrainFactory grains, HttpContext http)
    {
        var body = await ReadCappedBodyAsync(http.Request, ClientToolLimits.MaxResultRequestBytes);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (ParseToolResult(body) is not var (callId, result))
        {
            return Results.BadRequest("Expected {\"toolCallId\": \"...\", \"result\": ...}.");
        }

        var accepted = await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId))
            .SubmitClientToolResultAsync(callId, result);
        return accepted ? Results.NoContent() : Results.NotFound();
    }

    // The body, or null when it is longer than maxBytes.
    private static async Task<byte[]?> ReadCappedBodyAsync(HttpRequest request, int maxBytes)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    // The call id and the result as text: a JSON string's value, or any other JSON value's raw text (the browser
    // sends an object such as {"approved":true}). Null when the body is not that shape.
    private static (string CallId, string Result)? ParseToolResult(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("toolCallId", out var id) || id.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("result", out var result))
            {
                return null;
            }

            return (id.GetString()!, result.ValueKind == JsonValueKind.String ? result.GetString()! : result.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
