using System.Globalization;
using System.Reflection;
using System.Text;
using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>
/// The analyzer half of <see cref="FleetLauncher"/> (ADR 0122). Reads go through <see cref="FleetTaskService"/> and its
/// <see cref="IFleetStore"/>, which scope every call by user id, so another user's task reads as not found. Everything
/// returned is capped and framed as data by <see cref="FleetAnalysis"/> and <see cref="FleetToolLimits.WrapAsData"/>.
/// </summary>
public sealed partial class FleetLauncher
{
    public async Task<string> ListRecentAsync(string userId, int limit, CancellationToken cancellationToken = default)
    {
        var tasks = (await fleet.ListAsync(userId, cancellationToken))
            .OrderByDescending(t => t.StartedAt)
            .Take(FleetAnalyzerLimits.Clamp(limit, FleetAnalyzerLimits.DefaultListLimit, FleetAnalyzerLimits.MaxListLimit))
            .ToList();
        if (tasks.Count == 0)
        {
            return "The user has no fleet tasks.";
        }

        var lines = tasks.Select(t => string.Create(CultureInfo.InvariantCulture,
            $"{t.Id} | {t.Status} | {t.StartedAt:u} | tokens {t.Tokens:N0} | {FleetAnalyzerLimits.Short(t.Request.ReplaceLineEndings(" "), 120)}"));
        // The request text is the user's own, but a follow-up chain or an email-started task can carry others' words.
        return "Newest first (id | status | started | tokens | request):\n"
            + FleetToolLimits.WrapAsData(string.Join('\n', lines), tasks[0].Id);
    }

    public async Task<string> AnalyzeAsync(string userId, string taskId, CancellationToken cancellationToken = default)
    {
        if (await fleet.GetAsync(userId, taskId, cancellationToken) is not { } record)
        {
            return FleetAnalyzerLimits.NotFound(taskId);
        }

        var lines = await fleet.Store.ReadEventsAsync(userId, taskId, cancellationToken);
        return FleetAnalysis.Summarize(record, FleetAnalysis.Parse(lines));
    }

    public async Task<string> ReadEventsAsync(
        string userId, string taskId, string? types, long afterSeq, int limit, CancellationToken cancellationToken = default)
    {
        if (await fleet.GetAsync(userId, taskId, cancellationToken) is null)
        {
            return FleetAnalyzerLimits.NotFound(taskId);
        }

        var lines = await fleet.Store.ReadEventsAsync(userId, taskId, cancellationToken);
        var take = FleetAnalyzerLimits.Clamp(limit, FleetAnalyzerLimits.DefaultEventLimit, FleetAnalyzerLimits.MaxEventLimit);
        return FleetAnalysis.Page(FleetAnalysis.Parse(lines), types, Math.Max(0, afterSeq), take, taskId);
    }

    public async Task<string> ReadWorkspaceAsync(string userId, string taskId, string? path, CancellationToken cancellationToken = default)
    {
        if (await fleet.GetAsync(userId, taskId, cancellationToken) is not { } record)
        {
            return FleetAnalyzerLimits.NotFound(taskId);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var files = await fleet.Store.ListWorkspaceAsync(userId, record.WorkspaceId, cancellationToken);
            var listing = files.Count == 0
                ? "(the workspace is empty)"
                : string.Join('\n', files.Take(FleetAnalyzerLimits.MaxFileListing).Select(f => $"{f.Path} ({f.Size:N0} bytes)"));
            return $"{files.Count} workspace file(s):\n" + FleetToolLimits.WrapAsData(listing, taskId);
        }

        try
        {
            var bytes = await fleet.Store.GetWorkspaceFileAsync(userId, record.WorkspaceId, path.Trim(), cancellationToken);
            if (bytes is null)
            {
                return $"The workspace has no file '{FleetAnalyzerLimits.Short(path, 200)}'.";
            }

            // A binary file is not evidence the model can read; say how big it is instead of dumping it.
            return Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 4096)) >= 0
                ? $"'{FleetAnalyzerLimits.Short(path, 200)}' is a binary file of {bytes.Length:N0} bytes."
                : FleetToolLimits.WrapAsData(Encoding.UTF8.GetString(bytes), taskId);
        }
        catch (ArgumentException ex)
        {
            // FleetStore.NormalizeWorkspacePath rejects a path that leaves the workspace.
            return $"That is not a valid workspace path: {ex.Message}";
        }
    }

    public string DescribeConfig()
    {
        var lines = typeof(FleetOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => $"{p.Name} = {Convert.ToString(p.GetValue(fleet.Options), CultureInfo.InvariantCulture)}")
            .Append($"CopilotAvailable = {fleet.CopilotAvailable}");
        return "Fleet configuration in effect:\n" + string.Join('\n', lines);
    }

    public Task<string> ReadSpansAsync(string userId, string? search, int limit, CancellationToken cancellationToken = default)
    {
        if (services.GetService(typeof(LiveTelemetryBuffer)) is not LiveTelemetryBuffer buffer)
        {
            return Task.FromResult("The live telemetry buffer isn't running in this host.");
        }

        var spans = FleetSpans.Select(
            FleetSpans.Drain(buffer), userId, search, FleetAnalyzerLimits.Clamp(limit, FleetAnalyzerLimits.DefaultSpanLimit, FleetAnalyzerLimits.MaxSpanLimit));
        return Task.FromResult(spans);
    }

    public async Task<string> StopAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => await fleet.StopAsync(userId, taskId, cancellationToken) switch
        {
            FleetOutcome.Ok => $"Stop requested for task {taskId}. It ends as 'stopped' in a moment.",
            FleetOutcome.NotFound => FleetAnalyzerLimits.NotFound(taskId),
            _ => $"Task {taskId} isn't running, so there is nothing to stop.",
        };

    public async Task<FleetLaunchResult> RerunAsync(
        string userId, string taskId, string? note, IReadOnlyList<string>? disableTools, long? maxTokens,
        CancellationToken cancellationToken = default)
    {
        if (await fleet.GetAsync(userId, taskId, cancellationToken) is not { } original)
        {
            return new FleetLaunchResult(null, FleetAnalyzerLimits.NotFound(taskId));
        }

        var request = original.Request;
        if (!string.IsNullOrWhiteSpace(note))
        {
            request += "\n\nAdjustment for this re-run: " + FleetAnalyzerLimits.Short(note.Trim(), FleetAnalyzerLimits.MaxNoteChars);
        }

        var telemetry = RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext;
        // No origin conversation: the run is NOT auto-approved (ADR 0115 decision 2 is for tasks the user handed over from
        // a chat), so its questions reach the /fleet/ page instead of being answered by text the model composed.
        var (newId, error) = await fleet.SubmitAsync(
            userId,
            new FleetSubmit(
                request,
                Model: original.PlannerModel,
                MaxTokens: maxTokens ?? original.MaxTokens,
                EnabledTools: await NarrowedToolsAsync(userId, original, disableTools),
                CopyInputsOf: original.Id),
            telemetry,
            cancellationToken);
        return new FleetLaunchResult(newId, error);
    }

    // The original task's selection (or the user's default one) minus the keys to turn off. It can only remove tools.
    private async Task<IReadOnlyList<string>?> NarrowedToolsAsync(string userId, FleetTaskRecord original, IReadOnlyList<string>? disableTools)
    {
        if (disableTools is not { Count: > 0 })
        {
            return original.EnabledTools;
        }

        var baseline = original.EnabledTools
            ?? (await grains.GetGrain<IUserProfileGrain>(userId).GetAsync()).EnabledTools
            ?? ToolCatalog.AllSelectableToolKeys;
        var off = disableTools.ToHashSet(StringComparer.Ordinal);
        return [.. baseline.Where(key => !off.Contains(key))];
    }
}
