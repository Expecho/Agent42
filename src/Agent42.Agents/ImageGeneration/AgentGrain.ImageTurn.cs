using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The image-generation turn (ADR 0020): a picked image model produces an image instead of a chat
/// reply, via a different API from the chat client. The assistant image is <b>id-keyed</b>
/// (<c>assistantImageId</c>) and never joined by assistant-message position, which is not 1:1 with
/// turns.
///
/// <para><b>Cross-file invariant.</b> <see cref="GenerateImageTurnAsync"/> skips its state write when
/// the turn-scoped <c>_ephemeral</c> flag is set, but that flag is declared AND cleared in the core
/// part (<c>Conversations/AgentGrain.cs</c>). Read the ephemeral-chat entry in
/// <c>.agentic/gotchas.md</c> before touching either half — this is the one documented invariant this
/// split separates, and it is covered by the named ephemeral regression tests.</para>
/// </summary>
public sealed partial class AgentGrain
{
    // The message-level key linking an assistant image message to its durable AttachmentRef in
    // GeneratedImages (ADR 0020). Written into ChatMessage.AdditionalProperties, it survives session
    // serialize/deserialize AND an image-strip (Step-0 spike) — so the strip/restore stays reversible by id,
    // NOT by a fragile assistant-message position (assistant messages are not 1:1 with turns — a tool turn
    // emits a standalone FunctionCallContent message plus a separate text message).
    private const string AssistantImageIdKey = "assistantImageId";

    // Runs an image-generation turn (ADR 0020): resolve the input image (this turn's attachment, else the most
    // recent image in the conversation, else none → text-to-image), generate it, store the BYTES in blob
    // storage, and record a user+assistant turn into the session so it renders in history and drives later
    // edits. Bytes go to blob storage only; the session holds a short UriContent reference + the id (ADR 0004).
    private async IAsyncEnumerable<AgentStreamEvent> GenerateImageTurnAsync(
        string message, IReadOnlyList<AttachmentRef>? attachments)
    {
        var ownerKey = this.GetPrimaryKeyString();

        // The generator for this image model. The caller only branches here when GetCapabilities reports
        // image generation, and every wired factory returns a generator in that case — but guard rather than
        // `!`-deref, so a misconfigured factory (a test/DevHost declaring the capability without injecting a
        // generator) yields a clear notice instead of an NRE mid-turn (critic Minor-2).
        var generator = _chatClientFactory.GetImageGenerationService(_modelId);
        if (generator is null)
        {
            _logger.LogError(
                "Agent grain {GrainKey}: model {Model} reports image generation but no generator was resolved",
                this.GetPrimaryKeyString(), _modelId);
            yield return new AgentStreamEvent(
                AgentStreamEventType.TextDelta, Text: "_Image generation is unavailable for the selected model._\n\n");
            yield return new AgentStreamEvent(AgentStreamEventType.Done);
            yield break;
        }

        // Input image: this turn's attachment wins; else the most recent image already in the conversation
        // (user-uploaded or previously generated); else none (text-to-image). Bytes are read fresh from the
        // owner-scoped store — never from a session URL.
        var inputRef = attachments?.FirstOrDefault(
                a => a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            ?? FindMostRecentImageRef();
        ImageInput? input = null;
        if (inputRef is not null)
        {
            var content = await _attachments.OpenReadAsync(ownerKey, inputRef.Id);
            if (content is not null)
            {
                input = new ImageInput(content.Bytes, content.MediaType);
            }
        }

        var generated = await generator.GenerateAsync(message, input);

        // Store the bytes OUT of the session (ADR 0004) and keep the durable ref by id.
        var reference = await _attachments.SaveAsync(
            ownerKey, "generated" + ImageMediaTypes.ExtensionFor(generated.MediaType),
            generated.MediaType, generated.Bytes);
        _state.State.GeneratedImages[reference.Id] = reference;

        _logger.LogInformation(
            "Agent grain {GrainKey} generated an image ({Mode}) and stored it as {AttachmentId}",
            this.GetPrimaryKeyString(), input is null ? "text-to-image" : "image-to-image", reference.Id);

        // Build the USER turn for history: the prompt text + THIS turn's uploaded image(s), for display. Record
        // its refs in TurnAttachments, 1:1 with user turns, exactly like a chat turn — so the input image
        // rehydrates and rides the vision gate on any LATER chat turn.
        var userContents = new List<AIContent>();
        if (!string.IsNullOrEmpty(message))
        {
            userContents.Add(new TextContent(message));
        }

        var turnRefs = new List<AttachmentRef>();
        if (attachments is { Count: > 0 })
        {
            foreach (var attachment in attachments)
            {
                var url = await _attachments.GetReadUrlAsync(attachment);
                userContents.Add(new UriContent(url, attachment.MediaType));
                turnRefs.Add(attachment);
            }
        }

        // Never a content-less user message (rejected by OpenAI-compatible endpoints on a later replay).
        if (userContents.Count == 0)
        {
            userContents.Add(new TextContent(string.Empty));
        }

        AlignTurnAttachmentsToHistory();
        _state.State.TurnAttachments.Add(turnRefs);

        // The ASSISTANT turn: a short UriContent reference to the generated image (a blob SAS), never bytes.
        var assistantUrl = await _attachments.GetReadUrlAsync(reference);
        var assistantContents = new List<AIContent> { new UriContent(assistantUrl, reference.MediaType) };

        await RecordImageTurnAsync(new ChatMessage(ChatRole.User, userContents), assistantContents, reference.Id);

        // Ephemeral (ADR 0034): skip the session write. The generated image's bytes still land in the
        // attachments blob container (session-only ephemerality — an accepted, documented limitation),
        // but the conversation itself leaves no trace in Cosmos.
        if (!_ephemeral)
        {
            var serializedSession = await _agent.SerializeSessionAsync(_session);
            _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
            await _state.WriteStateAsync();

            // Search projection (ADR 0061). This site really does have an `if (!_ephemeral)` block —
            // unlike the other two, which are an early return and a no-guard-by-design path — so the
            // write goes INSIDE it. Best-effort inside.
            await IndexNewTurnsForSearchAsync();

            _logger.LogInformation("Agent grain {GrainKey} completed an image-generation turn and persisted it",
                this.GetPrimaryKeyString());
        }
        else
        {
            // Same reason as the chat path: consume the ordinals so a later normal turn on this
            // activation cannot re-project this incognito turn into the search index (ADR 0061).
            await SkipIndexingForEphemeralTurnAsync();

            _logger.LogInformation("Agent grain {GrainKey} completed an ephemeral image-generation turn (not persisted)",
                this.GetPrimaryKeyString());
        }

        yield return new AgentStreamEvent(
            AgentStreamEventType.Image, AttachmentId: reference.Id, MediaType: reference.MediaType);
        yield return new AgentStreamEvent(AgentStreamEventType.Done);
    }

    // Records a user + assistant(image) turn into the session via the one-shot ImageGenerationChatClient
    // adapter, then stamps the assistant message with its assistantImageId. AF 1.13 has no
    // append-without-inference (Step-0 spike), so the turn is recorded by RUNNING the adapter: snapshot the
    // real session → rebuild the agent onto the adapter → run → restore, the SAME dance as a model switch.
    // _agent (bound to the image model's chat client) is used ONLY to serialize/deserialize the session here —
    // never to run inference (calling chat on an image endpoint would 400).
    private async Task RecordImageTurnAsync(
        ChatMessage userMessage, IReadOnlyList<AIContent> assistantContents, string attachmentId)
    {
        var snapshot = await _agent.SerializeSessionAsync(_session);

        var adapter = new ChatClientAgent(
            new ImageGenerationChatClient(assistantContents),
            new ChatClientAgentOptions
            {
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    StorageInputRequestMessageFilter = ExcludeInjectedContextMessages,
                }),
            });

        var adapterSession = await adapter.DeserializeSessionAsync(snapshot);
        await adapter.RunAsync([userMessage], adapterSession);

        // Stamp the just-recorded assistant message with the durable link to its ref. By post-run mutation
        // (not on the returned message) because that is the shape the Step-0 spike proved round-trips.
        if (adapterSession.TryGetInMemoryChatHistory(out var messages) && messages is not null)
        {
            var lastAssistant = messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (lastAssistant is not null)
            {
                (lastAssistant.AdditionalProperties ??= [])[AssistantImageIdKey] = attachmentId;
            }
        }

        var newSnapshot = await adapter.SerializeSessionAsync(adapterSession);
        _session = await _agent.DeserializeSessionAsync(newSnapshot);
    }

    // Walks the session in order and returns the LAST image reference in the conversation — a user-uploaded
    // image (via TurnAttachments, positional per user turn) or a previously generated one (via the assistant
    // message's assistantImageId → GeneratedImages). The image-to-image input when the current turn has no
    // attachment (ADR 0020). Id-keyed for the assistant side — never a positional assistant join (C1).
    private AttachmentRef? FindMostRecentImageRef()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return null;
        }

        AttachmentRef? found = null;
        var userTurnIndex = 0;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                var refs = userTurnIndex < _state.State.TurnAttachments.Count
                    ? _state.State.TurnAttachments[userTurnIndex]
                    : null;
                userTurnIndex++;

                var image = refs?.LastOrDefault(
                    r => r.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
                if (image is not null)
                {
                    found = image;
                }
            }
            else if (message.Role == ChatRole.Assistant && AssistantImageRef(message) is { } generated)
            {
                found = generated;
            }
        }

        return found;
    }

    // The durable AttachmentRef for an assistant message's generated image, or null. Read by the message-level
    // assistantImageId (ADR 0020) — present even after the image UriContent was stripped for a no-vision model,
    // which is what makes the strip reversible and history render an image-only assistant turn.
    private AttachmentRef? AssistantImageRef(ChatMessage message)
        => message.AdditionalProperties?.TryGetValue(AssistantImageIdKey, out var value) == true
            && value?.ToString() is { Length: > 0 } id
            && _state.State.GeneratedImages.TryGetValue(id, out var reference)
            ? reference
            : null;
}
