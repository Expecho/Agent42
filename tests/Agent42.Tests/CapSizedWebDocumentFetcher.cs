using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IWebDocumentFetcher"/> that returns a document big enough to make
/// <c>read_web_document</c> hand back the <b>largest result it is allowed to</b> — more than
/// <see cref="WebDocumentLimits.MaxExcerpts"/> full-size chunks of distinct text (ADR 0066).
///
/// It exists so the session-budget test (ADR 0004) can actually fail:
/// <see cref="InMemoryWebDocumentFetcher"/> returns a three-line canned document, orders of magnitude
/// below any sane session ceiling, so a budget assertion made against it would stay green even if the
/// return caps were deleted altogether. Feeding the worst legal case in instead means the session-budget
/// test measures what this tool actually persists.
///
/// <para><b>Be precise about what that does and does not guard, because the arithmetic matters.</b> The
/// binding cap here is <see cref="WebDocumentLimits.MaxExcerpts"/> (5) × <c>DocumentChunker.ChunkSize</c>
/// (1 000) ≈ 5 KB per call — <b>not</b> <see cref="WebDocumentLimits.MaxReturnTextLength"/> (8 KB), which
/// those numbers cannot reach. Five document turns therefore contribute ~25 KB against the budget test's
/// 256 KB ceiling, so raising <c>MaxExcerpts</c> alone does <b>not</b> turn that test red. What this
/// fixture guards is that the excerpts are budgeted at all: delete the per-call cap and the whole
/// document (10 × 1 000-char sections, five times over) lands in the session and the ceiling is
/// exceeded. The <c>Assert.Contains("magrathea-", …)</c> line is what keeps it from being decorative.</para>
/// </summary>
public sealed class CapSizedWebDocumentFetcher : IWebDocumentFetcher
{
    public Task<WebDocument> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        // Distinct paragraphs, not a repeated character: the chunker splits on text, and a single
        // enormous run of one character would not produce the several full chunks this needs. Twice the
        // excerpt ceiling, so the cap actually has something to cut.
        var paragraphs = Enumerable.Range(0, WebDocumentLimits.MaxExcerpts * 2).Select(i =>
            $"Section {i}. " + string.Join(" ", Enumerable.Repeat($"magrathea-{i}", 200)));

        return Task.FromResult(new WebDocument(
            Url: url,
            FileName: "cap-sized.txt",
            MediaType: DocumentMediaTypes.PlainText,
            Content: System.Text.Encoding.UTF8.GetBytes(string.Join("\n\n", paragraphs))));
    }
}

/// <summary>
/// A silo like <see cref="TestSiloConfigurator"/>, but whose
/// <see cref="IWebDocumentFetcher"/> returns a cap-sized result — see <see cref="CapSizedWebDocumentFetcher"/>.
/// </summary>
public sealed class CapSizedFetcherSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient, FakeChatClient>();
            // The deterministic embedding generator is supplied here (unlike the other degraded silos)
            // because read_web_document has to actually INGEST for the session-budget test to measure its
            // return (ADR 0066) — without an embedding model the tool would return a one-line "not
            // configured" message and the cap-sized document below would prove nothing. The generator
            // comes from AddDocumentRagFakes further down, so it is resolved rather than constructed.
            services.AddSingleton<IChatClientFactory>(sp =>
                new SingleChatClientFactory(
                    sp.GetRequiredService<IChatClient>(), "fake-model",
                    embeddingGenerator: sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                    defaultEmbeddingModelId: "offline-embed"));
            services.AddSingleton<IUserChatClientFactoryProvider>(sp =>
                new SingleUserChatClientFactoryProvider(sp.GetRequiredService<IChatClientFactory>()));
            services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider());
            services.AddSingleton<ISkillPackageStore, InMemorySkillPackageStore>();
            services.AddSingleton<IUserSkillProvider, UserSkillProvider>();
            services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
            services.AddSingleton<IAgentWebhookSender, NullAgentWebhookSender>();
            services.AddSingleton<IChatDeliverySender, NullChatDeliverySender>();
            services.AddSingleton<IFleetLauncher, DisabledFleetLauncher>();
            services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
            // Also the source of this silo's IChatSearchIndex (ADR 0061) — a mandatory grain
            // dependency that deliberately rides the one helper every host calls, so it cannot be
            // missed by the hand-registration below.
            services.AddDocumentRagFakes();
            // Deliberately NOT AddWebToolFakes(): this silo swaps in the cap-sized document fetcher. That makes it
            // the one configurator where a new grain dependency has to be added by hand — miss it and
            // every activation on THIS silo fails (which is where the session-budget test runs).
            services.AddSingleton<IWebSearch, InMemoryWebSearch>();
            services.AddSingleton<IWebDocumentFetcher, CapSizedWebDocumentFetcher>();
            services.AddSingleton<IGeocoder, FakeGeocoder>();
            // Long-term memory (ADR 0056) OFF on this silo, deliberately — the NullPushNotificationSender
            // choice above, for the same reason. A recorder would attach a real context provider and put a
            // "## Memories" context message into the replies this silo's session-budget test measures. Still
            // registered: HindsightIntegration is a mandatory grain dependency, so omitting it fails
            // activation rather than disabling memory.
            services.AddSingleton(new HindsightIntegration(null));

            // Last: supplies the host-invariant services and fails the silo at startup, naming the type, if a grain
            // dependency above is missing (ADR 0105).
            services.AddAgent42Silo();
        });
    }
}
