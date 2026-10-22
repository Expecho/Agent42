namespace Agent42.Agents;

/// <summary>
/// Per-user store of chat-platform bots (ADR 0111), keyed by the user id. Owns each <see cref="ChatConnection"/>'s
/// lifecycle and keeps <see cref="IChatConnectionDirectoryGrain"/> in sync: a connection is listed there exactly while
/// it is <see cref="ChatConnection.IsRunnable"/>.
///
/// <para>Ordering is the share-link rule (gotcha "A read-only share link stores the RAW token and revokes by
/// deregistering the directory FIRST"): a change that makes a connection runnable writes first and registers after;
/// one that stops it (disable, delete, fault) deregisters first and writes after. A crash in between therefore leaves
/// a connection that is not run, never one that is run against the owner's wishes.</para>
///
/// <para>Times are passed in by the caller, so expiry is testable without a clock in the grain.</para>
/// </summary>
[Alias("Agent42.Agents.IUserChatConnectionsGrain")]
public interface IUserChatConnectionsGrain : IGrainWithStringKey
{
    [Alias("ListAsync")]
    Task<IReadOnlyList<ChatConnection>> ListAsync();

    [Alias("GetAsync")]
    Task<ChatConnection?> GetAsync(string connectionId);

    /// <summary>
    /// Creates a connection with no credentials yet (so it is not runnable). Returns <c>null</c> when the platform is
    /// unknown or the user already holds <see cref="ChatGatewayLimits.MaxConnectionsPerUser"/>.
    /// </summary>
    [Alias("CreateAsync")]
    Task<ChatConnection?> CreateAsync(string platform, ChatConnectionSettings settings, DateTimeOffset now);

    /// <summary>Replaces the editable settings. A target change applies to new conversations only.</summary>
    [Alias("UpdateSettingsAsync")]
    Task<ChatConnection?> UpdateSettingsAsync(string connectionId, ChatConnectionSettings settings, DateTimeOffset now);

    /// <summary>
    /// Stores new, already-validated credentials and the bot identity they belong to. Clears the owner, any fault and
    /// the poll cursor, and opens a fresh pairing window: a new bot is a new trust decision.
    /// </summary>
    [Alias("SetCredentialsAsync")]
    Task<ChatConnection?> SetCredentialsAsync(
        string connectionId, string credentialsCiphertext, ChatBotIdentity identity, DateTimeOffset now);

    /// <summary>Records the Signal number a registration was started for (phase 3), until verify or delete.</summary>
    [Alias("SetPendingSignalNumberAsync")]
    Task<ChatConnection?> SetPendingSignalNumberAsync(string connectionId, string? number, DateTimeOffset now);

    /// <summary>Clears the owner and opens a fresh pairing window (and lifts a pairing lockout).</summary>
    [Alias("RepairAsync")]
    Task<ChatConnection?> RepairAsync(string connectionId, DateTimeOffset now);

    /// <summary>
    /// A DM's text offered as the pairing code. Binds the sender as owner when a window is open, unexpired, not locked
    /// out and the code matches; anything else is <see cref="ChatPairingOutcome.Ignored"/> and counts as a failure
    /// when a window was open.
    /// </summary>
    [Alias("TryPairAsync")]
    Task<ChatPairingOutcome> TryPairAsync(
        string connectionId, string platformUserId, string? workspaceId, string candidate, DateTimeOffset now);

    /// <summary>Records the platform's resume cursor (Telegram <c>update_id</c>). Ignores a value that moves backwards.</summary>
    [Alias("SetPollOffsetAsync")]
    Task SetPollOffsetAsync(string connectionId, long offset);

    /// <summary>Sets the route notifications are delivered to (<c>/sethome</c>).</summary>
    [Alias("SetHomeRouteAsync")]
    Task SetHomeRouteAsync(string connectionId, string routeKey);

    /// <summary>Records the owner's telemetry identity from their latest signed-in save.</summary>
    [Alias("SetOwnerTelemetryAsync")]
    Task SetOwnerTelemetryAsync(string connectionId, UserTelemetryContext telemetry);

    /// <summary>Stops the connection with a reason the panel shows. Deregisters first.</summary>
    [Alias("RecordFaultAsync")]
    Task RecordFaultAsync(string connectionId, string reason, DateTimeOffset now);

    /// <summary>Deletes the connection. Deregisters first. Returns whether it existed.</summary>
    [Alias("DeleteAsync")]
    Task<bool> DeleteAsync(string connectionId);
}
