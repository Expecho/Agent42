namespace Agent42.Agents;

/// <summary>
/// Which replica this process is, stamped into a chat turn's meta so a reader can tell "its owner is alive" from "its owner
/// is gone" (ADR 0153, D4). Introduced minimally here; the clustering plan's own replica identity replaces it if that lands first.
/// </summary>
public interface IReplicaIdentity
{
    string ReplicaId { get; }
}

/// <summary>
/// <c>CONTAINER_APP_REPLICA_NAME</c> when the platform sets it (unverified, clustering plan spike S1), else a name generated
/// once per process. Either is unique per process, which is all the heartbeat needs.
/// </summary>
public sealed class EnvironmentReplicaIdentity : IReplicaIdentity
{
    public string ReplicaId { get; } =
        Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") is { Length: > 0 } name
            ? name
            : $"replica-{Guid.NewGuid():N}";
}
