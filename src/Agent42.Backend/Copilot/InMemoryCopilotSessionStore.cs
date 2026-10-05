using System.Collections.Concurrent;
using System.Text;

namespace Agent42.Backend;

/// <summary>
/// The in-process <see cref="ICopilotSessionStore"/>, for tests and DevHost. The same role
/// <c>InMemoryAttachmentStore</c> plays for Blob. Not durable: a restart forgets every transcript.
/// </summary>
public sealed class InMemoryCopilotSessionStore : ICopilotSessionStore
{
    private readonly ConcurrentDictionary<string, (string Content, DateTimeOffset Modified)> _files = new(StringComparer.Ordinal);

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_files.TryGetValue(key, out var file) ? file.Content : null);

    public Task WriteAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        _files[key] = (content, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task AppendAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        _files.AddOrUpdate(key, _ => (content, DateTimeOffset.UtcNow), (_, file) => (file.Content + content, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public Task<CopilotFileInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_files.TryGetValue(key, out var file)
            ? new CopilotFileInfo(Encoding.UTF8.GetByteCount(file.Content), file.Modified)
            : null);

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([.. _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))]);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _files.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
