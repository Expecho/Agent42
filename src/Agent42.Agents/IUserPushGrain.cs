namespace Agent42.Agents;

/// <summary>
/// Stores a signed-in user's Web Push subscriptions (ADR 0028) — one entry per subscribed device.
/// The grain key is the user id, like <see cref="IUserProviderConfigGrain"/> and
/// <see cref="IUserProfileGrain"/>.
///
/// <para>It is a <b>per-device collection</b>, not a scalar preference, which is why it rides its own
/// grain rather than <see cref="IUserProfileGrain"/> (ADR 0005 keeps that a singleton). It shares the
/// "agent-state" storage provider on a distinct "push-subs" state name, so it is a separate Cosmos item
/// from the 2 MB session (ADR 0004) — exactly as <see cref="IUserProviderConfigGrain"/> uses "providers"
/// and <see cref="IUserMcpConfigGrain"/> uses "mcp-servers".</para>
///
/// <para>Subscriptions are stored in <b>cleartext</b> (ADR 0028): a subscription is a device push
/// capability usable only against our own VAPID key pair, not a third-party account credential like the
/// provider/MCP secrets the repo DP-encrypts, and it is never echoed to another user. The send path both
/// reads all subscriptions and prunes dead ones (404/410 from the push service).</para>
/// </summary>
[Alias("Agent42.Agents.IUserPushGrain")]
public interface IUserPushGrain : IGrainWithStringKey
{
    /// <summary>Returns every stored subscription for the user (empty when never subscribed).</summary>
    [Alias("GetAllAsync")]
    Task<IReadOnlyList<PushSubscription>> GetAllAsync();

    /// <summary>
    /// Upserts a subscription <b>by endpoint</b> — the endpoint is the push service's unique URL for
    /// that device, so re-subscribing the same device (fresh <c>p256dh</c>/<c>auth</c>) replaces rather
    /// than duplicates. Persists.
    /// </summary>
    [Alias("AddAsync")]
    Task AddAsync(PushSubscription subscription);

    /// <summary>
    /// Removes the subscription with <paramref name="endpoint"/> — called by the UI on unsubscribe and
    /// by the sender when the push service reports the subscription gone. Persists only if one was removed.
    /// </summary>
    [Alias("RemoveAsync")]
    Task RemoveAsync(string endpoint);
}
