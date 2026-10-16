using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The model-invoked <c>send_push_notification</c> tool (ADR 0075) — a notification whose title and body
/// the <b>model writes</b>, raised from any path including the live one.
///
/// <para><b>This is a SIBLING of <c>AgentGrain.Push.cs</c>, not an extension of it, and the file split is
/// itself the documentation.</b> That part holds <c>SendAwayReplyPushAsync</c>, which is governed by ADR
/// 0028's rule — it belongs in the two ASYNC callers and must <b>never</b> be called from
/// <c>ChatCoreAsync</c>, because it exists to reach a user who is <i>not</i> watching. That rule is
/// <b>narrowed by ADR 0075, not repealed</b>: it governs <c>SendAwayReplyPushAsync</c> specifically, and it
/// keeps both of its placement traps (<c>EnqueuePromptAsync</c>'s <c>_modelId</c> gate;
/// <c>ReceiveReminder</c>'s post-<c>WriteStateAsync</c> position). A model-invoked notification is a
/// different thing — the model asked for it, about something it decided was worth interrupting for — so it
/// may fire from any path, the live turn included. Do not merge these two files, and do not "harmonise"
/// their placement rules.</para>
///
/// <para>The tool reads <see cref="IUserPushGrain"/> itself rather than trusting the sender, because
/// <see cref="IPushNotificationSender.SendAsync"/> returns <c>Task</c>, <b>never throws</b>, and silently
/// early-returns when the user has no subscriptions — so the sender can tell the tool nothing. Widening
/// that contract would touch four host registrations for one string; one grain read answers the only
/// user-visible question instead.</para>
///
/// <para><b>It still cannot report delivery, and must never pretend to.</b> Whether a notification arrived
/// is unknowable by design (ADR 0028's best-effort, prune-on-404 contract). "Queued to N device(s)" is the
/// strongest true statement available, and both this return value and the tool description say exactly
/// that.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// Agent tool: sends a Web Push notification to the user's subscribed devices (ADR 0075). Exposed to
    /// the model as <c>send_push_notification</c>.
    ///
    /// <para>The checks run in a deliberate order, and every one of them returns <b>without sending</b>:
    /// incognito first (it is a promise about the conversation, so it outranks everything), then the
    /// per-turn budget, then the text, then whether any device is subscribed at all. <b>The counter is
    /// incremented only on an actual send</b>, so a model that writes three unusable titles has not spent
    /// its allowance on them.</para>
    ///
    /// <para>The <see cref="CancellationToken"/> is supplied by <c>AIFunctionFactory</c> from the
    /// invocation context and kept out of the JSON schema, so the model never sees it — the same
    /// arrangement as <c>GetCurrentLocationAsync</c>. It is here because the grain is non-reentrant and an
    /// abandoned turn should not hold the activation for the send's full budget.</para>
    /// </summary>
    private async Task<string> SendPushNotificationAsync(
        [Description("The notification title — what the user reads first, on a locked screen, with no context. Put the actual news in it: 'Build 412 failed', not 'Update'. At most 100 characters; a longer one is shortened.")] string title,
        [Description("One or two sentences of detail under the title. At most 300 characters; a longer one is shortened. Plain text only — line breaks and blank lines are collapsed to spaces, because a notification is one paragraph.")] string body,
        CancellationToken cancellationToken)
    {
        // Incognito (ADR 0034), first and unconditionally. ADR 0034 names a Web Push, by itself, as
        // breaking its guarantee — which is why it refuses scheduling. Two concrete reasons it is right
        // about this tool too: the title and body become a DURABLE, plaintext artifact in the OS
        // notification shade, derived from a conversation that promised to leave no trace; and an incognito
        // chat has no sidebar row, so the notification would be the only handle to it, on any device — the
        // exact opposite of "not listed". This UPHOLDS 0034 rather than amending it. Do not "improve" it
        // into an allow by analogy with images, which persist as accepted blob bytes and are not text on a
        // lock screen.
        if (_ephemeral)
        {
            return "I cannot send a notification from an incognito chat. A notification would leave the "
                + "title and body sitting in the device's notification shade, which is exactly the trace an "
                + "incognito chat promises not to leave. Turn off incognito if you want notifications.";
        }

        // Per-turn budget (ADR 0075). Refused BEFORE any work, and without incrementing: this is a
        // ceiling on notifications SENT, not on calls attempted.
        if (_pushesThisTurn >= PushToolLimits.MaxPushesPerTurn)
        {
            return $"I have already sent {PushToolLimits.MaxPushesPerTurn} notifications during this turn, "
                + "which is the limit. Do not try again in this turn — put anything else you wanted to say "
                + "in your reply instead.";
        }

        var cleanTitle = PushToolLimits.SanitizeNotificationText(title, PushToolLimits.MaxTitleLength);
        var cleanBody = PushToolLimits.SanitizeNotificationText(body, PushToolLimits.MaxBodyLength);

        // Nothing usable survived sanitizing — an empty argument, or one made entirely of control, format
        // or unrenderable characters. A blank notification is worse than none: it is an interruption that
        // says nothing and cannot be explained.
        if (cleanTitle.Length == 0 || cleanBody.Length == 0)
        {
            return "That notification had no usable text left after cleaning, so I did not send it. Both "
                + "the title and the body must contain ordinary readable characters.";
        }

        // Read the subscriptions directly (see the class remarks — the sender cannot tell us this). With
        // none, there is no device to reach, and the model needs to know its notification went nowhere
        // rather than assuming the user was told.
        var devices = await GrainFactory.GetGrain<IUserPushGrain>(_userId).GetAllAsync();
        if (devices.Count == 0)
        {
            return "The user has no devices subscribed to notifications, so there was nothing to send to. "
                + "Tell them what you wanted to notify them about in your reply instead, and mention they "
                + "can turn notifications on in Settings if they want them in future.";
        }

        // Incremented HERE, on the send — see the budget guard above.
        _pushesThisTurn++;

        await _pushSender.SendAsync(
            _userId,
            new PushMessage(cleanTitle, cleanBody, ConversationIdFromGrainKey(this.GetPrimaryKeyString())),
            cancellationToken);

        // Never log the title or the body: they are model-authored text about whatever this user is doing,
        // and this repo stamps user identity onto every span (ADR 0010) — the same rule
        // GetCurrentLocationAsync carries for the coordinates. Log that the tool ran and how many devices
        // it queued to, nothing else. PushTool_DoesNotLogNotificationText pins this.
        _logger.LogInformation(
            "Agent grain {GrainKey} queued a model-invoked push notification to {Devices} device(s) ({Sent} this turn)",
            this.GetPrimaryKeyString(), devices.Count, _pushesThisTurn);

        // "Queued", never "delivered" or "sent to the user" — see the class remarks. The title is echoed
        // so the model can see what actually went out after sanitizing and capping.
        return $"Notification queued to {devices.Count} device(s), titled \"{cleanTitle}\". Web Push is "
            + "best-effort and there is no delivery receipt, so do not tell the user it definitely arrived.";
    }
}
