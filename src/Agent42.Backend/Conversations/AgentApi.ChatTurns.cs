using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The resumable chat turn's endpoints (ADR 0153): rejoin a turn in flight after a dead socket, an F5 or on another device.
/// All of them are authenticated and key everything by <c>GetUserId()</c>, so a request can only ever reach its own turns:
/// they are NOT in the Easy Auth <c>excludedPaths</c> or the Entra public list. The runner is resolved optionally (the
/// <c>/api/speech</c> pattern), so a host without it answers 503 instead of breaking routing for every endpoint.
/// </summary>
public static partial class AgentApi
{
    private const string TurnIdHeader = "X-Turn-Id";
    private const string ReplayThroughHeader = "X-Turn-Replay-Through";

    private static void MapChatTurnEndpoints(IEndpointRouteBuilder app)
    {
        // The conversation's newest turn and its status, or 204. `running` means a page may rejoin it; anything else means the
        // history already owns the outcome. `ephemeral` only chooses WHICH store to look in (incognito turns live in memory, D6):
        // a wrong flag finds nothing, it can never reach another user's turn.
        app.MapGet("/api/chat/{conversationId}/turn", async (
            string conversationId, bool? ephemeral, IServiceProvider services, HttpContext http, CancellationToken ct) =>
        {
            if (services.GetService<ChatTurnRunner>() is not { Enabled: true } runner)
            {
                return ChatTurnsUnavailable();
            }

            return await runner.GetActiveAsync(http.GetUserId(), conversationId, ephemeral == true, ct) is { } turn
                ? Results.Json(turn, ChatStreamJson.Options)
                : Results.NoContent();
        });

        // Replays a turn from `after` (the last seq the page saw), then follows it live, as NDJSON like the POST. The replay-through
        // header is the highest seq at attach time: the page renders events up to it but only runs client tools from events above it.
        app.MapGet("/api/chat/{conversationId}/turns/{turnId}/stream", async (
            string conversationId, string turnId, long? after, bool? ephemeral, IServiceProvider services, HttpContext http) =>
        {
            if (services.GetService<ChatTurnRunner>() is not { Enabled: true } runner)
            {
                await ChatTurnsUnavailable().ExecuteAsync(http);
                return;
            }

            var userId = http.GetUserId();
            var incognito = ephemeral == true;
            if (!await runner.ExistsAsync(userId, conversationId, turnId, incognito, http.RequestAborted))
            {
                await Results.NotFound().ExecuteAsync(http);
                return;
            }

            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers[TurnIdHeader] = turnId;
            http.Response.Headers[ReplayThroughHeader] =
                (await runner.LastSeqAsync(userId, conversationId, turnId, incognito, http.RequestAborted)).ToString();
            var session = runner.Open(userId, conversationId, turnId, incognito, Math.Max(0, after ?? 0));
            // No abort here, unlike the POST: a reattach that ends without a `done` is how the page learns the turn failed, was
            // interrupted or was cut short, and it reads that as "go and recover from history" without a pointless retry.
            await NdjsonStreamWriter.WriteAsync(http.Response, session, runner.KeepAliveInterval, http.RequestAborted);
        });
    }

    // The originating POST: stream the turn from its first event through the pump's feed (ADR 0153).
    private static async Task StreamThroughPumpAsync(
        ChatTurnRunner runner, IAsyncEnumerable<AgentStreamEvent> stream, string userId, string conversationId, bool ephemeral, HttpContext http)
    {
        var started = await runner.StartAsync(userId, conversationId, ephemeral, stream);
        http.Response.Headers[TurnIdHeader] = started.TurnId;
        await NdjsonStreamWriter.WriteAsync(http.Response, started.Session, runner.KeepAliveInterval, http.RequestAborted);
        AbortUnlessClean(started.Session, http);
    }

    // What a stream that broke used to look like: the old endpoint's await foreach threw, Kestrel reset the response, and the page's
    // reader failed into history recovery (ADR 0046). A turn that ended failed, interrupted or truncated keeps that exact signal,
    // because a response that simply ended would read as a clean finish to a client that never saw `done`.
    private static void AbortUnlessClean(ChatTurnTailSession session, HttpContext http)
    {
        if (session.End is { IsClean: false } && !http.RequestAborted.IsCancellationRequested)
        {
            http.Abort();
        }
    }

    // Deleting a conversation also deletes its turn logs (ADR 0153, D11). A store that is down must not block the delete: what is
    // left holds no more than the sweeper removes after the retention.
    private static async Task PurgeChatTurnsAsync(HttpContext http, string userId, string conversationId)
    {
        if (http.RequestServices.GetService<ChatTurnRunner>() is not { } runner)
        {
            return;
        }

        try
        {
            await runner.PurgeAsync(userId, conversationId, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Agent42.ChatTurns")
                .LogWarning(ex, "Could not purge a deleted conversation's chat turn logs; the sweeper will");
        }
    }

    private static IResult ChatTurnsUnavailable()
        => Results.Json(new { error = "Resumable chat turns are not available on this host" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}
