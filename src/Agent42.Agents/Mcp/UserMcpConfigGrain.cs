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
            };
            servers[existing] = stored;
        }
        else
        {
            stored = server;
            servers.Add(stored);
        }

        await BumpAsync(_state.State.Config with { Servers = servers });
        return stored;
    }

    public async Task DeleteAsync(string id)
    {
        var servers = _state.State.Config.Servers.ToList();
        if (servers.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await BumpAsync(_state.State.Config with { Servers = servers });
        }
    }

    private static string? Keep(string? incoming, string? stored) =>
        string.IsNullOrEmpty(incoming) ? stored : incoming;

    // Every write bumps the monotonic version, which the per-user tool-provider layer keys its cache on —
    // so a save is picked up on the next grain activation without an app restart.
    private async Task BumpAsync(UserMcpConfig next)
    {
        _state.State.Config = next with { Version = next.Version + 1 };
        await _state.WriteStateAsync();
    }
}
