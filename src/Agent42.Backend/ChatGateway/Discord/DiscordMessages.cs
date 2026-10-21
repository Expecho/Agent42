using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>Where a Discord message was posted.</summary>
public enum DiscordChannelKind
{
    /// <summary>A DM with the bot.</summary>
    Direct,

    /// <summary>A top-level guild channel.</summary>
    GuildChannel,

    /// <summary>A thread in a guild channel.</summary>
    Thread,
}

/// <summary>
/// The fields of a Discord message the gateway reads, copied out of Discord.Net's <c>SocketMessage</c>. This is the
/// projection seam (plan Step 12): the library's entities can't be built in a test, so the adapter maps them to this
/// plain record and <see cref="DiscordMessages.Parse"/> is tested on fixtures of it.
/// </summary>
public sealed record DiscordMessageView(
    string Id,
    string ChannelId,
    DiscordChannelKind ChannelKind,
    string? ParentChannelId,
    string AuthorId,
    bool AuthorIsBot,
    bool IsUserMessage,
    string Content,
    IReadOnlyCollection<string> MentionedUserIds,
    IReadOnlyList<ChatInboundFile> Attachments,
    DateTimeOffset Timestamp);

/// <summary>
/// Projects a Discord message into the gateway's <see cref="ChatInboundMessage"/> (ADR 0111). Pure.
///
/// <list type="bullet">
/// <item>Only ordinary user messages (types DEFAULT and REPLY): no system messages (joins, pins, "thread created").</item>
/// <item>The bot's own messages are dropped here; other bots and webhooks are flagged, and the pipeline drops them.</item>
/// <item><b>Routes:</b> a DM is the chat. In a guild every message is a thread: inside a thread it is that thread; at
/// the top of a channel it is the thread the reply will start on the message, which Discord gives the message's own id,
/// so follow-ups in that thread land in the same conversation.</item>
/// <item>A mention is Discord's own <c>mentions</c> array, never a name substring. A reply that pings the bot is in
/// that array too. Without the privileged MESSAGE_CONTENT intent (we never ask for it) a guild message that does NOT
/// mention the bot arrives with empty content, so "a reply to the bot" can't count on its own: only the mention does.</item>
/// </list>
/// </summary>
public static partial class DiscordMessages
{
    public static ChatInboundMessage? Parse(DiscordMessageView message, string? botUserId)
    {
        if (!message.IsUserMessage || message.AuthorId == botUserId)
        {
            return null;
        }

        var (chatId, threadId) = message.ChannelKind switch
        {
            DiscordChannelKind.Direct => (message.ChannelId, (string?)null),
            DiscordChannelKind.Thread when message.ParentChannelId is { } parent => (parent, message.ChannelId),
            DiscordChannelKind.Thread => (message.ChannelId, message.ChannelId),
            _ => (message.ChannelId, message.Id),
        };

        return new ChatInboundMessage(
            PlatformMessageId: message.Id,
            ChatId: chatId,
            ChatType: message.ChannelKind == DiscordChannelKind.Direct ? ChatType.Direct : ChatType.Group,
            ThreadId: threadId,
            SenderId: message.AuthorId,
            SenderWorkspaceId: null,
            SenderIsBot: message.AuthorIsBot,
            Text: TurnText(message.Content, botUserId),
            MentionsBot: botUserId is not null && message.MentionedUserIds.Contains(botUserId),
            RepliesToBot: false,
            Files: message.Attachments,
            ReceivedAt: message.Timestamp);
    }

    /// <summary>Whether <paramref name="token"/> has a bot token's shape: three dot-separated base64url parts.</summary>
    public static bool LooksLikeToken(string? token) => token is not null && TokenPattern().IsMatch(token);

    // Our own <@id> / <@!id> mention removed, so "@Agent42 /new" is a command.
    private static string TurnText(string content, string? botUserId)
        => (string.IsNullOrEmpty(botUserId) ? content : content.Replace($"<@{botUserId}>", "").Replace($"<@!{botUserId}>", "")).Trim();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{20,}$")]
    private static partial Regex TokenPattern();
}
