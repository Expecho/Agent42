using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// A <see cref="SharedClusterFixture"/> that also stands up the ASP.NET host once per class: one
/// <c>WebApplication</c> + <c>MapAgentApi</c> + <c>StartAsync</c> instead of one per test.
///
/// <para><b>Why per-class subclasses rather than one host registering the union of everyone's services.</b>
/// <c>MapAgentApi</c> maps every endpoint and the route matcher infers all of their parameters up front, so
/// a host must register services for endpoints its tests never call — which is what
/// <see cref="AddRouteMatcherServices"/> is, and that part unions cleanly.
///
/// What does not is that a few of those registrations are <i>inputs a test asserts on</i>:
/// <c>AgentApiTests</c> reads its model catalog back from <c>/api/models</c> and its
/// <see cref="PushConfig"/> from <c>/api/push/config</c>, and needs values the other seven classes do not.
/// A union host could technically adopt one class's values for everyone — no other class asserts on either
/// today — but that leaves every host silently carrying one class's expectations, with no way to express a
/// second class wanting different ones. The OAuth fixtures are the same shape: a fake token endpoint is
/// behaviour, not filler, and forcing it on every host makes each host's contents a question rather than
/// a statement.
///
/// A subclass per class keeps each host's registrations exactly what that class asked for, and costs no
/// extra deploys: xUnit gives every class its own fixture instance regardless of the fixture type. This is
/// a legibility choice, not a correctness one — Group C is where sharing a host is actually impossible,
/// because there the host's registrations are the variable under test.</para>
///
/// <para>Registration order is the override mechanism — a later <c>AddSingleton</c> of the same service
/// type wins for <c>GetRequiredService</c>, so <see cref="Configure"/> runs after the defaults and can
/// replace any of them.</para>
/// </summary>
public class SharedApiFixture : SharedClusterFixture
{
    public WebApplication App { get; private set; } = null!;

    /// <summary>The default client. Follows the TestServer's own redirect behaviour.</summary>
    public HttpClient Client { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseTestServer();
        appBuilder.Services.AddSingleton<IGrainFactory>(Cluster.GrainFactory);
        AddRouteMatcherServices(appBuilder.Services);
        Configure(appBuilder);

        App = appBuilder.Build();
        ConfigureApp(App);
        App.MapAgentApi();
        await App.StartAsync();
        Client = App.GetTestClient();
    }

    public override async Task DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// A client that returns 3xx responses instead of following them, for the OAuth start endpoints whose
    /// redirect Location is the thing under test.
    /// </summary>
    public HttpClient CreateNonRedirectingClient() =>
        new(App.GetTestServer().CreateHandler()) { BaseAddress = new Uri("http://localhost") };

    /// <summary>Extra registrations for one class's host, applied after (and so able to replace) the defaults.</summary>
    protected virtual void Configure(WebApplicationBuilder builder)
    {
    }

    /// <summary>
    /// Middleware and extra endpoint groups for one class's host, applied to the built app.
    ///
    /// <para>This exists because not every mapped group belongs to <c>MapAgentApi</c>. The server-side
    /// MCP surface (ADR 0070) is mapped from the composition root instead, for the reason the class
    /// remarks above give: its <c>MapMcp</c> needs <c>AddMcpServer().WithHttpTransport()</c> in DI, and a
    /// host that has not registered it would break routing for every <i>other</i> endpoint too. A
    /// subclass opts in by registering those services in <see cref="Configure"/> and mapping here.</para>
    ///
    /// <para>Called <b>before</b> <c>MapAgentApi</c>, mirroring <c>Program.cs</c>, where the gate
    /// middleware is installed above the mapping. Ordering between mapped groups is irrelevant; ordering
    /// of middleware relative to the endpoint terminal is not.</para>
    /// </summary>
    protected virtual void ConfigureApp(WebApplication app)
    {
    }

    /// <summary>
    /// The set every <c>MapAgentApi</c> host needs whether or not its tests touch those endpoints: minimal
    /// API infers each endpoint's parameters when the route is mapped, and a service parameter it cannot
    /// resolve is treated as a request body — which breaks routing for the WHOLE host, not just that route.
    /// </summary>
    internal static void AddRouteMatcherServices(IServiceCollection services)
    {
        // /api/providers and /api/mcp-servers encrypt secrets before the grain (ADR 0018 / 0024); an
        // ephemeral key ring is fine here because nothing is persisted beyond the run.
        services.AddSingleton<IProviderSecretProtector>(new ProviderSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        services.AddSingleton<IMcpSecretProtector>(new McpSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        // The agent webhook's secret (ADR 0094). Its endpoint resolves this optionally and answers 503 without it.
        services.AddSingleton<IWebhookSecretProtector>(new WebhookSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<WebhookSecretProtector>.Instance));
        // The production endpoint invariants (ADR 0105): McpOAuthClient, which /api/mcp/oauth/start takes as a HANDLER
        // parameter, and the two protectors, which TryAdd leaves as the ephemeral ones above. /api/speech and
        // /api/config-health resolve their own dependencies through IServiceProvider precisely to avoid adding to this.
        services.AddAgent42Api();
        services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        // /api/models is user-scoped (ADR 0018); the single static catalog stands in for every user.
        services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        // The in-memory store is process-global, so it is the same instance the silo grain sees.
        services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        // The upload endpoint resolves the ingestion service even on the image path (ADR 0007).
        services.AddDocumentRagFakes();
        // /api/push/config injects PushConfig (ADR 0028). Off by default — a class that asserts on it says so.
        services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));
    }
}
