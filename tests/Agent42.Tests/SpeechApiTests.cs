using System.Net;
using System.Net.Http.Json;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;
using Agent42.Agents.Speech;
using Agent42.Agents.Attachments;
using Agent42.Agents.Models;

namespace Agent42.Tests;

/// <summary>
/// The POST /api/speech contract (ADR 0038): synthesizes the voice reply server-side when the user has a
/// default speech model, else 409 so the browser voice (ADR 0037) takes over. Hosted in-memory (TestServer)
/// against a real Orleans test cluster (like <see cref="ProvidersApiTests"/>, so MapAgentApi's up-front param
/// inference resolves), with the per-user factory's <see cref="ISpeechService"/> toggled to prove both paths.
/// </summary>
public sealed class SpeechApiTests : IAsyncLifetime
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    // Builds a MapAgentApi host wiring the factory provider only when a speech service is supplied — so the
    // endpoint's optional GetService resolves it (bytes path) or returns null (409 path). Registers the same
    // fakes the other endpoints' param inference needs (see ProvidersApiTests).
    private async Task<(WebApplication App, HttpClient Client)> HostAsync(ISpeechService? speech)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IGrainFactory>(_cluster.GrainFactory);

        if (speech is not null)
        {
            var factory = new SingleChatClientFactory(new FakeChatClient(), speechGenerator: speech);
            builder.Services.AddSingleton<IChatClientFactory>(factory);
            builder.Services.AddSingleton<IUserChatClientFactoryProvider>(new SingleUserChatClientFactoryProvider(factory));
        }

        builder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        builder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        builder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        builder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        builder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        builder.Services.AddDocumentRagFakes();
        builder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

        var app = builder.Build();
        app.MapAgentApi();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage Post(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/speech") { Content = JsonContent.Create(body) };
        request.Headers.Add(IdHeader, "user-1");
        return request;
    }

    [Fact]
    public async Task Speech_WithConfiguredModel_ReturnsAudioBytes()
    {
        var (app, client) = await HostAsync(new InMemorySpeechService());
        try
        {
            var res = await client.SendAsync(Post(new { text = "hello there" }));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("audio/wav", res.Content.Headers.ContentType?.MediaType);
            Assert.NotEmpty(await res.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Speech_WithNoSpeechModel_Returns409()
    {
        var (app, client) = await HostAsync(speech: null);
        try
        {
            var res = await client.SendAsync(Post(new { text = "hello" }));
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Speech_WithBlankText_Returns400()
    {
        var (app, client) = await HostAsync(new InMemorySpeechService());
        try
        {
            var res = await client.SendAsync(Post(new { text = "   " }));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }
}
