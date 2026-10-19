using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Edit the last message, regenerate the last reply, and fork a conversation (ADR 0092): the logic that needs no grain
/// (ADR 0106).
///
/// <para><b>Edit and regenerate truncate the last WHOLE session turn in place</b> — the user message and everything
/// after it — the tail mirror of the retention prune in <see cref="HistoryRetention"/>, which mutates the same
/// <c>TryGetInMemoryChatHistory</c> list. Whole turns only, so a <see cref="FunctionCallContent"/> never loses its
/// <see cref="FunctionResultContent"/>. Everything the session does not hold is maintained by hand:
/// <c>TurnAttachments</c> (positional), <c>GeneratedImages</c> and the artifact versions the removed messages created.
/// The grain handles the incognito ordinals and the search index.</para>
///
/// <para><b>A fork never touches the source.</b> It cuts a COPY of the session after a whole exchange, and the new
/// grain copies every blob it keeps under its own owner key, because the attachment and artifact proxies resolve a
/// blob by <c>(ownerKey, id)</c>.</para>
/// </summary>
public sealed class ConversationForking(IAttachmentStore attachments, ILogger logger) : IConversationForking
{
    /// <summary>
    /// The concurrency token: how many user turns the client saw, AND the hash of the last one's text. A count alone
    /// can be replayed — a second tab loaded at count N, sending after an edit restored the count to N, would
    /// silently replace the freshly generated turn. The hash makes the token an identity.
    /// </summary>
    public static bool TokenMatches(
        IReadOnlyList<string> visibleUserTexts, int expectedUserTurnCount, string? expectedLastUserTextHash)
    {
        var hash = HistoryTextHash(visibleUserTexts.Count == 0 ? "" : visibleUserTexts[^1]);
        return visibleUserTexts.Count == expectedUserTurnCount
            && string.Equals(hash, expectedLastUserTextHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hex SHA-256 of a turn's text as UTF-8: the same value the browser computes with
    /// <c>crypto.subtle.digest</c> for the concurrency token.
    /// </summary>
    public static string HistoryTextHash(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Every (artifactId, version) that a create_artifact or update_artifact call in these messages stored. Taken
    /// from the tool RESULTS, joined to their calls by CallId (a result arrives on a later message than its call).
    /// A refused call returns plain text and contributes nothing.
    /// </summary>
    public static List<(string ArtifactId, int Version)> ArtifactVersionsCreatedIn(List<ChatMessage> messages)
    {
        var calls = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(c => c.Name is AgentGrain.CreateArtifactTool or AgentGrain.UpdateArtifactTool)
            .Select(c => c.CallId)
            .ToHashSet(StringComparer.Ordinal);

        return [.. messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Where(r => calls.Contains(r.CallId))
            .Select(r => AgentGrain.ParseArtifactResult(r.Result))
            .OfType<ArtifactInfo>()
            .Select(info => (info.ArtifactId, info.Version))];
    }

    public void RemoveLastTurn(EditContext context, List<int> userIndices)
    {
        var messages = context.Messages;
        var start = userIndices[^1];
        var removed = messages.GetRange(start, messages.Count - start);

        foreach (var message in removed)
        {
            if (AgentGrain.AssistantImageId(message) is { } imageId)
            {
                context.GeneratedImages.Remove(imageId);
            }
        }

        foreach (var (artifactId, version) in ArtifactVersionsCreatedIn(removed))
        {
            RemoveArtifactVersion(context.Artifacts, artifactId, version);
        }

        messages.RemoveRange(start, messages.Count - start);

        // TRUNCATED to the remaining user-turn count, never "remove the last entry": BuildUserTurnAsync appends a
        // turn's entry before its run, so a run that failed without storing its user message leaves an orphan
        // trailing entry, and dropping only the last one would keep the real turn's refs and mis-join the next one.
        var remaining = userIndices.Count - 1;
        if (context.TurnAttachments.Count > remaining)
        {
            context.TurnAttachments.RemoveRange(remaining, context.TurnAttachments.Count - remaining);
        }
    }

    public List<ArtifactRecord> ArtifactsWithout(
        IReadOnlyDictionary<string, ArtifactRecord> artifacts, List<(string ArtifactId, int Version)> excluded)
    {
        var copies = artifacts.Values.ToDictionary(
            a => a.Id,
            a => new ArtifactRecord
            {
                Id = a.Id,
                Kind = a.Kind,
                Title = a.Title,
                Language = a.Language,
                CreatedAt = a.CreatedAt,
                Versions = [.. a.Versions],
            },
            StringComparer.Ordinal);

        foreach (var (artifactId, version) in excluded)
        {
            RemoveArtifactVersion(copies, artifactId, version);
        }

        return [.. copies.Values];
    }

    // They are keyed by a message-level id (ADR 0020).
    public Dictionary<string, AttachmentRef> KeptGeneratedImages(
        List<ChatMessage> kept, IReadOnlyDictionary<string, AttachmentRef> images)
    {
        var keptImages = new Dictionary<string, AttachmentRef>(StringComparer.Ordinal);
        foreach (var message in kept)
        {
            if (AgentGrain.AssistantImageId(message) is { } id && images.TryGetValue(id, out var reference))
            {
                keptImages[id] = reference;
            }
        }

        return keptImages;
    }

    // A blob that cannot be copied keeps its id under the new key and is logged: one missing image must not lose the
    // fork, and the proxy then answers 404 for it exactly as the source's would.
    public async Task<AttachmentRef> CopyForkBlobAsync(
        AttachmentRef source, string ownerKey, Dictionary<string, AttachmentRef> copied)
    {
        if (copied.TryGetValue(source.Id, out var done))
        {
            return done;
        }

        AttachmentRef copy;
        try
        {
            copy = await attachments.CopyAsync(source, ownerKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent grain {GrainKey} could not copy attachment {AttachmentId} into a fork",
                ownerKey, source.Id);
            copy = source with { BlobPath = $"{ownerKey}/{source.Id}" };
        }

        copied[source.Id] = copy;
        return copy;
    }

    // A version holds only an attachment id, so its ref is looked up under the SOURCE key first; a version whose blob
    // is missing or cannot be copied is dropped and logged, never fatal — the same rule CopyForkBlobAsync applies to
    // images.
    public async Task<Dictionary<string, ArtifactRecord>> CopyForkArtifactsAsync(ConversationFork fork, string ownerKey)
    {
        var artifacts = new Dictionary<string, ArtifactRecord>(StringComparer.Ordinal);
        foreach (var record in fork.Artifacts)
        {
            var versions = new List<ArtifactVersionRef>();
            foreach (var version in record.Versions)
            {
                if (await TryCopyArtifactVersionAsync(fork.SourceOwnerKey, version, ownerKey))
                {
                    versions.Add(version);
                }
                else
                {
                    logger.LogWarning(
                        "Agent grain {GrainKey} dropped artifact {ArtifactId} v{Version} from a fork: its blob is missing",
                        ownerKey, record.Id, version.Version);
                }
            }

            if (versions.Count > 0)
            {
                record.Versions = versions;
                artifacts[record.Id] = record;
            }
        }

        return artifacts;
    }

    // Drops one version, and the record with it once it has none left. Its blob is orphaned (the purge gap).
    private static void RemoveArtifactVersion(Dictionary<string, ArtifactRecord> artifacts, string artifactId, int version)
    {
        if (!artifacts.TryGetValue(artifactId, out var record))
        {
            return;
        }

        record.Versions.RemoveAll(v => v.Version == version);
        if (record.Versions.Count == 0)
        {
            artifacts.Remove(artifactId);
        }
    }

    private async Task<bool> TryCopyArtifactVersionAsync(string sourceOwnerKey, ArtifactVersionRef version, string ownerKey)
    {
        try
        {
            if (await attachments.GetAsync(sourceOwnerKey, version.AttachmentId) is not { } source)
            {
                return false;
            }

            await attachments.CopyAsync(source, ownerKey);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent grain {GrainKey} could not copy artifact blob {AttachmentId} into a fork",
                ownerKey, version.AttachmentId);
            return false;
        }
    }
}
