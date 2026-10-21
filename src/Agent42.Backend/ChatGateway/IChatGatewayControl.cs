namespace Agent42.Backend;

/// <summary>
/// What the management endpoints may ask of the running gateway (ADR 0111): apply a saved change now, stop a deleted
/// connection, and say whether a connection has a live session. Implemented by <see cref="ChatGatewayService"/>.
/// </summary>
public interface IChatGatewayControl
{
    /// <summary>
    /// Re-reads the connection and (re)starts or stops its session to match: a credential, target or enabled change
    /// applies without a process restart. A no-op when this instance does not hold the gateway lease.
    /// </summary>
    Task RestartAsync(string userId, string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Stops the connection's session, if any. Called after the connection left the run list.</summary>
    Task StopAsync(string connectionId, CancellationToken cancellationToken = default);

    /// <summary>Whether a session for the connection is currently open.</summary>
    bool IsLive(string connectionId);
}
