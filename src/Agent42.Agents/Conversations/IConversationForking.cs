using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The pure halves of edit, regenerate and fork (ADR 0092), owned by the grain activation (ADR 0106): removing the last
/// whole turn and what indexes off it, computing a fork's payload, and copying a fork's blobs under the new owner key.
/// The grain keeps the refusals, the persist pipeline and the session binding, and orchestrates the three public
/// methods.
/// </summary>
public interface IConversationForking
{
    /// <summary>Removes the last whole session turn and everything in <paramref name="context"/> that indexes off it.</summary>
    void RemoveLastTurn(EditContext context, List<int> userIndices);

    /// <summary>A deep copy of <paramref name="artifacts"/> minus <paramref name="excluded"/>, for a fork payload.</summary>
    List<ArtifactRecord> ArtifactsWithout(
        IReadOnlyDictionary<string, ArtifactRecord> artifacts, List<(string ArtifactId, int Version)> excluded);

    /// <summary>The generated images the kept messages still reference.</summary>
    Dictionary<string, AttachmentRef> KeptGeneratedImages(
        List<ChatMessage> kept, IReadOnlyDictionary<string, AttachmentRef> images);

    /// <summary>One blob copied under <paramref name="ownerKey"/>, once per id. A failed copy keeps the id.</summary>
    Task<AttachmentRef> CopyForkBlobAsync(
        AttachmentRef source, string ownerKey, Dictionary<string, AttachmentRef> copied);

    /// <summary>The fork's artifacts with every version's blob copied; a version whose blob is missing is dropped.</summary>
    Task<Dictionary<string, ArtifactRecord>> CopyForkArtifactsAsync(ConversationFork fork, string ownerKey);
}

/// <summary>What a last-turn removal mutates: the session list and the three state collections that index off it.</summary>
public sealed record EditContext(
    List<ChatMessage> Messages,
    List<List<AttachmentRef>> TurnAttachments,
    Dictionary<string, AttachmentRef> GeneratedImages,
    Dictionary<string, ArtifactRecord> Artifacts);
