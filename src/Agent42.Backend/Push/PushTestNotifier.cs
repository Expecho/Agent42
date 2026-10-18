using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>The <see cref="PushTestResponse.Outcome"/> values of a test push.</summary>
public static class PushTestOutcomes
{
    /// <summary>The push service accepted the push. Not proof of display: Web Push has no receipt.</summary>
    public const string Sent = "sent";

    /// <summary>The endpoint is not stored for the caller, so nothing was sent.</summary>
    public const string NotRegistered = "not-registered";

    /// <summary>The push service answered 404/410; the subscription was pruned.</summary>
    public const string Gone = "gone";

    /// <summary>Any other failure; <see cref="PushTestResponse.StatusCode"/> carries the HTTP status if there was one.</summary>
    public const string Failed = "failed";

    /// <summary>The send did not finish within <see cref="WebPushNotificationSender.SendBudget"/>.</summary>
    public const string Timeout = "timeout";
}

/// <summary>
/// Sends a fixed test notification to <b>one</b> of the caller's stored subscriptions and reports the push
/// service's verdict (the Settings "Send test" button).
///
/// <para>Deliberately not <see cref="IPushNotificationSender"/>: that sender fans out to every device and
/// swallows every failure because it runs after a persisted turn inside a non-reentrant grain. A test that
/// cannot report failure is useless, so this calls <see cref="IWebPushClient"/> directly from the HTTP
/// request and maps each failure to an outcome. It never throws for a send failure.</para>
/// </summary>
public sealed class PushTestNotifier
{
    internal const string Title = "Agent 42";
    internal const string Body = "Test notification: push works on this device.";

    private readonly IGrainFactory _grainFactory;
    private readonly IWebPushClient _client;
    private readonly ILogger<PushTestNotifier> _logger;

    public PushTestNotifier(IGrainFactory grainFactory, IWebPushClient client, ILogger<PushTestNotifier> logger)
    {
        _grainFactory = grainFactory;
        _client = client;
        _logger = logger;
    }

    public async Task<PushTestResponse> SendAsync(string userId, string endpoint, CancellationToken cancellationToken)
    {
        var grain = _grainFactory.GetGrain<IUserPushGrain>(userId);
        PushSubscription? subscription;
        try
        {
            subscription = (await grain.GetAllAsync()).FirstOrDefault(s => s.Endpoint == endpoint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test push: reading the caller's subscriptions failed.");
            return new PushTestResponse(PushTestOutcomes.Failed);
        }

        if (subscription is null)
        {
            return new PushTestResponse(PushTestOutcomes.NotRegistered);
        }

        // An empty conversationId: the SW's click handler just focuses the app, and push.js ignores it.
        var payload = JsonSerializer.Serialize(
            new WebPushNotificationSender.PushPayload(Title, Body, ""), WebPushNotificationSender.PayloadJson);
        return await SendOneAsync(grain, subscription, payload, cancellationToken);
    }

    private async Task<PushTestResponse> SendOneAsync(
        IUserPushGrain grain, PushSubscription subscription, string payload, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(WebPushNotificationSender.SendBudget);
        try
        {
            await _client.SendAsync(subscription, payload, budget.Token);
            return new PushTestResponse(PushTestOutcomes.Sent);
        }
        catch (PushDeliveryException ex) when (ex.StatusCode is 404 or 410)
        {
            // Same prune as the real sender: the browser dropped this subscription. A failed prune still
            // reports "gone" rather than surfacing as a 500; the next send prunes it again.
            try
            {
                await grain.RemoveAsync(subscription.Endpoint);
            }
            catch (Exception pruneEx)
            {
                _logger.LogWarning(pruneEx, "Test push: pruning a gone subscription failed.");
            }
            return new PushTestResponse(PushTestOutcomes.Gone, ex.StatusCode);
        }
        catch (PushDeliveryException ex)
        {
            _logger.LogWarning(ex, "Test push rejected by the push service.");
            return new PushTestResponse(PushTestOutcomes.Failed, ex.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget or the named HttpClient's own Timeout (a TaskCanceledException) expired.
            return new PushTestResponse(PushTestOutcomes.Timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // DNS/connect errors arrive as HttpRequestException, untranslated by the adapter.
            _logger.LogWarning(ex, "Test push failed.");
            return new PushTestResponse(PushTestOutcomes.Failed);
        }
    }
}
