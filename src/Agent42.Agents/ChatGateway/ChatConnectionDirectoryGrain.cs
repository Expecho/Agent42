using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.ChatConnectionDirectoryState")]
public sealed class ChatConnectionDirectoryState
{
    [Id(0)]
    public List<ChatConnectionRef> Entries { get; set; } = [];
}

/// <summary>
/// The global run list of chat connections (ADR 0111); see <see cref="IChatConnectionDirectoryGrain"/>. Rides the
/// shared "agent-state" storage provider on its own state name, like the other directories, so no new container.
/// </summary>
public sealed class ChatConnectionDirectoryGrain : Grain, IChatConnectionDirectoryGrain
{
    private readonly IPersistentState<ChatConnectionDirectoryState> _state;

    public ChatConnectionDirectoryGrain(
        [PersistentState("chat-connection-directory", AgentGrain.StorageProviderName)] IPersistentState<ChatConnectionDirectoryState> state)
    {
        _state = state;
    }

    public async Task RegisterAsync(string userId, string connectionId)
    {
        var entry = new ChatConnectionRef(userId, connectionId);
        if (_state.State.Entries.Contains(entry))
        {
            return;
        }

        _state.State.Entries.Add(entry);
        await _state.WriteStateAsync();
    }

    public async Task DeregisterAsync(string userId, string connectionId)
    {
        if (_state.State.Entries.RemoveAll(e => e.UserId == userId && e.ConnectionId == connectionId) > 0)
        {
            await _state.WriteStateAsync();
        }
    }

    public Task<IReadOnlyList<ChatConnectionRef>> ListAsync()
        => Task.FromResult<IReadOnlyList<ChatConnectionRef>>(_state.State.Entries.ToList());
}
