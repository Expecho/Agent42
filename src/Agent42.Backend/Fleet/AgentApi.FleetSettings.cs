using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The fleet's Advanced settings (ADR 0135): the user's overrides of the operator's limits. Every route is behind Easy Auth and
/// scoped to <c>GetUserId()</c>, never excluded (rule 4), so another user's settings are unreachable. The service is resolved
/// optionally (the <c>/api/speech</c> pattern): a host without the fleet answers 503. The GET carries the bounds table the page
/// renders from, so the page never hardcodes a limit.
/// </summary>
public static partial class AgentApi
{
    private static void MapFleetSettingsEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/fleet/settings", GetFleetSettingsAsync);
        app.MapPut("/api/fleet/settings", PutFleetSettingsAsync);
        app.MapDelete("/api/fleet/settings", DeleteFleetSettingsAsync);
    }

    private static async Task<IResult> GetFleetSettingsAsync(IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetSettingsService>() is not { } settings)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        return Results.Json(new
        {
            settings = await settings.GetAsync(userId, ct),
            defaults = settings.OperatorValues(),
            fields = FleetSettingsService.Descriptors.Select(d => new
            {
                key = FleetSettingsService.CamelCase(d.Key),
                group = d.Group,
                kind = d.Kind,
                min = d.Kind is "int" or "number" ? (double?)d.Min : null,
                max = d.Kind is "int" or "number" ? (double?)d.Max : null,
                followsTeamDepth = d.FollowsTeamDepth,
            }),
            taskModels = (await settings.TaskModelChoicesAsync(userId, ct)).Select(m => new { id = m.Id, name = m.DisplayName ?? m.Id }),
            copilotModels = (await settings.CopilotModelChoicesAsync(userId, ct)).Select(m => new { id = m.Id, name = m.DisplayName ?? m.Id }),
        });
    }

    private static async Task<IResult> PutFleetSettingsAsync(FleetUserSettings request, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetSettingsService>() is not { } settings)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        var (field, error) = await settings.SaveAsync(userId, request, ct);
        return error is null
            ? Results.Json(await settings.GetAsync(userId, ct))
            : Results.Json(new { error, field = FleetSettingsService.CamelCase(field!) }, statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> DeleteFleetSettingsAsync(IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetSettingsService>() is not { } settings)
        {
            return FleetUnavailable();
        }

        await settings.ResetAsync(http.GetUserId(), ct);
        return Results.Json(new { });
    }
}
