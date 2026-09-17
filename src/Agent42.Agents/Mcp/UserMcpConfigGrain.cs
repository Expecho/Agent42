using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.UserMcpConfigState")]
public sealed class UserMcpConfigState
{
    // UserMcpConfig.Empty is a factory (fresh instance), so this is a per-state default, never a
    // shared mutable static — see the note on UserMcpConfig.Empty.
    [Id(0)]
    public UserMcpConfig Config { get; set; } = UserMcpConfig.Empty;
}

/// <summary>
/// Per-user remote-MCP-server overlay grain (config-driven remote MCP servers). Rides the shared
/// "agent-state" storage provider on a distinct "mcp-servers" state name (like
/// <see cref="UserProviderConfigGrain"/>'s "providers"), so it is a separate Cosmos item from the 2 MB
/// session (ADR 0004) — a user secret never lands in the session, and no new Cosmos container is needed.
/// </summary>
public sealed class UserMcpConfigGrain : Grain, IUserMcpConfigGrain
{
    private readonly IPersistentState<UserMcpConfigState> _state;

    public UserMcpConfigGrain(
        [PersistentState("mcp-servers", AgentGrain.StorageProviderName)] IPersistentState<UserMcpConfigState> state)
    {
        _state = state;
    }

    public Task<UserMcpConfig> GetAsync() => Task.FromResult(_state.State.Config);

    public Task<int> GetVersionAsync() => Task.FromResult(_state.State.Config.Version);

    public async Task<StoredMcpServer> SaveAsync(StoredMcpServer server)
    {
        var servers = _state.State.Config.Servers.ToList();
        var existing = servers.FindIndex(
            s => string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase));

        StoredMcpServer stored;
        if (existing >= 0)
        {
            // A blank incoming ciphertext on a secret field means "keep the stored secret" (the API sends
            // no ciphertext when the user left a write-only secret field empty) — so the user needn't
            // re-enter a secret to edit other fields. A non-blank ciphertext replaces it.
            var prev = servers[existing];
            stored = server with
            {
                HeaderValueCiphertext = Keep(server.HeaderValueCiphertext, prev.HeaderValueCiphertext),
                ClientSecretCiphertext = Keep(server.ClientSecretCiphertext, prev.ClientSecretCiphertext),
                TokenCiphertext = Keep(server.TokenCiphertext, prev.TokenCiphertext),
                // Discovered names are preserved for the same reason as the ciphertexts, and it is even less
                // optional: ToStored builds a FRESH record from the upsert request, which carries no names at
                // all, so without this line every rename, header rotation or enable/disable round-trip erases
                // them — and the tool picker loses its per-tool checkboxes until that user's next chat
                // reconnects. Edit_PreservesKnownToolNames pins it.
                KnownToolNames = server.KnownToolNames ?? prev.KnownToolNames,
                // Unconditional, not `?? prev`: the hash is grain-owned (ADR 0076). ToStored rebuilds the record
                // from a request that carries no hash, so without this every edit would silently kill the bridge
                // key; with `??` instead, nothing could ever clear it. Only Mint/Clear/Delete change it.
                BridgeKeyHash = prev.BridgeKeyHash,
            };
            servers[existing] = stored;
        }
        else
        {
            // An insert never arrives with a hash — a key exists only once MintBridgeKeyAsync has registered it.
            stored = server with { BridgeKeyHash = null };
            servers.Add(stored);
        }

        await BumpAsync(_state.State.Config with { Servers = servers });
        return stored;
    }

    public async Task DeleteAsync(string id)
    {
        var servers = _state.State.Config.Servers.ToList();
        var removed = servers.Find(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed is null)
        {
            return;
        }

        // Delete order (ADR 0022): write FIRST, then deregister. A mid-failure leaves an orphan directory
        // entry, which the bridge endpoint still rejects because the server it names no longer exists.
        servers.Remove(removed);
        await BumpAsync(_state.State.Config with { Servers = servers });
        await DeregisterAsync(removed.BridgeKeyHash);
    }

    public async Task<string?> MintBridgeKeyAsync(string serverId)
    {
        var servers = _state.State.Config.Servers.ToList();
        var index = IndexOf(servers, serverId);
        if (index < 0 || servers[index].Auth != nameof(McpAuthMode.LocalBridge))
        {
            return null;
        }

        // Regenerate order (ADR 0022) — the OPPOSITE of clear/delete, deliberately: deregister the OLD hash
        // first, so any crash from here on leaves the old key dead rather than a revoked key still resolving.
        await DeregisterAsync(servers[index].BridgeKeyHash);

        var (raw, hash) = BridgeKey.Generate();
        servers[index] = servers[index] with { BridgeKeyHash = hash };

        // Not BumpAsync: the hash is not connection config (see IUserMcpConfigGrain.MintBridgeKeyAsync).
        _state.State.Config = _state.State.Config with { Servers = servers };
        await _state.WriteStateAsync();

        await GrainFactory.GetGrain<ILocalBridgeKeyDirectoryGrain>(hash).RegisterAsync(this.GetPrimaryKeyString(), servers[index].Id);
        return raw;
    }

    public async Task ClearBridgeKeyAsync(string serverId)
    {
        var servers = _state.State.Config.Servers.ToList();
        var index = IndexOf(servers, serverId);
        var hash = index < 0 ? null : servers[index].BridgeKeyHash;
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        servers[index] = servers[index] with { BridgeKeyHash = null };
        _state.State.Config = _state.State.Config with { Servers = servers };
        await _state.WriteStateAsync();
        await DeregisterAsync(hash);
    }

    public async Task RecordToolNamesAsync(string serverId, IReadOnlyList<string> toolNames)
    {
        var servers = _state.State.Config.Servers.ToList();
        var index = servers.FindIndex(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return; // a server deleted between connect and record — nothing to attach names to
        }

        // No-op when unchanged: the common case is re-discovering the same tools on every activation, and a
        // write per activation would be pure Cosmos churn. Ordinal and order-sensitive on purpose — a server
        // that reorders its tools has genuinely changed what the picker should render.
        var current = servers[index].KnownToolNames;
        if (current is not null && current.SequenceEqual(toolNames, StringComparer.Ordinal))
        {
            return;
        }

        // .ToList(), NOT a collection expression. A collection expression targeting IReadOnlyList<T> compiles
        // to a compiler-internal <>z__ReadOnlyList<T>, whose type name Newtonsoft writes into the Cosmos
        // document's $type and then CANNOT construct on read — the whole mcp-servers document becomes
        // unreadable, taking every one of that user's MCP servers with it. Measured; see the gotcha and
        // KnownToolNames_SurviveTheCosmosJsonRoundTrip. AgentGrain.BuildAgent.cs:317 already guards the same
        // way for EnabledTools.
        servers[index] = servers[index] with { KnownToolNames = toolNames.ToList() };

        // Deliberately NOT BumpAsync — see IUserMcpConfigGrain.RecordToolNamesAsync. Bumping here would
        // invalidate the provider's live-socket cache for every warm grain of this user.
        _state.State.Config = _state.State.Config with { Servers = servers };
        await _state.WriteStateAsync();
    }

    public Task InvalidateToolCacheAsync() => BumpAsync(_state.State.Config);

    private static int IndexOf(List<StoredMcpServer> servers, string id) =>
        servers.FindIndex(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    private Task DeregisterAsync(string? hash) =>
        string.IsNullOrEmpty(hash)
            ? Task.CompletedTask
            : GrainFactory.GetGrain<ILocalBridgeKeyDirectoryGrain>(hash).DeregisterAsync();

    private static string? Keep(string? incoming, string? stored) =>
        string.IsNullOrEmpty(incoming) ? stored : incoming;

    // Every CONFIGURATION write bumps the monotonic version, which the per-user tool-provider layer keys its
    // cache on — so a save is picked up on the next grain activation without an app restart.
    // RecordToolNamesAsync is the one write that deliberately does not go through here.
    private async Task BumpAsync(UserMcpConfig next)
    {
        _state.State.Config = next with { Version = next.Version + 1 };
        await _state.WriteStateAsync();
    }
}
