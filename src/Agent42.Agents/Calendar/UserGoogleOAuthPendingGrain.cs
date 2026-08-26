using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// One in-flight Google authorization (ADR 0053), persisted between <c>/api/google/oauth/start</c> and
/// <c>/callback</c>. Holds the PKCE verifier and the CSRF <see cref="State"/>.
///
/// The literal shape of <see cref="McpOAuthPending"/>, minus the fields the Google flow does not need:
/// Google's authorization and token endpoints are <b>constants</b> (no PRM/RFC-8414 discovery), so
/// there is nothing to carry forward from start to callback except the verifier, the state and where to
/// send the browser back to.
///
/// Like the MCP one, the verifier is short-lived and single-use, so it is stored plaintext in its own
/// per-user state — never the session — and consumed on the first matching callback.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.GoogleOAuthPending")]
public sealed record GoogleOAuthPending(
    [property: Id(0)] string State,
    [property: Id(1)] string CodeVerifier,
    [property: Id(2)] string RedirectUri,
    [property: Id(3)] DateTimeOffset StartedAt);

[GenerateSerializer]
[Alias("Agent42.Agents.UserGoogleOAuthPendingState")]
public sealed class UserGoogleOAuthPendingState
{
    [Id(0)]
    public List<GoogleOAuthPending> Pending { get; set; } = [];
}

/// <summary>
/// Per-user store of pending Google authorizations, keyed by user id. Entries are <b>one-time</b>:
/// <see cref="TakeAsync"/> returns and removes the entry for a <c>state</c>, so a replayed callback
/// finds nothing. The list is bounded so a user cannot accumulate unbounded entries by starting flows
/// they never finish.
/// </summary>
[Alias("Agent42.Agents.IUserGoogleOAuthPendingGrain")]
public interface IUserGoogleOAuthPendingGrain : IGrainWithStringKey
{
    /// <summary>Stores a pending authorization; evicts the oldest if the bounded list is full.</summary>
    [Alias("PutAsync")]
    Task PutAsync(GoogleOAuthPending pending);

    /// <summary>Returns and REMOVES the pending entry for <paramref name="state"/>, or null if none.</summary>
    [Alias("TakeAsync")]
    Task<GoogleOAuthPending?> TakeAsync(string state);
}

/// <summary>
/// Rides the shared "agent-state" storage provider on a distinct "google-oauth-pending" state name — a
/// separate Cosmos item from the 2 MB session (ADR 0004), no new container.
/// </summary>
public sealed class UserGoogleOAuthPendingGrain : Grain, IUserGoogleOAuthPendingGrain
{
    // Bounds how many concurrent unfinished authorizations a user can hold; oldest is evicted first.
    private const int MaxPending = 8;

    private readonly IPersistentState<UserGoogleOAuthPendingState> _state;

    public UserGoogleOAuthPendingGrain(
        [PersistentState("google-oauth-pending", AgentGrain.StorageProviderName)] IPersistentState<UserGoogleOAuthPendingState> state)
    {
        _state = state;
    }

    public async Task PutAsync(GoogleOAuthPending pending)
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

    public async Task<GoogleOAuthPending?> TakeAsync(string state)
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
