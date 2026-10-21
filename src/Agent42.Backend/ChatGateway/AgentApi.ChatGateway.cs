using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>
/// Managing the user's chat bots (ADR 0111): list, create, edit (settings and write-only credentials), re-pair and
/// delete. Every route sits behind Easy Auth and takes the user from <c>GetUserId()</c>; nothing here is public. The
/// gateway itself has no inbound HTTP surface in phase 1: it connects OUT to each platform.
///
/// <para>Services are resolved from <see cref="IServiceProvider"/>, the <c>/api/speech</c> pattern (rule 7): a host
/// without <c>AddChatGateway</c> still routes the whole API, and these answer 503.</para>
/// </summary>
public static partial class AgentApi
{
    private static void MapChatGatewayEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/chat-connections", ListChatConnectionsAsync);
        app.MapPost("/api/chat-connections", CreateChatConnectionAsync);
        app.MapPut("/api/chat-connections/{id}", UpdateChatConnectionAsync);
        app.MapPost("/api/chat-connections/{id}/repair", RepairChatConnectionAsync);
        app.MapDelete("/api/chat-connections/{id}", DeleteChatConnectionAsync);
    }

    private static async Task<IResult> ListChatConnectionsAsync(IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        if (ChatGatewayParts.From(services) is not { } parts)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var connections = await ChatConnections(grains, http).ListAsync();
        var now = parts.Clock.GetUtcNow();
        return Results.Json(new ChatConnectionsResponse(
            parts.Adapters.Keys.Order(StringComparer.Ordinal).ToList(),
            connections.Select(c => ChatConnectionStatus.View(c, parts.Control.IsLive(c.Id), now)).ToList()));
    }

    private static async Task<IResult> CreateChatConnectionAsync(
        ChatConnectionRequest request, IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        if (ChatGatewayParts.From(services) is not { } parts)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (request.Platform is null || !parts.Adapters.ContainsKey(request.Platform))
        {
            return ChatBadRequest("Pick a chat platform this Agent42 supports.");
        }

        if (await ChatTargetProblemAsync(grains, http, request.TargetAgentId) is { } targetProblem)
        {
            return ChatBadRequest(targetProblem);
        }

        // Check pasted credentials BEFORE creating anything: a rejected token must not leave a half-made bot in the list.
        ChatValidatedCredentials? validated = null;
        if (request.Credentials is { } pasted)
        {
            var (value, problem) = await ValidateChatCredentialsAsync(parts.Adapters[request.Platform], pasted, http.RequestAborted);
            if (problem is not null)
            {
                return problem;
            }

            validated = value;
        }

        var created = await ChatConnections(grains, http).CreateAsync(request.Platform, ChatSettings(request), parts.Clock.GetUtcNow());
        if (created is null)
        {
            return ChatBadRequest($"You can connect at most {ChatGatewayLimits.MaxConnectionsPerUser} chat bots.");
        }

        return await SaveChatConnectionAsync(created, request, validated, parts, grains, http, StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateChatConnectionAsync(
        string id, ChatConnectionRequest request, IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        if (ChatGatewayParts.From(services) is not { } parts)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (await ChatConnections(grains, http).GetAsync(id) is not { } existing)
        {
            return Results.NotFound();
        }

        if (await ChatTargetProblemAsync(grains, http, request.TargetAgentId) is { } targetProblem)
        {
            return ChatBadRequest(targetProblem);
        }

        ChatValidatedCredentials? validated = null;
        if (request.Credentials is { } pasted)
        {
            var (value, problem) = await ValidateChatCredentialsAsync(parts.Adapters[existing.Platform], pasted, http.RequestAborted);
            if (problem is not null)
            {
                return problem;
            }

            validated = value;
        }

        return await SaveChatConnectionAsync(existing, request, validated, parts, grains, http, StatusCodes.Status200OK);
    }

    // Stores already-validated credentials (a bad token never gets this far), then the settings, then tells the running
    // gateway to apply both.
    private static async Task<IResult> SaveChatConnectionAsync(
        ChatConnection connection, ChatConnectionRequest request, ChatValidatedCredentials? validated, ChatGatewayParts parts,
        IGrainFactory grains, HttpContext http, int successStatus)
    {
        var store = ChatConnections(grains, http);
        if (validated is { } credentials)
        {
            await store.SetCredentialsAsync(connection.Id, parts.Protector.Protect(credentials.Credentials),
                credentials.Identity, parts.Clock.GetUtcNow());
        }

        var saved = await store.UpdateSettingsAsync(connection.Id, ChatSettings(request), parts.Clock.GetUtcNow());
        if (saved is null)
        {
            return Results.NotFound();
        }

        // A chat turn has no request, so it borrows the identity the owner had when they last saved (ADR 0010/0031).
        if (RequestContext.Get(UserTelemetryContext.RequestContextKey) is UserTelemetryContext telemetry)
        {
            await store.SetOwnerTelemetryAsync(connection.Id, telemetry);
        }

        await parts.Control.RestartAsync(http.GetUserId(), connection.Id, http.RequestAborted);
        return Results.Json(ChatConnectionStatus.View(saved, parts.Control.IsLive(saved.Id), parts.Clock.GetUtcNow()), statusCode: successStatus);
    }

    private static async Task<IResult> RepairChatConnectionAsync(
        string id, IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        if (ChatGatewayParts.From(services) is not { } parts)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var repaired = await ChatConnections(grains, http).RepairAsync(id, parts.Clock.GetUtcNow());
        return repaired is null
            ? Results.NotFound()
            : Results.Json(ChatConnectionStatus.View(repaired, parts.Control.IsLive(id), parts.Clock.GetUtcNow()));
    }

    private static async Task<IResult> DeleteChatConnectionAsync(
        string id, IGrainFactory grains, IServiceProvider services, HttpContext http)
    {
        if (ChatGatewayParts.From(services) is not { } parts)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // The grain deregisters from the run list before it deletes; only then is the live session stopped, so a
        // restart racing this delete finds nothing to run.
        if (!await ChatConnections(grains, http).DeleteAsync(id))
        {
            return Results.NotFound();
        }

        await parts.Control.StopAsync(id, http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<(ChatValidatedCredentials? Value, IResult? Problem)> ValidateChatCredentialsAsync(
        IChatPlatformAdapter adapter, ChatCredentialsRequest pasted, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pasted.BotToken))
        {
            return (null, ChatBadRequest("Paste the bot token."));
        }

        var credentials = new ChatCredentials(pasted.BotToken.Trim(), string.IsNullOrWhiteSpace(pasted.AppToken) ? null : pasted.AppToken.Trim());
        try
        {
            return (new ChatValidatedCredentials(credentials, await adapter.ValidateAsync(credentials, cancellationToken)), null);
        }
        catch (ChatAuthException ex)
        {
            return (null, ChatBadRequest(ex.Message));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, Results.Text($"Couldn't reach {adapter.Kind} to check the token. Try again in a moment.",
                "text/plain", statusCode: StatusCodes.Status502BadGateway));
        }
    }

    // Null when the target is plain chat or one of the caller's own stored agents.
    private static async Task<string?> ChatTargetProblemAsync(IGrainFactory grains, HttpContext http, string? targetAgentId)
        => string.IsNullOrWhiteSpace(targetAgentId)
            || await grains.GetGrain<IAgentRegistryGrain>(http.GetUserId()).GetAsync(targetAgentId) is not null
            ? null
            : "That agent doesn't exist.";

    private static ChatConnectionSettings ChatSettings(ChatConnectionRequest request) => new()
    {
        Name = request.Name ?? "",
        TargetAgentId = request.TargetAgentId,
        Enabled = request.Enabled,
        GroupsEnabled = request.GroupsEnabled,
        VoiceNotesEnabled = request.VoiceNotesEnabled,
        NotificationsEnabled = request.NotificationsEnabled,
    };

    private static IResult ChatBadRequest(string message)
        => Results.Text(message, "text/plain", statusCode: StatusCodes.Status400BadRequest);

    private static IUserChatConnectionsGrain ChatConnections(IGrainFactory grains, HttpContext http)
        => grains.GetGrain<IUserChatConnectionsGrain>(http.GetUserId());
}

/// <summary>Pasted credentials the platform accepted, with the bot identity it reported.</summary>
internal readonly record struct ChatValidatedCredentials(ChatCredentials Credentials, ChatBotIdentity Identity);

/// <summary>The gateway services the management endpoints need, or none at all when the host did not register them.</summary>
internal sealed record ChatGatewayParts(
    IReadOnlyDictionary<string, IChatPlatformAdapter> Adapters,
    IChatGatewaySecretProtector Protector,
    IChatGatewayControl Control,
    TimeProvider Clock)
{
    public static ChatGatewayParts? From(IServiceProvider services)
        => services.GetService<IChatGatewaySecretProtector>() is { } protector
            && services.GetService<IChatGatewayControl>() is { } control
                ? new ChatGatewayParts(
                    services.GetServices<IChatPlatformAdapter>().ToDictionary(a => a.Kind, StringComparer.Ordinal),
                    protector,
                    control,
                    services.GetService<TimeProvider>() ?? TimeProvider.System)
                : null;
}
