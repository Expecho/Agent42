using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The two places a chat turn's replay log can live (ADR 0153, D6): the durable store behind <see cref="IChatTurnBlobs"/>,
/// and an in-memory twin for an incognito turn, which must leave no trace (ADR 0034) whatever <c>Storage:Provider</c> is.
/// The incognito twin is same-replica only, so a reattach for it elsewhere answers 404 and the client falls back to history
/// recovery. Both are swept by <see cref="ChatTurnSweeper"/>, so incognito text never sits in memory past the retention.
/// </summary>
public sealed class ChatTurnStores
{
    public ChatTurnStores(IChatTurnBlobs durableBlobs, ChatTurnOptions options, TimeProvider time)
    {
        Durable = new ChatTurnStore(durableBlobs, options, time);
        Incognito = new ChatTurnStore(new InMemoryChatTurnBlobs(time), options, time);
    }

    /// <summary>The store of an ordinary turn: Blob in Azure, files when self-hosting, memory in DevHost.</summary>
    public ChatTurnStore Durable { get; }

    /// <summary>The in-memory store of an incognito turn.</summary>
    public ChatTurnStore Incognito { get; }

    /// <summary>The store for a turn that is, or is not, incognito.</summary>
    public ChatTurnStore For(bool ephemeral) => ephemeral ? Incognito : Durable;
}
