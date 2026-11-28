using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Picks the reader for a turn and yields what a client has not seen (ADR 0153, D2, D14): the owner's in-process feed when this
/// process has it, else the store. A feed reader that falls behind its ring continues from the store at the sequence it had
/// reached, so a slow client never loses an event and never sees one twice. Every outgoing batch goes through
/// <see cref="ReplayCoalescer"/>.
/// </summary>
public sealed class ChatTurnTail(ChatTurnRegistry registry, ChatTurnStores stores, ChatTurnOptions options, TimeProvider time)
{
    /// <summary>Opens a read of the turn from <paramref name="after"/> on. Nothing happens until the session is enumerated.</summary>
    public ChatTurnTailSession Open(string userId, string conversationId, string turnId, bool ephemeral, long after)
    {
        var feed = registry.TryGet(userId, conversationId, turnId, ephemeral);
        return new ChatTurnTailSession(
            feed is null ? null : new FeedReader(feed),
            () => new StoreReader(stores.For(ephemeral), userId, conversationId, turnId, options, time),
            after);
    }

    /// <summary>
    /// The highest sequence number the turn has at this moment: the value an attaching page treats as "history" (it renders those
    /// events but never runs a client tool from them, D13). From the live feed when this process has it, else from the log.
    /// </summary>
    public async Task<long> LastSeqAsync(string userId, string conversationId, string turnId, bool ephemeral, CancellationToken cancellationToken = default)
    {
        if (registry.TryGet(userId, conversationId, turnId, ephemeral) is { } feed)
        {
            return feed.LastSeq;
        }

        var slice = await stores.For(ephemeral).ReadFromAsync(userId, conversationId, turnId, 0, cancellationToken);
        return slice is null ? 0 : LastSeqOf(slice.Bytes);
    }

    // The seq of the last complete line of a log.
    private static long LastSeqOf(byte[] log)
    {
        var end = Array.LastIndexOf(log, (byte)'\n');
        if (end < 0)
        {
            return 0;
        }

        var start = end == 0 ? 0 : Array.LastIndexOf(log, (byte)'\n', end - 1) + 1;
        var line = Encoding.UTF8.GetString(log, start, end - start);
        return JsonSerializer.Deserialize<AgentStreamEvent>(line, ChatStreamJson.Options)?.Seq ?? 0;
    }
}

/// <summary>One read of one turn: the batches to send, and afterwards how the turn ended.</summary>
public sealed class ChatTurnTailSession(ITurnEventReader? feedReader, Func<ITurnEventReader> storeReaderFactory, long after)
{
    /// <summary>How the turn ended; null until <see cref="ReadAsync"/> has run to its end.</summary>
    public TurnEnd? End { get; private set; }

    public async IAsyncEnumerable<IReadOnlyList<LoggedEvent>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = after;
        if (feedReader is not null)
        {
            await using var enumerator = feedReader.ReadAsync(cursor, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                IReadOnlyList<LoggedEvent> batch;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        End = feedReader.End;
                        yield break;
                    }

                    batch = enumerator.Current;
                }
                catch (TurnFeedBehindException)
                {
                    break;
                }

                cursor = batch[^1].Seq;
                yield return ReplayCoalescer.Coalesce(batch, 0);
            }
        }

        var storeReader = storeReaderFactory();
        await foreach (var batch in storeReader.ReadAsync(cursor, cancellationToken))
        {
            yield return ReplayCoalescer.Coalesce(batch, 0);
        }

        End = storeReader.End;
    }
}
