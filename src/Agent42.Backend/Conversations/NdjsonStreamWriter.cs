using System.Text.Json;

namespace Agent42.Backend;

/// <summary>
/// Writes a turn's batches to the response as NDJSON, one event per line, flushing each line, with a blank-line keep-alive when
/// the turn is quiet (ADR 0153; the shape of <c>WriteEventStreamAsync</c> in the fleet endpoint). The browser's reader skips blank
/// lines, so the keep-alive costs nothing there. Returns when the session ends or the client goes: on a client abort it simply
/// stops, and the turn itself carries on, because the pump never observes the request.
/// </summary>
public static class NdjsonStreamWriter
{
    public static async Task WriteAsync(
        HttpResponse response, ChatTurnTailSession session, TimeSpan keepAlive, CancellationToken cancellationToken)
    {
        try
        {
            await using var batches = session.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            var next = batches.MoveNextAsync().AsTask();
            while (true)
            {
                using var tick = new CancellationTokenSource();
                var finished = await Task.WhenAny(next, Task.Delay(keepAlive, tick.Token));
                await tick.CancelAsync();

                if (finished != next)
                {
                    await WriteLineAsync(response, string.Empty, cancellationToken);
                    continue;
                }

                if (!await next)
                {
                    return;
                }

                foreach (var logged in batches.Current)
                {
                    await WriteLineAsync(response, JsonSerializer.Serialize(logged.Event, ChatStreamJson.Options), cancellationToken);
                }

                next = batches.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client left. Only this reader stops.
        }
    }

    private static async Task WriteLineAsync(HttpResponse response, string line, CancellationToken cancellationToken)
    {
        await response.WriteAsync(line + "\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
