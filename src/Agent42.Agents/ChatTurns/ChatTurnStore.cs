using System.Text.Json;

namespace Agent42.Agents;

/// <summary>
/// ALL the log logic of the resumable chat turn (ADR 0153) over the <see cref="IChatTurnBlobs"/> seam, so the contract tests
/// cover it over <see cref="InMemoryChatTurnBlobs"/> and the adapters stay shells (the <see cref="FleetStore"/> shape). The
/// layout, per conversation, with both ids escaped with <see cref="Uri.EscapeDataString(string)"/> so none can add a level:
/// <code>
/// {userId}/{conversationId}/active.json            (a hint: the newest turn's id, last writer wins)
/// {userId}/{conversationId}/{turnId}.meta.json
/// {userId}/{conversationId}/{turnId}.ndjson        (append blob, one raw event per line)
/// </code>
/// The store is the truth and the pump's in-process feed is an optimisation (D2): a reader on any replica can rebuild what a
/// turn said from here. Raw events are stored and coalescing happens at replay time only (D3).
/// </summary>
public sealed class ChatTurnStore(IChatTurnBlobs blobs, ChatTurnOptions options, TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string MetaSuffix = ".meta.json";
    private const string LogSuffix = ".ndjson";

    /// <summary>
    /// Creates the turn: its empty log (so a later append to a missing log is a purge fence), its meta (running) and the
    /// active pointer. Cleans the conversation's earlier turns that are already terminal; a running one is never touched,
    /// because the grain accepts a second concurrent turn and its log must stay readable.
    /// </summary>
    public async Task<ChatTurnMeta> StartAsync(
        string userId, string conversationId, string turnId, string ownerReplicaId, CancellationToken cancellationToken = default)
    {
        await DeleteTerminalTurnsAsync(userId, conversationId, cancellationToken);

        var now = time.GetUtcNow();
        var meta = new ChatTurnMeta(turnId, ChatTurnStatus.Running, now, ownerReplicaId, now);
        await blobs.CreateAsync(LogKey(userId, conversationId, turnId), cancellationToken);
        await WriteMetaAsync(userId, conversationId, meta, cancellationToken);
        await blobs.PutAsync(ActiveKey(userId, conversationId), JsonSerializer.SerializeToUtf8Bytes(new ActivePointer(turnId), JsonOptions), cancellationToken);
        return meta;
    }

    /// <summary>
    /// Appends one batch at <paramref name="expectedOffset"/>. Over the log cap nothing is written and the outcome is
    /// <see cref="ChatTurnAppendOutcome.Truncated"/>. A batch that landed but whose answer was lost (a timed-out Azure
    /// append, D12) is recognised by the log's length and reported as appended, never written twice. A missing log is
    /// <see cref="ChatTurnAppendOutcome.Missing"/>: the turn was purged and is not recreated (D11). Anything else the blob
    /// seam throws is the caller's to retry.
    /// </summary>
    public async Task<ChatTurnAppendResult> AppendAsync(
        string userId, string conversationId, string turnId, byte[] batch, long expectedOffset, CancellationToken cancellationToken = default)
    {
        if (expectedOffset + batch.Length > options.MaxLogBytes)
        {
            return await AppendWhatFitsAsync(userId, conversationId, turnId, batch, expectedOffset, cancellationToken);
        }

        var key = LogKey(userId, conversationId, turnId);
        var landed = expectedOffset + batch.Length;
        try
        {
            var result = await blobs.AppendAsync(key, batch, expectedOffset, cancellationToken);
            return result is { Outcome: ChatTurnAppendOutcome.OffsetMismatch, Length: { } length } && length == landed
                ? new ChatTurnAppendResult(ChatTurnAppendOutcome.Appended, landed)
                : result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ambiguous: the bytes may have landed. If the log is exactly as long as a landed batch would make it, they did.
            if (await TryProbeLengthAsync(key, cancellationToken) == landed)
            {
                return new ChatTurnAppendResult(ChatTurnAppendOutcome.Appended, landed);
            }

            throw;
        }
    }

    // A batch that would pass the cap still lands as far as it fits, in whole lines, so a turn that emits everything inside one
    // flush keeps its start instead of losing all of it. The answer is Truncated either way, with the log's length after.
    private async Task<ChatTurnAppendResult> AppendWhatFitsAsync(
        string userId, string conversationId, string turnId, byte[] batch, long expectedOffset, CancellationToken cancellationToken)
    {
        var room = (int)Math.Max(0, Math.Min(batch.Length, options.MaxLogBytes - expectedOffset));
        var fits = room == 0 ? -1 : Array.LastIndexOf(batch, (byte)'\n', room - 1);
        if (fits < 0)
        {
            return new ChatTurnAppendResult(ChatTurnAppendOutcome.Truncated, expectedOffset);
        }

        var written = await AppendAsync(userId, conversationId, turnId, batch[..(fits + 1)], expectedOffset, cancellationToken);
        return written.Outcome == ChatTurnAppendOutcome.Appended
            ? new ChatTurnAppendResult(ChatTurnAppendOutcome.Truncated, written.Length)
            : written;
    }

    /// <summary>Writes the turn's meta. Only the pump's writer calls this; a reader never does (D4).</summary>
    public Task WriteMetaAsync(string userId, string conversationId, ChatTurnMeta meta, CancellationToken cancellationToken = default)
        => blobs.PutAsync(MetaKey(userId, conversationId, meta.TurnId), JsonSerializer.SerializeToUtf8Bytes(meta, JsonOptions), cancellationToken);

    /// <summary>
    /// Writes the meta of a RUNNING turn (heartbeat, flags, terminal status) unless the turn's log is gone: false means the
    /// conversation was purged under it, and nothing was written (the same fence as an append, D11). Check-then-write, so a purge
    /// landing between the two can leave one small meta blob with no conversation text in it; the sweeper removes it.
    /// </summary>
    public async Task<bool> UpdateMetaAsync(string userId, string conversationId, ChatTurnMeta meta, CancellationToken cancellationToken = default)
    {
        if (await blobs.GetLengthAsync(LogKey(userId, conversationId, meta.TurnId), cancellationToken) is null)
        {
            return false;
        }

        await WriteMetaAsync(userId, conversationId, meta, cancellationToken);
        return true;
    }

    /// <summary>The turn's meta as stored, or null when there is none (never started, purged or swept).</summary>
    public async Task<ChatTurnMeta?> GetMetaAsync(string userId, string conversationId, string turnId, CancellationToken cancellationToken = default)
    {
        var content = await blobs.GetAsync(MetaKey(userId, conversationId, turnId), cancellationToken);
        return content is null ? null : JsonSerializer.Deserialize<ChatTurnMeta>(content, JsonOptions);
    }

    /// <summary>The conversation's most recently started turn, or null. A hint: with two concurrent turns it names the later start.</summary>
    public async Task<ChatTurnMeta?> GetActiveAsync(string userId, string conversationId, CancellationToken cancellationToken = default)
    {
        var content = await blobs.GetAsync(ActiveKey(userId, conversationId), cancellationToken);
        return content is not null && JsonSerializer.Deserialize<ActivePointer>(content, JsonOptions) is { TurnId: { Length: > 0 } turnId }
            ? await GetMetaAsync(userId, conversationId, turnId, cancellationToken)
            : null;
    }

    /// <summary>
    /// The status a reader should report: a running turn whose heartbeat is older than
    /// <see cref="ChatTurnOptions.StaleAfter"/> is <c>interrupted</c>. Evaluated at read time and never written back, because a
    /// reader that rewrote it would corrupt a live turn on another replica (D4).
    /// </summary>
    public ChatTurnStatus StatusOf(ChatTurnMeta meta)
        => meta.Status == ChatTurnStatus.Running && time.GetUtcNow() - meta.LastHeartbeatUtc > options.StaleAfter
            ? ChatTurnStatus.Interrupted
            : meta.Status;

    /// <summary>The log from <paramref name="offset"/> on and its total length, or null when the log doesn't exist.</summary>
    public Task<ChatTurnSlice?> ReadFromAsync(string userId, string conversationId, string turnId, long offset, CancellationToken cancellationToken = default)
        => blobs.GetRangeAsync(LogKey(userId, conversationId, turnId), offset, cancellationToken);

    /// <summary>Deletes every blob of the conversation: its logs, metas and pointer. Called when the conversation is deleted (D11).</summary>
    public async Task PurgeAsync(string userId, string conversationId, CancellationToken cancellationToken = default)
    {
        foreach (var item in await blobs.ListAsync(ConversationPrefix(userId, conversationId), cancellationToken))
        {
            await blobs.DeleteAsync(item.Key, cancellationToken);
        }
    }

    /// <summary>
    /// Deletes every blob last changed before <paramref name="olderThan"/> (D10). A running turn stamps its meta every
    /// heartbeat and its log every flush, so it is never old; a turn whose owner died is, and goes with the rest. Idempotent,
    /// so sweeps on several replicas are harmless. Returns how many blobs went.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var item in await blobs.ListAsync(string.Empty, cancellationToken))
        {
            if (item.LastModified < olderThan)
            {
                await blobs.DeleteAsync(item.Key, cancellationToken);
                deleted++;
            }
        }

        return deleted;
    }

    private async Task DeleteTerminalTurnsAsync(string userId, string conversationId, CancellationToken cancellationToken)
    {
        var prefix = ConversationPrefix(userId, conversationId);
        foreach (var item in await blobs.ListAsync(prefix, cancellationToken))
        {
            if (!item.Key.EndsWith(MetaSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var turnId = Uri.UnescapeDataString(item.Key[prefix.Length..^MetaSuffix.Length]);
            if (await GetMetaAsync(userId, conversationId, turnId, cancellationToken) is { Status: not ChatTurnStatus.Running })
            {
                await blobs.DeleteAsync(item.Key, cancellationToken);
                await blobs.DeleteAsync(LogKey(userId, conversationId, turnId), cancellationToken);
            }
        }
    }

    private async Task<long?> TryProbeLengthAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await blobs.GetLengthAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string ConversationPrefix(string userId, string conversationId)
        => $"{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(conversationId)}/";

    private static string ActiveKey(string userId, string conversationId) => ConversationPrefix(userId, conversationId) + "active.json";

    private static string MetaKey(string userId, string conversationId, string turnId)
        => ConversationPrefix(userId, conversationId) + Uri.EscapeDataString(turnId) + MetaSuffix;

    private static string LogKey(string userId, string conversationId, string turnId)
        => ConversationPrefix(userId, conversationId) + Uri.EscapeDataString(turnId) + LogSuffix;

    private sealed record ActivePointer(string TurnId);
}
