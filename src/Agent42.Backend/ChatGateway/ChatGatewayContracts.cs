using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>Creates or edits a chat connection. Credentials are optional on an edit and never echoed back.</summary>
public sealed record ChatConnectionRequest(
    string? Platform,
    string? Name,
    string? TargetAgentId,
    bool Enabled = true,
    bool GroupsEnabled = false,
    bool VoiceNotesEnabled = false,
    bool NotificationsEnabled = false,
    ChatCredentialsRequest? Credentials = null);

/// <summary>Pasted credentials. Write-only: a GET reports only whether they are set.</summary>
public sealed record ChatCredentialsRequest(string? BotToken, string? AppToken = null);

/// <summary>The panel's view of the user's connections, plus which platforms this deployment can run.</summary>
public sealed record ChatConnectionsResponse(IReadOnlyList<string> Platforms, IReadOnlyList<ChatConnectionView> Connections);

/// <summary>
/// One connection as the panel shows it. Never the credentials or their ciphertext. The pairing code IS shown: it is
/// the owner's to read and send to the bot, valid for minutes and single-use.
/// </summary>
public sealed record ChatConnectionView(
    string Id,
    string Platform,
    string Name,
    string? TargetAgentId,
    bool Enabled,
    bool GroupsEnabled,
    bool VoiceNotesEnabled,
    bool NotificationsEnabled,
    bool HasCredentials,
    string? BotUserName,
    string Status,
    string? PairingCode,
    DateTimeOffset? PairingExpiresAt,
    string? FaultReason,
    bool HomeChatSet);

/// <summary>The status strings the panel renders. One home, so the panel tests can pin them.</summary>
public static class ChatConnectionStatus
{
    public const string Disabled = "disabled";
    public const string NeedsCredentials = "needs-credentials";
    public const string AwaitingPairing = "awaiting-pairing";
    public const string PairingLocked = "pairing-locked";
    public const string Faulted = "faulted";
    public const string Connecting = "connecting";
    public const string Connected = "connected";

    /// <summary>
    /// The status of <paramref name="connection"/> at <paramref name="now"/>, given whether the gateway currently holds
    /// a live session for it. Order matters: a fault outranks pairing, pairing outranks liveness.
    /// </summary>
    public static string Of(ChatConnection connection, bool live, DateTimeOffset now)
    {
        if (!connection.Enabled)
        {
            return Disabled;
        }

        if (string.IsNullOrEmpty(connection.CredentialsCiphertext))
        {
            return NeedsCredentials;
        }

        if (connection.FaultReason is not null)
        {
            return Faulted;
        }

        if (!connection.IsPaired)
        {
            return connection.PairingFailures >= ChatGatewayLimits.MaxPairingFailures
                || connection.PairingExpiresAt is not { } expires || now >= expires
                ? PairingLocked
                : AwaitingPairing;
        }

        return live ? Connected : Connecting;
    }

    public static ChatConnectionView View(ChatConnection connection, bool live, DateTimeOffset now)
    {
        var status = Of(connection, live, now);
        var showCode = status == AwaitingPairing;
        return new ChatConnectionView(
            connection.Id,
            connection.Platform,
            connection.Name,
            connection.TargetAgentId,
            connection.Enabled,
            connection.GroupsEnabled,
            connection.VoiceNotesEnabled,
            connection.NotificationsEnabled,
            !string.IsNullOrEmpty(connection.CredentialsCiphertext),
            connection.BotUserName,
            status,
            showCode ? connection.PairingCode : null,
            showCode ? connection.PairingExpiresAt : null,
            connection.FaultReason,
            connection.HomeRouteKey is not null);
    }
}
