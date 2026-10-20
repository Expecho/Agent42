using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// One in-flight "Sign in with GitHub" (ADR 0108), persisted between <c>/api/providers/github/start</c> and
/// <c>/callback</c>: the CSRF <see cref="State"/>, the PKCE verifier and the redirect URI the code is bound to. Short-lived
/// and single-use, so stored plaintext in its own per-user state (never the session), like
/// <see cref="McpOAuthPending"/>.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.GitHubOAuthPending")]
public sealed record GitHubOAuthPending(
    [property: Id(0)] string State,
    [property: Id(1)] string CodeVerifier,
    [property: Id(2)] string RedirectUri);

/// <summary>
/// Per-user store of pending GitHub sign-ins, keyed by user id. One-time: <see cref="TakeAsync"/> returns and removes the
/// entry for a <c>state</c>, so a replayed callback finds nothing. Bounded, so unfinished sign-ins can't accumulate.
/// Separate from <see cref="IUserMcpOAuthPendingGrain"/>, so neither callback can consume the other's state.
/// </summary>
[Alias("Agent42.Agents.IUserGitHubOAuthPendingGrain")]
public interface IUserGitHubOAuthPendingGrain : IGrainWithStringKey
{
    /// <summary>Stores a pending sign-in; evicts the oldest if the bounded list is full.</summary>
    [Alias("PutAsync")]
    Task PutAsync(GitHubOAuthPending pending);

    /// <summary>Returns and REMOVES the pending entry for <paramref name="state"/>, or null if none.</summary>
    [Alias("TakeAsync")]
    Task<GitHubOAuthPending?> TakeAsync(string state);
}

[GenerateSerializer]
[Alias("Agent42.Agents.UserGitHubOAuthPendingState")]
public sealed class UserGitHubOAuthPendingState
{
    [Id(0)]
    public List<GitHubOAuthPending> Pending { get; set; } = [];
}

/// <summary>
/// Rides the shared "agent-state" storage provider on a distinct "github-oauth-pending" state name — a separate Cosmos
/// item from the 2 MB session (ADR 0004), no new container.
/// </summary>
public sealed class UserGitHubOAuthPendingGrain : Grain, IUserGitHubOAuthPendingGrain
{
    private const int MaxPending = 4;

    private readonly IPersistentState<UserGitHubOAuthPendingState> _state;

    public UserGitHubOAuthPendingGrain(
        [PersistentState("github-oauth-pending", AgentGrain.StorageProviderName)] IPersistentState<UserGitHubOAuthPendingState> state)
    {
        _state = state;
    }

    public async Task PutAsync(GitHubOAuthPending pending)
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

    public async Task<GitHubOAuthPending?> TakeAsync(string state)
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
