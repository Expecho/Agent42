using Agent42.Agents;
using Orleans.Core.Internal;
using Orleans.TestingHost;
using Agent42.Agents.Models;

namespace Agent42.Tests;

public sealed class UserProviderConfigGrainTests : IAsyncLifetime
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

    private static StoredProvider Ollama(string? cipher = "cipher") => new(
        Key: "ollama",
        Kind: "OpenAICompatible",
        DisplayName: "Local Ollama",
        Endpoint: "http://localhost:11434/v1",
        ApiKeyCiphertext: cipher,
        Models: [new StoredModel("llama3.1", "Llama 3.1", SupportsTools: true, SupportsVision: false)],
        UseResponsesApi: false);

    [Fact]
    public async Task Save_RoundTrips_AndSurvivesReactivation()
    {
        const string user = "providers-roundtrip-user";
        var grain = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>(user);

        // Never saved → the empty default.
        var empty = await grain.GetAsync();
        Assert.Empty(empty.Providers);
        Assert.Equal(0, empty.Version);

        await grain.SaveAsync(Ollama());

        // Force deactivation so the next call re-reads persisted state.
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>(user);
        var stored = await reactivated.GetAsync();
        var provider = Assert.Single(stored.Providers);
        Assert.Equal("ollama", provider.Key);
        Assert.Equal("http://localhost:11434/v1", provider.Endpoint);
        Assert.Equal("cipher", provider.ApiKeyCiphertext);
        Assert.Single(provider.Models);
    }

    [Fact]
    public async Task Save_BumpsVersion_OnEveryWrite()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>("providers-version-user");

        Assert.Equal(0, await grain.GetVersionAsync());
        await grain.SaveAsync(Ollama());
        Assert.Equal(1, await grain.GetVersionAsync());
        await grain.SaveAsync(Ollama() with { DisplayName = "renamed" });
        Assert.Equal(2, await grain.GetVersionAsync());
    }

    // AC5: editing without re-entering the key (blank ciphertext) keeps the stored key.
    [Fact]
    public async Task BlankKeyOnUpdate_KeepsStoredKey()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>("providers-blankkey-user");

        await grain.SaveAsync(Ollama(cipher: "secret-cipher"));

        // Update another field but send NO key (blank ciphertext) — the stored key must survive.
        var updated = await grain.SaveAsync(Ollama(cipher: "") with { DisplayName = "New name" });
        Assert.Equal("secret-cipher", updated.ApiKeyCiphertext);
        Assert.Equal("New name", updated.DisplayName);

        var stored = Assert.Single((await grain.GetAsync()).Providers);
        Assert.Equal("secret-cipher", stored.ApiKeyCiphertext);

        // A non-blank ciphertext replaces it.
        await grain.SaveAsync(Ollama(cipher: "rotated-cipher"));
        Assert.Equal("rotated-cipher", Assert.Single((await grain.GetAsync()).Providers).ApiKeyCiphertext);
    }

    [Fact]
    public async Task Delete_RemovesProvider_AndBumpsVersion()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>("providers-delete-user");

        await grain.SaveAsync(Ollama());
        Assert.Single((await grain.GetAsync()).Providers);
        var versionAfterSave = await grain.GetVersionAsync();

        await grain.DeleteAsync("ollama");
        Assert.Empty((await grain.GetAsync()).Providers);
        Assert.Equal(versionAfterSave + 1, await grain.GetVersionAsync());

        // Deleting an absent key is a no-op that does NOT bump the version.
        await grain.DeleteAsync("nope");
        Assert.Equal(versionAfterSave + 1, await grain.GetVersionAsync());
    }

    [Fact]
    public async Task SetDefault_RoundTrips_AndBumpsVersion()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>("providers-default-user");

        await grain.SetDefaultAsync("ollama:llama3.1");
        Assert.Equal("ollama:llama3.1", (await grain.GetAsync()).Default);
        Assert.Equal(1, await grain.GetVersionAsync());

        // Blank clears it back to null.
        await grain.SetDefaultAsync("  ");
        Assert.Null((await grain.GetAsync()).Default);
    }

    // AC2: the embedding default round-trips, bumps the version, and clears on blank — independent of Default.
    [Fact]
    public async Task SetEmbeddingDefault_RoundTrips_AndBumpsVersion()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>("providers-embedding-default-user");

        await grain.SetEmbeddingDefaultAsync("foundry:text-embedding-3-large");
        Assert.Equal("foundry:text-embedding-3-large", (await grain.GetAsync()).EmbeddingDefault);
        Assert.Equal(1, await grain.GetVersionAsync());

        // Independent of the chat default.
        await grain.SetDefaultAsync("ollama:llama3.1");
        var config = await grain.GetAsync();
        Assert.Equal("foundry:text-embedding-3-large", config.EmbeddingDefault);
        Assert.Equal("ollama:llama3.1", config.Default);

        // Blank clears it back to null.
        await grain.SetEmbeddingDefaultAsync("  ");
        Assert.Null((await grain.GetAsync()).EmbeddingDefault);
    }

    [Fact]
    public async Task IsIsolatedPerUser()
    {
        var alice = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>("providers-alice");
        var bob = _cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>("providers-bob");

        await alice.SaveAsync(Ollama());

        Assert.Single((await alice.GetAsync()).Providers);
        Assert.Empty((await bob.GetAsync()).Providers);
    }
}
