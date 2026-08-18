using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Attachment rehydration (ADR 0004): image bytes live in blob storage, never in the session item, so
/// their SAS URLs are re-minted on read. The vision gate applies to the whole HISTORY, not just the
/// new turn — otherwise old image URLs still reach a model that cannot see them (ADR 0013).
/// </summary>
public sealed partial class AgentGrain
{
    // Pads TurnAttachments with empty entries so it has one entry per user turn already in the
    // session before the caller appends this turn's entry. This keeps the index join in
    // GetHistoryAsync correct even for conversations that predate TurnAttachments (old Cosmos
    // state deserializes it empty) — the first new turn back-fills the gap.
    private void AlignTurnAttachmentsToHistory()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        var priorUserTurns = messages.Count(m => m.Role == ChatRole.User);
        while (_state.State.TurnAttachments.Count < priorUserTurns)
        {
            _state.State.TurnAttachments.Add([]);
        }
    }

    // Refreshes the image URLs on the session's prior turns so the next model call always sends
    // currently-valid URLs. Azure OpenAI re-fetches a prior turn's image URL on every later turn
    // (Step 1 spike), and a blob SAS is short-lived, so stale URLs would 400. Called before every
    // model call (not just on activation), because a warm grain serves many turns per activation.
    // Each user turn's UriContent parts are matched, in order, to that turn's stored attachment
    // refs. Prod re-mints a fresh SAS; the in-memory store returns its stable placeholder (a no-op).
    //
    // <paramref name="includeImages"/> is the VISION gate applied to HISTORY, and it is the other half
    // of the gate in ChatCoreAsync (ADR 0013). Withholding images only from the new turn would be a hole:
    // prior turns' UriContent parts live on in the session and would be replayed to the newly picked
    // model anyway — handing a third-party endpoint a SAS URL into our private attachments container
    // (ADR 0004) precisely when it cannot even read it.
    //
    // The withholding is REVERSIBLE, and that is why this reads from TurnAttachments rather than just
    // deleting parts: TurnAttachments is the durable record of a turn's images, so a stripped part is
    // reconstructed from it the moment the conversation switches back to a vision-capable model. A
    // destructive strip would silently blind the conversation's history forever after one visit to a
    // text-only model.
    private async Task RehydrateAttachmentUrlsAsync(bool includeImages)
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        var userTurnIndex = 0;
        foreach (var message in messages)
        {
            // Assistant generated images (ADR 0020) ride the SAME vision gate: a generated image is a
            // private-container SAS too, so it must be withheld from a no-vision chat model and restored on
            // switch-back. Reversible by the message-level assistantImageId (survives the strip), NOT by a
            // positional assistant join (assistant messages are not 1:1 with turns — C1).
            if (message.Role == ChatRole.Assistant)
            {
                if (AssistantImageRef(message) is { } generated)
                {
                    await RehydrateAssistantImageAsync(message, generated, includeImages);
                }

                continue;
            }

            if (message.Role != ChatRole.User)
            {
                continue;
            }

            var refs = userTurnIndex < _state.State.TurnAttachments.Count
                ? _state.State.TurnAttachments[userTurnIndex]
                : null;
            userTurnIndex++;

            if (!includeImages)
            {
                // Strip on the MESSAGE's own parts, not on `refs` — a conversation from before
                // TurnAttachments existed deserializes that list empty (ADR 0004 gotcha) while its
                // session still holds image parts. Keying the strip off `refs` would skip those and
                // leak them to the model this gate exists to withhold them from.
                StripImageParts(message);
                continue;
            }

            if (refs is not { Count: > 0 })
            {
                continue;
            }

            var uriParts = message.Contents.OfType<UriContent>().ToList();
            for (var i = 0; i < refs.Count; i++)
            {
                var url = await _attachments.GetReadUrlAsync(refs[i]);

                if (i < uriParts.Count)
                {
                    uriParts[i].Uri = url;
                }
                else
                {
                    // Restoring a part a previous no-vision turn withheld.
                    message.Contents.Add(new UriContent(url, refs[i].MediaType));
                }
            }

            // Undo the placeholder below, so a restored turn reads as the user wrote it.
            for (var i = message.Contents.Count - 1; i >= 0; i--)
            {
                if (message.Contents[i] is TextContent { Text: WithheldImagePlaceholder })
                {
                    message.Contents.RemoveAt(i);
                }
            }
        }
    }

    // What a user turn says once its images are withheld. An IMAGE-ONLY turn (blank text, one photo — a
    // shape the composer deliberately allows) would otherwise be left with ZERO content parts, and an
    // empty message is rejected outright by OpenAI-compatible endpoints: the conversation could then
    // never be reopened on that model, which is the exact failure this whole gate exists to prevent.
    // StripToolHistory can just drop an emptied message; this cannot — the TurnAttachments↔user-turn
    // index join is positional, so a dropped turn would misalign every image after it.
    private const string WithheldImagePlaceholder = "[image omitted: the selected model cannot read images]";

    private static void StripImageParts(ChatMessage message)
    {
        var removed = 0;
        for (var i = message.Contents.Count - 1; i >= 0; i--)
        {
            if (message.Contents[i] is UriContent)
            {
                message.Contents.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0 && message.Contents.Count == 0)
        {
            message.Contents.Add(new TextContent(WithheldImagePlaceholder));
        }
    }

    // Applies the vision gate to ONE assistant generated-image message (ADR 0020), reversibly. The link to the
    // ref is the message-level assistantImageId (stamped in RecordImageTurnAsync), which survives both the
    // strip and session serialization — so this needs no positional bookkeeping.
    //   - no-vision: drop the image UriContent(s); the id stays in AdditionalProperties for a later restore.
    //     A content-less assistant message is rejected by OpenAI-compatible endpoints on replay, so an emptied
    //     one keeps an empty TextContent (mirrors the user-side WithheldImagePlaceholder shape).
    //   - vision: ensure exactly one image UriContent with a FRESH url (re-mint the SAS, or re-add it if a
    //     previous no-vision turn stripped it).
    private async Task RehydrateAssistantImageAsync(ChatMessage message, AttachmentRef generated, bool includeImages)
    {
        if (!includeImages)
        {
            var removed = 0;
            for (var i = message.Contents.Count - 1; i >= 0; i--)
            {
                if (message.Contents[i] is UriContent)
                {
                    message.Contents.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0 && message.Contents.Count == 0)
            {
                message.Contents.Add(new TextContent(string.Empty));
            }

            return;
        }

        var url = await _attachments.GetReadUrlAsync(generated);
        var existing = message.Contents.OfType<UriContent>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Uri = url;
        }
        else
        {
            message.Contents.Add(new UriContent(url, generated.MediaType));
        }
    }
}
