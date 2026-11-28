using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Reads a turn from the store (ADR 0153, D2): a second device, another replica, or the owner's own request once its feed
/// is gone or its ring has moved on. It replays the log once, then follows it by byte offset, polling every
/// <see cref="ChatTurnOptions.PollInterval"/> (a local wake signal would only save latency, so it is left out until measured).
///
/// <para>Order matters: the meta is read BEFORE the log slice. The writer flushes the last batch before it writes the terminal
/// meta, so a terminal status seen first guarantees the slice that follows holds every event, and one more empty read is never
/// needed. Events are deduplicated by <c>seq</c> (a retried batch can never be shown twice), and only complete lines are consumed,
/// so a reader that catches an append half-way carries the rest to the next poll. A capped log ends as <c>truncated</c> the moment
/// the reader has everything the store has.</para>
/// </summary>
public sealed class StoreReader(
    ChatTurnStore store, string userId, string conversationId, string turnId, ChatTurnOptions options, TimeProvider time)
    : ITurnEventReader
{
    public TurnEnd? End { get; private set; }

    public async IAsyncEnumerable<IReadOnlyList<LoggedEvent>> ReadAsync(
        long after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long offset = 0;
        var lastSeq = 0L;
        var carry = new List<byte>();

        while (true)
        {
            var meta = await store.GetMetaAsync(userId, conversationId, turnId, cancellationToken);
            var slice = await store.ReadFromAsync(userId, conversationId, turnId, offset, cancellationToken);
            if (meta is null || slice is null)
            {
                // Purged or swept under the reader: the turn is gone, and the client recovers from history.
                End = new TurnEnd(ChatTurnStatus.Interrupted);
                yield break;
            }

            var batch = Parse(slice.Bytes, carry, ref lastSeq, after);
            offset = slice.Length;
            if (batch.Count > 0)
            {
                yield return batch;
            }

            var status = store.StatusOf(meta);
            if (status != ChatTurnStatus.Running || (meta.Truncated && slice.Bytes.Length == 0))
            {
                End = new TurnEnd(status, meta.Truncated, meta.Degraded);
                yield break;
            }

            if (batch.Count == 0)
            {
                await Task.Delay(options.PollInterval, time, cancellationToken);
            }
        }
    }

    // Splits what was read (after any partial line carried from last time) into events with Seq above both `after` and what
    // was already delivered; a trailing partial line stays in the carry.
    private static List<LoggedEvent> Parse(byte[] bytes, List<byte> carry, ref long lastSeq, long after)
    {
        var batch = new List<LoggedEvent>();
        carry.AddRange(bytes);
        var end = carry.LastIndexOf((byte)'\n');
        if (end < 0)
        {
            return batch;
        }

        var complete = carry.GetRange(0, end + 1).ToArray();
        carry.RemoveRange(0, end + 1);
        foreach (var line in Encoding.UTF8.GetString(complete).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (JsonSerializer.Deserialize<AgentStreamEvent>(line, ChatStreamJson.Options) is not { Seq: { } seq } evt || seq <= lastSeq)
            {
                continue;
            }

            lastSeq = seq;
            if (seq > after)
            {
                batch.Add(new LoggedEvent(seq, evt));
            }
        }

        return batch;
    }
}
