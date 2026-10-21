using System.Collections.Concurrent;

namespace Agent42.Backend;

/// <summary>
/// The live session of each connection (ADR 0111), written by <see cref="ChatGatewayService"/> and read by the
/// <see cref="ChatTurnPipeline"/> at send time. A reply resolves the session per call rather than capturing it, so a
/// turn that outlives a reconnect sends through the new session instead of a disposed one. In-process (rule 12).
/// </summary>
public sealed class ChatSessionRegistry
{
    private readonly ConcurrentDictionary<string, IChatPlatformSession> _sessions = new(StringComparer.Ordinal);

    public void Set(string connectionId, IChatPlatformSession session) => _sessions[connectionId] = session;

    /// <summary>Removes the entry only if it is still <paramref name="session"/> (a newer session is left alone).</summary>
    public void Remove(string connectionId, IChatPlatformSession session)
        => _sessions.TryRemove(new KeyValuePair<string, IChatPlatformSession>(connectionId, session));

    public IChatPlatformSession? Get(string connectionId) => _sessions.GetValueOrDefault(connectionId);
}
