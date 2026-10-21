using System.Text.Json;

namespace Agent42.Backend;

/// <summary>
/// Projects one signal-cli SSE <c>receive</c> event (<c>{"account":…,"envelope":{…}}</c>) into the gateway's
/// <see cref="ChatInboundMessage"/> (ADR 0111, phase 3). Pure, so every rule is tested against fixtures.
///
/// <list type="bullet">
/// <item><b>The filter (SC4):</b> only an envelope carrying a <c>dataMessage</c> with text, attachments or both is a
/// message. Sync, story, receipt, typing, call and edit envelopes are dropped (an edit never re-runs a turn), as is a
/// data message that is a reaction or a remote delete, and anything sent by the bot's own ACI.</item>
/// <item><b>Ids:</b> Signal has no message id; a message is its sender plus its sent <c>timestamp</c>, so the dedupe
/// id is <c>sourceUuid:timestamp</c>.</item>
/// <item><b>Routes:</b> the chat is <c>groupInfo.groupId</c> in a group, else the sender's ACI. There are no threads.</item>
/// <item><b>Mentions and quotes</b> come from uuids (<c>mentions[].uuid</c>, <c>quote.authorUuid</c>), never the text.
/// Signal leaves U+FFFC where a mention sits; it is removed from the turn text, so <c>@bot /new</c> is a command.</item>
/// </list>
/// </summary>
public static class SignalEvents
{
    private const char MentionPlaceholder = '￼';

    public static ChatInboundMessage? Parse(JsonElement evt, string? botAci)
    {
        if (!TryObject(evt, "envelope", out var envelope)
            || !TryObject(envelope, "dataMessage", out var data)
            || Str(envelope, "sourceUuid") is not { Length: > 0 } sender
            || sender == botAci
            || !envelope.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Number || !ts.TryGetInt64(out var timestamp)
            || Present(data, "reaction") || Present(data, "remoteDelete"))
        {
            return null;
        }

        var text = (Str(data, "message") ?? "").Replace(MentionPlaceholder.ToString(), "").Trim();
        var files = Files(data);
        if (text.Length == 0 && files.Count == 0)
        {
            return null;
        }

        string chatId;
        if (!TryObject(data, "groupInfo", out var group))
        {
            chatId = sender;
        }
        else if (Str(group, "groupId") is { Length: > 0 } groupId)
        {
            chatId = groupId;
        }
        else
        {
            return null;
        }

        return new ChatInboundMessage(
            PlatformMessageId: sender + ":" + timestamp,
            ChatId: chatId,
            ChatType: chatId == sender ? ChatType.Direct : ChatType.Group,
            ThreadId: null,
            SenderId: sender,
            SenderWorkspaceId: null,
            SenderIsBot: false,
            Text: text,
            MentionsBot: MentionsBot(data, botAci),
            RepliesToBot: TryObject(data, "quote", out var quote) && !string.IsNullOrEmpty(botAci) && Str(quote, "authorUuid") == botAci,
            Files: files.Select(f => f with { FileRef = AttachmentRef(chatId, f.FileRef) }).ToList(),
            ReceivedAt: ReceivedAt(timestamp));
    }

    /// <summary>
    /// An inbound file's opaque ref: the chat it arrived in and the daemon's attachment id. <c>getAttachment</c> needs
    /// both (the sender or the group, plus the id); neither an ACI, a phone number nor a base64 group id contains '|'.
    /// </summary>
    public static string AttachmentRef(string chatId, string attachmentId) => chatId + "|" + attachmentId;

    /// <summary>The inverse of <see cref="AttachmentRef"/>; <c>null</c> for a ref this adapter didn't make.</summary>
    public static (string ChatId, string AttachmentId)? ParseAttachmentRef(string fileRef)
        => fileRef.IndexOf('|', StringComparison.Ordinal) is var split and > 0 && split < fileRef.Length - 1
            ? (fileRef[..split], fileRef[(split + 1)..])
            : null;

    private static bool MentionsBot(JsonElement data, string? botAci)
        => !string.IsNullOrEmpty(botAci)
            && data.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array
            && mentions.EnumerateArray().Any(m => Str(m, "uuid") == botAci);

    private static IReadOnlyList<ChatInboundFile> Files(JsonElement data)
    {
        if (!data.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ChatInboundFile>();
        foreach (var attachment in attachments.EnumerateArray())
        {
            if (Str(attachment, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            result.Add(new ChatInboundFile(
                id,
                Str(attachment, "filename"),
                Str(attachment, "contentType"),
                attachment.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) ? bytes : null,
                attachment.TryGetProperty("isVoiceNote", out var voice) && voice.ValueKind == JsonValueKind.True));
        }

        return result;
    }

    private static DateTimeOffset ReceivedAt(long timestamp)
        => timestamp is >= 0 and <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : DateTimeOffset.UnixEpoch;

    private static bool TryObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static bool Present(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
