namespace Agent42.Agents;

/// <summary>
/// The chat gateway's limits and its conversation-id shape (ADR 0111). One home for every number, so a reviewer
/// finds them in one place.
/// </summary>
public static class ChatGatewayLimits
{
    /// <summary>How many chat connections (bots) one user may hold.</summary>
    public const int MaxConnectionsPerUser = 10;

    /// <summary>Longest display name a connection may carry.</summary>
    public const int MaxConnectionNameChars = 64;

    /// <summary>How long a freshly minted pairing code stays valid.</summary>
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Wrong pairing codes tolerated before the connection locks until the owner re-pairs from Agent42. The code is
    /// ~50 bits, so this is defence in depth against a stranger who found the bot, not the primary control.
    /// </summary>
    public const int MaxPairingFailures = 5;

    /// <summary>
    /// User turns one chat conversation may hold before the route rolls over to a fresh conversation.
    ///
    /// <para>A DM on a platform with no threads (Telegram, WhatsApp, Signal) is one conversation until the owner
    /// types <c>/new</c>, and a conversation's whole history is one 2 MB session item (ADR 0004). ADR 0084's
    /// compaction stubs tool payloads (artifact sources, <c>read_document</c> results) but never plain message text,
    /// so nothing else bounds such a conversation. The rollover is never silent: the chat is told a new conversation
    /// started and where the old one is (ADR 0111). Time never triggers it (Hermes' rule: idle time is not a reason
    /// to forget a conversation).</para>
    /// </summary>
    public const int MaxTurnsPerConversation = 100;

    /// <summary>
    /// The conversation-id prefix a chat route mints. Disjoint from ADR 0016's <c>agent-</c> and ADR 0070's
    /// <c>mcp-</c>, so ADR 0068's 24-hour retention window, which keys on those two prefixes, deliberately never
    /// applies here: a chat conversation is bounded by <see cref="MaxTurnsPerConversation"/> instead.
    /// </summary>
    public const string ConversationIdPrefix = "chat-";

    /// <summary>Mints a fresh conversation id in the shape <see cref="IsValidConversationId"/> accepts.</summary>
    public static string NewConversationId() => ConversationIdPrefix + Guid.NewGuid().ToString("d");

    /// <summary>
    /// Whether <paramref name="conversationId"/> is exactly a minted chat id: the prefix and a canonical lowercase
    /// GUID, nothing else. The id is never caller-supplied today; validating the whole string (the
    /// <c>AgentMcpLimits.IsValidConversationId</c> rule) keeps a future caller from widening it into the
    /// <c>{userId}/{conversationId}</c> grain key and blob prefix.
    /// </summary>
    public static bool IsValidConversationId(string? conversationId)
    {
        if (conversationId is null || !conversationId.StartsWith(ConversationIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = conversationId[ConversationIdPrefix.Length..];
        return Guid.TryParse(remainder, out var parsed)
            && string.Equals(parsed.ToString("d"), remainder, StringComparison.Ordinal);
    }
}
