using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IWebPageFetcher"/> that returns the <b>largest result the real fetcher is allowed to
/// return</b> — a root page at <see cref="WebToolLimits.MaxPageTextLength"/> plus
/// <see cref="WebToolLimits.MaxFollowedLinks"/> followed pages, the whole thing sized to
/// <see cref="WebToolLimits.MaxTotalPageTextLength"/>.
///
/// It exists so the session-budget test (ADR 0004) can actually fail. <see cref="InMemoryWebPageFetcher"/>
/// returns a ~50-character canned page, three orders of magnitude below any sane session ceiling, so a
/// budget assertion made against it would stay green even if the caps were removed altogether — the
/// exact regression the test is supposed to catch. Feeding the worst legal case in instead makes the
/// ceiling bind on <see cref="WebToolLimits"/>: because the sizes below are derived from those
/// constants, raising <c>MaxPageTextLength</c>, <c>MaxTotalPageTextLength</c> or <c>MaxFollowedLinks</c>
/// pushes the persisted session past the test's ceiling and it goes red. Following is worst-cased
/// unconditionally (it ignores the filter) — its whole job is to emit the largest legal payload, not to
/// model opt-in, which is unit-tested against <c>ContextWebPageFetcher</c> instead.
/// </summary>
public sealed class CapSizedWebPageFetcher : IWebPageFetcher
{
    public Task<WebPageContent> FetchAsync(
        string url,
        string? followLinkFilter = null,
        int maxFollowedLinks = 1,
        CancellationToken cancellationToken = default)
    {
        // The followed pages share the budget left after the root's full per-page allowance, split
        // evenly across the ceiling number of links — exactly the worst case the real fetcher's total
        // budget permits.
        var perFollowed =
            (WebToolLimits.MaxTotalPageTextLength - WebToolLimits.MaxPageTextLength)
            / WebToolLimits.MaxFollowedLinks;

        var followed = Enumerable.Range(0, WebToolLimits.MaxFollowedLinks)
            .Select(i => new FollowedPage(
                LinkText: $"followed link {i + 1}",
                Url: $"{url}#followed-{i + 1}",
                Title: "A full-size followed page",
                Text: new string('y', perFollowed),
                Truncated: true,
                Error: null))
            .ToList();

        return Task.FromResult(new WebPageContent(
            Url: url,
            Title: "A full-size page",
            Text: new string('x', WebToolLimits.MaxPageTextLength) + WebToolLimits.TruncationMarker,
            Truncated: true,
            Followed: followed));
    }
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
            services.AddSingleton<IUserChatClientFactoryProvider>(sp =>
                new SingleUserChatClientFactoryProvider(sp.GetRequiredService<IChatClientFactory>()));
            services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider());
            services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
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
