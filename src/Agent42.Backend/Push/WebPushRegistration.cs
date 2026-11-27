using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Registers Web Push (ADR 0028) — the one startup decision the Push feature owns: whether push is on at all.
///
/// <para>Lifted out of <c>Program.cs</c> (cc-06), the <see cref="HindsightRegistration"/> shape: a feature's
/// <c>if</c>/<c>else</c> belongs in the feature's folder, not in the composition root.</para>
/// </summary>
public static class WebPushRegistration
{
    /// <summary>
    /// Registers <see cref="PushConfig"/> and the sender. The VAPID private key is the ONLY secret and is optional
    /// and not on the availability path (the <c>GOOGLE_MAPS_API_KEY</c> shape, NOT <c>SEARXNG_SECRET</c>'s): with no key
    /// the app starts, <see cref="PushConfig.Enabled"/> is false, <c>GET /api/push/config</c> returns
    /// <c>enabled:false</c>, the UI hides the toggle, and the no-op sender does nothing. The public key + subject are
    /// non-secret (committed in appsettings.json); the browser fetches the public key from that endpoint.
    /// <see cref="PushConfig.Enabled"/> is the single source of the "push is on" fact: the same signal that decides
    /// real-vs-null sender here and what the endpoint reports.
    /// </summary>
    public static void AddWebPush(this IServiceCollection services, IConfiguration configuration)
    {
        var vapidPublicKey = configuration["Push:VapidPublicKey"] ?? "";
        var vapidPrivateKey = configuration["Push:VapidPrivateKey"] ?? "";
        var vapidSubject = configuration["Push:VapidSubject"] ?? "";

        // "Enabled" must mean "actually usable", so it requires BOTH the private key (the secret that lets us
        // send) AND the committed public key (which the browser needs to subscribe). Gating on the private key
        // alone would let a half-configured operator — secret set but public key not yet pasted — ship a visible
        // toggle whose Enable click calls pushManager.subscribe with an empty applicationServerKey and silently
        // fails. With either missing, push is simply off (the optional-secret degradation).
        var pushEnabled = !string.IsNullOrWhiteSpace(vapidPrivateKey) && !string.IsNullOrWhiteSpace(vapidPublicKey);

        services.AddSingleton(new PushConfig(pushEnabled, vapidPublicKey, vapidSubject));

        if (!pushEnabled)
        {
            services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
            return;
        }

        // Same explicit-timeout / no-retry discipline as the web + geocoding clients, and for the same
        // reason: the send runs inside a NON-REENTRANT AgentGrain activation, so a retried send to a dead push
        // endpoint would multiply the stall on the grain. The timeout matches the sender's whole-fan-out budget;
        // RemoveAllResilienceHandlers is what actually opts out of the inherited 3-retry pipeline.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(WebPushClientAdapter.HttpClientName, http =>
        {
            http.Timeout = WebPushNotificationSender.SendBudget;
        })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.AddSingleton<IWebPushClient>(sp => new WebPushClientAdapter(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebPushClientAdapter.HttpClientName),
            vapidSubject, vapidPublicKey, vapidPrivateKey));
        services.AddSingleton<IPushNotificationSender, WebPushNotificationSender>();
        // The Settings "Send test" path: one device, reports its result (resolved optionally by /api/push/test).
        services.AddSingleton<PushTestNotifier>();
    }
}
