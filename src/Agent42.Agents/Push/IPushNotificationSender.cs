namespace Agent42.Agents;

/// <summary>
/// Delivers a Web Push notification to every device a user has subscribed (ADR 0028). Injected into
/// <see cref="AgentGrain"/>, which calls it from <b>two different places with two different rules</b> —
/// and conflating them is the mistake to avoid here.
///
/// <para><b>The away-reply push</b> (<c>SendAwayReplyPushAsync</c>) is raised on the two
/// <b>asynchronous</b> reply paths only — a fired reminder (<c>ReceiveReminder</c>) and an external
/// agent-run (<c>EnqueuePromptAsync</c>, ADR 0022) — so it fires exactly when the user is <i>not</i> the
/// one driving the turn. It is <b>never</b> sent from the live interactive stream
/// (<c>ChatAsync</c>/<c>ChatCoreAsync</c>): a user actively chatting already sees the reply in-stream.</para>
///
/// <para><b>The model-invoked tool</b> (<c>send_push_notification</c>, ADR 0075) has the opposite rule: the
/// model asked for the notification, so it may fire from <b>any</b> path including the live turn. It is
/// also the reason the away-reply push above now carries an extra condition at both of its call sites —
/// it is suppressed when the tool already pushed during the turn, so one reply never stacks two entries
/// in a shade that sets no <c>tag</c>.</para>
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
/// The payload of one push notification. <paramref name="ConversationId"/> lets the service worker open
/// the right conversation on tap.
///
/// <para><b>The title and body mean different things depending on which sender built this.</b> For the
/// away-reply push (ADR 0028) they are fixed app text. For the model-invoked tool (ADR 0075) they are
/// <b>model-authored</b>, and therefore attacker-influenceable through every prompt-injection channel in
/// the app — which is why that path sanitizes and caps both before constructing this record
/// (<see cref="PushToolLimits.SanitizeNotificationText"/>). Anything constructing a <c>PushMessage</c>
/// from model or tool output must do the same.</para>
/// </summary>
/// <param name="Title">Notification title — the bound agent's name or "Agent 42" on the away-reply path; model-authored and sanitized on the tool path.</param>
/// <param name="Body">A short generic body line on the away-reply path; model-authored and sanitized on the tool path.</param>
/// <param name="ConversationId">The conversation to open when the user taps the notification.</param>
/// <param name="Url">
/// A same-origin page to open on tap instead of a conversation (ADR 0144: a fleet task's <c>/fleet/?task={id}</c>). Null on
/// every chat path. This record never crosses a grain boundary (the sender is a silo service), so it needs no <c>[Id]</c>.
/// </param>
public sealed record PushMessage(string Title, string Body, string ConversationId, string? Url = null);
