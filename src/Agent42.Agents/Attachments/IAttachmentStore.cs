namespace Agent42.Agents.Attachments;

/// <summary>
/// Stores the durable bytes of a conversation attachment (an image, this phase) outside
/// the Orleans grain state. Attachments are kept out of the serialized Agent Framework
/// session — which is written to a single Cosmos item on every turn, hard-capped at 2 MB —
/// so a real photo can't blow that limit (ADR 0004). The model instead receives an
/// attachment as a short URL via <see cref="GetReadUrlAsync"/>; the browser fetches the
/// bytes through a first-party proxy backed by <see cref="OpenReadAsync"/>.
///
/// Production is blob-backed (<c>BlobAttachmentStore</c>); local dev and tests use an
/// in-memory implementation (<see cref="InMemoryAttachmentStore"/>), mirroring how
/// <see cref="IChatClientFactory"/> has a real and a canned implementation.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>
    /// Persists <paramref name="content"/> and returns a reference to it. The blob is
    /// addressed under <paramref name="ownerKey"/> (the per-user, per-conversation grain
    /// key <c>{userId}/{conversationId}</c>), so one user can never reach another's bytes.
    /// </summary>
    Task<AttachmentRef> SaveAsync(
        string ownerKey, string fileName, string mediaType, byte[] content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the reference for an already-stored attachment (its metadata, not its bytes), or
    /// null when it doesn't exist under <paramref name="ownerKey"/>. The chat endpoint uses this
    /// to resolve the ids a client sends back into refs the grain can attach — resolving them
    /// server-side (rather than trusting client-supplied blob paths) keeps attachments user-scoped.
    /// </summary>
    Task<AttachmentRef?> GetAsync(
        string ownerKey, string attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the stored bytes for the browser display proxy, or null when the attachment
    /// doesn't exist under <paramref name="ownerKey"/>. Scoping by owner key here is what
    /// keeps the display endpoint user-scoped (constraint: ADR 0001 grain-key scoping).
    /// </summary>
    Task<AttachmentContent?> OpenReadAsync(
        string ownerKey, string attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a URL the *model* can fetch the image from. In production this is a short-lived
    /// blob user-delegation SAS URL; because Azure OpenAI re-fetches a prior turn's image URL on
    /// every subsequent turn (verified by the Step 1 spike), the grain refreshes these on
    /// activation — so the URL only needs to outlive a single turn. The in-memory store returns
    /// a stable placeholder (the offline chat clients ignore image content).
    /// </summary>
    Task<Uri> GetReadUrlAsync(AttachmentRef reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// The bytes of a stored attachment plus the metadata the display proxy needs to serve
/// them. Never crosses the grain boundary (endpoint ↔ store, same process), so it is a
/// plain record — unlike <see cref="AttachmentRef"/>.
/// </summary>
public sealed record AttachmentContent(byte[] Bytes, string MediaType, string FileName);
