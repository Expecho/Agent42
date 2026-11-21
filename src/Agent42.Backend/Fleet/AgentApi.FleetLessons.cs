using System.Globalization;
using Agent42.Agents;
using Microsoft.AspNetCore.Mvc;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>The Lessons dialog's settings: the nightly toggle and the model the dream runs on (null: the default).</summary>
public sealed record FleetLessonSettingsRequest(bool Enabled, string? Model);

/// <summary>"Dream now": an optional model.</summary>
public sealed record FleetDreamRequest(string? Model);

/// <summary>
/// The fleet's lessons (ADR 0124): what the nightly dream learned, where it was applied, and the controls. Every route is behind
/// Easy Auth and scoped to <c>GetUserId()</c>, never excluded (rule 4), so another user's lesson id is a 404 and their lessons are
/// never listed. The service is resolved optionally (the <c>/api/speech</c> pattern): a host without the fleet answers 503.
/// </summary>
public static partial class AgentApi
{
    private static void MapFleetLessonEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/fleet/lessons", GetFleetLessonsAsync);
        app.MapPut("/api/fleet/lessons/settings", PutFleetLessonSettingsAsync);
        app.MapPost("/api/fleet/lessons/dream", DreamFleetLessonsAsync);
        app.MapPost("/api/fleet/lessons/seen", MarkFleetLessonsSeenAsync);
        app.MapPost("/api/fleet/lessons/{id}/disable", (string id, IServiceProvider services, HttpContext http, CancellationToken ct)
            => EditFleetLessonAsync(services, http, (s, user) => s.DisableAsync(user, id, ct)));
        app.MapPost("/api/fleet/lessons/{id}/enable", (string id, IServiceProvider services, HttpContext http, CancellationToken ct)
            => EditFleetLessonAsync(services, http, (s, user) => s.EnableAsync(user, id, ct)));
        app.MapDelete("/api/fleet/lessons/{id}", (string id, IServiceProvider services, HttpContext http, CancellationToken ct)
            => EditFleetLessonAsync(services, http, (s, user) => s.DeleteAsync(user, id, ct)));
    }

    private static IResult FleetLessonNotFound() => Results.Json(new { error = "No such lesson." }, statusCode: StatusCodes.Status404NotFound);

    // Best effort: the reminder is a heal, never a reason to fail the page or the save. A transient reminder-table failure is logged
    // and the next GET or PUT tries again. (RegisterOrUpdateReminder on an existing name also moves its next tick to about a minute out.)
    private static async Task TryFleetDreamScheduleAsync(IServiceProvider services, IFleetDreamGrain grain, bool schedule)
    {
        try
        {
            await (schedule ? grain.ScheduleAsync() : grain.UnscheduleAsync());
        }
        catch (Exception ex)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("FleetLessons").LogWarning(ex, "The fleet dream reminder could not be updated");
        }
    }

    private static async Task<IResult> GetFleetLessonsAsync(IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetLessonService>() is not { } lessons)
        {
            return FleetUnavailable();
        }

        var userId = http.GetUserId();
        var document = await lessons.GetAsync(userId, ct);
        // The user's own thresholds and window (ADR 0135): what the next dream really uses.
        var effective = await lessons.EffectiveOptionsAsync(userId, ct);
        if (document.Settings.Enabled && lessons.Options.DreamEnabled)
        {
            // Idempotent: heals a reminder table that was reset.
            await TryFleetDreamScheduleAsync(services, grains.GetGrain<IFleetDreamGrain>(userId), schedule: true);
        }

        return Results.Json(new
        {
            lessons = document.Lessons.OrderByDescending(l => l.LastSupportedAt),
            settings = document.Settings,
            runs = document.Runs,
            newCount = FleetLessonService.NewCount(document),
            running = lessons.IsRunning(userId),
            dreamEnabled = lessons.Options.DreamEnabled,
            minNewTasks = Math.Max(1, effective.DreamMinNewTasks),
            minTasks = Math.Max(1, effective.DreamMinTasks),
            ttlDays = Math.Max(1, effective.LessonTtlDays),
            schedule = FleetDreamSchedule(effective),
        });
    }

    // The window as the operator configured it (UTC), and the same time in Amsterdam when the zone is known, so the page can
    // say "about 02:00 Amsterdam time". There is no per-user timezone in the codebase.
    private static object FleetDreamSchedule(FleetOptions options)
    {
        var at = TimeOnly.TryParseExact(options.DreamAtUtc, "HH:mm", null, DateTimeStyles.None, out var parsed) ? parsed : new TimeOnly(1, 0);
        string? local = null;
        try
        {
            var utc = DateTime.UtcNow.Date.Add(at.ToTimeSpan());
            local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")).ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // No tz database on this host: the page falls back to UTC.
        }

        return new { atUtc = at.ToString("HH:mm", CultureInfo.InvariantCulture), windowHours = Math.Max(1, options.DreamWindowHours), amsterdam = local };
    }

    private static async Task<IResult> PutFleetLessonSettingsAsync(
        FleetLessonSettingsRequest request, IServiceProvider services, IGrainFactory grains, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetLessonService>() is not { } lessons)
        {
            return FleetUnavailable();
        }

        // `auto` is a chat-picker value (ADR 0137): the dream runs on a real model.
        if (AutoModelId.IsAuto(request.Model))
        {
            return FleetBadRequest(AutoModelRefusal.Message);
        }

        var userId = http.GetUserId();
        var (settings, error) = await lessons.SetSettingsAsync(userId, request.Enabled, request.Model, ct);
        if (settings is null)
        {
            return FleetBadRequest(error ?? "Pick one of your models that supports tools.");
        }

        var grain = grains.GetGrain<IFleetDreamGrain>(userId);
        if (settings.Enabled && lessons.Options.DreamEnabled)
        {
            await TryFleetDreamScheduleAsync(services, grain, schedule: true);
        }
        else
        {
            await TryFleetDreamScheduleAsync(services, grain, schedule: false);
        }

        return Results.Json(settings);
    }

    private static async Task<IResult> DreamFleetLessonsAsync(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] FleetDreamRequest? request, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetLessonService>() is not { } lessons)
        {
            return FleetUnavailable();
        }

        if (AutoModelId.IsAuto(request?.Model))
        {
            return FleetBadRequest(AutoModelRefusal.Message);
        }

        var telemetry = RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext;
        var result = await lessons.RunManualAsync(http.GetUserId(), request?.Model, telemetry, ct);
        return result.Kind switch
        {
            FleetDreamResultKind.Ran => Results.Json(result.Run),
            FleetDreamResultKind.Busy => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
            FleetDreamResultKind.BadModel => FleetBadRequest(result.Error ?? "Pick one of your models that supports tools."),
            _ => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static async Task<IResult> MarkFleetLessonsSeenAsync(IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        if (services.GetService<FleetLessonService>() is not { } lessons)
        {
            return FleetUnavailable();
        }

        await lessons.MarkSeenAsync(http.GetUserId(), ct);
        return Results.Json(new { });
    }

    private static async Task<IResult> EditFleetLessonAsync(IServiceProvider services, HttpContext http, Func<FleetLessonService, string, Task<bool>> edit)
        => services.GetService<FleetLessonService>() is not { } lessons ? FleetUnavailable()
            : await edit(lessons, http.GetUserId()) ? Results.Json(new { }) : FleetLessonNotFound();
}
