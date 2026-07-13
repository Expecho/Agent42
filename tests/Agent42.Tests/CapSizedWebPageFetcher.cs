using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IWebPageFetcher"/> that returns the <b>largest page the real fetcher is allowed to
/// return</b> — exactly <see cref="WebToolLimits.MaxPageTextLength"/> characters, marked truncated.
///
/// It exists so the session-budget test (ADR 0004) can actually fail. <see cref="InMemoryWebPageFetcher"/>
/// returns a ~50-character canned page, three orders of magnitude below any sane session ceiling, so a
/// budget assertion made against it would stay green even if the 8 KB cap were removed altogether —
/// the exact regression the test is supposed to catch. Feeding the worst legal case in instead makes
/// the ceiling bind on <see cref="WebToolLimits"/>.
/// </summary>
public sealed class CapSizedWebPageFetcher : IWebPageFetcher
{
    public Task<WebPageContent> FetchAsync(string url, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WebPageContent(
            Url: url,
            Title: "A full-size page",
            Text: new string('x', WebToolLimits.MaxPageTextLength) + WebToolLimits.TruncationMarker,
            Truncated: true));
}

/// <summary>
/// A silo like <see cref="TestSiloConfigurator"/>, but whose <see cref="IWebPageFetcher"/> returns
/// cap-sized pages — see <see cref="CapSizedWebPageFetcher"/>.
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
            services.AddSingleton<IChatClientFactory>(sp =>
                new SingleChatClientFactory(sp.GetRequiredService<IChatClient>(), "fake-model"));
            services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
            services.AddDocumentRagFakes();
            // Deliberately NOT AddWebToolFakes(): this silo swaps in the cap-sized fetcher. That makes it
            // the one configurator where a new grain dependency has to be added by hand — miss it and
            // every activation on THIS silo fails (which is where the session-budget test runs).
            services.AddSingleton<IWebSearch, InMemoryWebSearch>();
            services.AddSingleton<IWebPageFetcher, CapSizedWebPageFetcher>();
            services.AddSingleton<IGeocoder, FakeGeocoder>();
        });
    }
}
