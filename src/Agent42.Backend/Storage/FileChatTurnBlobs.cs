using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IChatTurnBlobs"/> on the local volume (<c>Storage:Provider = local</c>): the chat-turn store's already-escaped
/// keys become files under the chat-turns folder. It holds no logic, like <see cref="AzureChatTurnBlobs"/>; the conditional
/// append is the one primitive <see cref="LocalFileStore.AppendAtAsync"/> provides under the file's gate.
/// </summary>
public sealed class FileChatTurnBlobs(LocalFileStore files) : IChatTurnBlobs
{
    private const string Folder = "chat-turns/";

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => files.ReadAsync(Folder + key, cancellationToken);

    public Task<long?> GetLengthAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<long?>(files.GetInfo(Folder + key)?.Size);

    // A turn log is capped at ChatTurnOptions.MaxLogBytes (4 MB), so reading the file and slicing it is cheap enough.
    public async Task<ChatTurnSlice?> GetRangeAsync(string key, long offset, CancellationToken cancellationToken = default)
    {
        if (await files.ReadAsync(Folder + key, cancellationToken) is not { } bytes)
        {
            return null;
        }

        var start = (int)Math.Min(Math.Max(offset, 0), bytes.Length);
        return new ChatTurnSlice(bytes[start..], bytes.Length);
    }

    public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        => files.WriteAsync(Folder + key, content, cancellationToken);

    public Task CreateAsync(string key, CancellationToken cancellationToken = default)
        => files.CreateEmptyAsync(Folder + key, cancellationToken);

    public Task<ChatTurnAppendResult> AppendAsync(string key, byte[] content, long expectedOffset, CancellationToken cancellationToken = default)
        => files.AppendAtAsync(Folder + key, content, expectedOffset, cancellationToken);

    public Task<IReadOnlyList<ChatTurnBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChatTurnBlobItem>>(
            [.. files.List(Folder + prefix).Select(i => new ChatTurnBlobItem(i.Key[Folder.Length..], i.Size, i.Modified))]);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => files.DeleteAsync(Folder + key);
}
