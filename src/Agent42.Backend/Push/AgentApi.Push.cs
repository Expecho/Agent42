
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// Web Push configuration and subscription storage (ADR 0028).
/// </summary>
public static partial class AgentApi
{
    private static void MapPushEndpoints(IEndpointRouteBuilder app)
    {
        // Whether push is enabled (a VAPID private key is configured on the server) and the non-secret
        // PUBLIC key + subject the browser needs to subscribe. Behind Easy Auth like every other endpoint,
        // but returns nothing sensitive — the private key never leaves the server. When disabled the UI
        // hides the toggle (the optional-secret shape: no key → app runs, feature simply off).
        app.MapGet("/api/push/config", (PushConfig config) =>
            Results.Json(new PushConfigResponse(config.Enabled, config.PublicKey)));

        // ————— configuration health (Preferences → Configuration) —————

        // Registers (upserts by endpoint) the caller's push subscription for THIS device. The subscription
        // is a device push capability, stored in cleartext in the per-user grain (ADR 0028).
        app.MapPost("/api/push/subscriptions", async (PushSubscriptionRequest request, IGrainFactory grains, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint)
                || string.IsNullOrWhiteSpace(request.P256dh)
                || string.IsNullOrWhiteSpace(request.Auth))
            {
                return Results.Text(
                    "A push subscription requires endpoint, p256dh and auth.",
                    "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await grains.GetGrain<IUserPushGrain>(http.GetUserId())
                .AddAsync(new PushSubscription(request.Endpoint, request.P256dh, request.Auth));
            return Results.NoContent();
        });

        // Removes the caller's subscription for this device (unsubscribe) — endpoint via query. The sender
        // also prunes an endpoint the push service reports gone (404/410), so this is the explicit-opt-out
        // half of the same operation.
        app.MapDelete("/api/push/subscriptions", async (string? endpoint, IGrainFactory grains, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return Results.Text(
                    "endpoint is required.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            await grains.GetGrain<IUserPushGrain>(http.GetUserId()).RemoveAsync(endpoint);
            return Results.NoContent();
        });

        // Sends a fixed test notification to THIS device (the Settings "Send test" button) and returns the
        // push service's verdict. Only an endpoint already stored for the caller is pushed. The notifier is
        // resolved optionally (the /api/speech pattern): it exists only when push is configured, and a
        // parameter a host does not register would break routing for the whole host.
        app.MapPost("/api/push/test", async (PushTestRequest request, IServiceProvider services, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                return Results.Text(
                    "endpoint is required.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var notifier = services.GetService<PushTestNotifier>();
            if (notifier is null)
            {
                return Results.Text(
                    "Push is not configured.", "text/plain", statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Json(await notifier.SendAsync(http.GetUserId(), request.Endpoint, ct));
        });

        // ————— remote MCP servers (per-user overlay, config-driven remote MCP servers) —————
    }
}
