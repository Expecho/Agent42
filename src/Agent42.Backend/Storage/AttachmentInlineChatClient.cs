using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>Size ceilings for the images inlined into one model request (local storage only).</summary>
/// <param name="PerImageBytes">One image over this is replaced by a placeholder. Under Anthropic's 5 MB per-image limit.</param>
/// <param name="TotalBytes">All images of one request together. Past it, the OLDEST images become placeholders.</param>
public sealed record AttachmentInlineOptions(long PerImageBytes = 4 * 1024 * 1024, long TotalBytes = 16 * 1024 * 1024);

/// <summary>
/// Turns the reference URLs <see cref="FileAttachmentStore"/> hands out into the image bytes a model can read, at request
/// time. The persisted session is never touched: it keeps the tiny reference URL, and only the copy of the messages sent
/// to the model carries bytes.
///
/// <para>Every historical image of a long chat would be re-inlined on every turn, so there are two ceilings
/// (<see cref="AttachmentInlineOptions"/>). An image over the per-image ceiling, or beyond the cumulative budget, is
/// replaced by a short text placeholder, oldest first, and the turn proceeds: it is never failed.</para>
/// </summary>
public sealed class AttachmentInliner(IAttachmentStore attachments, AttachmentInlineOptions options)
{
    /// <summary>The text that stands in for an image that could not be inlined.</summary>
    public const string Placeholder = "[An earlier image is not shown: it is too large to send again, or is no longer stored.]";

    /// <summary>
    /// The messages with each reference image replaced, or <paramref name="messages"/> itself (the same instance)
    /// when none carries one. Originals are never mutated.
    /// </summary>
    public async Task<IEnumerable<ChatMessage>> InlineAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var source = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var references = FindReferences(source);
        if (references.Count == 0)
        {
            return source;
        }

        // Newest first, so the budget is spent on the images the model most needs and the oldest give way.
        var resolved = new Dictionary<(int Message, int Content), DataContent?>();
        long used = 0;
        foreach (var (message, content, ownerKey, id) in Enumerable.Reverse(references))
        {
            if (used >= options.TotalBytes)
            {
                resolved[(message, content)] = null;
                continue;
            }

            var stored = await attachments.OpenReadAsync(ownerKey, id, cancellationToken);
            if (stored is null || stored.Bytes.LongLength > options.PerImageBytes || used + stored.Bytes.LongLength > options.TotalBytes)
            {
                resolved[(message, content)] = null;
                continue;
            }

            used += stored.Bytes.LongLength;
            resolved[(message, content)] = new DataContent(stored.Bytes, stored.MediaType) { Name = stored.FileName };
        }

        return Rewrite(source, resolved);
    }

    private static List<(int Message, int Content, string OwnerKey, string Id)> FindReferences(IReadOnlyList<ChatMessage> source)
    {
        var found = new List<(int, int, string, string)>();
        for (var m = 0; m < source.Count; m++)
        {
            var contents = source[m].Contents;
            for (var c = 0; c < contents.Count; c++)
            {
                if (contents[c] is UriContent { Uri: { } uri } && FileAttachmentStore.TryParseReference(uri, out var owner, out var id))
                {
                    found.Add((m, c, owner, id));
                }
            }
        }

        return found;
    }

    private static List<ChatMessage> Rewrite(IReadOnlyList<ChatMessage> source, Dictionary<(int Message, int Content), DataContent?> resolved)
    {
        var result = new List<ChatMessage>(source.Count);
        for (var m = 0; m < source.Count; m++)
        {
            var original = source[m];
            if (!resolved.Keys.Any(k => k.Message == m))
            {
                result.Add(original);
                continue;
            }

            var contents = new List<AIContent>(original.Contents.Count);
            for (var c = 0; c < original.Contents.Count; c++)
            {
                contents.Add(resolved.TryGetValue((m, c), out var data)
                    ? (data is null ? new TextContent(Placeholder) : data)
                    : original.Contents[c]);
            }

            result.Add(new ChatMessage(original.Role, contents)
            {
                AuthorName = original.AuthorName,
                MessageId = original.MessageId,
                CreatedAt = original.CreatedAt,
                AdditionalProperties = original.AdditionalProperties,
                RawRepresentation = original.RawRepresentation,
            });
        }

        return result;
    }
}

/// <summary>
/// Applies <see cref="AttachmentInliner"/> to every request, in <c>MultiProviderChatClientFactory.Build</c> just above
/// <see cref="ToolImageChatClient"/>: below OpenTelemetry (which records message contents, so a span would otherwise carry
/// every image's base64) and above the provider's own client, for every provider kind. Registered only when
/// <c>Storage:Provider</c> is <c>local</c>; with Azure storage the factory is built exactly as before.
/// </summary>
public sealed class AttachmentInlineChatClient(IChatClient inner, AttachmentInliner inliner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => await base.GetResponseAsync(await inliner.InlineAsync(messages, cancellationToken), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inlined = await inliner.InlineAsync(messages, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(inlined, options, cancellationToken))
        {
            yield return update;
        }
    }
}
