namespace Agent42.Agents;

/// <summary>
/// One user's bot on one chat platform (ADR 0111): the credentials Agent42 connects it with, who owns it, and what it
/// talks to. Owned by <see cref="UserChatConnectionsGrain"/>.
///
/// <para><b>A class with property initialisers, not a positional record.</b> On the Cosmos JSON path a record
/// parameter a stored document lacks binds to <c>null</c>, not to its C# default (gotcha "A record parameter a Cosmos
/// document lacks binds to <c>null</c>"), and this type will gain fields phase by phase. Every new field is a trailing
/// <c>[Id(n)]</c> with a default.</para>
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatConnection")]
public sealed class ChatConnection
{
    [Id(0)]
    public string Id { get; set; } = "";

    /// <summary>One of <see cref="ChatPlatformKind"/>'s strings.</summary>
    [Id(1)]
    public string Platform { get; set; } = "";

    [Id(2)]
    public string Name { get; set; } = "";

    /// <summary>The stored agent new conversations bind to, or <c>null</c> for plain chat.</summary>
    [Id(3)]
    public string? TargetAgentId { get; set; }

    /// <summary>
    /// Whether the gateway should run this connection at all. Set from the owner's settings on create.
    ///
    /// <para><b>No <c>= true</c> initialiser, on purpose.</b> The Cosmos JSON serializer omits a member that holds its
    /// CLR default, so a disabled connection is written WITHOUT "Enabled"; on read the property keeps its initialiser,
    /// and an initialiser of <c>true</c> would silently re-enable every disabled bot after a reload (measured on this
    /// type, pinned by <c>ChatConnection_Disabled_StaysDisabledThroughCosmosJson</c>). Every bool here defaults to
    /// false for the same reason.</para>
    /// </summary>
    [Id(4)]
    public bool Enabled { get; set; }

    /// <summary>
    /// The platform credentials, protected by the API layer (purpose <c>Agent42.ChatGateway.Secret.v1</c>) before
    /// they ever reach this grain. Opaque here; never returned by a GET. <c>null</c> until the owner pastes them.
    /// </summary>
    [Id(5)]
    public string? CredentialsCiphertext { get; set; }

    /// <summary>The bot's own user id on the platform, from validation. Used to drop the bot's own echoes.</summary>
    [Id(6)]
    public string? BotUserId { get; set; }

    /// <summary>The bot's handle (Telegram <c>@username</c>), shown in the panel and used for mention parsing.</summary>
    [Id(7)]
    public string? BotUserName { get; set; }

    /// <summary>The workspace the bot lives in, where the platform has one (Slack team, Discord application).</summary>
    [Id(8)]
    public string? WorkspaceId { get; set; }

    /// <summary>The paired owner's platform user id. <c>null</c> while the connection awaits pairing.</summary>
    [Id(9)]
    public string? OwnerPlatformUserId { get; set; }

    /// <summary>The workspace the owner paired from (Slack team). Compared with the owner id on every message.</summary>
    [Id(10)]
    public string? OwnerWorkspaceId { get; set; }

    [Id(11)]
    public string? PairingCode { get; set; }

    [Id(12)]
    public DateTimeOffset? PairingExpiresAt { get; set; }

    [Id(13)]
    public int PairingFailures { get; set; }

    /// <summary>Answer in groups and channels when mentioned (phase 1c). Off by default: every member sees a reply.</summary>
    [Id(14)]
    public bool GroupsEnabled { get; set; }

    /// <summary>Transcribe voice notes into turns (phase 1c).</summary>
    [Id(15)]
    public bool VoiceNotesEnabled { get; set; }

    /// <summary>Deliver away replies and model push notifications to <see cref="HomeRouteKey"/> (phase 1c).</summary>
    [Id(16)]
    public bool NotificationsEnabled { get; set; }

    /// <summary>The route <c>/sethome</c> picked, where notifications go. <c>null</c> until set.</summary>
    [Id(17)]
    public string? HomeRouteKey { get; set; }

    /// <summary>
    /// The platform's resume cursor, where it has one. Telegram: the last processed <c>update_id</c>, so a restart
    /// neither replays nor drops updates (Telegram keeps unconfirmed updates for 24 hours).
    /// </summary>
    [Id(18)]
    public long PollOffset { get; set; }

    /// <summary>Why the gateway stopped this connection (an auth failure, another poller), or <c>null</c>.</summary>
    [Id(19)]
    public string? FaultReason { get; set; }

    [Id(20)]
    public DateTimeOffset CreatedAt { get; set; }

    [Id(21)]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The owner's telemetry identity, captured from their signed-in request when they last saved the connection. A chat
    /// turn has no HTTP request, so without it every chat turn would reach <c>/usage</c> and <c>/logs</c> unattributed
    /// (ADR 0010/0031). <c>null</c> until the first save through the API.
    /// </summary>
    [Id(22)]
    public UserTelemetryContext? OwnerTelemetry { get; set; }

    /// <summary>Whether a paired owner exists.</summary>
    public bool IsPaired => !string.IsNullOrEmpty(OwnerPlatformUserId);

    /// <summary>Whether the gateway should hold a live session for this connection.</summary>
    public bool IsRunnable => Enabled && !string.IsNullOrEmpty(CredentialsCiphertext) && FaultReason is null;
}

/// <summary>The settings an owner edits on a connection; credentials are set separately and write-only.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatConnectionSettings")]
public sealed class ChatConnectionSettings
{
    [Id(0)]
    public string Name { get; set; } = "";

    [Id(1)]
    public string? TargetAgentId { get; set; }

    [Id(2)]
    public bool Enabled { get; set; } = true;

    [Id(3)]
    public bool GroupsEnabled { get; set; }

    [Id(4)]
    public bool VoiceNotesEnabled { get; set; }

    [Id(5)]
    public bool NotificationsEnabled { get; set; }
}

/// <summary>The bot identity a platform reported when its credentials were validated.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatBotIdentity")]
public sealed record ChatBotIdentity(
    [property: Id(0)] string BotUserId,
    [property: Id(1)] string BotUserName,
    [property: Id(2)] string? WorkspaceId = null);

/// <summary>What a pairing attempt did. Only <see cref="Paired"/> is ever answered in the chat.</summary>
public enum ChatPairingOutcome
{
    /// <summary>The code matched: the sender is now the owner.</summary>
    Paired,

    /// <summary>No pairing was open, the code was wrong or expired, or the connection is locked. Stay silent.</summary>
    Ignored,
}
