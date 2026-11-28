namespace Agent42.Agents;

/// <summary>
/// The narrow Blob seam under <see cref="ChatTurnStore"/> (ADR 0153): the replay log of a chat turn in flight. Like
/// <see cref="IFleetBlobs"/> it holds NO logic, so the Azure and file adapters stay thin shells and the store's tests run
/// over <see cref="InMemoryChatTurnBlobs"/>. It differs from the fleet seam in two ways the pump depends on: an append is
/// <b>conditional</b> (a retried Azure append is not idempotent), and a listing carries the last-modified time (the sweeper
/// needs the age).
/// </summary>
public interface IChatTurnBlobs
{
    /// <summary>The blob's bytes, or null when it doesn't exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>The blob's current length, or null when it doesn't exist.</summary>
    Task<long?> GetLengthAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bytes from <paramref name="offset"/> on and the blob's total length, or null when it doesn't exist. No growth
    /// is an empty slice, never an error (an adapter must not throw for a range past the end).
    /// </summary>
    Task<ChatTurnSlice?> GetRangeAsync(string key, long offset, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a blob.</summary>
    Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Creates an empty append blob when none exists; an existing blob is left as it is. The only way a log comes into being.</summary>
    Task CreateAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends only when the blob exists and is exactly <paramref name="expectedOffset"/> bytes long. A <b>missing</b> blob is
    /// <see cref="ChatTurnAppendOutcome.Missing"/> at ANY offset, 0 included, and is never created here: that is the delete fence
    /// (ADR 0153, D11). A turn that has not flushed anything yet appends at offset 0, so "0 creates" would let a purge be undone.
    /// </summary>
    Task<ChatTurnAppendResult> AppendAsync(string key, byte[] content, long expectedOffset, CancellationToken cancellationToken = default);

    /// <summary>The blobs whose key starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<ChatTurnBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Deletes a blob; a missing blob is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>A tail of a blob and the blob's total length.</summary>
public sealed record ChatTurnSlice(byte[] Bytes, long Length);

/// <summary>What a conditional append did.</summary>
public enum ChatTurnAppendOutcome
{
    /// <summary>The bytes were appended.</summary>
    Appended,

    /// <summary>The blob exists but isn't <c>expectedOffset</c> long; <see cref="ChatTurnAppendResult.Length"/> is its length.</summary>
    OffsetMismatch,

    /// <summary>The blob doesn't exist: the turn was purged, so nothing is written and nothing is created.</summary>
    Missing,

    /// <summary>
    /// Store level only (<see cref="ChatTurnStore.AppendAsync"/>): the batch would pass the log cap, so only the whole lines that fit
    /// were written (possibly none); <see cref="ChatTurnAppendResult.Length"/> is the log's length after.
    /// </summary>
    Truncated,
}

/// <summary>The outcome of one conditional append, and the blob's length after it (null when it is missing).</summary>
public sealed record ChatTurnAppendResult(ChatTurnAppendOutcome Outcome, long? Length);

/// <summary>A blob key, its size and when it last changed.</summary>
public sealed record ChatTurnBlobItem(string Key, long Size, DateTimeOffset LastModified);
