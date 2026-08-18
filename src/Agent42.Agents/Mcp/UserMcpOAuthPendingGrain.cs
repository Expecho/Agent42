using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// One in-flight interactive-OAuth authorization (config-driven remote MCP servers, Phase 3), persisted
/// between <c>/api/mcp/oauth/start</c> and <c>/callback</c>. Holds the PKCE verifier, the CSRF
/// <see cref="State"/>, and everything the callback needs to exchange the code without re-discovering.
/// Secrets here (the verifier) are short-lived and single-use, so this is stored plaintext in its own
/// per-user state, never the session — it is consumed (removed) on the first matching callback.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.McpOAuthPending")]
public sealed record McpOAuthPending(
    [property: Id(0)] string State,
    [property: Id(1)] string ServerId,
    [property: Id(2)] string CodeVerifier,
    [property: Id(3)] string ClientId,
    [property: Id(4)] string TokenEndpoint,
    [property: Id(5)] string RedirectUri,
    [property: Id(6)] IReadOnlyList<string> Scopes,
    [property: Id(7)] string Resource);

/// <summary>
/// Per-user store of pending interactive-OAuth authorizations, keyed by user id (like
/// <see cref="IUserMcpConfigGrain"/>). Entries are one-time: <see cref="TakeAsync"/> returns and removes the
/// entry for a <c>state</c>, so a replayed callback finds nothing. The pending list is bounded so a user
/// cannot accumulate unbounded entries by starting flows they never finish.
/// </summary>
[Alias("Agent42.Agents.IUserMcpOAuthPendingGrain")]
public interface IUserMcpOAuthPendingGrain : IGrainWithStringKey
{
    /// <summary>Stores a pending authorization; evicts the oldest if the bounded list is full.</summary>
    [Alias("PutAsync")]
    Task PutAsync(McpOAuthPending pending);

    /// <summary>Returns and REMOVES the pending entry for <paramref name="state"/>, or null if none.</summary>
    [Alias("TakeAsync")]
    Task<McpOAuthPending?> TakeAsync(string state);
}

[GenerateSerializer]
[Alias("Agent42.Agents.UserMcpOAuthPendingState")]
public sealed class UserMcpOAuthPendingState
{
    [Id(0)]
    public List<McpOAuthPending> Pending { get; set; } = [];
}

/// <summary>
/// Rides the shared "agent-state" storage provider on a distinct "mcp-oauth-pending" state name — a
/// separate Cosmos item from the 2 MB session (ADR 0004), no new container.
/// </summary>
public sealed class UserMcpOAuthPendingGrain : Grain, IUserMcpOAuthPendingGrain
{
    // Bounds how many concurrent unfinished authorizations a user can hold; oldest is evicted first.
    private const int MaxPending = 8;

    private readonly IPersistentState<UserMcpOAuthPendingState> _state;

    public UserMcpOAuthPendingGrain(
        [PersistentState("mcp-oauth-pending", AgentGrain.StorageProviderName)] IPersistentState<UserMcpOAuthPendingState> state)
    {
        _state = state;
    }

    public async Task PutAsync(McpOAuthPending pending)
    {
        var list = _state.State.Pending;
        list.RemoveAll(p => string.Equals(p.State, pending.State, StringComparison.Ordinal));
        list.Add(pending);
        while (list.Count > MaxPending)
        {
            list.RemoveAt(0);
        }

        await _state.WriteStateAsync();
    }

    public async Task<McpOAuthPending?> TakeAsync(string state)
    {
        var index = _state.State.Pending.FindIndex(p => string.Equals(p.State, state, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var pending = _state.State.Pending[index];
        _state.State.Pending.RemoveAt(index);
        await _state.WriteStateAsync();
        return pending;
    }
}
