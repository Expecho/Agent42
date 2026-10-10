using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IDocumentIndex"/> over the ordinary in-memory one that <b>counts</b> its
/// <see cref="SearchAsync"/> calls and can pretend to be a fixed-dimension store.
///
/// <para>The counter is what makes AC14 real: <c>read_web_document</c> must rank the chunks it just
/// embedded <b>in process</b> and never read a top-K back out of the index (ADR 0066). A read-back would
/// be invisible in the tool's output — the excerpts would look identical — so the only observable
/// difference is that the index was queried.</para>
///
/// <para><see cref="DimensionOverride"/> models the production Cosmos container, whose vector policy is
/// fixed at 3072 while the offline generator is 128-dim — the mismatch AC12 needs.</para>
///
/// <para>There is deliberately <b>no</b> <c>Reset()</c>. Every test using this probe deploys its own
/// one-silo cluster, so there is no shared state to restore — and an unwired reset method is exactly the
/// shape ADR 0055 warns about: correct the day it is written, silently wrong the day someone adds a test
/// that reads a counter and assumes something calls it.</para>
/// </summary>
public sealed class ProbeDocumentIndex : IDocumentIndex
{
    private readonly InMemoryDocumentIndex _inner = new();
    private int _searchCalls;

    /// <summary>How many times the index has been queried since the last <see cref="Reset"/>.</summary>
    public int SearchCalls => Volatile.Read(ref _searchCalls);

    /// <summary>When set, the store reports a fixed vector dimension (the prod Cosmos shape).</summary>
    public int? DimensionOverride { get; set; }

    public int? ExpectedDimensions => DimensionOverride;

    public Task UpsertChunksAsync(
        string ownerKey, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
        => _inner.UpsertChunksAsync(ownerKey, chunks, cancellationToken);

    public Task<IReadOnlyList<DocumentChunk>> SearchAsync(
        string ownerKey, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _searchCalls);
        return _inner.SearchAsync(ownerKey, queryEmbedding, topK, cancellationToken);
    }

    public Task DeleteAsync(string ownerKey, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(ownerKey, cancellationToken);
}

/// <summary>
/// A factory provider whose factory flips between "this user has a default embedding model" and "this
/// user has none" (ADR 0030) — the double AC11 needs.
///
/// <para>The grain resolves its factory ONCE on activation, so the flip must happen before the
/// conversation's first turn. Every test here uses a fresh grain key, so it does.</para>
/// </summary>
public sealed class ProbeEmbeddingFactoryProvider : IUserChatClientFactoryProvider
{
    private readonly IChatClientFactory _withEmbeddings;
    private readonly IChatClientFactory _withoutEmbeddings;

    public ProbeEmbeddingFactoryProvider(
        IChatClient client, IEmbeddingGenerator<string, Embedding<float>> embeddings)
    {
        _withEmbeddings = new SingleChatClientFactory(
            client, TestSiloConfigurator.DefaultModel, TestSiloConfigurator.Capabilities,
            embeddingGenerator: embeddings, defaultEmbeddingModelId: "offline-embed");
        _withoutEmbeddings = new SingleChatClientFactory(
            client, TestSiloConfigurator.DefaultModel, TestSiloConfigurator.Capabilities);
    }

    /// <summary>When false the user has no default embedding model, so ingest cannot run.</summary>
    public bool HasEmbeddingModel { get; set; } = true;

    public Task<IChatClientFactory> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(HasEmbeddingModel ? _withEmbeddings : _withoutEmbeddings);
}

/// <summary>
/// A silo like <see cref="TestSiloConfigurator"/>, but with the two probes above swapped in so the
/// <c>read_web_document</c> tests can observe index queries and simulate an unusable embedding model
/// (ADR 0066). Both are registered AFTER <c>AddDocumentRagFakes</c> so they win the resolve.
/// </summary>
public sealed class WebDocumentProbeSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient, FakeChatClient>();
            services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider());
            services.AddSingleton<ISkillPackageStore, InMemorySkillPackageStore>();
            services.AddSingleton<IUserSkillProvider, UserSkillProvider>();
            services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
            services.AddSingleton<IAgentWebhookSender, NullAgentWebhookSender>();
            // Memory OFF on this silo (the CapSized precedent): a recorder would attach a context
            // provider and put a "## Memories" block into the instructions these tests read.
            services.AddSingleton(new HindsightIntegration(null));
            services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
            services.AddDocumentRagFakes();
            services.AddWebToolFakes();

            // The probes, registered last so they beat the defaults AddDocumentRagFakes put in.
            services.AddSingleton<ProbeDocumentIndex>();
            services.AddSingleton<IDocumentIndex>(sp => sp.GetRequiredService<ProbeDocumentIndex>());
            services.AddSingleton(sp => new ProbeEmbeddingFactoryProvider(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()));
            services.AddSingleton<IUserChatClientFactoryProvider>(
                sp => sp.GetRequiredService<ProbeEmbeddingFactoryProvider>());
        });
    }
}
