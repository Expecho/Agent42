using System.Text.RegularExpressions;
using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>
/// Registering a Signal number for a chat connection (ADR 0111, phase 3). Signal has no bot token to paste: the user
/// registers a spare number of their own in the gateway's signal-cli daemon (<c>register</c>, then the code Signal sends
/// by SMS or voice, then <c>verify</c>), and the verified number becomes the connection's credential. The generic
/// create/edit endpoints refuse a pasted Signal credential, so this is the only way one is stored.
///
/// <para><b>One number, one connection</b> (<see cref="ISignalNumberDirectoryGrain"/>): the number is claimed before
/// <c>register</c> is called, because a <c>register</c> over someone else's number would reset their registration. A
/// claim another connection holds is a generic 409 that doesn't say whose.</para>
///
/// <para>Behind Easy Auth like the rest of the gateway's management; the user is <c>GetUserId()</c>. Only the instance
/// holding the gateway lease runs the daemon, so another answers 503 (a revision rollover).</para>
/// </summary>
public static partial class AgentApi
{
    private static void MapChatGatewaySignalEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat-connections/{id}/signal/register", RegisterSignalNumberAsync);
        app.MapPost("/api/chat-connections/{id}/signal/verify", VerifySignalNumberAsync);
    }

    /// <summary>Where a user gets the captcha token Signal asks for before it sends a code.</summary>
    public const string SignalCaptchaUrl = "https://signalcaptchas.org/registration/generate.html";

    private static async Task<IResult> RegisterSignalNumberAsync(
        string id, SignalRegisterRequest request, IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        var context = await SignalContextAsync(id, grains, services, http);
        if (context.Problem is { } problem)
        {
            return problem;
        }

        if (NormalizeSignalNumber(request.Number) is not { } number)
        {
            return ChatBadRequest("Enter the number in international format, starting with + and the country code.");
        }

        var (connection, parts, rpc) = (context.Connection!, context.Parts!, context.Rpc!);
        if (parts.Protector.Unprotect(connection.CredentialsCiphertext)?.BotToken is { } current && current != number)
        {
            return ChatBadRequest("This connection already has a number. Delete it and add a new connection for another number.");
        }

        var userId = http.GetUserId();
        var claim = grains.GetGrain<ISignalNumberDirectoryGrain>(number);
        if (!await claim.TryClaimAsync(userId, connection.Id, parts.Clock.GetUtcNow()))
        {
            return Results.Text("That number is already in use in Agent42.", "text/plain", statusCode: StatusCodes.Status409Conflict);
        }

        // Switching to another number: the earlier, never-verified one is no longer this connection's to hold.
        if (connection.PendingSignalNumber is { } previous && previous != number)
        {
            await grains.GetGrain<ISignalNumberDirectoryGrain>(previous).ReleaseAsync(connection.Id);
        }

        var store = ChatConnections(grains, http);
        await store.SetPendingSignalNumberAsync(connection.Id, number, parts.Clock.GetUtcNow());
        var answer = await CallSignalAsync(() => rpc.RegisterAsync(number, Trimmed(request.Captcha), request.Voice, http.RequestAborted));
        if (answer is not { Ok: true })
        {
            // A registration that didn't start must not squat the number; a working connection keeps its own claim.
            if ((await claim.GetAsync())?.State == SignalNumberClaimState.Pending)
            {
                await claim.ReleaseAsync(connection.Id);
                await store.SetPendingSignalNumberAsync(connection.Id, null, parts.Clock.GetUtcNow());
            }

            return SignalRegisterProblem(answer);
        }

        return await SignalViewAsync(store, connection.Id, parts);
    }

    private static async Task<IResult> VerifySignalNumberAsync(
        string id, SignalVerifyRequest request, IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        var context = await SignalContextAsync(id, grains, services, http);
        if (context.Problem is { } problem)
        {
            return problem;
        }

        var (connection, parts, rpc) = (context.Connection!, context.Parts!, context.Rpc!);
        if (connection.PendingSignalNumber is not { } number)
        {
            return ChatBadRequest("Start the registration first: enter the number and request a code.");
        }

        var code = new string((request.Code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (code.Length != 6)
        {
            return ChatBadRequest("Enter the six-digit code Signal sent.");
        }

        // The pending claim may have expired and been taken while the user fetched the code: check before verifying, so a
        // number another connection now holds is never verified (and its registration disturbed) on this one's behalf.
        var claim = grains.GetGrain<ISignalNumberDirectoryGrain>(number);
        if ((await claim.GetAsync())?.ConnectionId != connection.Id)
        {
            return Results.Text("This registration expired. Request a new code.", "text/plain", statusCode: StatusCodes.Status409Conflict);
        }

        var verified = await CallSignalAsync(() => rpc.VerifyAsync(number, code, Trimmed(request.Pin), http.RequestAborted));
        if (verified is not { Ok: true })
        {
            return verified is null
                ? SignalUnavailable()
                : ChatBadRequest($"Signal didn't accept the code: {verified.Message}");
        }

        var aci = (await rpc.ListAccountsAsync(http.RequestAborted))?.FirstOrDefault(a => a.Number == number)?.Aci;
        if (aci is null)
        {
            return Results.Text("Signal verified the number, but the daemon doesn't list it yet. Verify again in a moment.",
                "text/plain", statusCode: StatusCodes.Status502BadGateway);
        }

        if (!await claim.ActivateAsync(connection.Id))
        {
            return Results.Text("That number is already in use in Agent42.", "text/plain", statusCode: StatusCodes.Status409Conflict);
        }

        var store = ChatConnections(grains, http);
        await store.SetCredentialsAsync(connection.Id, parts.Protector.Protect(new ChatCredentials(number)),
            new ChatBotIdentity(aci, number), parts.Clock.GetUtcNow());
        if (RequestContext.Get(UserTelemetryContext.RequestContextKey) is UserTelemetryContext telemetry)
        {
            await store.SetOwnerTelemetryAsync(connection.Id, telemetry);
        }

        await parts.Control.RestartAsync(http.GetUserId(), connection.Id, http.RequestAborted);
        return await SignalViewAsync(store, connection.Id, parts);
    }

    /// <summary>
    /// The deleted connection's Signal number: unregistered and its keys deleted by the adapter, then its claim released.
    /// Called by the delete endpoint after the session stopped. Never throws.
    /// </summary>
    private static async Task ForgetChatConnectionAsync(
        ChatConnection connection, ChatGatewayParts parts, IGrainFactory grains, CancellationToken cancellationToken)
    {
        var credentials = parts.Protector.Unprotect(connection.CredentialsCiphertext);
        var number = connection.Platform == ChatPlatformKind.Signal ? credentials?.BotToken ?? connection.PendingSignalNumber : null;
        var claim = number is { Length: > 0 } ? grains.GetGrain<ISignalNumberDirectoryGrain>(number) : null;

        // Only the claim's holder may make the daemon forget a number. An abandoned registration's claim can have expired
        // and been taken by another connection since; forgetting it then would unregister that connection's number.
        var ownsNumber = claim is null || (await claim.GetAsync())?.ConnectionId == connection.Id;
        if (ownsNumber && parts.Adapters.GetValueOrDefault(connection.Platform) is IChatPlatformCleanup cleanup)
        {
            await cleanup.ForgetAsync(connection, credentials, cancellationToken);
        }

        if (claim is not null)
        {
            await claim.ReleaseAsync(connection.Id);
        }
    }

    /// <summary>A pasted credential for a platform that only accepts registered ones (Signal), as a 400; otherwise null.</summary>
    private static IResult? RefusedPastedCredentials(string? platform, ChatCredentialsRequest? credentials)
        => platform == ChatPlatformKind.Signal && credentials is not null
            ? ChatBadRequest("Signal numbers are registered, not pasted. Use the Signal registration steps.")
            : null;

    /// <summary>
    /// <c>+</c>, then a country code and the subscriber number: 7 to 15 digits (E.164). Spaces, dashes, dots and
    /// parentheses people type are dropped; a leading <c>00</c> becomes <c>+</c>.
    /// </summary>
    public static string? NormalizeSignalNumber(string? typed)
    {
        var compact = SignalNumberNoise().Replace(typed ?? "", "");
        if (compact.StartsWith("00", StringComparison.Ordinal))
        {
            compact = "+" + compact[2..];
        }

        return SignalNumberShape().IsMatch(compact) ? compact : null;
    }

    [GeneratedRegex(@"[\s\-().]")]
    private static partial Regex SignalNumberNoise();

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex SignalNumberShape();

    // Everything both endpoints need, or the problem to answer with.
    private static async Task<SignalEndpointContext> SignalContextAsync(
        string id, IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        if (ChatGatewayParts.From(services) is not { } parts || services.GetService<SignalRpcClient>() is not { } rpc)
        {
            return SignalEndpointContext.Fail(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        if (await ChatConnections(grains, http).GetAsync(id) is not { Platform: ChatPlatformKind.Signal } connection)
        {
            return SignalEndpointContext.Fail(Results.NotFound());
        }

        return parts.Control.HoldsLease
            ? new SignalEndpointContext(connection, parts, rpc, null)
            : SignalEndpointContext.Fail(SignalUnavailable());
    }

    // null when the daemon could not be used here (no lease, or it didn't start).
    private static async Task<SignalRpcResult?> CallSignalAsync(Func<Task<SignalRpcResult>> call)
    {
        try
        {
            return await call();
        }
        catch (SignalDaemonUnavailableException)
        {
            return null;
        }
    }

    private static IResult SignalRegisterProblem(SignalRpcResult? answer) => answer switch
    {
        null => SignalUnavailable(),
        { IsCaptchaRequired: true } => ChatBadRequest(
            $"Signal wants a captcha first. Solve it at {SignalCaptchaUrl}, copy the signalcaptcha:// link it gives you, and paste it here."),
        { IsRateLimited: true } => Results.Text("Signal is rate limiting registrations for this number. Try again later.",
            "text/plain", statusCode: StatusCodes.Status429TooManyRequests),
        _ => ChatBadRequest($"Signal didn't start the registration: {answer.Message}"),
    };

    private static IResult SignalUnavailable()
        => Results.Text("Signal isn't ready on this Agent42 right now. Try again in a minute.", "text/plain",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> SignalViewAsync(IUserChatConnectionsGrain store, string connectionId, ChatGatewayParts parts)
        => await store.GetAsync(connectionId) is { } saved
            ? Results.Json(ChatConnectionStatus.View(saved, parts.Control.IsLive(saved.Id), parts.Clock.GetUtcNow()))
            : Results.NotFound();

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Either everything an endpoint needs (Problem null), or the problem it answers with.
    private sealed record SignalEndpointContext(
        ChatConnection? Connection, ChatGatewayParts? Parts, SignalRpcClient? Rpc, IResult? Problem)
    {
        public static SignalEndpointContext Fail(IResult problem) => new(null, null, null, problem);
    }
}
