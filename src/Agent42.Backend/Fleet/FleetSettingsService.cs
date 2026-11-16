using System.Globalization;
using System.Reflection;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>One user-settable fleet limit: its key (the property name on both <see cref="FleetOptions"/> and
/// <see cref="FleetUserSettings"/>), where the page groups it, and its bounds.</summary>
/// <param name="Key">The property name, which is also the JSON member (camelCased).</param>
/// <param name="Group"><c>behaviour</c> or <c>dream</c>.</param>
/// <param name="Kind"><c>int</c>, <c>number</c>, <c>time</c> (<c>HH:mm</c>) or <c>model</c>.</param>
/// <param name="Min">The lowest allowed value (numbers only).</param>
/// <param name="Max">The highest allowed value (numbers only).</param>
/// <param name="FollowsTeamDepth">The upper bound is the effective <c>MaxTeamDepth</c> (the user's, else the operator's).</param>
public sealed record FleetSettingDescriptor(string Key, string Group, string Kind, double Min = 0, double Max = 0, bool FollowsTeamDepth = false);

/// <summary>
/// The user's fleet overrides (ADR 0135): ONE bounds table drives server validation, the clamp on resolve and the
/// <c>GET /api/fleet/settings</c> payload the page renders from. <see cref="ResolveAsync"/> returns a clone of the
/// operator's <see cref="FleetOptions"/> with the user's clamped overrides applied, which the run path uses wherever it
/// used to read the singleton. The operator-only fields (token caps, concurrency, size caps, kill switches) are not in
/// the table, so no request can reach them.
/// </summary>
public sealed class FleetSettingsService(FleetOptions options, IFleetStore store, IUserModelCatalogProvider catalogs)
{
    /// <summary>The user-settable fields, in the order they are validated and applied (<c>MaxTeamDepth</c> before the depth
    /// fields that are bounded by it).</summary>
    public static readonly IReadOnlyList<FleetSettingDescriptor> Descriptors =
    [
        new(nameof(FleetOptions.MaxTeamDepth), "behaviour", "int", 1, 10),
        new(nameof(FleetOptions.MaxAgentsPerTask), "behaviour", "int", 1, 100),
        new(nameof(FleetOptions.MaxConcurrentSubTeamsPerLevel), "behaviour", "int", 1, 12),
        new(nameof(FleetOptions.DefaultMaxGroupChatRounds), "behaviour", "int", 1, 200),
        new(nameof(FleetOptions.BudgetSoftLimit), "behaviour", "number", 0.1, 1.0),
        new(nameof(FleetOptions.JudgeDepth), "behaviour", "int", -1, 10, FollowsTeamDepth: true),
        new(nameof(FleetOptions.PassingScore), "behaviour", "int", 1, 10),
        new(nameof(FleetOptions.MaxAttempts), "behaviour", "int", 1, 6),
        new(nameof(FleetOptions.MaxFixRounds), "behaviour", "int", 0, 5),
        new(nameof(FleetOptions.ApprovalDepth), "behaviour", "int", -1, 10, FollowsTeamDepth: true),
        new(nameof(FleetOptions.MaxClarifyingQuestions), "behaviour", "int", 1, 20),
        new(nameof(FleetOptions.SelfReviewMaxIterations), "behaviour", "int", 1, 6),
        new(nameof(FleetOptions.DefaultCopilotModel), "behaviour", "model"),
        new(nameof(FleetOptions.DreamAtUtc), "dream", "time"),
        new(nameof(FleetOptions.DreamWindowHours), "dream", "int", 1, 24),
        new(nameof(FleetOptions.DreamMaxTasks), "dream", "int", 1, 200),
        new(nameof(FleetOptions.DreamMinTasks), "dream", "int", 1, 200),
        new(nameof(FleetOptions.DreamMinNewTasks), "dream", "int", 1, 200),
        new(nameof(FleetOptions.DreamMaxDigestChars), "dream", "int", 1_000, 200_000),
        new(nameof(FleetOptions.DreamMinSupport), "dream", "int", 1, 20),
        new(nameof(FleetOptions.DreamMaxNewPerRun), "dream", "int", 1, 20),
        new(nameof(FleetOptions.LessonMaxChars), "dream", "int", 20, 2_000),
        new(nameof(FleetOptions.LessonsInPrompt), "dream", "int", 1, 50),
        new(nameof(FleetOptions.LessonTtlDays), "dream", "int", 1, 3_650),
        new(nameof(FleetOptions.LessonsMaxStored), "dream", "int", 1, 1_000),
    ];

    private static readonly IReadOnlyDictionary<string, (PropertyInfo Settings, PropertyInfo Options)> Properties =
        Descriptors.ToDictionary(
            d => d.Key,
            d => (typeof(FleetUserSettings).GetProperty(d.Key)!, typeof(FleetOptions).GetProperty(d.Key)!));

    /// <summary>The operator's options: what an unset field falls back to.</summary>
    public FleetOptions Operator => options;

    public Task<FleetUserSettings> GetAsync(string userId, CancellationToken cancellationToken = default)
        => store.GetSettingsAsync(userId, cancellationToken);

    public Task ResetAsync(string userId, CancellationToken cancellationToken = default)
        => store.SetSettingsAsync(userId, new FleetUserSettings(), cancellationToken);

    /// <summary>
    /// The options a user's task or dream runs on: a clone of the operator's with the user's overrides applied. An
    /// out-of-range number is clamped and a bad time is ignored (defence in depth for a hand-edited blob), so this
    /// never throws on stored content.
    /// </summary>
    public async Task<FleetOptions> ResolveAsync(string userId, CancellationToken cancellationToken = default)
        => Apply(await store.GetSettingsAsync(userId, cancellationToken));

    /// <summary>Applies <paramref name="settings"/> to a clone of the operator's options.</summary>
    public FleetOptions Apply(FleetUserSettings settings)
    {
        var result = options.Clone();
        foreach (var descriptor in Descriptors)
        {
            var (source, target) = Properties[descriptor.Key];
            if (source.GetValue(settings) is { } value && Coerce(descriptor, value, result) is { } coerced)
            {
                target.SetValue(result, coerced);
            }
        }

        return result;
    }

    /// <summary>
    /// Validates the full override set and stores it. A null (or blank) field means "use the operator's value", so one
    /// call both sets and clears. On a refusal nothing is stored and the field's name comes back.
    /// </summary>
    public async Task<(string? Field, string? Error)> SaveAsync(string userId, FleetUserSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = settings with { DefaultCopilotModel = string.IsNullOrWhiteSpace(settings.DefaultCopilotModel) ? null : settings.DefaultCopilotModel.Trim() };
        var depth = normalized.MaxTeamDepth ?? options.MaxTeamDepth;
        foreach (var descriptor in Descriptors)
        {
            if (Properties[descriptor.Key].Settings.GetValue(normalized) is { } value
                && ValidateField(descriptor, value, depth) is { } error)
            {
                return (descriptor.Key, error);
            }
        }

        if (normalized.DefaultCopilotModel is { } model && !await IsKnownCopilotModelAsync(userId, model, cancellationToken))
        {
            return (nameof(FleetOptions.DefaultCopilotModel), "Pick one of your models that supports tools.");
        }

        await store.SetSettingsAsync(userId, normalized, cancellationToken);
        return (null, null);
    }

    private static string? ValidateField(FleetSettingDescriptor descriptor, object value, int teamDepth)
    {
        switch (descriptor.Kind)
        {
            case "time":
                return TimeOnly.TryParseExact((string)value, "HH:mm", null, DateTimeStyles.None, out _) ? null : "Use a time of day as HH:mm (UTC).";
            case "model":
                return null;
            default:
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                var max = descriptor.FollowsTeamDepth ? teamDepth : descriptor.Max;
                return number >= descriptor.Min && number <= max
                    ? null
                    : $"Must be between {Format(descriptor.Min)} and {Format(max)}.";
        }
    }

    private static object? Coerce(FleetSettingDescriptor descriptor, object value, FleetOptions effective)
    {
        switch (descriptor.Kind)
        {
            case "time":
                return TimeOnly.TryParseExact((string)value, "HH:mm", null, DateTimeStyles.None, out _) ? value : null;
            case "model":
                return string.IsNullOrWhiteSpace((string)value) ? null : value;
            default:
                var max = descriptor.FollowsTeamDepth ? effective.MaxTeamDepth : descriptor.Max;
                var clamped = Math.Clamp(Convert.ToDouble(value, CultureInfo.InvariantCulture), descriptor.Min, max);
                return value is int ? (object)(int)clamped : clamped;
        }
    }

    /// <summary>The operator's value of every user-settable field, by its camelCased key: what a blank field falls back to.</summary>
    public IReadOnlyDictionary<string, object?> OperatorValues()
        => Descriptors.ToDictionary(d => CamelCase(d.Key), d => Properties[d.Key].Options.GetValue(options));

    public static string CamelCase(string key) => char.ToLowerInvariant(key[0]) + key[1..];

    /// <summary>The models a plain <c>copilot</c> agent can run on: the user's tool-capable catalog models and the GitHub
    /// Copilot ones (ADR 0108), the same set the run receives.</summary>
    public async Task<IReadOnlyList<ModelInfo>> CopilotModelChoicesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var catalog = await (await catalogs.GetForUserAsync(userId, cancellationToken)).GetAsync(cancellationToken);
        return [.. catalog.Models.Where(m => FleetTaskService.IsToolCapable(m) || (m.CopilotOnly && m.SupportsTools))];
    }

    private async Task<bool> IsKnownCopilotModelAsync(string userId, string model, CancellationToken cancellationToken)
        => (await CopilotModelChoicesAsync(userId, cancellationToken)).Any(m => m.Id.Equals(model, StringComparison.Ordinal));

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
