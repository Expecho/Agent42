using System.ClientModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// Regression (prod 2026-10-17): when the user's embedding provider rejected the call (there, HTTP 400
/// "API version not supported"), the document upload escaped as an unhandled exception and a bare 500.
/// It is now a 502 that carries the provider's message, as text for the composer's error chip. The host
/// follows <see cref="SpeechApiTests"/>: a per-test MapAgentApi host with its own per-user factory, here
/// one whose embedding generator throws what the OpenAI SDK throws.
/// </summary>
public sealed class DocumentUploadProviderErrorTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    [Fact]
    public async Task Upload_Document_WhenEmbeddingProviderRejects_Returns502WithProviderMessage()
    {
        var (app, client) = await HostAsync();
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Magrathea is a legendary planet."));
            file.Headers.ContentType = new MediaTypeHeaderValue(DocumentMediaTypes.PlainText);
            form.Add(file, "file", "notes.txt");

            var res = await client.PostAsync("/api/conversations/provider-error-conversation/attachments", form);

            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            Assert.Equal("text/plain", res.Content.Headers.ContentType?.MediaType);
            Assert.Contains("API version not supported", await res.Content.ReadAsStringAsync());
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    // Registers the failing provider BEFORE AddDocumentRagFakes, whose TryAdd then leaves it in place. The
    // other registrations are the ones MapAgentApi's parameter inference needs (see SpeechApiTests).
    private async Task<(WebApplication App, HttpClient Client)> HostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IGrainFactory>(Cluster.GrainFactory);

        var factory = new SingleChatClientFactory(
            NoModelChatClient.Instance,
            embeddingGenerator: new RejectingEmbeddingGenerator(),
            defaultEmbeddingModelId: "offline-embed");
        builder.Services.AddSingleton<IChatClientFactory>(factory);
        builder.Services.AddSingleton<IUserChatClientFactoryProvider>(new SingleUserChatClientFactoryProvider(factory));

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

    private sealed class RejectingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new ClientResultException("HTTP 400 (BadRequest)\n\nAPI version not supported");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
