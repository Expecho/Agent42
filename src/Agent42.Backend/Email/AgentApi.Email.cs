using Agent42.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Agent42.Backend;

/// <summary>
/// Inbound email (ADR 0096): Mailgun forwards a message sent to an agent's address to <c>POST /api/email-in</c>, which
/// runs that agent on it in a new listed conversation — the ADR 0022 agent-run door with the credential carried in the
/// address instead of a header. On <c>excludedPaths</c>, so it self-enforces and takes the userId from the email
/// directory, never from GetUserId.
/// </summary>
public static partial class AgentApi
{
    private static void MapEmailEndpoints(IEndpointRouteBuilder app)
    {
        // Whether inbound email is on, and the domain the editor appends to an agent's token. Optional resolution (the
        // /api/speech pattern), so a host that never registers the feature still routes.
        app.MapGet("/api/email/config", (IServiceProvider services) =>
        {
            var config = services.GetService<EmailConfig>();
            return Results.Json(new EmailConfigResponse(config?.Enabled == true, config?.Domain ?? ""));
        });

        // Bounded twice: Kestrel refuses a larger body (also one with no Content-Length), and the form reader refuses a
        // larger multipart body, before any of it is buffered. See EmailLimits.MaxRequestBytes.
        app.MapPost("/api/email-in", ReceiveEmailAsync).DisableAntiforgery()
            .WithMetadata(
                new RequestSizeLimitAttribute(EmailLimits.MaxRequestBytes),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = EmailLimits.MaxRequestBytes });
    }

    // Status codes follow Mailgun's retry rule (only 200 is success; 406 is never retried; anything else is retried for
    // 8 hours): 503 with no signing key, so mail arrives once the operator sets it; 401 for a bad, stale or replayed
    // signature, which genuine traffic only sends during a key rotation, where a retry can succeed; 406 for every
    // rejection of a correctly signed message, which no retry can fix. Unknown address and deleted agent share one
    // 406, and answering 406 rather than 401 there leaks nothing — only a holder of the signing key gets that far.
    private static async Task<IResult> ReceiveEmailAsync(
        HttpContext http, IGrainFactory grains, IAttachmentStore attachments, DocumentIngestionService ingestion,
        IServiceProvider services, ILoggerFactory loggers)
    {
        var config = services.GetService<EmailConfig>();
        var guard = services.GetService<MailgunReplayGuard>();
        if (config is null || guard is null || string.IsNullOrWhiteSpace(config.SigningKey))
        {
            // Never "skip verification": an unset optional secret must not become an optional security control.
            return Results.Text("Inbound email is not configured.", "text/plain",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Refused before the form is read: the signature is inside the body, so nothing is authenticated yet. 406, not 413,
        // because a genuine oversize message from Mailgun cannot be fixed by its 8 hours of retries.
        if (http.Request.ContentLength > EmailLimits.MaxRequestBytes)
        {
            return Results.Text("The email is too large.", "text/plain", statusCode: StatusCodes.Status406NotAcceptable);
        }

        if (!http.Request.HasFormContentType)
        {
            return EmailUnauthorized();
        }

        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var token = form["token"].ToString();
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        if (!MailgunWebhook.IsValid(config.SigningKey, form["timestamp"], token, form["signature"], now))
        {
            return EmailUnauthorized();
        }

        if (!guard.TryClaim(token, now))
        {
            // Signed, so worth a log line: a replay and a full guard both land here, and only the log tells them apart.
            loggers.CreateLogger("Agent42.Email").LogWarning(
                "Inbound email refused: Mailgun token already used, or the replay guard is full");
            return EmailUnauthorized();
        }

        try
        {
            return await AcceptEmailAsync(form, grains, attachments, ingestion, loggers.CreateLogger("Agent42.Email"));
        }
        catch
        {
            // An unexpected failure is a 500, which Mailgun retries — give the token back so the retry is not refused
            // as a replay.
            guard.Release(token);
            throw;
        }
    }

    private static async Task<IResult> AcceptEmailAsync(
        IFormCollection form, IGrainFactory grains, IAttachmentStore attachments, DocumentIngestionService ingestion,
        ILogger logger)
    {
        var (owner, definition) = await ResolveEmailRecipientAsync(form["recipient"], grains);
        if (owner is null || definition is null)
        {
            return EmailRejected(logger, "Unknown recipient.");
        }

        var (usable, skipped) = PartitionEmailFiles(form.Files, AgentKinds.IsCopilot(definition.Kind));
        string? subject = form["subject"], bodyPlain = form["body-plain"], strippedText = form["stripped-text"];
        if (!InboundEmailPrompt.HasText(subject, bodyPlain, strippedText) && usable.Count == 0)
        {
            return EmailRejected(logger, "The email has no subject, no plain-text body and no usable attachment.");
        }

        var conversationId = Guid.NewGuid().ToString();
        var (imageRefs, documentNames, storeError) =
            await StoreAgentRunFilesAsync(usable, attachments, ingestion, AgentKey(owner.UserId, conversationId));
        if (storeError is not null)
        {
            return EmailRejected(logger, storeError);
        }

        var prompt = InboundEmailPrompt.Build(
            form["from"], subject, bodyPlain, strippedText, !StringValues.IsNullOrEmpty(form["body-html"]), skipped);
        var title = InboundEmailPrompt.Title(subject, usable.FirstOrDefault()?.FileName);
        await StartAgentRunAsync(grains, owner.UserId, definition, conversationId, title, prompt, imageRefs, documentNames);

        // 200, never 202: Mailgun documents only 200 as success, and a retried email would run its turn again.
        return Results.Json(new { conversationId });
    }

    // The first address in `recipient` (the SMTP envelope RCPT TO) whose token resolves in the EMAIL directory to an
    // agent that still exists. Never the API-key directory (ADR 0096). Lowercasing happens in EmailAddress.Hash.
    private static async Task<(AgentKeyOwner? Owner, AgentDefinition? Definition)> ResolveEmailRecipientAsync(
        string? recipient, IGrainFactory grains)
    {
        var addresses = (recipient ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var address in addresses.Take(EmailLimits.MaxRecipients))
        {
            if (EmailAddress.TokenFromAddress(address) is not { } token)
            {
                continue;
            }

            var owner = await grains.GetGrain<IEmailAddressDirectoryGrain>(EmailAddress.Hash(token)).ResolveAsync();
            // The agent must still exist: a mid-failure delete can leave an orphan entry, which must fail closed.
            var definition = owner is null
                ? null
                : await grains.GetGrain<IAgentRegistryGrain>(owner.UserId).GetAsync(owner.AgentId);
            if (definition is not null)
            {
                return (owner, definition);
            }
        }

        return (null, null);
    }

    // Unlike the agent-run door, an email with a file that cannot ride the turn is not rejected whole: the sender gets
    // no reply (D1) and the owner would never learn the mail was dropped. The file is skipped and the agent told why.
    private static (List<IFormFile> Usable, List<string> Skipped) PartitionEmailFiles(IFormFileCollection files, bool copilot)
    {
        var usable = new List<IFormFile>();
        var skipped = new List<string>();
        foreach (var file in files)
        {
            if (AgentRunFileError(file, copilot) is { } error)
            {
                skipped.Add(error);
            }
            else
            {
                usable.Add(file);
            }
        }

        return (usable, skipped);
    }

    private static IResult EmailUnauthorized()
        => Results.Text("Unauthorized.", "text/plain", statusCode: StatusCodes.Status401Unauthorized);

    private static IResult EmailRejected(ILogger logger, string reason)
    {
        // The sender is never told (no reply path), so the log is the only trace a rejected email leaves.
        logger.LogWarning("Inbound email rejected: {Reason}", reason);
        return Results.Text(reason, "text/plain", statusCode: StatusCodes.Status406NotAcceptable);
    }
}
