using System.Text.Json;
using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>
/// Drains ONE chat turn's grain stream on a detached task and records every event (ADR 0153, D1). The POST and every reattach
/// GET are only readers of what this produces: the in-process <see cref="TurnFeed"/> for the originating request, the store
/// for everyone else. Per event it assigns a gapless sequence number from 1, stamps it on the event, pushes it to the feed and
/// hands its NDJSON line to the <see cref="ChatTurnWriter"/>, whose loop is the only thing that touches the store.
///
/// <para><b>The pump's lifetime is the host's <c>ApplicationStopping</c>, never <c>HttpContext.RequestAborted</c></b>: a client
/// that leaves must not stop the turn (ADR 0046). Host stopping marks the turn <c>interrupted</c> at once, so a graceful rollover
/// does not make readers wait out the heartbeat. A grain stream that throws ends the turn <c>failed</c>. The pump never reads
/// <c>HttpContext</c>; the request's identity was captured before leaving the request and is re-published here, because a usage
/// span with no user is silently unattributed (ADR 0010/0031) and non-owner span redaction depends on it (ADR 0121).</para>
/// </summary>
public sealed class ChatTurnPump(
    ChatTurnWriter writer, TurnFeed feed, ILogger<ChatTurnPump> logger, UserTelemetryContext? telemetry, string? application)
{
    /// <summary>Drains <paramref name="source"/> to its end, then closes the feed and the writer and waits for the writer's last write.</summary>
    public async Task RunAsync(IAsyncEnumerable<AgentStreamEvent> source, CancellationToken hostStopping)
    {
        PublishIdentity();
        var writerTask = writer.RunAsync();
        var status = await DrainAsync(source, hostStopping);

        // The feed ends first so the originating request stops waiting; the writer then flushes and writes the terminal status.
        feed.Complete(status);
        writer.Complete(status);
        await writerTask;
    }

    private async Task<ChatTurnStatus> DrainAsync(IAsyncEnumerable<AgentStreamEvent> source, CancellationToken hostStopping)
    {
        long seq = 0;
        try
        {
            await foreach (var evt in source.WithCancellation(hostStopping))
            {
                var stamped = evt with { Seq = ++seq };
                var line = JsonSerializer.Serialize(stamped, ChatStreamJson.Options);
                feed.Append(new LoggedEvent(seq, stamped), line.Length + 1);
                writer.Enqueue(line);
            }

            return ChatTurnStatus.Completed;
        }
        catch (OperationCanceledException) when (hostStopping.IsCancellationRequested)
        {
            return ChatTurnStatus.Interrupted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A chat turn's grain stream threw after {Events} events; the turn ends failed", seq);
            return ChatTurnStatus.Failed;
        }
    }

    // The detached task has no request, so the identity the middleware put in RequestContext is set again here, like the
    // fleet and the chat gateway do. Absent identity is left absent, never invented.
    private void PublishIdentity()
    {
        if (telemetry is not null)
        {
            RequestContext.Set(UserTelemetryContext.RequestContextKey, telemetry);
        }

        if (application is not null)
        {
            RequestContext.Set(UsageSpanProjector.ApplicationContextKey, application);
        }
    }
}
