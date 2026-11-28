using System.Text.Json.Serialization;

namespace Agent42.Agents;

/// <summary>Where a chat turn is in its life (ADR 0153, D4). <c>Completed</c> includes a turn the user stopped.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChatTurnStatus>))]
public enum ChatTurnStatus
{
    /// <summary>The owner is draining the grain stream.</summary>
    Running,

    /// <summary>The grain stream ended; its <c>Done</c> event is in the log.</summary>
    Completed,

    /// <summary>The grain's chat call threw mid-stream.</summary>
    Failed,

    /// <summary>The owner is gone: the host stopped, or the heartbeat went stale.</summary>
    Interrupted,
}

/// <summary>
/// A turn's small JSON record. <b>One writer per turn</b>: the pump's writer loop stamps the heartbeat, the flags and the
/// terminal status, and a reader only ever evaluates it through <see cref="ChatTurnStore.StatusOf"/>, never writes it back.
/// </summary>
/// <param name="Degraded">Persisting stopped (the store failed or the log was purged); the owner's live feed was unaffected.</param>
/// <param name="Truncated">The log hit <see cref="ChatTurnOptions.MaxLogBytes"/>; events past the cap are not in the store.</param>
public sealed record ChatTurnMeta(
    string TurnId,
    ChatTurnStatus Status,
    DateTimeOffset StartedUtc,
    string OwnerReplicaId,
    DateTimeOffset LastHeartbeatUtc,
    bool Degraded = false,
    bool Truncated = false,
    DateTimeOffset? EndedUtc = null);
