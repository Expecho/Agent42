using System.Collections.Concurrent;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

// An IAttachmentStore that counts GetReadUrlAsync calls per attachment id, so a test can prove
// the grain re-mints prior turns' image URLs on EVERY model call (rehydrate-per-turn) — not only
// on activation. Only GetReadUrlAsync needs real behavior for that assertion.
public sealed class CountingAttachmentStore : IAttachmentStore
{
    public static readonly ConcurrentDictionary<string, int> ReadUrlCalls = new();

    public Task<AttachmentRef> SaveAsync(
        string ownerKey, string fileName, string mediaType, byte[] content, CancellationToken cancellationToken = default)
        => Task.FromResult(new AttachmentRef(Guid.NewGuid().ToString("n"), $"{ownerKey}/x", mediaType, fileName));

    public Task<AttachmentRef?> GetAsync(string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
        => Task.FromResult<AttachmentRef?>(new AttachmentRef(attachmentId, $"{ownerKey}/{attachmentId}", "image/png", "x.png"));

    public Task<AttachmentContent?> OpenReadAsync(string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
        => Task.FromResult<AttachmentContent?>(null);

    public Task<Uri> GetReadUrlAsync(AttachmentRef reference, CancellationToken cancellationToken = default)
    {
        var count = ReadUrlCalls.AddOrUpdate(reference.Id, 1, (_, c) => c + 1);
        return Task.FromResult(new Uri($"test://attachment/{reference.Id}/{count}"));
    }
}

public sealed class CountingStoreSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient, FakeChatClient>();
            services.AddSingleton<IChatClientFactory>(sp =>
                new SingleChatClientFactory(sp.GetRequiredService<IChatClient>(), "fake-model"));
            services.AddSingleton<IUserChatClientFactoryProvider>(sp =>
                new SingleUserChatClientFactoryProvider(sp.GetRequiredService<IChatClientFactory>()));
            services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider());
            services.AddSingleton<ISkillPackageStore, InMemorySkillPackageStore>();
            services.AddSingleton<IUserSkillProvider, UserSkillProvider>();
            services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
            services.AddSingleton<IAttachmentStore, CountingAttachmentStore>();
            // The grain resolves the document retrieval stand-ins (ADR 0007) and the web tools (ADR 0011).
            services.AddDocumentRagFakes();
            services.AddWebToolFakes();
        });
    }
}

public sealed class AttachmentRehydrateTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        CountingAttachmentStore.ReadUrlCalls.Clear();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<CountingStoreSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    // Regression (critic HIGH): a warm grain serves many turns without reactivating, and a blob
    // SAS is short-lived, so a prior turn's image URL must be re-minted on EACH later turn — not
    // only on activation — or the model re-fetches an expired URL and 400s. Turn 1 attaches an
    // image; turn 2 is plain text on the SAME activation (no deactivation between). The image's
    // URL must be minted again on turn 2. With activation-only rehydrate, the count would stay 1.
    [Fact]
    public async Task PriorTurnImageUrl_IsRefreshedOnEveryTurn_NotJustOnActivation()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("rehydrate-per-turn-conversation");
        var image = new AttachmentRef("rehy-1", "k/rehy-1", "image/png", "p.png");

        await foreach (var _ in agent.ChatAsync("first, with an image", null, [image]))
        {
        }
        var afterTurn1 = CountingAttachmentStore.ReadUrlCalls.GetValueOrDefault("rehy-1");

        // A second turn with no attachments, on the same (still-warm) grain — no reactivation.
        await foreach (var _ in agent.ChatAsync("second, text only", null))
        {
        }
        var afterTurn2 = CountingAttachmentStore.ReadUrlCalls.GetValueOrDefault("rehy-1");

        Assert.Equal(1, afterTurn1); // minted once, for the sending turn
        Assert.True(
            afterTurn2 > afterTurn1,
            $"Expected the prior image URL to be re-minted on turn 2 (rehydrate-per-turn); it stayed at {afterTurn2}.");
    }
}
