using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Real <see cref="IPushNotificationSender"/> (ADR 0028): fans a <see cref="PushMessage"/> out to every
/// device the user has subscribed (<see cref="IUserPushGrain"/>), over the RFC 8291-encrypted Web Push
/// transport (<see cref="IWebPushClient"/>), and prunes subscriptions the push service reports gone.
///
/// <para><b>Best effort — a push failure never propagates into the turn.</b> By the time this runs the
/// reply is already persisted (ADR 0004), so a slow/dead push service must cost the user nothing. Every
/// failure is swallowed and logged. Because <c>AgentGrain</c> is non-reentrant the whole fan-out is
/// bounded by a short <see cref="SendBudget"/> and the sends run concurrently, so N dead endpoints cost
/// one budget, not N timeouts serially.</para>
/// </summary>
public sealed class WebPushNotificationSender : IPushNotificationSender
{
    /// <summary>
    /// Total wall-clock budget for one fan-out. Short because the send blocks a non-reentrant
    /// <c>AgentGrain</c> activation — an undelivered push is not worth stalling the grain over.
    /// </summary>
    public static readonly TimeSpan SendBudget = TimeSpan.FromSeconds(5);

    private readonly IGrainFactory _grainFactory;
    private readonly IWebPushClient _client;
    private readonly ILogger<WebPushNotificationSender> _logger;

    public WebPushNotificationSender(
        IGrainFactory grainFactory, IWebPushClient client, ILogger<WebPushNotificationSender> logger)
    {
        _grainFactory = grainFactory;
        _client = client;
        _logger = logger;
    }

    public async Task SendAsync(string userId, PushMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var grain = _grainFactory.GetGrain<IUserPushGrain>(userId);
            var subscriptions = await grain.GetAllAsync();
            if (subscriptions.Count == 0)
            {
                return;
            }

            // The service worker reads title/body/conversationId off event.data.json() (ADR 0028, sw.js).
            var payload = JsonSerializer.Serialize(
                new PushPayload(message.Title, message.Body, message.ConversationId), PayloadJson);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(SendBudget);

            await Task.WhenAll(subscriptions.Select(
                subscription => SendOneAsync(grain, subscription, payload, budget.Token)));
        }
        catch (Exception ex)
        {
            // The fan-out itself failing (e.g. the grain read) must never fault the turn either.
            _logger.LogWarning(ex, "Push fan-out failed for user; the reply was already delivered.");
        }
    }

    private async Task SendOneAsync(
        IUserPushGrain grain, PushSubscription subscription, string payload, CancellationToken cancellationToken)
    {
        try
        {
            await _client.SendAsync(subscription, payload, cancellationToken);
        }
        catch (PushDeliveryException ex) when (ex.StatusCode is 404 or 410)
        {
            // 404/410 = the browser dropped this subscription. Prune it so we stop pushing to a dead
            // endpoint. RemoveAsync takes no token — pruning must complete even if the budget expired.
            await grain.RemoveAsync(subscription.Endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push send to one subscription failed; swallowed.");
        }
    }

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerOptions.Web);

    private sealed record PushPayload(string Title, string Body, string ConversationId);
}
