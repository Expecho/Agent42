using Orleans.Runtime;
using Agent42.Agents.Conversations;

namespace Agent42.Agents.Push;

/// <summary>
/// One Web Push subscription for one device (ADR 0028). Stored in cleartext — a device push capability
/// usable only against our own VAPID pair, not a third-party credential (see <see cref="IUserPushGrain"/>).
/// </summary>
/// <param name="Endpoint">The push service URL for this device — the dedup/prune key.</param>
/// <param name="P256dh">The subscription's public key (base64url), for RFC 8291 payload encryption.</param>
/// <param name="Auth">The subscription's auth secret (base64url), for RFC 8291 payload encryption.</param>
[GenerateSerializer]
[Alias("Agent42.Agents.PushSubscription")]
public sealed record PushSubscription(
    [property: Id(0)] string Endpoint,
    [property: Id(1)] string P256dh,
    [property: Id(2)] string Auth);

[GenerateSerializer]
[Alias("Agent42.Agents.UserPushState")]
public sealed class UserPushState
{
    [Id(0)]
    public List<PushSubscription> Subscriptions { get; set; } = [];
}

/// <summary>
/// Per-user Web Push subscription store (ADR 0028). Rides the shared "agent-state" storage provider on a
/// distinct "push-subs" state name — the same overlay-grain shape as <see cref="UserProviderConfigGrain"/>
/// ("providers") and <see cref="UserMcpConfigGrain"/> ("mcp-servers"), so no new Cosmos container.
/// </summary>
public sealed class UserPushGrain : Grain, IUserPushGrain
{
    private readonly IPersistentState<UserPushState> _state;

    public UserPushGrain(
        [PersistentState("push-subs", AgentGrain.StorageProviderName)] IPersistentState<UserPushState> state)
    {
        _state = state;
    }

    public Task<IReadOnlyList<PushSubscription>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<PushSubscription>>(_state.State.Subscriptions.ToList());

    public async Task AddAsync(PushSubscription subscription)
    {
        var subs = _state.State.Subscriptions;

        // Upsert by endpoint: the same device re-subscribing (rotated p256dh/auth) must replace its old
        // row, not accumulate a duplicate that the send path would then push to twice.
        var existing = subs.FindIndex(
            s => string.Equals(s.Endpoint, subscription.Endpoint, StringComparison.Ordinal));
        if (existing >= 0)
        {
            subs[existing] = subscription;
        }
        else
        {
            subs.Add(subscription);
        }

        await _state.WriteStateAsync();
    }

    public async Task RemoveAsync(string endpoint)
    {
        if (_state.State.Subscriptions.RemoveAll(
                s => string.Equals(s.Endpoint, endpoint, StringComparison.Ordinal)) > 0)
        {
            await _state.WriteStateAsync();
        }
    }
}
