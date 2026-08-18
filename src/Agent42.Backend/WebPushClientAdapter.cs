using WebPush;
using PushSubscription = Agent42.Agents.Push.PushSubscription;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IWebPushClient"/> over the <c>WebPush</c> library (ADR 0028). The <b>only</b> file that
/// references the library — the seam keeps the package choice isolated (see <see cref="IWebPushClient"/>).
///
/// <para>The <see cref="HttpClient"/> is a named client with an explicit timeout and <b>no</b> resilience
/// pipeline (its registration in Program.cs calls <c>RemoveAllResilienceHandlers()</c>) — the same
/// discipline as <see cref="GoogleGeocoder"/>'s client, and for the same reason: the send runs inside a
/// <b>non-reentrant</b> <c>AgentGrain</c> activation (gotcha: every Backend named client silently inherits
/// <c>AddServiceDefaults</c>' 3-retry pipeline), so a retried send to a dead endpoint would quadruple the
/// stall on the user's turn.</para>
/// </summary>
public sealed class WebPushClientAdapter : IWebPushClient
{
    /// <summary>Name of the DI-registered <see cref="HttpClient"/> this adapter resolves.</summary>
    public const string HttpClientName = "web-push";

    private readonly WebPushClient _client;
    private readonly VapidDetails _vapid;

    public WebPushClientAdapter(HttpClient httpClient, string vapidSubject, string vapidPublicKey, string vapidPrivateKey)
    {
        _client = new WebPushClient(httpClient);
        _vapid = new VapidDetails(vapidSubject, vapidPublicKey, vapidPrivateKey);
    }

    public async Task SendAsync(PushSubscription subscription, string payload, CancellationToken cancellationToken)
    {
        var target = new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
        try
        {
            await _client.SendNotificationAsync(target, payload, _vapid, cancellationToken);
        }
        catch (WebPushException ex)
        {
            // Translate the library's exception to the seam's — carrying only the status the sender needs
            // to decide prune-vs-log — so nothing downstream depends on the WebPush package.
            throw new PushDeliveryException((int)ex.StatusCode, ex);
        }
    }
}
