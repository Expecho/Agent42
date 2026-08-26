using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// One user's stored Google connection (ADR 0053): the OAuth token bundle plus the identity to show
/// in Preferences → Calendar.
///
/// <para>Both token fields are stored <b>already encrypted</b> — the Backend protects them via
/// <c>IGoogleSecretProtector</c> before the grain ever sees them, exactly as
/// <see cref="StoredMcpServer"/>'s ciphertext fields do. The grain stores ciphertext, never plaintext,
/// and it never leaves this grain: <c>ICalendarService</c> carries no credential and no endpoint
/// echoes one.</para>
///
/// <para><see cref="AccountEmail"/> is the <b>primary calendar's id</b>, which for a Google account is
/// the account address — which is why the connection needs no <c>openid</c>/<c>email</c> scope.</para>
///
/// <para><b>The refresh token is the whole connection.</b> Google only returns one on a consent that
/// carried <c>access_type=offline</c> and <c>prompt=consent</c>; lose it and the connection dies at the
/// first access-token expiry, silently, an hour later. It is also why the Data Protection key ring must
/// persist out-of-process in production (ADR 0018): a rotated ring makes <c>Unprotect</c> return null,
/// and the user's calendar simply goes quiet with no error anywhere.</para>
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.GoogleConnection")]
public sealed record GoogleConnection(
    [property: Id(0)] string AccessTokenCiphertext,
    [property: Id(1)] string RefreshTokenCiphertext,
    [property: Id(2)] DateTimeOffset AccessTokenExpiresAt,
    [property: Id(3)] string? AccountEmail,
    [property: Id(4)] DateTimeOffset ConnectedAt);

[GenerateSerializer]
[Alias("Agent42.Agents.UserGoogleState")]
public sealed class UserGoogleState
{
    /// <summary>The user's connection, or null when they have never connected (or disconnected).</summary>
    [Id(0)]
    public GoogleConnection? Connection { get; set; }
}

/// <summary>
/// Per-user Google credential store, keyed by user id like <see cref="IUserPushGrain"/> and
/// <see cref="IUserMcpConfigGrain"/>. Rides the shared "agent-state" storage provider on a distinct
/// "google-credentials" state name — a separate Cosmos item from the 2 MB session (ADR 0004), so no
/// new container and no Bicep change.
/// </summary>
[Alias("Agent42.Agents.IUserGoogleGrain")]
public interface IUserGoogleGrain : IGrainWithStringKey
{
    /// <summary>Returns the stored connection, or null when the user has not connected.</summary>
    [Alias("GetAsync")]
    Task<GoogleConnection?> GetAsync();

    /// <summary>Stores (or replaces) the connection. Persists.</summary>
    [Alias("SetAsync")]
    Task SetAsync(GoogleConnection connection);

    /// <summary>
    /// Removes the connection. Idempotent — disconnecting when not connected is a no-op, not an error.
    /// This revokes nothing at Google's end; the user must also visit
    /// <c>myaccount.google.com/permissions</c> to withdraw the grant itself.
    /// </summary>
    [Alias("ClearAsync")]
    Task ClearAsync();
}

/// <inheritdoc cref="IUserGoogleGrain"/>
public sealed class UserGoogleGrain : Grain, IUserGoogleGrain
{
    private readonly IPersistentState<UserGoogleState> _state;

    public UserGoogleGrain(
        [PersistentState("google-credentials", AgentGrain.StorageProviderName)] IPersistentState<UserGoogleState> state)
    {
        _state = state;
    }

    public Task<GoogleConnection?> GetAsync() => Task.FromResult(_state.State.Connection);

    public async Task SetAsync(GoogleConnection connection)
    {
        _state.State.Connection = connection;
        await _state.WriteStateAsync();
    }

    public async Task ClearAsync()
    {
        if (_state.State.Connection is null)
        {
            return;
        }

        _state.State.Connection = null;
        await _state.WriteStateAsync();
    }
}
