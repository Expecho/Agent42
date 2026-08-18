using Orleans.Core.Internal;
using Orleans.TestingHost;
using Agent42.Agents.Mcp;

namespace Agent42.Tests;

public sealed class UserMcpConfigGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

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
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);

        var empty = await grain.GetAsync();
        Assert.Empty(empty.Servers);
        Assert.Equal(0, empty.Version);

        await grain.SaveAsync(Server());

        // Force deactivation so the next call re-reads persisted state.
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        var stored = await reactivated.GetAsync();
        var server = Assert.Single(stored.Servers);
        Assert.Equal("srv1", server.Id);
        Assert.Equal("https://example.crm4.dynamics.com/mcp", server.Endpoint);
        Assert.Equal("cipher", server.HeaderValueCiphertext);
        Assert.True(server.Enabled);
    }

    // AC7: version bumps on every write.
    [Fact]
    public async Task Save_BumpsVersion_OnEveryWrite()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-version-user");

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
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-blanksecret-user");

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
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-delete-user");

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
        var alice = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-alice");
        var bob = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-bob");

        await alice.SaveAsync(Server());

        Assert.Single((await alice.GetAsync()).Servers);
        Assert.Empty((await bob.GetAsync()).Servers);
    }
}
