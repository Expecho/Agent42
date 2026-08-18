namespace Agent42.Agents.Push;

/// <summary>
/// Delivers a Web Push notification to every device a user has subscribed (ADR 0028). Injected into
/// <see cref="AgentGrain"/> and called on the two <b>asynchronous</b> reply paths only — a fired
/// reminder (<c>ReceiveReminder</c>) and an external agent-run (<c>EnqueuePromptAsync</c>, ADR 0022) —
/// so a push is raised exactly when the user is <i>not</i> the one driving the turn. It is <b>never</b>
/// called from the live interactive stream (<c>ChatAsync</c>/<c>ChatCoreAsync</c>): a user actively
/// chatting already sees the reply in-stream.
///
/// <para>The interface lives in <c>Agent42.Agents</c> with the concrete <c>WebPushNotificationSender</c>
/// in <c>Agent42.Backend</c> — the same real/no-op split as <see cref="IGeocoder"/>/<see cref="IWebSearch"/>.
/// Offline and any host without a configured VAPID private key register
/// <see cref="NullPushNotificationSender"/>. <b>Every host that activates <see cref="AgentGrain"/> must
/// register an <see cref="IPushNotificationSender"/></b> or activation fails — like the
/// <see cref="IGeocoder"/>/<c>IUserMcpToolProvider</c> rule.</para>
/// </summary>
public interface IPushNotificationSender
{
    /// <summary>
    /// Sends <paramref name="message"/> to every subscription of <paramref name="userId"/>. <b>Best
    /// effort — never throws into the caller's turn.</b> The turn has already been persisted by the time
    /// this runs, so a push service being slow or down must not fault the reply. Implementations bound
    /// the whole operation with a short internal timeout (<see cref="AgentGrain"/> is non-reentrant, so
    /// this call blocks the activation) and prune subscriptions the push service reports gone.
    /// </summary>
    Task SendAsync(string userId, PushMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// The payload of one push notification. <paramref name="Title"/> is the agent's name (ADR 0028);
/// <paramref name="ConversationId"/> lets the service worker open the right conversation on tap.
/// </summary>
/// <param name="Title">Notification title — the bound agent's name, or "Agent 42" on a plain conversation.</param>
/// <param name="Body">A short generic body line.</param>
/// <param name="ConversationId">The conversation to open when the user taps the notification.</param>
public sealed record PushMessage(string Title, string Body, string ConversationId);
