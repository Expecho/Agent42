using System.Threading.Channels;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The SINGLE writer of one chat turn's blobs (ADR 0153, D4, D12): the batch flushes, the heartbeat, the degraded and
/// truncated flags and the terminal status all happen on this one loop, one after another. That is what makes "a late
/// heartbeat can never overwrite <c>completed</c>" true by construction: when the loop ends nothing writes any more, and the
/// terminal status is the last thing it writes.
///
/// <para>The pump hands it lines through <see cref="Enqueue"/> and never waits for the store, so a slow or dead store cannot
/// slow or stop the grain drain or the originating stream (the log failure never fails the turn rule). A batch is retried up
/// to <see cref="ChatTurnOptions.MaxAppendAttempts"/> times, the store recognising one that landed but whose answer was lost;
/// after that the turn is marked <c>degraded</c> and persisting stops. A <b>missing</b> log is the delete fence (D11): the
/// conversation was purged, so the writer stops touching the store altogether and never recreates a blob.</para>
/// </summary>
public sealed class ChatTurnWriter(
    ChatTurnStore store, string userId, string conversationId, ChatTurnMeta meta,
    ChatTurnOptions options, TimeProvider time, ILogger logger, bool persisting = true)
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private ChatTurnMeta _meta = meta;
    private ChatTurnStatus _terminal = ChatTurnStatus.Completed;

    // The log is created empty by StartAsync, so a fresh turn's first append is at 0. Seeded from 0 on purpose: one writer
    // per turn and a fresh blob per turn (unlike the fleet log's counter, which is not reseeded after a restart).
    private long _offset;
    // A turn the store could not even create (persisting = false) still drains the queue and ends, and touches the store never.
    private bool _persistingLog = persisting;
    private bool _purged = !persisting;

    /// <summary>Queues one NDJSON line for the next flush. Never blocks and never throws.</summary>
    public void Enqueue(string line) => _lines.Writer.TryWrite(line);

    /// <summary>Ends the turn with <paramref name="status"/>: the loop flushes what is queued, writes it and stops.</summary>
    public void Complete(ChatTurnStatus status)
    {
        _terminal = status;
        _lines.Writer.TryComplete();
    }

    /// <summary>The writer loop. Completes after <see cref="Complete"/>, once the terminal meta has been written (or given up on).</summary>
    public async Task RunAsync()
    {
        var batch = new EventBatch();
        var reader = _lines.Reader;
        var heartbeatDue = time.GetUtcNow() + options.HeartbeatInterval;
        DateTimeOffset? flushDue = null;
        var pending = reader.WaitToReadAsync().AsTask();

        while (true)
        {
            using var wakeCts = new CancellationTokenSource();
            var delay = Task.Delay(Max(TimeSpan.Zero, Earliest(heartbeatDue, flushDue) - time.GetUtcNow()), time, wakeCts.Token);
            var finished = await Task.WhenAny(pending, delay);
            await wakeCts.CancelAsync();

            if (finished == pending)
            {
                if (!await pending)
                {
                    break;
                }

                Drain(reader, batch);
                flushDue ??= time.GetUtcNow() + options.FlushInterval;
                pending = reader.WaitToReadAsync().AsTask();
            }

            var now = time.GetUtcNow();
            if (batch.Count > 0 && (batch.Bytes >= options.FlushBytes || now >= flushDue))
            {
                await FlushAsync(batch);
                flushDue = null;
            }

            if (now >= heartbeatDue)
            {
                await TryWriteMetaAsync(_meta with { LastHeartbeatUtc = now });
                heartbeatDue = now + options.HeartbeatInterval;
            }
        }

        Drain(reader, batch);
        await FlushAsync(batch);
        await FinishAsync();
    }

    private static void Drain(ChannelReader<string> reader, EventBatch batch)
    {
        while (reader.TryRead(out var line))
        {
            batch.Add(line);
        }
    }

    private async Task FlushAsync(EventBatch batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var bytes = batch.ToBytes();
        batch.Clear();
        if (!_persistingLog || _purged)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await store.AppendAsync(userId, conversationId, _meta.TurnId, bytes, _offset);
                if (await ApplyAsync(result))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Chat turn {TurnId}: append attempt {Attempt} failed", _meta.TurnId, attempt);
            }

            if (attempt >= options.MaxAppendAttempts)
            {
                await DegradeAsync("the store kept failing");
                return;
            }
        }
    }

    // True when the batch is dealt with (appended, or persisting stopped on purpose); false when an offset mismatch means retry.
    private async Task<bool> ApplyAsync(ChatTurnAppendResult result)
    {
        switch (result.Outcome)
        {
            case ChatTurnAppendOutcome.Appended:
                _offset = result.Length ?? _offset;
                return true;
            case ChatTurnAppendOutcome.Truncated:
                _offset = result.Length ?? _offset;
                _persistingLog = false;
                await TryWriteMetaAsync(_meta with { Truncated = true });
                return true;
            case ChatTurnAppendOutcome.Missing:
                // The conversation was purged under a running turn (D11): stop persisting, write nothing more, recreate nothing.
                _purged = true;
                _persistingLog = false;
                return true;
            default:
                // Someone else moved the log: with one writer per turn that means it is no longer ours to extend.
                await DegradeAsync("the log changed under the writer");
                return true;
        }
    }

    private async Task DegradeAsync(string reason)
    {
        logger.LogWarning("Chat turn {TurnId} is degraded: {Reason}. The live stream is unaffected; reattach falls back to history", _meta.TurnId, reason);
        _persistingLog = false;
        await TryWriteMetaAsync(_meta with { Degraded = true });
    }

    private async Task FinishAsync()
    {
        var now = time.GetUtcNow();
        await TryWriteMetaAsync(_meta with { Status = _terminal, EndedUtc = now, LastHeartbeatUtc = now });
    }

    // Best effort and bounded: a meta that cannot be written must never fail or stall the turn. The flags are kept in memory
    // first so a later successful write carries them.
    private async Task TryWriteMetaAsync(ChatTurnMeta next)
    {
        _meta = next;
        if (_purged)
        {
            return;
        }

        for (var attempt = 1; attempt <= options.MaxAppendAttempts; attempt++)
        {
            try
            {
                _purged = !await store.UpdateMetaAsync(userId, conversationId, next);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Chat turn {TurnId}: meta write attempt {Attempt} failed", next.TurnId, attempt);
            }
        }
    }

    private static DateTimeOffset Earliest(DateTimeOffset heartbeatDue, DateTimeOffset? flushDue)
        => flushDue is { } flush && flush < heartbeatDue ? flush : heartbeatDue;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
