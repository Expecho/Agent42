using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.AgentRegistryState")]
public sealed class AgentRegistryState
{
    [Id(0)]
    public List<AgentDefinition> Agents { get; set; } = [];

    /// <summary>
    /// True once the research agent has been seeded for this user (ADR 0082). Set in the SAME write as the
    /// insert, so a crash can never seed twice, and never cleared: a user who deletes the research agent does
    /// not get it back. Trailing and defaulted, so every existing registry reads false and is seeded once.
    /// </summary>
    [Id(1)]
    public bool ResearchAgentSeeded { get; set; }
}

/// <summary>
/// Per-user registry of stored agent personas. Rides the shared "agent-state" storage
/// provider (Orleans partitions by grain type, so no dedicated Cosmos container is
/// needed — see ADR 0001 and the pre-declared-containers gotcha).
/// </summary>
public sealed class AgentRegistryGrain : Grain, IAgentRegistryGrain
{
    private readonly IPersistentState<AgentRegistryState> _state;

    public AgentRegistryGrain(
        [PersistentState("agents", AgentGrain.StorageProviderName)] IPersistentState<AgentRegistryState> state)
    {
        _state = state;
    }

    public Task<IReadOnlyList<AgentDefinition>> GetAllAsync()
        => Task.FromResult<IReadOnlyList<AgentDefinition>>(
            [.. _state.State.Agents.OrderByDescending(a => a.UpdatedAt)]);

    public Task<AgentDefinition?> GetAsync(string id)
        => Task.FromResult(_state.State.Agents.FirstOrDefault(a => a.Id == id));

    public async Task<AgentSaveResult> SaveAsync(AgentDefinition agent, bool regenerateKey = false, bool regenerateEmail = false)
    {
        var agents = _state.State.Agents;
        var now = DateTimeOffset.UtcNow;
        var userId = this.GetPrimaryKeyString();

        var existing = string.IsNullOrEmpty(agent.Id)
            ? -1
            : agents.FindIndex(a => a.Id == agent.Id);

        if (existing < 0)
        {
            // A request can never create the research kind: that is set only by seeding (ADR 0082). Copilot is the
            // one kind a user may choose, on create only; anything else stores an ordinary agent. A per-chat-only
            // kind is never continuous.
            var kind = AgentKinds.IsCopilot(agent.Kind) ? AgentKinds.Copilot : null;
            return await InsertAsync(agent with
            {
                Kind = kind,
                Continuous = agent.Continuous && !AgentKinds.IsPerChatOnly(kind),
            });
        }

        // ── Update ── keep the existing hash by default; the request never carries it (the grain is the
        // authority). Only an explicit regenerate mints a new key.
        var previous = agents[existing];
        var storedHash = previous.ApiKeyHash;
        string? plaintext = null;

        if (regenerateKey)
        {
            // Regenerate order (constraint 2) — the OPPOSITE of create/delete, deliberately: deregister
            // the OLD hash FIRST, then write the new hash, then register it. Any crash then leaves the
            // old key DEAD (worst case a brief window where neither resolves), never a permanently-live
            // revoked key. Writing the registry first would strand a live old-hash entry the stored new
            // hash could never clear.
            if (!string.IsNullOrEmpty(previous.ApiKeyHash))
            {
                await Directory(previous.ApiKeyHash).DeregisterAsync();
            }

            var (raw, newHash) = ApiKey.Generate();
            plaintext = raw;
            storedHash = newHash;
        }

        // The email token follows the same regenerate order, independently (ADR 0095): the OLD address dies first.
        var storedEmailToken = previous.EmailToken;
        if (regenerateEmail)
        {
            await DeregisterEmailAsync(previous.EmailToken);
            storedEmailToken = EmailAddress.Generate().Token;
        }

        // Kind is preserved exactly like the hash: the grain is the authority, and an update never changes it
        // (ADR 0082). Research and copilot agents are per-chat only (ADR 0016: a continuous chat is one 2 MB
        // grain), so Continuous is forced off rather than trusted.
        var updated = agent with
        {
            CreatedAt = previous.CreatedAt,
            UpdatedAt = now,
            ApiKeyHash = storedHash,
            EmailToken = storedEmailToken,
            Kind = previous.Kind,
            Continuous = agent.Continuous && !AgentKinds.IsPerChatOnly(previous.Kind),
        };
        agents[existing] = updated;
        await _state.WriteStateAsync();

        if (regenerateKey)
        {
            await Directory(storedHash).RegisterAsync(userId, updated.Id);
        }

        if (regenerateEmail)
        {
            await EmailDirectory(storedEmailToken!).RegisterAsync(userId, updated.Id);
        }

        return new AgentSaveResult(updated, plaintext);
    }

    public async Task EnsureResearchAgentAsync(string model)
    {
        // No concrete model means no provider yet (ADR 0019). Seeding with "" would hand the editor the
        // no-model sentinel it cannot save, so wait: the flag stays false and the next list after the user adds
        // a provider seeds it.
        if (_state.State.ResearchAgentSeeded || string.IsNullOrEmpty(model))
        {
            return;
        }

        // The raw key is discarded: nobody asked for one, and the user regenerates to get it (ADR 0022).
        await InsertAsync(
            new AgentDefinition(
                "", ResearchAgentDefaults.Name, ResearchAgentDefaults.Description, model,
                ResearchAgentDefaults.SystemPrompt, default, default,
                // ToList, never a collection expression: this list is persisted into IReadOnlyList<string>, and
                // a spread there is a compiler-internal type the Cosmos serializer cannot read back (rule 2).
                EnabledTools: ResearchAgentDefaults.EnabledTools.ToList(), Kind: AgentKinds.Research),
            markResearchSeeded: true);
    }

    // ── Insert ── mint a key and an email token (ADR 0095). Create order (constraint 2): write registry state (carrying the new hash) FIRST,
    // THEN register the hash. A crash in between leaves the agent with a hash but no directory entry — the key is
    // simply unusable (user regenerates), never a live orphan pointing at a stranger's agent. The seeded flag
    // rides the SAME write as the insert, so a crash can never seed twice.
    private async Task<AgentSaveResult> InsertAsync(AgentDefinition agent, bool markResearchSeeded = false)
    {
        var now = DateTimeOffset.UtcNow;
        var (raw, hash) = ApiKey.Generate();
        var emailToken = EmailAddress.Generate().Token;
        var inserted = agent with
        {
            Id = Guid.NewGuid().ToString(),
            CreatedAt = now,
            UpdatedAt = now,
            ApiKeyHash = hash,
            EmailToken = emailToken,
        };
        _state.State.Agents.Add(inserted);
        _state.State.ResearchAgentSeeded |= markResearchSeeded;
        await _state.WriteStateAsync();

        await Directory(hash).RegisterAsync(this.GetPrimaryKeyString(), inserted.Id);
        await EmailDirectory(emailToken).RegisterAsync(this.GetPrimaryKeyString(), inserted.Id);
        return new AgentSaveResult(inserted, raw);
    }

    public async Task DeleteAsync(string id)
    {
        var agent = _state.State.Agents.FirstOrDefault(a => a.Id == id);
        if (agent is null)
        {
            return;
        }

        // The webhook is cleared BEFORE anything else (ADR 0094), the opposite of the key order below, on purpose:
        // conversations outlive their agent (ADR 0003) and keep delivering under its id without consulting this
        // registry, so a webhook left behind by a mid-failure would keep SENDING content. Clearing first fails safe —
        // a failed clear throws here and the agent survives, without a webhook at worst.
        await GrainFactory.GetGrain<IAgentWebhookGrain>(AgentWebhookKeys.KeyFor(this.GetPrimaryKeyString(), id))
            .ClearAsync();

        // Delete order (constraint 2): remove from the registry (write) FIRST, THEN deregister the hash.
        // A mid-failure leaves at worst an orphan directory entry, and the run endpoint re-checks the
        // agent exists after resolving — so a resolved-but-deleted agent still fails closed (401).
        _state.State.Agents.RemoveAll(a => a.Id == id);
        await _state.WriteStateAsync();

        if (!string.IsNullOrEmpty(agent.ApiKeyHash))
        {
            await Directory(agent.ApiKeyHash).DeregisterAsync();
        }

        await DeregisterEmailAsync(agent.EmailToken);
    }

    // The email directory entry for a raw token (ADR 0095): its own grain type, never the key directory, so an
    // address can never authenticate as an API key. The registry keeps it in sync on every token change.
    private IEmailAddressDirectoryGrain EmailDirectory(string token)
        => GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(EmailAddress.Hash(token));

    private async Task DeregisterEmailAsync(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            await EmailDirectory(token).DeregisterAsync();
        }
    }

    // The global key-hash → owner directory grain for a given hash (ADR 0022). Keyed by the hash, so
    // this is an O(1) grain reference — the registry keeps it in sync on every hash change.
    private IAgentKeyDirectoryGrain Directory(string hash)
        => GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash);
}
