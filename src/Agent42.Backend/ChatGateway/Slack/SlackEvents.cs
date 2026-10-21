using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>
/// Projects a Socket Mode <c>events_api</c> payload into the gateway's <see cref="ChatInboundMessage"/> (ADR 0111). Pure,
/// so every rule is tested against fixtures built from the documented field names.
///
/// <list type="bullet">
/// <item><b>The event filter (old C4, the self-reply loop):</b> only <c>message</c> and <c>app_mention</c> events with no
/// subtype or <c>file_share</c> (rejecting every subtype would drop every file message). <c>message_changed</c> is how
/// each of our own throttled edits comes back, and is never a turn. A message from our own bot user is dropped here;
/// any other <c>bot_id</c> is flagged, and the pipeline drops it again.</item>
/// <item><b>Routes:</b> a DM (<c>channel_type: im</c>) is the chat, or a thread in it. In a channel, a message is
/// always a thread: its own <c>thread_ts</c>, or for a top-level mention its own <c>ts</c>, so the reply starts a thread
/// under it and follow-ups there stay in one conversation.</item>
/// <item><b>Ids:</b> a <c>ts</c> is unique per channel only, so the dedupe id is <c>channel:ts</c>. The same mention
/// arrives twice, as <c>message</c> and <c>app_mention</c>; one id makes the second a duplicate.</item>
/// <item><b>Mentions</b> are Slack's own <c>&lt;@U…&gt;</c> encoding in the text or a <c>user</c> element in the blocks,
/// never a display-name substring. They are stripped from the turn text, so <c>@Agent42 !new</c> is a command.</item>
/// </list>
/// </summary>
public static partial class SlackEvents
{
    public static ChatInboundMessage? Parse(JsonElement payload, string? botUserId, Func<string, string, bool>? isTrackedThread = null)
    {
        if (!payload.TryGetProperty("event", out var evt) || Str(evt, "type") is not ("message" or "app_mention")
            || Str(evt, "subtype") is not (null or "file_share")
            || Str(evt, "user") is not { Length: > 0 } user
            || Str(evt, "channel") is not { Length: > 0 } channel
            || Str(evt, "ts") is not { Length: > 0 } ts)
        {
            return null;
        }

        // Our own message (a send, or an edit that slipped through as a plain message event).
        if (user == botUserId)
        {
            return null;
        }

        var isDirect = Str(evt, "channel_type") is { } channelType ? channelType == "im" : channel.StartsWith('D');
        var threadTs = Str(evt, "thread_ts");
        var text = Str(evt, "text") ?? "";
        return new ChatInboundMessage(
            PlatformMessageId: channel + ":" + ts,
            ChatId: channel,
            ChatType: isDirect ? ChatType.Direct : ChatType.Group,
            ThreadId: isDirect ? threadTs : threadTs ?? ts,
            SenderId: user,
            SenderWorkspaceId: Str(evt, "user_team") ?? Str(evt, "team") ?? Str(payload, "team_id"),
            SenderIsBot: evt.TryGetProperty("bot_id", out var botId) && botId.ValueKind == JsonValueKind.String,
            Text: TurnText(text, botUserId),
            MentionsBot: Str(evt, "type") == "app_mention" || Mentions(text, evt, botUserId),
            RepliesToBot: threadTs is not null
                && (Str(evt, "parent_user_id") == botUserId || isTrackedThread?.Invoke(channel, threadTs) == true),
            Files: Files(evt),
            ReceivedAt: ReceivedAt(ts));
    }

    /// <summary>Whether <paramref name="token"/> looks like a bot token (<c>xoxb-</c>).</summary>
    public static bool LooksLikeBotToken(string? token) => token?.StartsWith("xoxb-", StringComparison.Ordinal) == true;

    /// <summary>Whether <paramref name="token"/> looks like an app-level token (<c>xapp-</c>), which Socket Mode needs.</summary>
    public static bool LooksLikeAppToken(string? token) => token?.StartsWith("xapp-", StringComparison.Ordinal) == true;

    /// <summary>Whether <paramref name="token"/> is a USER token, a common paste mistake (Hermes warns on it too).</summary>
    public static bool LooksLikeUserToken(string? token) => token?.StartsWith("xoxp-", StringComparison.Ordinal) == true;

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Mentions(string text, JsonElement evt, string? botUserId)
    {
        if (string.IsNullOrEmpty(botUserId))
        {
            return false;
        }

        if (MentionPattern().Matches(text).Any(m => m.Groups["id"].Value == botUserId))
        {
            return true;
        }

        // A mention can sit only in the rich-text blocks (Hermes slack/adapter.py), so walk them for a user element.
        return evt.TryGetProperty("blocks", out var blocks) && ContainsUser(blocks, botUserId);
    }

    private static bool ContainsUser(JsonElement node, string botUserId) => node.ValueKind switch
    {
        JsonValueKind.Array => node.EnumerateArray().Any(child => ContainsUser(child, botUserId)),
        JsonValueKind.Object => (Str(node, "type") == "user" && Str(node, "user_id") == botUserId)
            || node.EnumerateObject().Any(p => p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object && ContainsUser(p.Value, botUserId)),
        _ => false,
    };

    // The words the owner typed: our own mention removed, and Slack's three escapes undone (Slack escapes & < > in text).
    private static string TurnText(string text, string? botUserId)
    {
        var withoutBot = string.IsNullOrEmpty(botUserId)
            ? text
            : MentionPattern().Replace(text, m => m.Groups["id"].Value == botUserId ? "" : m.Value);
        return withoutBot.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&").Trim();
    }

    private static IReadOnlyList<ChatInboundFile> Files(JsonElement evt)
    {
        if (!evt.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ChatInboundFile>();
        foreach (var file in files.EnumerateArray())
        {
            if ((Str(file, "url_private_download") ?? Str(file, "url_private")) is not { } url)
            {
                continue;
            }

            result.Add(new ChatInboundFile(
                url,
                Str(file, "name"),
                Str(file, "mimetype"),
                file.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
                // A recorded clip; Slack labels its media type inconsistently (Hermes), so the subtype decides.
                Str(file, "subtype") == "slack_audio"));
        }

        return result;
    }

    private static DateTimeOffset ReceivedAt(string ts)
        => double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
            : DateTimeOffset.UnixEpoch;

    [GeneratedRegex(@"<@(?<id>[A-Z0-9]+)(\|[^>]*)?>")]
    private static partial Regex MentionPattern();
}
