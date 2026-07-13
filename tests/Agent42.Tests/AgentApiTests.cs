using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Hosts the minimal-API endpoints in-memory (TestServer) against a real Orleans test
/// cluster, so the HTTP contract the browser depends on is exercised end to end.
/// </summary>
public sealed class AgentApiTests : IAsyncLifetime
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
        // Registered so the /api/models endpoint's parameter is resolved as a service
        // (otherwise minimal-API metadata inference treats it as a body and fails to map).
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        // The chat endpoint resolves attachments through the store; the in-memory store is
        // process-global, so it is the same instance the silo grain sees (constraint 10).
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        // Document ingestion + retrieval stand-ins for the upload endpoint and grain (ADR 0007).
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

    [Fact]
    public async Task SelectedModelEndpoint_RespondsWithJson_NotPlainText()
    {
        // Regression: the endpoint used to return the model as a bare string, which the
        // framework serves as text/plain. The UI parses this response as JSON, so a
        // text/plain body threw in response.json() and silently aborted rendering the
        // conversation history alongside it.
        var response = await _client.GetAsync("/api/conversations/model-contract-conversation/model");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var model = await response.Content.ReadFromJsonAsync<string>();
        Assert.False(string.IsNullOrWhiteSpace(model));
    }

    [Fact]
    public async Task MessagesEndpoint_ReturnsTheConversationHistoryAsJson()
    {
        const string id = "api-history-conversation";

        using (var chat = await _client.PostAsJsonAsync($"/api/chat/{id}", new ChatRequest("hi", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain the NDJSON stream so the turn is persisted
        }

        var response = await _client.GetAsync($"/api/conversations/{id}/messages");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var turns = await response.Content.ReadFromJsonAsync<List<ChatTurn>>();
        Assert.NotNull(turns);
        Assert.Contains(turns!, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(turns!, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    // AC9 — a valid position on the request reaches the grain, and the tool resolves it to an address.
    // Asserted end-to-end on the NDJSON stream (the address is FakeGeocoder's canned one), because the
    // whole point of this step is the hand-off from HTTP request to grain call.
    [Fact]
    public async Task Chat_ForwardsLocation()
    {
        using var chat = await _client.PostAsJsonAsync(
            "/api/chat/api-location-conversation",
            new ChatRequest("where am i", null, null, null, new UserLocation(52.3676, 4.9041, 12)));

        chat.EnsureSuccessStatusCode();
        var body = await chat.Content.ReadAsStringAsync();

        Assert.Contains(FakeGeocoder.Address, body);
    }

    // AC9 — an out-of-range fix is DROPPED, not rejected. A bad position must cost the user their
    // location for this turn, never their turn: the request still succeeds and the tool simply reports
    // the position as unknown. (A hand-rolled body is the only way to get here — the browser cannot
    // produce a latitude of 999.)
    [Theory]
    [InlineData(999, 4.9041)]
    [InlineData(52.3676, 999)]
    public async Task Chat_DropsOutOfRangeLocation(double latitude, double longitude)
    {
        using var chat = await _client.PostAsJsonAsync(
            $"/api/chat/api-bad-location-{latitude}-{longitude}",
            new ChatRequest("where am i", null, null, null, new UserLocation(latitude, longitude)));

        chat.EnsureSuccessStatusCode();
        var body = await chat.Content.ReadAsStringAsync();

        Assert.Contains("has not shared their location", body);
        Assert.DoesNotContain(FakeGeocoder.Address, body);
    }

    [Fact]
    public async Task DeleteConversation_RemovesItFromTheListAndClearsHistory()
    {
        const string id = "api-delete-conversation";

        using (var chat = await _client.PostAsJsonAsync($"/api/chat/{id}", new ChatRequest("hi", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain the NDJSON stream so the conversation is persisted
        }

        var before = await _client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations");
        Assert.Contains(before!, c => c.Id == id);

        var delete = await _client.DeleteAsync($"/api/conversations/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var after = await _client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations");
        Assert.DoesNotContain(after!, c => c.Id == id);

        var history = await _client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{id}/messages");
        Assert.Empty(history!);
    }

    // AC5: agents CRUD is reachable over HTTP and returns JSON.
    [Fact]
    public async Task AgentsEndpoints_CrudRoundTrip_AsJson()
    {
        var createRes = await _client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Marvin", "Paranoid android", "gpt-4o", "You are perpetually depressed."));
        createRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", createRes.Content.Headers.ContentType?.MediaType);
        var created = await createRes.Content.ReadFromJsonAsync<AgentDefinition>();
        Assert.NotNull(created);
        Assert.False(string.IsNullOrEmpty(created!.Id));
        Assert.Equal("Marvin", created.Name);

        var list = await _client.GetFromJsonAsync<List<AgentDefinition>>("/api/agents");
        Assert.Contains(list!, a => a.Id == created.Id && a.Name == "Marvin");

        var updateRes = await _client.PutAsJsonAsync($"/api/agents/{created.Id}",
            new AgentUpsertRequest("Marvin II", "Still paranoid", "gpt-4o", "You are brilliant but morose."));
        updateRes.EnsureSuccessStatusCode();
        var updated = await updateRes.Content.ReadFromJsonAsync<AgentDefinition>();
        Assert.Equal(created.Id, updated!.Id);
        Assert.Equal("Marvin II", updated.Name);

        var deleteRes = await _client.DeleteAsync($"/api/agents/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        var after = await _client.GetFromJsonAsync<List<AgentDefinition>>("/api/agents");
        Assert.DoesNotContain(after!, a => a.Id == created.Id);
    }

    // AC6: starting a chat with an agent id binds it; the conversation reports its agent and
    // the agent's pinned model.
    [Fact]
    public async Task ChatWithAgentId_BindsAgent_AndReportsIt()
    {
        const string convId = "api-agent-chat";
        var created = await (await _client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Zaphod", "Two heads", "zaphod-model", "You are Zaphod.")))
            .Content.ReadFromJsonAsync<AgentDefinition>();

        using (var chat = await _client.PostAsJsonAsync($"/api/chat/{convId}",
            new ChatRequest("hi", null, created!.Id)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync(); // drain the NDJSON stream so the turn is persisted
        }

        var bindingRes = await _client.GetAsync($"/api/conversations/{convId}/agent");
        bindingRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", bindingRes.Content.Headers.ContentType?.MediaType);
        var binding = await bindingRes.Content.ReadFromJsonAsync<AgentBinding>();
        Assert.NotNull(binding);
        Assert.Equal(created.Id, binding!.AgentId);
        Assert.Equal("You are Zaphod.", binding.SystemPrompt);

        // The model endpoint reports the agent's pinned model, not the server default.
        var model = await _client.GetFromJsonAsync<string>($"/api/conversations/{convId}/model");
        Assert.Equal("zaphod-model", model);
    }

    // AC6 (negative): a normal chat reports no agent, as JSON null (honors the text/plain gotcha).
    [Fact]
    public async Task NormalChat_HasNoAgentBinding()
    {
        const string convId = "api-normal-chat";
        using (var chat = await _client.PostAsJsonAsync($"/api/chat/{convId}", new ChatRequest("hi", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var bindingRes = await _client.GetAsync($"/api/conversations/{convId}/agent");
        bindingRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", bindingRes.Content.Headers.ContentType?.MediaType);
        var binding = await bindingRes.Content.ReadFromJsonAsync<AgentBinding>();
        Assert.Null(binding);
    }

    // AC7: resuming an agent chat after its agent is deleted still uses the pinned persona and
    // does not error — the persona is snapshotted into the grain, not referenced live (ADR 0003).
    [Fact]
    public async Task ChatAfterAgentDeleted_StillUsesPinnedPersona_NoError()
    {
        const string convId = "api-agent-deleted-chat";
        var created = await (await _client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Trillian", "", "trillian-model", "You are Trillian.")))
            .Content.ReadFromJsonAsync<AgentDefinition>();

        using (var chat = await _client.PostAsJsonAsync($"/api/chat/{convId}",
            new ChatRequest("hi", null, created!.Id)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        (await _client.DeleteAsync($"/api/agents/{created.Id}")).EnsureSuccessStatusCode();

        // Another turn still carrying the now-deleted agent id must succeed, not 500.
        using (var chat = await _client.PostAsJsonAsync($"/api/chat/{convId}",
            new ChatRequest("hello again", null, created.Id)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        var binding = await (await _client.GetAsync($"/api/conversations/{convId}/agent"))
            .Content.ReadFromJsonAsync<AgentBinding>();
        Assert.NotNull(binding);
        Assert.Equal(created.Id, binding!.AgentId);
        Assert.Equal("You are Trillian.", binding.SystemPrompt);
        Assert.Equal("trillian-model", binding.Model);
    }

    // AC2: GET /api/profile is application/json and returns the empty default when never set
    // (honors the JSON-contract gotcha — a non-null object via Results.Json, never a bare string).
    [Fact]
    public async Task ProfileEndpoint_ReturnsJson_WithDefaultWhenUnset()
    {
        var response = await _client.GetAsync("/api/profile");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var profile = await response.Content.ReadFromJsonAsync<UserProfile>();
        Assert.NotNull(profile);
        Assert.Equal("", profile!.PreferredLanguage);
    }

    // AC3: PUT persists the language; the next GET reflects it.
    [Fact]
    public async Task ProfileEndpoint_PersistsPreferredLanguage()
    {
        var putRes = await _client.PutAsJsonAsync("/api/profile", new ProfileUpdateRequest("nl-NL"));
        putRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", putRes.Content.Headers.ContentType?.MediaType);
        var saved = await putRes.Content.ReadFromJsonAsync<UserProfile>();
        Assert.Equal("nl-NL", saved!.PreferredLanguage);

        var getBack = await _client.GetFromJsonAsync<UserProfile>("/api/profile");
        Assert.Equal("nl-NL", getBack!.PreferredLanguage);
    }

    // AC4: the profile is isolated per user (the Entra object id). The shared _client sends no
    // principal header (→ "local-dev"), so this builds explicit requests carrying distinct
    // X-MS-CLIENT-PRINCIPAL-ID values; TestServer has no ingress stripping X-MS-CLIENT-* headers.
    [Fact]
    public async Task ProfileEndpoint_IsScopedPerUser()
    {
        await PutProfileAsAsync("user-alice", "de-DE");

        var bobProfile = await GetProfileAsAsync("user-bob");
        Assert.Equal("", bobProfile.PreferredLanguage);

        var aliceProfile = await GetProfileAsAsync("user-alice");
        Assert.Equal("de-DE", aliceProfile.PreferredLanguage);
    }

    // AC1 + AC (Step 5): a document upload is accepted (not rejected as non-image), returns a
    // document-kind marker with NO id (so the client never sends it back as an attachmentId), and
    // its text is indexed and searchable via the process-global in-memory index the grain also sees.
    [Fact]
    public async Task Upload_Document_IsAccepted_IndexedAndNotAnAttachment()
    {
        const string convId = "doc-upload-conversation";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(TestDocuments.SampleTwoPagePdf());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(DocumentMediaTypes.Pdf);
        form.Add(fileContent, "file", "guide.pdf");

        var response = await _client.PostAsync($"/api/conversations/{convId}/attachments", form);

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        // A document marker, not an AttachmentMetadata — there is no id for the client to send back.
        Assert.DoesNotContain("\"id\"", raw);
        var result = JsonSerializer.Deserialize<DocumentUploadResult>(raw, JsonSerializerOptions.Web);
        Assert.NotNull(result);
        Assert.Equal("document", result!.Kind);
        Assert.Equal("guide.pdf", result.FileName);
        Assert.True(result.ChunkCount > 0, "the PDF should have produced at least one indexed chunk");

        // The chunk text is retrievable from the same process-global index the grain queries,
        // scoped to this conversation's owner key. The default (headerless) user id is "local-dev".
        var index = new InMemoryDocumentIndex();
        var embedder = new DeterministicEmbeddingGenerator();
        var query = (await embedder.GenerateAsync(["magrathea"]))[0].Vector;
        var hits = await index.SearchAsync($"local-dev/{convId}", query, topK: 3);
        Assert.Contains(hits, h => h.Text.Contains("magrathea", StringComparison.OrdinalIgnoreCase));
        Assert.All(hits, h => Assert.Equal("guide.pdf", h.FileName));
    }

    // Regression: Windows browsers frequently send application/octet-stream (or no type) for a
    // document, which used to be rejected as "unsupported" — the upload 400'd, the frontend silently
    // dropped it, no chip appeared and the grain never learned the document existed. The handler now
    // resolves the type from the file extension, so the same bytes ingest as a document.
    [Fact]
    public async Task Upload_Document_WithGenericContentType_IsResolvedByExtensionAndIngested()
    {
        const string convId = "doc-octet-stream-conversation";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(TestDocuments.SampleTwoPagePdf());
        // The generic type a browser sends when the OS has no MIME registration for the extension.
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "guide.pdf");

        var response = await _client.PostAsync($"/api/conversations/{convId}/attachments", form);

        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<DocumentUploadResult>(
            await response.Content.ReadAsStringAsync(), JsonSerializerOptions.Web);
        Assert.NotNull(result);
        Assert.Equal("document", result!.Kind);
        Assert.True(result.ChunkCount > 0, "the PDF should have produced at least one indexed chunk");
    }

    private async Task PutProfileAsAsync(string principalId, string language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/profile")
        {
            Content = JsonContent.Create(new ProfileUpdateRequest(language)),
        };
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", principalId);
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<UserProfile> GetProfileAsAsync(string principalId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", principalId);
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserProfile>())!;
    }
}
