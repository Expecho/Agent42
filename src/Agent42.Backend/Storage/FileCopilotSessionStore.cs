using System.Text;

namespace Agent42.Backend;

/// <summary>
/// <see cref="ICopilotSessionStore"/> on the local volume (<c>Storage:Provider = local</c>): the runtime's session files under
/// the copilot-sessions folder, keyed <c>{userId}/{conversationId}/{path}</c> exactly as <see cref="BlobCopilotSessionStore"/>
/// keys them. A file has no append-count cap here, so the Blob store's 50,000-block ceiling does not apply.
/// </summary>
public sealed class FileCopilotSessionStore(LocalFileStore files) : ICopilotSessionStore
{
    private const string Folder = "copilot-sessions/";

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await files.ReadAsync(Folder + key, cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public Task WriteAsync(string key, string content, CancellationToken cancellationToken = default)
        => files.WriteAsync(Folder + key, Encoding.UTF8.GetBytes(content), cancellationToken);

    public Task AppendAsync(string key, string content, CancellationToken cancellationToken = default)
        => files.AppendAsync(Folder + key, Encoding.UTF8.GetBytes(content), cancellationToken);

    public Task<CopilotFileInfo?> GetInfoAsync(string key, CancellationToken cancellationToken = default)
    {
        var info = files.GetInfo(Folder + key);
        return Task.FromResult(info is null ? null : new CopilotFileInfo(info.Size, info.Modified));
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([.. files.List(Folder + prefix).Select(i => i.Key[Folder.Length..])]);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => files.DeleteAsync(Folder + key);
}
