using System.Collections.Concurrent;

namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="IChatTurnBlobs"/> for DevHost, tests and incognito turns (ADR 0034, 0153 D6): nothing is written
/// anywhere, and the sweeper empties it on the same schedule as the durable store.
/// </summary>
public sealed class InMemoryChatTurnBlobs(TimeProvider? time = null) : IChatTurnBlobs
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _blobs = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_blobs.TryGetValue(key, out var entry) ? entry.Bytes.ToArray() : null);

    public Task<long?> GetLengthAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<long?>(_blobs.TryGetValue(key, out var entry) ? entry.Bytes.Length : null);

    public Task<ChatTurnSlice?> GetRangeAsync(string key, long offset, CancellationToken cancellationToken = default)
    {
        if (!_blobs.TryGetValue(key, out var entry))
        {
            return Task.FromResult<ChatTurnSlice?>(null);
        }

        var start = (int)Math.Min(Math.Max(offset, 0), entry.Bytes.Length);
        return Task.FromResult<ChatTurnSlice?>(new ChatTurnSlice(entry.Bytes[start..], entry.Bytes.Length));
    }

    public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        _blobs[key] = new Entry(content.ToArray(), _time.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task CreateAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _blobs.TryAdd(key, new Entry([], _time.GetUtcNow()));
        }

        return Task.CompletedTask;
    }

    public Task<ChatTurnAppendResult> AppendAsync(string key, byte[] content, long expectedOffset, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_blobs.TryGetValue(key, out var existing))
            {
                return Task.FromResult(new ChatTurnAppendResult(ChatTurnAppendOutcome.Missing, null));
            }

            if (existing.Bytes.Length != expectedOffset)
            {
                return Task.FromResult(new ChatTurnAppendResult(ChatTurnAppendOutcome.OffsetMismatch, existing.Bytes.Length));
            }

            var grown = new Entry([.. existing.Bytes, .. content], _time.GetUtcNow());
            _blobs[key] = grown;
            return Task.FromResult(new ChatTurnAppendResult(ChatTurnAppendOutcome.Appended, grown.Bytes.Length));
        }
    }

    public Task<IReadOnlyList<ChatTurnBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChatTurnBlobItem>>(
            [.. _blobs.Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(b => b.Key, StringComparer.Ordinal)
                .Select(b => new ChatTurnBlobItem(b.Key, b.Value.Bytes.Length, b.Value.Modified))]);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _blobs.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private sealed record Entry(byte[] Bytes, DateTimeOffset Modified);
}
