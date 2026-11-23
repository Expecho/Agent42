using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

public sealed class AgentRegistryGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    // AC1: create → list → get → update → delete, and the store survives grain deactivation.
    [Fact]
    public async Task AgentRegistry_CrudRoundTrips_AndSurvivesReactivation()
    {
        const string user = "registry-crud-user";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        // Create: an empty id means insert; the grain assigns the id + timestamps.
        // Continuous: true exercises the history-mode flag (ADR 0016) through the whole
        // create → get → update → reactivate cycle.
        var createResult = await registry.SaveAsync(new AgentDefinition(
            Id: "", Name: "Marvin", Description: "A paranoid android",
            Model: "gpt-4o", SystemPrompt: "You are perpetually depressed.",
            CreatedAt: default, UpdatedAt: default, Continuous: true));
        var created = createResult.Agent;

        Assert.False(string.IsNullOrEmpty(created.Id), "SaveAsync should assign an id on insert.");
        Assert.NotEqual(default, created.CreatedAt);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.True(created.Continuous, "the history-mode flag should persist on insert.");

        // List + get.
        Assert.Equal(created, Assert.Single(await registry.GetAllAsync()));
        Assert.Equal(created, await registry.GetAsync(created.Id));

        // Update: same id replaces in place, preserving CreatedAt and refreshing UpdatedAt.
        var updated = (await registry.SaveAsync(created with
        {
            Name = "Marvin the Paranoid Android",
            SystemPrompt = "You are brilliant but morose.",
        })).Agent;

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
        Assert.Equal("Marvin the Paranoid Android", updated.Name);
        Assert.Single(await registry.GetAllAsync());

        // Force deactivation so the next call re-reads persisted state (Cosmos in prod).
        await registry.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        var afterReactivation = await reactivated.GetAsync(updated.Id);
        Assert.Equal(updated, afterReactivation);
        Assert.True(afterReactivation!.Continuous, "the history-mode flag should survive reactivation.");

        // Delete.
        await reactivated.DeleteAsync(updated.Id);
        Assert.Empty(await reactivated.GetAllAsync());
        Assert.Null(await reactivated.GetAsync(updated.Id));
    }

    [Fact]
    public async Task AgentRegistry_ListsMostRecentlyUpdatedFirst()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-order-user");

        var first = (await registry.SaveAsync(new AgentDefinition(
            "", "First", "", "gpt-4o", "sys", default, default))).Agent;
        var second = (await registry.SaveAsync(new AgentDefinition(
            "", "Second", "", "gpt-4o", "sys", default, default))).Agent;

        // Touch the first one so it becomes the most recently updated.
        var firstTouched = (await registry.SaveAsync(first with { Description = "touched" })).Agent;

        var all = await registry.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(firstTouched.Id, all[0].Id);
        Assert.Equal(second.Id, all[1].Id);
    }

    // AC1: creating an agent returns a non-empty raw key ONCE; only its hash is stored; the hash
    // resolves in the global directory to (user, agentId).
    [Fact]
    public async Task AgentRegistry_Create_MintsKey_StoresHashOnly()
    {
        const string user = "registry-mintkey-user";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var result = await registry.SaveAsync(new AgentDefinition(
            "", "Keyed", "", "gpt-4o", "sys", default, default));

        Assert.False(string.IsNullOrEmpty(result.PlaintextApiKey), "insert should mint a raw key.");
        Assert.StartsWith("sk_agent_", result.PlaintextApiKey!);
        // Only the hash is stored, and it is the hash OF the returned raw key.
        Assert.Equal(ApiKey.Hash(result.PlaintextApiKey!), result.Agent.ApiKeyHash);

        // The hash resolves to this agent in the global directory.
        var owner = await Cluster.GrainFactory
            .GetGrain<IAgentKeyDirectoryGrain>(result.Agent.ApiKeyHash).ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal(user, owner!.UserId);
        Assert.Equal(result.Agent.Id, owner.AgentId);

        // A plain update (no regenerate) mints no key and keeps the same hash.
        var updated = await registry.SaveAsync(result.Agent with { Name = "Renamed" });
        Assert.Null(updated.PlaintextApiKey);
        Assert.Equal(result.Agent.ApiKeyHash, updated.Agent.ApiKeyHash);
    }

    // AC6: regenerating a key invalidates the old one — new hash resolves, old hash no longer does.
    [Fact]
    public async Task AgentKey_Regenerate_InvalidatesOldKey()
    {
        const string user = "registry-regen-user";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var created = await registry.SaveAsync(new AgentDefinition(
            "", "Rotatable", "", "gpt-4o", "sys", default, default));
        var oldHash = created.Agent.ApiKeyHash;

        var regenerated = await registry.SaveAsync(created.Agent, regenerateKey: true);

        Assert.False(string.IsNullOrEmpty(regenerated.PlaintextApiKey), "regenerate should mint a new key.");
        Assert.NotEqual(oldHash, regenerated.Agent.ApiKeyHash);
        Assert.Equal(ApiKey.Hash(regenerated.PlaintextApiKey!), regenerated.Agent.ApiKeyHash);

        // Old hash no longer resolves; new hash does.
        Assert.Null(await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(oldHash).ResolveAsync());
        var owner = await Cluster.GrainFactory
            .GetGrain<IAgentKeyDirectoryGrain>(regenerated.Agent.ApiKeyHash).ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal(created.Agent.Id, owner!.AgentId);
    }

    // AC7: deleting an agent deregisters its key (the hash no longer resolves).
    [Fact]
    public async Task AgentDelete_DeregistersKey()
    {
        const string user = "registry-delete-user";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var created = await registry.SaveAsync(new AgentDefinition(
            "", "Doomed", "", "gpt-4o", "sys", default, default));
        var hash = created.Agent.ApiKeyHash;
        Assert.NotNull(await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash).ResolveAsync());

        await registry.DeleteAsync(created.Agent.Id);

        Assert.Null(await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash).ResolveAsync());
    }

    // AC2 (ADR 0082): no concrete model means no seed. The flag stays false, so the first call that DOES carry a
    // model seeds, and a later one does not seed again.
    [Fact]
    public async Task NoModel_DoesNotSeed_ThenSeedsLater()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-research-nomodel");

        await registry.EnsureResearchAgentAsync("");
        Assert.Empty(await registry.GetAllAsync());

        await registry.EnsureResearchAgentAsync("foundry:gpt-5.4");
        await registry.EnsureResearchAgentAsync("foundry:gpt-5.4");

        var seeded = Assert.Single(await registry.GetAllAsync());
        Assert.Equal(AgentKinds.Research, seeded.Kind);
        Assert.Equal(ResearchAgentDefaults.Name, seeded.Name);
        Assert.Equal("foundry:gpt-5.4", seeded.Model);
        Assert.Equal([ToolCatalog.WebSearch], seeded.EnabledTools);
        Assert.False(seeded.Continuous);

        // It went through the normal insert path, so its ADR 0022 key hash is registered like any other agent's.
        var owner = await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(seeded.ApiKeyHash).ResolveAsync();
        Assert.Equal(seeded.Id, owner!.AgentId);
    }

    // AC3 (ADR 0082): a deleted research agent stays deleted, including across a reactivation, which proves the
    // flag was persisted rather than merely held in memory.
    [Fact]
    public async Task DeletedResearchAgent_IsNotReseeded()
    {
        const string user = "registry-research-deleted";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        await registry.EnsureResearchAgentAsync("fake-model");
        await registry.DeleteAsync(Assert.Single(await registry.GetAllAsync()).Id);

        await registry.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        await reactivated.EnsureResearchAgentAsync("fake-model");

        Assert.Empty(await reactivated.GetAllAsync());
    }

    // AC4 (ADR 0082): an update can change everything the user owns, but not Kind (the request never carries
    // it), and a research agent cannot be made continuous.
    [Fact]
    public async Task Update_PreservesKind_AndForcesPerChat()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-research-update");
        await registry.EnsureResearchAgentAsync("fake-model");
        var seeded = Assert.Single(await registry.GetAllAsync());

        var updated = (await registry.SaveAsync(seeded with
        {
            Name = "Deep dive",
            Kind = null,
            Continuous = true,
        })).Agent;

        Assert.Equal("Deep dive", updated.Name);
        Assert.Equal(AgentKinds.Research, updated.Kind);
        Assert.False(updated.Continuous);
        Assert.Equal(seeded.ApiKeyHash, updated.ApiKeyHash);
    }

    // Research is set ONLY by seeding (ADR 0082): an insert that claims it, or an unknown kind, stores an ordinary
    // agent, and an ordinary agent can still be continuous. Copilot is the one kind a create may claim, and it is
    // forced per-chat.
    [Theory]
    [InlineData(AgentKinds.Research, null, true)]
    [InlineData("made-up", null, true)]
    [InlineData(AgentKinds.Copilot, AgentKinds.Copilot, false)]
    public async Task Insert_CanClaimCopilot_NotResearch(string claimed, string? expectedKind, bool expectedContinuous)
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>($"registry-kind-insert-{claimed}");

        var created = (await registry.SaveAsync(new AgentDefinition(
            "", "Impostor", "", "fake-model", "sys", default, default, Continuous: true,
            Kind: claimed))).Agent;

        Assert.Equal(expectedKind, created.Kind);
        Assert.Equal(expectedContinuous, created.Continuous);
    }

    // An update never changes Kind in either direction: it cannot turn an ordinary agent into a copilot one, and a
    // copilot agent stays copilot (and per-chat) whatever the request says.
    [Fact]
    public async Task Update_PreservesCopilotKind_AndForcesPerChat()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-copilot-update");
        var copilot = (await registry.SaveAsync(new AgentDefinition(
            "", "Pilot", "", "fake-model", "sys", default, default, Kind: AgentKinds.Copilot))).Agent;
        var plain = (await registry.SaveAsync(new AgentDefinition(
            "", "Plain", "", "fake-model", "sys", default, default))).Agent;

        var updatedCopilot = (await registry.SaveAsync(copilot with { Name = "Pilot 2", Kind = null, Continuous = true })).Agent;
        var updatedPlain = (await registry.SaveAsync(plain with { Kind = AgentKinds.Copilot, Continuous = true })).Agent;

        Assert.Equal("Pilot 2", updatedCopilot.Name);
        Assert.Equal(AgentKinds.Copilot, updatedCopilot.Kind);
        Assert.False(updatedCopilot.Continuous);
        Assert.Null(updatedPlain.Kind);
        Assert.True(updatedPlain.Continuous);
    }

    // AC9 (ADR 0094): deleting an agent clears its webhook — URL, secret and log.
    [Fact]
    public async Task AgentDelete_ClearsWebhook()
    {
        const string user = "registry-webhook-delete";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        var agent = (await registry.SaveAsync(NewAgent())).Agent;
        var webhook = Cluster.GrainFactory.GetGrain<IAgentWebhookGrain>(AgentWebhookKeys.KeyFor(user, agent.Id));
        await webhook.SaveAsync("https://hooks.example.com/delete", "secret");

        await registry.DeleteAsync(agent.Id);

        var status = await webhook.GetAsync();
        Assert.Null(status.Url);
        Assert.False(status.HasSecret);
        Assert.Empty(await registry.GetAllAsync());
    }

    // AC9 (ADR 0094): the clear runs BEFORE the registry write — a clear that throws leaves the agent in place, so a
    // failure can never strand a live webhook behind a deleted agent.
    [Fact]
    public async Task AgentDelete_WhenWebhookClearFails_AgentSurvives()
    {
        const string user = "registry-" + FailingWebhookClearFilter.Marker;
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        var agent = (await registry.SaveAsync(NewAgent())).Agent;

        await Assert.ThrowsAnyAsync<Exception>(() => registry.DeleteAsync(agent.Id));

        Assert.Equal(agent.Id, Assert.Single(await registry.GetAllAsync()).Id);
    }

    // ADR 0096 AC9: create mints an email token that resolves, in the EMAIL directory, to (user, agentId).
    [Fact]
    public async Task AgentCreate_MintsResolvableEmailAddress()
    {
        const string user = "registry-email-create-user";
        var created = (await Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user).SaveAsync(NewAgent())).Agent;

        Assert.Matches("^agent-[a-z2-7]{32}$", created.EmailToken);
        var owner = await Cluster.GrainFactory
            .GetGrain<IEmailAddressDirectoryGrain>(EmailAddress.Hash(created.EmailToken!)).ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal(user, owner!.UserId);
        Assert.Equal(created.Id, owner.AgentId);
    }

    // ADR 0096 AC10: regenerate kills the old address and the new one resolves. A plain update and a KEY regenerate
    // both keep the address (the grain is the authority; a request carrying a token is ignored).
    [Fact]
    public async Task EmailAddress_Regenerate_InvalidatesOld()
    {
        const string user = "registry-email-regen-user";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        var created = (await registry.SaveAsync(NewAgent())).Agent;
        var oldToken = created.EmailToken!;

        var plain = (await registry.SaveAsync(created with { Name = "Renamed", EmailToken = "agent-forged" })).Agent;
        var keyRotated = (await registry.SaveAsync(plain, regenerateKey: true)).Agent;
        Assert.Equal(oldToken, plain.EmailToken);
        Assert.Equal(oldToken, keyRotated.EmailToken);

        var regenerated = await registry.SaveAsync(keyRotated, regenerateEmail: true);

        Assert.NotEqual(oldToken, regenerated.Agent.EmailToken);
        Assert.Null(regenerated.PlaintextApiKey);
        Assert.Equal(keyRotated.ApiKeyHash, regenerated.Agent.ApiKeyHash);
        Assert.Null(await Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(EmailAddress.Hash(oldToken)).ResolveAsync());
        var owner = await Cluster.GrainFactory
            .GetGrain<IEmailAddressDirectoryGrain>(EmailAddress.Hash(regenerated.Agent.EmailToken!)).ResolveAsync();
        Assert.Equal(created.Id, owner?.AgentId);
    }

    // ADR 0096 AC11: delete deregisters BOTH the API-key and the email directories.
    [Fact]
    public async Task AgentDelete_DeregistersBothDirectories()
    {
        const string user = "registry-email-delete-user";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        var created = (await registry.SaveAsync(NewAgent())).Agent;
        var emailHash = EmailAddress.Hash(created.EmailToken!);
        Assert.NotNull(await Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(emailHash).ResolveAsync());

        await registry.DeleteAsync(created.Id);

        Assert.Null(await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(created.ApiKeyHash).ResolveAsync());
        Assert.Null(await Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(emailHash).ResolveAsync());
    }

    // AC2 (ADR 0140): a preset persists on insert and update, and survives reactivation-free reads.
    [Fact]
    public async Task Save_Retention_PersistsOnInsertAndUpdate()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-retention");

        var created = (await registry.SaveAsync(NewAgent() with { RetentionMinutes = 60 })).Agent;
        Assert.Equal(60, created.RetentionMinutes);

        var updated = (await registry.SaveAsync(created with { RetentionMinutes = 10080 })).Agent;
        Assert.Equal(10080, updated.RetentionMinutes);

        var cleared = (await registry.SaveAsync(updated with { RetentionMinutes = null })).Agent;
        Assert.Null(cleared.RetentionMinutes);
    }

    // AC2: per-chat-only kinds (copilot, research) never carry a retention value, on insert or update.
    [Fact]
    public async Task Save_Retention_ForcedNullForPerChatOnlyKinds()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-retention-kinds");

        var copilot = (await registry.SaveAsync(NewAgent() with { Kind = AgentKinds.Copilot, RetentionMinutes = 60 })).Agent;
        Assert.Null(copilot.RetentionMinutes);
        var again = (await registry.SaveAsync(copilot with { RetentionMinutes = 60 })).Agent;
        Assert.Null(again.RetentionMinutes);

        await registry.EnsureResearchAgentAsync("fake-model");
        var research = (await registry.GetAllAsync()).Single(a => a.Kind == AgentKinds.Research);
        var updated = (await registry.SaveAsync(research with { RetentionMinutes = 60 })).Agent;
        Assert.Null(updated.RetentionMinutes);
    }

    private static AgentDefinition NewAgent()
        => new("", "Hooky", "", "gpt-4o", "You are Hooky.", default, default);

    // ── Plain-name inbound address (ADR 0141) ──────────────────────────────────────────────────────────────────────
    // Slugs are global across the shared silo, so every test derives its name from a unique suffix.

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private static AgentDefinition SafePlainAgent(string name)
        => NewAgent() with { Name = name, EnabledTools = new List<string> { ToolCatalog.WebSearch } };

    private IEmailAddressDirectoryGrain EmailDir(string token)
        => Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(EmailAddress.Hash(token));

    private async Task<AgentDefinition> OptInAsync(IAgentRegistryGrain registry, string name)
    {
        var created = (await registry.SaveAsync(SafePlainAgent(name))).Agent;
        var result = await registry.SaveAsync(created, plainEmail: true);
        Assert.Null(result.EmailError);
        return result.Agent;
    }

    [Fact]
    public async Task ExistingAgent_NoPlainFlag_KeepsRandomAddress()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-default-" + Unique());
        var created = (await registry.SaveAsync(NewAgent())).Agent;
        var saved = (await registry.SaveAsync(created with { Name = "Other" })).Agent;

        Assert.Null(saved.EmailPlain);
        Assert.Equal(created.EmailToken, saved.EmailToken);
        Assert.Matches("^agent-[a-z2-7]{32}$", saved.EmailToken);
        Assert.NotNull(await EmailDir(saved.EmailToken!).ResolveAsync());
    }

    [Fact]
    public async Task PlainEmail_OptIn_ReplacesRandomAddress()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-optin-" + u);
        var created = (await registry.SaveAsync(SafePlainAgent("Calendar " + u))).Agent;
        var randomToken = created.EmailToken!;

        var plain = (await registry.SaveAsync(created, plainEmail: true)).Agent;

        Assert.True(plain.EmailPlain);
        Assert.Equal("calendar-" + u, plain.EmailToken);
        Assert.Null(await EmailDir(randomToken).ResolveAsync());
        Assert.Equal(created.Id, (await EmailDir(plain.EmailToken!).ResolveAsync())?.AgentId);
    }

    [Fact]
    public async Task PlainEmail_Collision_Refused_NothingChanged()
    {
        var u = Unique();
        var first = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-coll-a-" + u);
        var second = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-coll-b-" + u);
        await OptInAsync(first, "Shared " + u);

        var created = (await second.SaveAsync(SafePlainAgent("Shared " + u))).Agent;
        var refused = await second.SaveAsync(created, plainEmail: true);

        Assert.Equal(EmailErrors.Taken, refused.EmailError);
        var stored = (await second.GetAsync(created.Id))!;
        Assert.Equal(created.EmailToken, stored.EmailToken);
        Assert.Null(stored.EmailPlain);
        Assert.Equal(created.Id, (await EmailDir(created.EmailToken!).ResolveAsync())?.AgentId);
    }

    [Fact]
    public async Task PlainEmail_OtherUsersCollision_Refused()
    {
        var u = Unique();
        var owner = await OptInAsync(Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-x-a-" + u), "Cross " + u);
        var other = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-x-b-" + u);
        var created = (await other.SaveAsync(SafePlainAgent("cross-" + u.ToUpperInvariant()))).Agent;

        Assert.Equal(EmailErrors.Taken, (await other.SaveAsync(created, plainEmail: true)).EmailError);
        Assert.Equal("plain-x-a-" + u, (await EmailDir(owner.EmailToken!).ResolveAsync())?.UserId);
    }

    [Fact]
    public async Task PlainEmail_CaseVariantName_CollidesAcrossUsers()
    {
        var u = Unique();
        await OptInAsync(Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-case-a-" + u), "Mixed Case " + u);
        var other = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-case-b-" + u);
        var created = (await other.SaveAsync(SafePlainAgent("MIXED  case " + u))).Agent;

        Assert.Equal(EmailErrors.Taken, (await other.SaveAsync(created, plainEmail: true)).EmailError);
    }

    [Fact]
    public async Task PlainEmail_CollidesWithRandomToken_Refused()
    {
        var u = Unique();
        var holder = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-rnd-a-" + u);
        var held = (await holder.SaveAsync(NewAgent())).Agent;
        var other = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-rnd-b-" + u);
        var created = (await other.SaveAsync(SafePlainAgent("x " + u))).Agent;
        // Pre-seed a directory entry that equals the slug the opt-in would derive, held by a live agent's random token.
        await EmailDir("x-" + u).RegisterAsync("plain-rnd-a-" + u, held.Id);

        Assert.Equal(EmailErrors.Taken, (await other.SaveAsync(created, plainEmail: true)).EmailError);
    }

    [Fact]
    public async Task PlainEmail_OptIn_EmptySlug_Refused()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-empty-" + Unique());
        var created = (await registry.SaveAsync(SafePlainAgent("日本語"))).Agent;

        Assert.Equal(EmailErrors.Empty, (await registry.SaveAsync(created, plainEmail: true)).EmailError);
    }

    [Fact]
    public async Task PlainEmail_OptIn_Reserved_Refused()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-reserved-" + Unique());
        var created = (await registry.SaveAsync(SafePlainAgent("Postmaster"))).Agent;

        Assert.Equal(EmailErrors.Reserved, (await registry.SaveAsync(created, plainEmail: true)).EmailError);
    }

    [Fact]
    public async Task PlainEmail_Rename_MovesAddress_OldStopsResolving()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-rename-" + u);
        var plain = await OptInAsync(registry, "Before " + u);

        var renamed = (await registry.SaveAsync(plain with { Name = "After " + u })).Agent;

        Assert.Equal("after-" + u, renamed.EmailToken);
        Assert.True(renamed.EmailPlain);
        Assert.Null(await EmailDir("before-" + u).ResolveAsync());
        Assert.Equal(plain.Id, (await EmailDir("after-" + u).ResolveAsync())?.AgentId);
    }

    [Fact]
    public async Task PlainEmail_RenameToTaken_Refused_OldStillWorks()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-rn-taken-" + u);
        await OptInAsync(Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-rn-other-" + u), "Taken " + u);
        var plain = await OptInAsync(registry, "Mine " + u);

        var refused = await registry.SaveAsync(plain with { Name = "Taken " + u });

        Assert.Equal(EmailErrors.Taken, refused.EmailError);
        Assert.Equal("Mine " + u, (await registry.GetAsync(plain.Id))!.Name);
        Assert.Equal(plain.Id, (await EmailDir("mine-" + u).ResolveAsync())?.AgentId);
    }

    [Fact]
    public async Task PlainEmail_RenameToEmptySlug_Refused()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-rn-empty-" + u);
        var plain = await OptInAsync(registry, "Name " + u);

        Assert.Equal(EmailErrors.Empty, (await registry.SaveAsync(plain with { Name = "!!!" })).EmailError);
        Assert.NotNull(await EmailDir("name-" + u).ResolveAsync());
    }

    [Fact]
    public async Task PlainEmail_OptOut_MintsRandom_ReleasesName()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-optout-" + u);
        var plain = await OptInAsync(registry, "Leaving " + u);

        var result = await registry.SaveAsync(plain, plainEmail: false);

        Assert.Null(result.EmailError);
        Assert.Null(result.Agent.EmailPlain);
        Assert.Matches("^agent-[a-z2-7]{32}$", result.Agent.EmailToken);
        Assert.Null(await EmailDir("leaving-" + u).ResolveAsync());
        Assert.Equal(plain.Id, (await EmailDir(result.Agent.EmailToken!).ResolveAsync())?.AgentId);
    }

    [Fact]
    public async Task PlainEmail_Delete_ReleasesName()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-delete-" + u);
        var plain = await OptInAsync(registry, "Gone " + u);

        await registry.DeleteAsync(plain.Id);

        Assert.Null(await EmailDir("gone-" + u).ResolveAsync());
    }

    [Fact]
    public async Task PlainEmail_Regenerate_Refused()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-regen-" + u);
        var plain = await OptInAsync(registry, "Fixed " + u);

        var result = await registry.SaveAsync(plain, regenerateEmail: true);

        Assert.Equal(EmailErrors.NotPlain, result.EmailError);
        Assert.Equal(plain.Id, (await EmailDir("fixed-" + u).ResolveAsync())?.AgentId);
    }

    [Fact]
    public async Task PlainEmail_StaleClaim_IsHealed()
    {
        var u = Unique();
        var user = "plain-stale-" + u;
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        var created = (await registry.SaveAsync(SafePlainAgent("Stale " + u))).Agent;
        // A crash left a claim held by an agent of the same user that no longer exists.
        await EmailDir("stale-" + u).RegisterAsync(user, "vanished-agent");

        var result = await registry.SaveAsync(created, plainEmail: true);

        Assert.Null(result.EmailError);
        Assert.Equal(created.Id, (await EmailDir("stale-" + u).ResolveAsync())?.AgentId);
    }

    [Fact]
    public async Task PlainEmail_SameUserHolder_NoDeadlock()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-self-" + u);
        await OptInAsync(registry, "Twin " + u);
        var second = (await registry.SaveAsync(SafePlainAgent("Twin " + u))).Agent;

        var result = await registry.SaveAsync(second, plainEmail: true).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EmailErrors.Taken, result.EmailError);
    }

    [Fact]
    public async Task PlainEmail_CrossUserOrphan_Refused()
    {
        var u = Unique();
        // Another user's claim whose agent does not exist: never verified across users, so it stays refused.
        await EmailDir("orphan-" + u).RegisterAsync("plain-orphan-owner-" + u, "no-such-agent");
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-orphan-" + u);
        var created = (await registry.SaveAsync(SafePlainAgent("Orphan " + u))).Agent;

        Assert.Equal(EmailErrors.Taken, (await registry.SaveAsync(created, plainEmail: true)).EmailError);
    }

    [Fact]
    public async Task PlainEmail_CrashAfterDeregister_ResaveHeals()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-crash-" + u);
        var plain = await OptInAsync(registry, "Crashy " + u);
        // The crash window: the directory entry is gone but state still says plain.
        await EmailDir("crashy-" + u).DeregisterAsync();
        Assert.Null(await EmailDir("crashy-" + u).ResolveAsync());

        var healed = await registry.SaveAsync(plain with { Description = "touched" });

        Assert.Null(healed.EmailError);
        Assert.Equal(plain.Id, (await EmailDir("crashy-" + u).ResolveAsync())?.AgentId);
    }

    [Fact]
    public async Task PlainEmail_OptOut_CrashAfterDeregister_SlugDead()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-oo-crash-" + u);
        var plain = await OptInAsync(registry, "Fade " + u);
        // Opt-out deregisters the slug first; after that crash the slug must not resolve even though state is still plain.
        await EmailDir("fade-" + u).DeregisterIfHeldByAsync("plain-oo-crash-" + u, plain.Id);

        Assert.Null(await EmailDir("fade-" + u).ResolveAsync());
        Assert.True((await registry.GetAsync(plain.Id))!.EmailPlain);
    }

    [Fact]
    public async Task Put_OmitsPlainEmail_PreservesFlag()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-omit-" + u);
        var plain = await OptInAsync(registry, "Keep " + u);

        // A client that never heard of the field (Android, scripts) sends EmailToken/EmailPlain unset.
        var saved = (await registry.SaveAsync(plain with { EmailPlain = null, EmailToken = null, Description = "d" })).Agent;

        Assert.True(saved.EmailPlain);
        Assert.Equal("keep-" + u, saved.EmailToken);
    }

    [Fact]
    public async Task PlainEmail_CopilotKind_Refused()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-copilot-" + Unique());
        var created = (await registry.SaveAsync(SafePlainAgent("Shell " + Unique()) with { Kind = AgentKinds.Copilot })).Agent;

        Assert.Equal(EmailErrors.UnsafeTools, (await registry.SaveAsync(created, plainEmail: true)).EmailError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("memory")]
    [InlineData("past_chats")]
    public async Task PlainEmail_NullOrMemoryOrPastChatsTools_Refused(string? tool)
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-unsafe-" + Unique());
        var created = (await registry.SaveAsync(SafePlainAgent("Risky " + Unique()))).Agent;
        var risky = created with { EnabledTools = tool is null ? null : new List<string> { tool } };

        Assert.Equal(EmailErrors.UnsafeTools, (await registry.SaveAsync(risky, plainEmail: true)).EmailError);
    }

    [Fact]
    public async Task PlainEmail_ToolsEnabledLaterWhilePlain_Refused()
    {
        var u = Unique();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("plain-later-" + u);
        var plain = await OptInAsync(registry, "Later " + u);

        var refused = await registry.SaveAsync(plain with { EnabledTools = new List<string> { ToolCatalog.Memory } });

        Assert.Equal(EmailErrors.UnsafeTools, refused.EmailError);
        Assert.Equal(new[] { ToolCatalog.WebSearch }, (await registry.GetAsync(plain.Id))!.EnabledTools);
    }
}
