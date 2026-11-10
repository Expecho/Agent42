using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>
/// The fleet's side of <see cref="IFleetLauncher"/> (ADR 0115): submits a task tied to the conversation, so it runs
/// in auto mode and reports back to it. It picks the model itself, with the predicate <see cref="FleetTaskService"/>
/// checks: the conversation's model when it is in the user's catalog and can run the fleet, otherwise none, so the
/// catalog default applies. Never by matching the service's error text. The analyzer's half (ADR 0122) is in
/// <c>FleetLauncher.Analyze.cs</c>.
/// </summary>
public sealed partial class FleetLauncher(
    FleetTaskService fleet, IUserModelCatalogProvider catalogs, IGrainFactory grains, IServiceProvider services, FleetLessonService? lessons = null) : IFleetLauncher
{
    public async Task<FleetLaunchResult> StartAsync(
        string userId, string conversationId, string task, string? model, string? followUpOf, CancellationToken cancellationToken = default)
    {
        var catalog = await (await catalogs.GetForUserAsync(userId, cancellationToken)).GetAsync(cancellationToken);
        var usable = model is not null
            && catalog.Models.Any(m => m.Id.Equals(model, StringComparison.Ordinal) && FleetTaskService.IsToolCapable(m));

        // The grain call carries the user's telemetry identity (ADR 0010), so the task's tokens are attributed as a
        // page task's are.
        var telemetry = RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext;
        var (taskId, error) = await fleet.SubmitAsync(
            userId,
            new FleetSubmit(task, FollowUpOf: followUpOf, Model: usable ? model : null, OriginConversationId: conversationId),
            telemetry,
            cancellationToken);
        return new FleetLaunchResult(taskId, error);
    }

    public Task<FleetTaskRecord?> GetAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => fleet.GetAsync(userId, taskId, cancellationToken);

    public Task<FleetDreamTick> RunNightlyDreamAsync(string userId, CancellationToken cancellationToken = default)
        => lessons is null
            ? Task.FromResult(FleetDreamTick.Unavailable)
            : lessons.TickAsync(userId, RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext, cancellationToken);
}
