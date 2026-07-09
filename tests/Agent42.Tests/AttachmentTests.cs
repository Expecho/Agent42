using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Exercises the attachment store and the upload/display endpoints end to end. Like
/// <see cref="AgentApiTests"/>, the minimal-API endpoints run in a TestServer against a real
/// Orleans test cluster — with the in-memory store registered in <b>both</b> DI containers. That
/// is only correct because the store is process-global (constraint 10): an upload served by the
/// WebApplication must be visible to the grain running in the separate silo, and vice-versa.
/// </summary>
public sealed class AttachmentTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseTestServer();
        appBuilder.Services.AddSingleton<IGrainFactory>(_cluster.GrainFactory);
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        // The upload endpoint resolves the ingestion service even on the image path (ADR 0007).
        appBuilder.Services.AddDocumentRagFakes();
        _app = appBuilder.Build();
        _app.MapAgentApi();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _cluster.DisposeAsync();
    }

    private static MultipartFormDataContent ImageForm(byte[] bytes, string contentType, string fileName)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        return form;
    }

    // Step 2: the in-memory store round-trips the bytes and metadata, and scopes reads by owner
    // key so one conversation can never read another's attachment.
    [Fact]
    public async Task InMemoryStore_SaveThenRead_RoundTripsBytesAndMetadata()
    {
        var store = new InMemoryAttachmentStore();
        var bytes = new byte[] { 1, 2, 3, 4, 5, 42 };

        var reference = await store.SaveAsync("user-1/conv-1", "cat.png", "image/png", bytes);

        Assert.False(string.IsNullOrEmpty(reference.Id));
        Assert.Equal("image/png", reference.MediaType);
        Assert.Equal("cat.png", reference.FileName);
        Assert.StartsWith("user-1/conv-1/", reference.BlobPath);

        var content = await store.OpenReadAsync("user-1/conv-1", reference.Id);
        Assert.NotNull(content);
        Assert.Equal(bytes, content!.Bytes);
        Assert.Equal("image/png", content.MediaType);
        Assert.Equal("cat.png", content.FileName);

        // The chat path resolves an id back into a ref, scoped by owner key.
        var resolved = await store.GetAsync("user-1/conv-1", reference.Id);
        Assert.Equal(reference, resolved);
        Assert.Null(await store.GetAsync("user-2/conv-1", reference.Id));

        var url = await store.GetReadUrlAsync(reference);
        Assert.NotNull(url);

        // Another owner key cannot read it: user scoping is enforced by the blob path.
        Assert.Null(await store.OpenReadAsync("user-2/conv-1", reference.Id));
    }

    // AC1: uploading an image returns its id + metadata as JSON.
    [Fact]
    public async Task Upload_ReturnsAttachmentMetadataAsJson()
    {
        var response = await _client.PostAsync(
            "/api/conversations/ac1-conversation/attachments",
            ImageForm([1, 2, 3, 4], "image/png", "cat.png"));

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var metadata = await response.Content.ReadFromJsonAsync<AttachmentMetadata>();
        Assert.NotNull(metadata);
        Assert.False(string.IsNullOrEmpty(metadata!.Id));
        Assert.Equal("image/png", metadata.MediaType);
        Assert.Equal("cat.png", metadata.FileName);
    }

    // AC2: an unsupported media type or an oversize upload is rejected with 400. Documents
    // (PDF/Office/text) are no longer rejected — they take the ingestion path (ADR 0007) — so this
    // now asserts rejection of a genuinely unsupported binary type.
    [Fact]
    public async Task Upload_RejectsUnsupportedTypeAndOversize()
    {
        var unsupported = await _client.PostAsync(
            "/api/conversations/ac2-conversation/attachments",
            ImageForm([1, 2, 3], "application/octet-stream", "blob.bin"));
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        var oversize = await _client.PostAsync(
            "/api/conversations/ac2-conversation/attachments",
            ImageForm(new byte[(11 * 1024 * 1024)], "image/png", "big.png"));
        Assert.Equal(HttpStatusCode.BadRequest, oversize.StatusCode);
    }

    // AC4: history for an image turn includes the attachment ref (endpoint → grain path over the
    // shared static store, constraint 10) so a reopened conversation can re-render it.
    [Fact]
    public async Task History_IncludesAttachmentRefs()
    {
        const string id = "ac4-conversation";

        var metadata = await (await _client.PostAsync(
            $"/api/conversations/{id}/attachments", ImageForm([7, 7, 7], "image/png", "p.png")))
            .Content.ReadFromJsonAsync<AttachmentMetadata>();

        using (var chat = await _client.PostAsJsonAsync($"/api/chat/{id}",
            new ChatRequest("look at this", null, null, [metadata!.Id])))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain NDJSON so the turn is persisted
        }

        var turns = await _client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{id}/messages");
        var userTurn = Assert.Single(turns!, t => t.Role == "user" && t.Text == "look at this");
        Assert.NotNull(userTurn.Attachments);
        Assert.Equal(metadata.Id, Assert.Single(userTurn.Attachments!).Id);
    }

    // AC5: the display endpoint streams the stored bytes back with the right content type, and a
    // missing attachment is a 404.
    [Fact]
    public async Task Display_StreamsStoredImageBytes()
    {
        const string id = "ac5-conversation";
        var bytes = new byte[] { 9, 8, 7, 6, 5 };

        var metadata = await (await _client.PostAsync(
            $"/api/conversations/{id}/attachments", ImageForm(bytes, "image/png", "pic.png")))
            .Content.ReadFromJsonAsync<AttachmentMetadata>();

        var display = await _client.GetAsync($"/api/conversations/{id}/attachments/{metadata!.Id}");
        display.EnsureSuccessStatusCode();
        Assert.Equal("image/png", display.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await display.Content.ReadAsByteArrayAsync());

        var missing = await _client.GetAsync($"/api/conversations/{id}/attachments/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
