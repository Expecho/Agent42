using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Registers the outbound webhook (ADR 0094) — the feature's own startup wiring, kept out of <c>Program.cs</c> for
/// the S3776 reason <see cref="HindsightRegistration"/> records.
/// </summary>
public static class WebhookRegistration
{
    /// <summary>
    /// The named client, the secret protector and the sender. <see cref="IAgentWebhookSender"/> is a mandatory grain
    /// dependency (rule 7); the endpoints resolve both services optionally.
    ///
    /// <para><b>The guard is the control, and it is wired here or nowhere.</b> The webhook URL is owner config, but
    /// unlike a provider endpoint it is guarded (ADR 0094 says why): the primary handler refuses every non-public
    /// address at CONNECT time, after DNS, so a hostname that resolves inward is caught too. Redirects are off — a
    /// redirect on a signed POST is meaningless, and following one would re-open the URL-string bypass. The
    /// AddServiceDefaults retry is removed (rule 8): the sender runs its own bounded loop. A source-text test pins all
    /// three lines, because a guard that is built but not wired still ships green (the ADR 0066 lesson).</para>
    /// </summary>
    public static void AddAgentWebhooks(this IServiceCollection services)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(HttpAgentWebhookSender.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = GuardedDocumentFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable);
                handler.AllowAutoRedirect = false;
                // A proxy would make ConnectCallback dial the PROXY, not the target, and the guard would vet the wrong
                // address. None is configured today; this keeps it that way for this client whatever the environment says.
                handler.UseProxy = false;
                return handler;
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.AddSingleton<IWebhookSecretProtector, WebhookSecretProtector>();
        services.AddSingleton<IAgentWebhookSender, HttpAgentWebhookSender>();
    }
}
