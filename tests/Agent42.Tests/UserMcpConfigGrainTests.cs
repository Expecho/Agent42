using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Storage;

namespace Agent42.Tests;

public sealed class UserMcpConfigGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static StoredMcpServer Server(string id = "srv1", string? headerCipher = "cipher") => new(
        Id: id,
        Name: "Dataverse",
        Endpoint: "https://example.crm4.dynamics.com/mcp",
        Auth: nameof(McpAuthMode.Header),
        Enabled: true,
        HeaderName: "Authorization",
        HeaderValueCiphertext: headerCipher);

    // AC7: state round-trips and survives reactivation.
    [Fact]
    public async Task Save_RoundTrips_AndSurvivesReactivation()
    {
        const string user = "mcp-roundtrip-user";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);

        var empty = await grain.GetAsync();
        Assert.Empty(empty.Servers);
        Assert.Equal(0, empty.Version);

        await grain.SaveAsync(Server());

        // Force deactivation so the next call re-reads persisted state.
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        var stored = await reactivated.GetAsync();
        var server = Assert.Single(stored.Servers);
        Assert.Equal("srv1", server.Id);
        Assert.Equal("https://example.crm4.dynamics.com/mcp", server.Endpoint);
        Assert.Equal("cipher", server.HeaderValueCiphertext);
        Assert.True(server.Enabled);
    }

    // AC7: version bumps on every CONFIGURATION write. Renamed from Save_BumpsVersion_OnEveryWrite when
    // per-tool selection added RecordToolNamesAsync, which writes state WITHOUT bumping — so "every write
    // bumps" became false and a test name asserting it was doc debt inside the test file.
    [Fact]
    public async Task Save_BumpsVersion_OnEveryConfigurationWrite()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-version-user");

        Assert.Equal(0, await grain.GetVersionAsync());
        await grain.SaveAsync(Server());
        Assert.Equal(1, await grain.GetVersionAsync());
        await grain.SaveAsync(Server() with { Name = "renamed" });
        Assert.Equal(2, await grain.GetVersionAsync());
    }

    // AC5/AC7: editing without re-entering the secret (blank ciphertext) keeps the stored secret.
    [Fact]
    public async Task BlankSecretOnUpdate_KeepsStoredSecret()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-blanksecret-user");

        await grain.SaveAsync(Server(headerCipher: "secret-cipher"));

        // Update another field but send NO secret (blank ciphertext) — the stored secret must survive.
        var updated = await grain.SaveAsync(Server(headerCipher: "") with { Name = "New name" });
        Assert.Equal("secret-cipher", updated.HeaderValueCiphertext);
        Assert.Equal("New name", updated.Name);

        var stored = Assert.Single((await grain.GetAsync()).Servers);
        Assert.Equal("secret-cipher", stored.HeaderValueCiphertext);

        // A non-blank ciphertext replaces it.
        await grain.SaveAsync(Server(headerCipher: "rotated-cipher"));
        Assert.Equal("rotated-cipher", Assert.Single((await grain.GetAsync()).Servers).HeaderValueCiphertext);
    }

    [Fact]
    public async Task Delete_RemovesServer_AndBumpsVersion()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-delete-user");

        await grain.SaveAsync(Server());
        Assert.Single((await grain.GetAsync()).Servers);
        var versionAfterSave = await grain.GetVersionAsync();

        await grain.DeleteAsync("srv1");
        Assert.Empty((await grain.GetAsync()).Servers);
        Assert.Equal(versionAfterSave + 1, await grain.GetVersionAsync());

        // Deleting an absent id is a no-op that does NOT bump the version.
        await grain.DeleteAsync("nope");
        Assert.Equal(versionAfterSave + 1, await grain.GetVersionAsync());
    }

    // AC7: per-user isolation.
    [Fact]
    public async Task IsIsolatedPerUser()
    {
        var alice = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-alice");
        var bob = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-bob");

        await alice.SaveAsync(Server());

        Assert.Single((await alice.GetAsync()).Servers);
        Assert.Empty((await bob.GetAsync()).Servers);
    }

    // AC10 — RecordToolNamesAsync persists discovered names WITHOUT bumping the version. The version is the
    // key the per-user tool provider caches LIVE MCP SOCKETS under, so bumping here would tear down every warm
    // grain's connections for what is only picker metadata. If this ever fails, that is the cost being paid.
    [Fact]
    public async Task RecordToolNames_DoesNotBumpVersion()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-record-nobump-user");
        await grain.SaveAsync(Server());
        var versionAfterSave = await grain.GetVersionAsync();

        await grain.RecordToolNamesAsync("srv1", ["alpha", "beta"]);

        Assert.Equal(versionAfterSave, await grain.GetVersionAsync());

        // ...and the names really were persisted — otherwise "did not bump" would pass trivially.
        var stored = Assert.Single((await grain.GetAsync()).Servers);
        Assert.Equal(["alpha", "beta"], stored.KnownToolNames);
    }

    // The deliberate counterpart to the test above: an explicit, user-pressed invalidation DOES bump, because
    // the provider's cache hit returns without connecting and nothing else can dislodge it. It must change the
    // version and nothing else — a server list altered here would make a diagnostic action a config write.
    [Fact]
    public async Task InvalidateToolCache_BumpsTheVersion_AndLeavesTheServersAlone()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-invalidate-user");
        await grain.SaveAsync(Server());
        await grain.RecordToolNamesAsync("srv1", ["alpha", "beta"]);
        var before = await grain.GetAsync();

        await grain.InvalidateToolCacheAsync();

        var after = await grain.GetAsync();
        Assert.Equal(before.Version + 1, after.Version);
        var stored = Assert.Single(after.Servers);
        Assert.Equal(["alpha", "beta"], stored.KnownToolNames);

        // Whole-record equality with the names NULLED on both sides. A positional record's synthesized Equals
        // compares its IReadOnlyList members by REFERENCE, and the grain hands back a fresh list every read —
        // so comparing the records as-is fails on two field-identical values. Nulling that one member keeps
        // this an every-other-field check (which is the point: an invalidation must not be a config write)
        // instead of an accidental assertion about list identity.
        Assert.Equal(before.Servers[0] with { KnownToolNames = null }, stored with { KnownToolNames = null });
    }

    // AC11 — recording the SAME names again writes nothing at all. Every activation of every conversation
    // re-discovers the same tool list, so without this the feature would cost one Cosmos write per activation.
    // Proved on the storage ETag rather than on the values: re-writing identical values would leave the state
    // looking identical, so only the ETag can tell a no-op from a redundant write.
    [Fact]
    public async Task RecordToolNames_UnchangedNames_WritesNothing()
    {
        const string user = "mcp-record-noop-user";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(Server());
        await grain.RecordToolNamesAsync("srv1", ["alpha", "beta"]);

        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);

        async Task<string?> EtagAsync()
        {
            var state = new GrainState<UserMcpConfigState>(new UserMcpConfigState());
            await storage.ReadStateAsync("mcp-servers", grain.GetGrainId(), state);
            return state.ETag;
        }

        var before = await EtagAsync();
        await grain.RecordToolNamesAsync("srv1", ["alpha", "beta"]);
        Assert.Equal(before, await EtagAsync());

        // A genuine change still writes — or the no-op would be indistinguishable from a broken method.
        await grain.RecordToolNamesAsync("srv1", ["alpha", "beta", "gamma"]);
        Assert.NotEqual(before, await EtagAsync());
    }

    // Recording against a server id that is not in the overlay is a silent no-op, never a throw: a server can
    // be deleted between the provider connecting to it and the names coming back.
    [Fact]
    public async Task RecordToolNames_UnknownServer_IsANoOp()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-record-unknown-user");
        await grain.SaveAsync(Server());

        await grain.RecordToolNamesAsync("deleted-server", ["alpha"]);

        var stored = Assert.Single((await grain.GetAsync()).Servers);
        Assert.Null(stored.KnownToolNames);
    }

    // The other half of the "names are never erased" invariant, at grain level: SaveAsync must carry
    // KnownToolNames forward exactly as it carries the secret ciphertexts. See Edit_PreservesKnownToolNames in
    // McpServersApiTests for the same property through the real PUT endpoint, which is where ToStored — the
    // thing that actually drops them — lives.
    [Fact]
    public async Task Save_WithNullKnownToolNames_KeepsStoredNames()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-keep-names-user");
        await grain.SaveAsync(Server());
        await grain.RecordToolNamesAsync("srv1", ["alpha", "beta"]);

        // A fresh record built from an edit request — no names on it at all, exactly like ToStored produces.
        var updated = await grain.SaveAsync(Server() with { Name = "renamed" });

        Assert.Equal(["alpha", "beta"], updated.KnownToolNames);
        Assert.Equal("renamed", updated.Name);
    }

    // AC13 (ADR 0067): the seeding opt-in is a SETTING, so it must behave as the mirror image of the
    // KnownToolNames test directly above — carried by whatever the incoming record says, in BOTH directions.
    //
    // The "can be turned off" half is the load-bearing one. Adding a Keep clause for this field (by analogy
    // with the ciphertexts and the names, which is exactly the mistake to make here) would make the opt-in
    // one-way: the user could tick the box and never untick it, while the UI cheerfully showed it unticked.
    [Fact]
    public async Task Edit_PreservesSeedingFlag_AndCanDisableIt()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-seeding-user");
        await grain.SaveAsync(Server() with { SeedsFetchableUrls = true });

        var renamed = await grain.SaveAsync(Server() with { SeedsFetchableUrls = true, Name = "renamed" });
        Assert.True(renamed.SeedsFetchableUrls);
        Assert.Equal("renamed", renamed.Name);

        var disabled = await grain.SaveAsync(Server() with { SeedsFetchableUrls = false });
        Assert.False(disabled.SeedsFetchableUrls);
        Assert.False(Assert.Single((await grain.GetAsync()).Servers).SeedsFetchableUrls);
    }
}