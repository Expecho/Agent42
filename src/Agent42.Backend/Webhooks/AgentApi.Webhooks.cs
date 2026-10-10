using System.Net;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The stored agent's outbound webhook (ADR 0094): read, save (minting the signing secret once), remove, and send a
/// test. Every route sits behind Easy Auth and takes the user from <c>GetUserId()</c>, and each answers 404 unless the
/// agent is in the caller's own registry, so a webhook grain key can never be reached for someone else's agent.
/// </summary>
public static partial class AgentApi
{
    private static void MapWebhookEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agents/{id}/webhook", GetWebhookAsync);
        app.MapPut("/api/agents/{id}/webhook", PutWebhookAsync);
        app.MapDelete("/api/agents/{id}/webhook", DeleteWebhookAsync);
        app.MapPost("/api/agents/{id}/webhook/test", TestWebhookAsync);
    }

    // The URL, whether a secret exists, and the delivery log newest first. Never the secret or its ciphertext.
    private static async Task<IResult> GetWebhookAsync(string id, IGrainFactory grains, HttpContext http)
        => await OwnedAgentAsync(grains, http, id) is null
            ? Results.NotFound()
            : Results.Json(await Webhook(grains, http, id).GetAsync());

    // Saves the URL. The secret is minted on the first save and on rotateSecret, protected here so the grain only ever
    // holds ciphertext, and returned in THIS response only. IServiceProvider rather than the protector itself, for the
    // /api/speech reason: a test host without it must still route the whole API, and answers 503 here.
    private static async Task<IResult> PutWebhookAsync(
        string id, WebhookUpsertRequest request, IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        if (WebhookUrlProblem(request.Url) is { } problem)
        {
            return Results.Text(problem, "text/plain", statusCode: StatusCodes.Status400BadRequest);
        }

        if (services.GetService<IWebhookSecretProtector>() is not { } protector)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (await OwnedAgentAsync(grains, http, id) is null)
        {
            return Results.NotFound();
        }

        var webhook = Webhook(grains, http, id);
        var url = request.Url.Trim();
        var secret = request.RotateSecret || !(await webhook.GetAsync()).HasSecret ? WebhookSigner.GenerateSecret() : null;
        await webhook.SaveAsync(url, secret is null ? null : protector.Protect(secret));
        return Results.Json(new WebhookSaveResponse(url, true, secret));
    }

    private static async Task<IResult> DeleteWebhookAsync(string id, IGrainFactory grains, HttpContext http)
    {
        if (await OwnedAgentAsync(grains, http, id) is null)
        {
            return Results.NotFound();
        }

        await Webhook(grains, http, id).ClearAsync();
        return Results.NoContent();
    }

    // Sends a content-free webhook.test now. 409 when no webhook is configured. A delivery can take ~40 s, so the
    // dialog waits on its own client-side timeout.
    private static async Task<IResult> TestWebhookAsync(string id, IGrainFactory grains, HttpContext http)
    {
        if (await OwnedAgentAsync(grains, http, id) is not { } agent)
        {
            return Results.NotFound();
        }

        return await Webhook(grains, http, id).SendTestAsync(agent.Name) is { } result
            ? Results.Json(new WebhookTestResponse(
                result.StatusCode, result.Error, result.Attempts, (long)result.Duration.TotalMilliseconds))
            : Results.Text("No webhook is configured for this agent.", "text/plain",
                statusCode: StatusCodes.Status409Conflict);
    }

    private static Task<AgentDefinition?> OwnedAgentAsync(IGrainFactory grains, HttpContext http, string id)
        => grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).GetAsync(id);

    private static IAgentWebhookGrain Webhook(IGrainFactory grains, HttpContext http, string id)
        => grains.GetGrain<IAgentWebhookGrain>(AgentWebhookKeys.KeyFor(http.GetUserId(), id));

    /// <summary>
    /// Why <paramref name="url"/> cannot be a webhook, or null when it can. A UX check only: an absolute https URL with
    /// no userinfo and no private IP-literal host. The CONTROL is the connect-time guard in
    /// <see cref="WebhookRegistration"/>, which also catches a hostname that resolves inward.
    /// </summary>
    internal static string? WebhookUrlProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Trim().Length > WebhookLimits.MaxUrlLength
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return "Enter an absolute URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "The webhook URL must use https.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "Put no user name or password in the URL; the request is signed instead.";
        }

        return IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && !PrivateAddressGuard.IsPubliclyRoutable(address)
            ? "The webhook URL cannot point at a private, loopback or link-local address."
            : null;
    }
}
