namespace Agent42.Agents;

/// <summary>
/// The reserved model value a client sends to ask for routing (ADR 0137). It exists ONLY on the wire and in
/// the picker: it must never be persisted as a deployment name or reach <c>ModelId.Parse</c>.
/// </summary>
public static class AutoModelId
{
    public const string Value = "auto";

    public static bool IsAuto(string? model) => string.Equals(model?.Trim(), Value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One model the router may choose, built from the user's catalog. <see cref="Hint"/> is the user's own
/// "when to use" text, <see cref="CardText"/> an optional public model-card description (ADR 0137 layer 2).
/// </summary>
public sealed record RouterCandidate(
    string Id,
    string DisplayName,
    bool SupportsTools,
    bool SupportsVision,
    bool CopilotOnly = false,
    bool IsChat = true,
    string? Hint = null,
    string? CardText = null,
    bool IsImageGeneration = false);

/// <summary>Per-turn facts the grain hands the router. Never stored on a grain field (ADR 0106).</summary>
public sealed record RouterTurn(
    string Message, bool HasImages, bool ToolsEnabled, string? CurrentModelId, bool PreviousReplyWasImage = false);

/// <summary>
/// The router's answer: the model to run the turn on, and whether that differs from the current one. <see cref="ImageGeneration"/>
/// marks a one-off picture turn on an image model: the conversation's own model is not switched (ADR 0138).
/// </summary>
public sealed record RouterDecision(string ModelId, string DisplayName, bool Changed, bool ImageGeneration = false);

/// <summary>Chooses a model for a turn. Returns <c>null</c> when nothing could be decided (the caller keeps its model).</summary>
public interface IModelRouter
{
    Task<RouterDecision?> RouteAsync(RouterTurn turn, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the router for a user, or <c>null</c> when they have no enabled DecisionModel provider (no Auto, nothing
/// is sent anywhere). Mirrors <see cref="IUserChatClientFactoryProvider"/>.
/// </summary>
public interface IUserModelRouterProvider
{
    Task<IModelRouter?> GetForUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="IUserModelRouterProvider"/> that gives every user the same router (DevHost and tests), or none.</summary>
public sealed class SingleUserModelRouterProvider(IModelRouter? router) : IUserModelRouterProvider
{
    public Task<IModelRouter?> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(router);
}

/// <summary>
/// The classification call itself: given the turn text and keyed option descriptions, the probability of each key,
/// or <c>null</c> on any failure (timeout, non-2xx, bad JSON). Implemented by the Backend's Foundry client.
/// </summary>
public interface IDecisionClient
{
    Task<IReadOnlyDictionary<string, double>?> ChooseAsync(
        string state, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken = default);
}

/// <summary>Router thresholds. Constants with defaults; deliberately no per-user knob in v1 (ADR 0137).</summary>
public sealed class ModelRouterOptions
{
    /// <summary>The winner must reach this probability to be chosen.</summary>
    public double MinProbability { get; set; } = 0.6;

    /// <summary>The winner must also beat the current model's probability by this much (stickiness).</summary>
    public double Margin { get; set; } = 0.25;

    /// <summary>
    /// An image-generation option needs at least this probability (and to be the largest answer) to be chosen. Higher than
    /// <see cref="MinProbability"/> because a wrong picture costs money and a wrong chat reply does not (ADR 0138).
    /// </summary>
    public double ImageMinProbability { get; set; } = 0.8;

    /// <summary>The message is cut to its last this-many characters (Decision-1 has a 32k-token window).</summary>
    public int MaxMessageChars { get; set; } = 8000;

    /// <summary>The key of the "cannot tell" option; choosing it keeps the current model.</summary>
    public string UnknownKey { get; set; } = "none";
}
