using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The running turns of THIS process, by (user, conversation, turn, incognito) (ADR 0153, D2). Process state, so it is an
/// optimisation: a reader on another replica, or after a feed is evicted, finds the same turn in the store. A feed outlives its
/// turn by <see cref="ChatTurnOptions.FeedGrace"/> so a client that reconnects a moment after <c>Done</c> still costs nothing.
/// The key carries the authenticated user id, so a request can only ever look up its own turns, and the incognito flag, so
/// the client-supplied <c>ephemeral</c> query flag decides which turns are visible exactly as it does for the store (D6): an
/// incognito turn is never found without it.
/// </summary>
public sealed class ChatTurnRegistry(ChatTurnOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, TurnFeed> _feeds = new(StringComparer.Ordinal);

    /// <summary>The number of feeds currently held (tests and diagnostics).</summary>
    public int Count => _feeds.Count;

    /// <summary>Creates and registers the feed of a starting turn.</summary>
    public TurnFeed Add(string userId, string conversationId, string turnId, bool ephemeral = false)
    {
        var feed = new TurnFeed(options.FeedMaxBytes);
        _feeds[Key(userId, conversationId, turnId, ephemeral)] = feed;
        return feed;
    }

    /// <summary>The local feed of the turn, or null when this process doesn't own it (or it was evicted).</summary>
    public TurnFeed? TryGet(string userId, string conversationId, string turnId, bool ephemeral = false)
        => _feeds.TryGetValue(Key(userId, conversationId, turnId, ephemeral), out var feed) ? feed : null;

    /// <summary>Drops the feed <see cref="ChatTurnOptions.FeedGrace"/> from now. Fire and forget: a failure to schedule only leaks one bounded ring.</summary>
    public void EvictAfterGrace(string userId, string conversationId, string turnId, bool ephemeral = false)
    {
        var key = Key(userId, conversationId, turnId, ephemeral);
        _ = Task.Delay(options.FeedGrace, time).ContinueWith(_ => _feeds.TryRemove(key, out TurnFeed? _), TaskScheduler.Default);
    }

    private static string Key(string userId, string conversationId, string turnId, bool ephemeral)
        => $"{(ephemeral ? "i" : "d")}/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(conversationId)}/{Uri.EscapeDataString(turnId)}";
}
