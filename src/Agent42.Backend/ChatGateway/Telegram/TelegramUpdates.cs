using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>
/// Projects a Bot API <c>Update</c> into the gateway's <see cref="ChatInboundMessage"/> (ADR 0111). Pure, so every rule
/// is tested against fixtures built from the documented field names.
///
/// <list type="bullet">
/// <item>Only <c>message</c> updates (private chats, groups, supergroups); channel posts and edits are not turns.</item>
/// <item>A mention is a server-side <c>mention</c> entity naming this bot, a <c>text_mention</c> of its id, or a
/// <c>bot_command</c> addressed <c>@</c> it. Never a substring match. Entity offsets are UTF-16 units, which is what
/// a .NET string index is.</item>
/// <item>A message without a sender (an anonymous group admin) is dropped: there is nobody to compare with the owner.</item>
/// </list>
/// </summary>
public static partial class TelegramUpdates
{
    public static ChatInboundMessage? Parse(JsonElement update, string? botUserId, string? botUserName)
    {
        if (!update.TryGetProperty("message", out var message)
            || !message.TryGetProperty("from", out var from)
            || !message.TryGetProperty("chat", out var chat))
        {
            return null;
        }

        var chatType = chat.GetProperty("type").GetString() switch
        {
            "private" => ChatType.Direct,
            "group" or "supergroup" => ChatType.Group,
            _ => (ChatType?)null,
        };
        if (chatType is null)
        {
            return null;
        }

        var (text, entities) = TextAndEntities(message);
        return new ChatInboundMessage(
            PlatformMessageId: message.GetProperty("message_id").GetRawText(),
            ChatId: chat.GetProperty("id").GetRawText(),
            ChatType: chatType.Value,
            ThreadId: TopicId(message),
            SenderId: from.GetProperty("id").GetRawText(),
            SenderWorkspaceId: null,
            SenderIsBot: from.TryGetProperty("is_bot", out var isBot) && isBot.GetBoolean(),
            Text: text,
            MentionsBot: MentionsBot(text, entities, botUserId, botUserName),
            RepliesToBot: RepliesTo(message, botUserId),
            Files: Files(message),
            ReceivedAt: DateTimeOffset.FromUnixTimeSeconds(message.GetProperty("date").GetInt64()));
    }

    // A photo or document carries its words as a caption, with caption_entities.
    private static (string Text, JsonElement Entities) TextAndEntities(JsonElement message)
        => message.TryGetProperty("text", out var text)
            ? (text.GetString() ?? "", message.TryGetProperty("entities", out var entities) ? entities : default)
            : (message.TryGetProperty("caption", out var caption) ? caption.GetString() ?? "" : "",
                message.TryGetProperty("caption_entities", out var captionEntities) ? captionEntities : default);

    // A forum topic is a thread; any other message_thread_id (a reply chain) is not.
    private static string? TopicId(JsonElement message)
        => message.TryGetProperty("is_topic_message", out var topic) && topic.GetBoolean()
            && message.TryGetProperty("message_thread_id", out var thread) ? thread.GetRawText() : null;

    private static bool RepliesTo(JsonElement message, string? botUserId)
        => message.TryGetProperty("reply_to_message", out var replyTo)
            && replyTo.TryGetProperty("from", out var replyFrom) && replyFrom.GetProperty("id").GetRawText() == botUserId;

    /// <summary>Whether <paramref name="token"/> has a Bot API token's shape (<c>digits:secret</c>).</summary>
    public static bool LooksLikeToken(string? token) => token is not null && TokenPattern().IsMatch(token);

    /// <summary>A request body addressed to <paramref name="target"/>'s chat (and forum topic).</summary>
    internal static Dictionary<string, object> WithTarget(ChatTarget target, Dictionary<string, object> body)
    {
        body["chat_id"] = target.ChatId;
        if (target.ThreadId is { } thread && long.TryParse(thread, out var threadId))
        {
            body["message_thread_id"] = threadId;
        }

        return body;
    }

    private static bool MentionsBot(string text, JsonElement entities, string? botUserId, string? botUserName)
    {
        if (entities.ValueKind != JsonValueKind.Array || string.IsNullOrEmpty(botUserName))
        {
            return false;
        }

        foreach (var entity in entities.EnumerateArray())
        {
            var type = entity.GetProperty("type").GetString();
            if (type == "text_mention")
            {
                if (entity.TryGetProperty("user", out var user) && user.GetProperty("id").GetRawText() == botUserId)
                {
                    return true;
                }

                continue;
            }

            if (type is "mention" or "bot_command" && Slice(text, entity) is { } value
                && value.EndsWith("@" + botUserName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Slice(string text, JsonElement entity)
    {
        var offset = entity.GetProperty("offset").GetInt32();
        var length = entity.GetProperty("length").GetInt32();
        return offset >= 0 && length > 0 && offset + length <= text.Length ? text.Substring(offset, length) : null;
    }

    private static IReadOnlyList<ChatInboundFile> Files(JsonElement message)
    {
        var files = new List<ChatInboundFile>();
        if (message.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Array && photo.GetArrayLength() > 0)
        {
            // Every size of one photo; the largest is the last (the order Telegram sends them in).
            var largest = photo[photo.GetArrayLength() - 1];
            files.Add(new ChatInboundFile(largest.GetProperty("file_id").GetString()!, "photo.jpg", "image/jpeg", Size(largest), false));
        }

        foreach (var (field, isVoice) in new[] { ("document", false), ("voice", true), ("audio", false) })
        {
            if (message.TryGetProperty(field, out var file))
            {
                files.Add(new ChatInboundFile(
                    file.GetProperty("file_id").GetString()!,
                    file.TryGetProperty("file_name", out var name) ? name.GetString() : isVoice ? "voice.ogg" : null,
                    file.TryGetProperty("mime_type", out var mime) ? mime.GetString() : null,
                    Size(file),
                    isVoice));
            }
        }

        return files;
    }

    private static long? Size(JsonElement file) => file.TryGetProperty("file_size", out var size) ? size.GetInt64() : null;

    [GeneratedRegex(@"^\d{5,15}:[A-Za-z0-9_-]{30,64}$")]
    private static partial Regex TokenPattern();
}
