using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.UserChatConnectionsState")]
public sealed class UserChatConnectionsState
{
    [Id(0)]
    public List<ChatConnection> Connections { get; set; } = [];
}

/// <summary>
/// Per-user chat-platform bots (ADR 0111); see <see cref="IUserChatConnectionsGrain"/>. Rides the shared "agent-state"
/// storage provider on its own state name, the per-user overlay shape of <see cref="UserShareLinksGrain"/>.
/// </summary>
public sealed class UserChatConnectionsGrain : Grain, IUserChatConnectionsGrain
{
    private readonly IPersistentState<UserChatConnectionsState> _state;

    public UserChatConnectionsGrain(
        [PersistentState("chat-connections", AgentGrain.StorageProviderName)] IPersistentState<UserChatConnectionsState> state)
    {
        _state = state;
    }

    private string UserId => this.GetPrimaryKeyString();

    private IChatConnectionDirectoryGrain Directory
        => GrainFactory.GetGrain<IChatConnectionDirectoryGrain>(IChatConnectionDirectoryGrain.Key);

    public Task<IReadOnlyList<ChatConnection>> ListAsync()
        => Task.FromResult<IReadOnlyList<ChatConnection>>(_state.State.Connections.ToList());

    public Task<ChatConnection?> GetAsync(string connectionId) => Task.FromResult(Find(connectionId));

    public async Task<ChatConnection?> CreateAsync(string platform, ChatConnectionSettings settings, DateTimeOffset now)
    {
        if (!ChatPlatformKind.IsKnown(platform)
            || _state.State.Connections.Count >= ChatGatewayLimits.MaxConnectionsPerUser)
        {
            return null;
        }

        var connection = new ChatConnection
        {
            Id = Guid.NewGuid().ToString("N"),
            Platform = platform,
            CreatedAt = now,
        };
        Apply(connection, settings, now);

        // Not runnable yet (no credentials), so there is nothing to register.
        _state.State.Connections.Add(connection);
        await _state.WriteStateAsync();
        return connection;
    }

    // A save is also the owner's "try again": it clears a fault (another poller, a platform outage at connect time), and
    // the gateway restarts the session. A fault that persists simply comes back.
    public Task<ChatConnection?> UpdateSettingsAsync(string connectionId, ChatConnectionSettings settings, DateTimeOffset now)
        => MutateAsync(connectionId, c =>
        {
            Apply(c, settings, now);
            c.FaultReason = null;
        });

    public Task<ChatConnection?> SetCredentialsAsync(
        string connectionId, string credentialsCiphertext, ChatBotIdentity identity, DateTimeOffset now)
        => MutateAsync(connectionId, c =>
        {
            c.CredentialsCiphertext = credentialsCiphertext;
            c.BotUserId = identity.BotUserId;
            c.BotUserName = identity.BotUserName;
            c.WorkspaceId = identity.WorkspaceId;
            c.PollOffset = 0;
            c.FaultReason = null;
            OpenPairing(c, now);
        });

    public Task<ChatConnection?> RepairAsync(string connectionId, DateTimeOffset now)
        => MutateAsync(connectionId, c => OpenPairing(c, now));

    public async Task<ChatPairingOutcome> TryPairAsync(
        string connectionId, string platformUserId, string? workspaceId, string candidate, DateTimeOffset now)
    {
        var connection = Find(connectionId);
        if (connection is null || connection.IsPaired || connection.PairingCode is null)
        {
            return ChatPairingOutcome.Ignored;
        }

        if (connection.PairingExpiresAt is not { } expires || now >= expires
            || connection.PairingFailures >= ChatGatewayLimits.MaxPairingFailures)
        {
            return ChatPairingOutcome.Ignored;
        }

        if (!ChatPairingCode.Matches(candidate, connection.PairingCode))
        {
            connection.PairingFailures++;
            await _state.WriteStateAsync();
            return ChatPairingOutcome.Ignored;
        }

        connection.OwnerPlatformUserId = platformUserId;
        connection.OwnerWorkspaceId = workspaceId;
        connection.PairingCode = null;
        connection.PairingExpiresAt = null;
        connection.PairingFailures = 0;
        connection.UpdatedAt = now;
        await _state.WriteStateAsync();
        return ChatPairingOutcome.Paired;
    }

    public async Task SetPollOffsetAsync(string connectionId, long offset)
    {
        var connection = Find(connectionId);
        if (connection is null || offset <= connection.PollOffset)
        {
            return;
        }

        connection.PollOffset = offset;
        await _state.WriteStateAsync();
    }

    public async Task SetHomeRouteAsync(string connectionId, string routeKey)
    {
        var connection = Find(connectionId);
        if (connection is null)
        {
            return;
        }

        connection.HomeRouteKey = routeKey;
        await _state.WriteStateAsync();
    }

    public async Task SetOwnerTelemetryAsync(string connectionId, UserTelemetryContext telemetry)
    {
        var connection = Find(connectionId);
        if (connection is null || SameIdentity(connection.OwnerTelemetry, telemetry))
        {
            return;
        }

        connection.OwnerTelemetry = telemetry;
        await _state.WriteStateAsync();
    }

    // Record equality compares Roles (a string[]) by reference, so it would never match and every save would write.
    private static bool SameIdentity(UserTelemetryContext? stored, UserTelemetryContext incoming)
        => stored is not null
            && stored with { Roles = null } == incoming with { Roles = null }
            && (stored.Roles ?? []).SequenceEqual(incoming.Roles ?? []);

    public async Task RecordFaultAsync(string connectionId, string reason, DateTimeOffset now)
        => await MutateAsync(connectionId, c =>
        {
            c.FaultReason = reason;
            c.UpdatedAt = now;
        });

    public async Task<bool> DeleteAsync(string connectionId)
    {
        var connection = Find(connectionId);
        if (connection is null)
        {
            return false;
        }

        // Deregister FIRST: the directory is what the gateway runs, so a crash after this leaves a stale, unrun entry
        // here, never a running bot the owner deleted.
        await Directory.DeregisterAsync(UserId, connectionId);
        _state.State.Connections.Remove(connection);
        await _state.WriteStateAsync();
        return true;
    }

    private ChatConnection? Find(string connectionId)
        => _state.State.Connections.FirstOrDefault(c => c.Id == connectionId);

    // Applies a change and keeps the directory in step with IsRunnable, ordered so a crash fails toward "not run".
    private async Task<ChatConnection?> MutateAsync(string connectionId, Action<ChatConnection> change)
    {
        var connection = Find(connectionId);
        if (connection is null)
        {
            return null;
        }

        var wasRunnable = connection.IsRunnable;
        change(connection);
        var isRunnable = connection.IsRunnable;

        if (wasRunnable && !isRunnable)
        {
            await Directory.DeregisterAsync(UserId, connectionId);
        }

        await _state.WriteStateAsync();

        if (isRunnable)
        {
            // Also on runnable -> runnable: re-registering is idempotent and repairs a directory a crash left stale.
            await Directory.RegisterAsync(UserId, connectionId);
        }

        return connection;
    }

    private static void Apply(ChatConnection connection, ChatConnectionSettings settings, DateTimeOffset now)
    {
        connection.Name = settings.Name.Trim() is { Length: > 0 } name
            ? name[..Math.Min(name.Length, ChatGatewayLimits.MaxConnectionNameChars)]
            : connection.Platform;
        connection.TargetAgentId = string.IsNullOrWhiteSpace(settings.TargetAgentId) ? null : settings.TargetAgentId;
        connection.Enabled = settings.Enabled;
        connection.GroupsEnabled = settings.GroupsEnabled;
        connection.VoiceNotesEnabled = settings.VoiceNotesEnabled;
        connection.NotificationsEnabled = settings.NotificationsEnabled;
        connection.UpdatedAt = now;
    }

    private static void OpenPairing(ChatConnection connection, DateTimeOffset now)
    {
        connection.OwnerPlatformUserId = null;
        connection.OwnerWorkspaceId = null;
        connection.PairingCode = ChatPairingCode.Generate();
        connection.PairingExpiresAt = now + ChatGatewayLimits.PairingCodeLifetime;
        connection.PairingFailures = 0;
        connection.UpdatedAt = now;
    }
}
