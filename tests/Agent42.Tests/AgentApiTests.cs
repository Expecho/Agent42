using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        // (otherwise minimal-API metadata inference treats it as a body and fails to map). Two
        // providers, so the endpoint's qualified-id contract (ADR 0013) is assertable.
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("foundry:gpt-4o",
        [
            new ModelInfo("foundry:gpt-4o", "foundry", "GPT-4o", "gpt-4o", "2024-08-06", true, true),
            new ModelInfo("github:openai/gpt-4o-mini", "github", "GPT-4o mini", "openai/gpt-4o-mini",
                null, SupportsTools: true, SupportsVision: false),
        ]));
        // /api/models is user-scoped (ADR 0018); the single static catalog stands in for every user.
        appBuilder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        // /api/providers encrypts keys, so the protector must resolve for the route matcher (ADR 0018).
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        // /api/mcp-servers encrypts secrets, so its protector must also resolve for the route matcher
        // (minimal-API infers every endpoint's parameters up front — an unregistered service param would be
        // treated as a body and break routing for the whole host).
        appBuilder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        appBuilder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
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
            new AgentUpsertRequest("Marvin", "Paranoid android", "gpt-4o", "You are perpetually depressed.",
                Continuous: true));
        createRes.EnsureSuccessStatusCode();
        Assert.Equal("application/json", createRes.Content.Headers.ContentType?.MediaType);
        // The create/update body is now AgentSaveResponse { agent, apiKey } (ADR 0022), not a bare
        // AgentDefinition — read the wrapper and use .Agent.
        var createBody = await createRes.Content.ReadFromJsonAsync<AgentSaveResponse>();
        Assert.NotNull(createBody);
        var created = createBody!.Agent;
        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.Equal("Marvin", created.Name);
        // AC1: creating an agent returns a raw key ONCE.
        Assert.False(string.IsNullOrEmpty(createBody.ApiKey), "create should mint an API key.");
        // AC2: POST echoes the history-mode flag.
        Assert.True(created.Continuous);

        var list = await _client.GetFromJsonAsync<List<AgentDefinition>>("/api/agents");
        // AC2: GET carries it too.
        Assert.Contains(list!, a => a.Id == created.Id && a.Name == "Marvin" && a.Continuous);

        // AC2: PUT accepts and echoes a flipped flag.
        var updateRes = await _client.PutAsJsonAsync($"/api/agents/{created.Id}",
            new AgentUpsertRequest("Marvin II", "Still paranoid", "gpt-4o", "You are brilliant but morose.",
                Continuous: false));
        updateRes.EnsureSuccessStatusCode();
        var updateBody = await updateRes.Content.ReadFromJsonAsync<AgentSaveResponse>();
        var updated = updateBody!.Agent;
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Marvin II", updated.Name);
        Assert.False(updated.Continuous);
        // A plain update (no regenerate) mints no key.
        Assert.Null(updateBody.ApiKey);

        // AC3: an old-shape client that OMITS `continuous` entirely stores false (per-chat) — the
        // real "old data defaults to per-chat" property, asserted at the JSON layer (not the ctor).
        // An anonymous payload without the field, so it is genuinely absent on the wire.
        var oldShapeRes = await _client.PostAsJsonAsync("/api/agents",
            new { name = "Legacy", description = "no flag", model = "gpt-4o", systemPrompt = "You are old." });
        oldShapeRes.EnsureSuccessStatusCode();
        var legacy = (await oldShapeRes.Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;
        Assert.NotNull(legacy);
        Assert.False(legacy.Continuous, "an omitted `continuous` must default to false (per-chat).");
        await _client.DeleteAsync($"/api/agents/{legacy.Id}");

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
        var created = (await (await _client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Zaphod", "Two heads", "zaphod-model", "You are Zaphod.")))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

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
        var created = (await (await _client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest("Trillian", "", "trillian-model", "You are Trillian.")))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

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

    // Theme (ADR 0005 field #2) persists through PUT and is reflected by the next GET.
    [Fact]
    public async Task ProfileEndpoint_PersistsTheme()
    {
        var putRes = await _client.PutAsJsonAsync("/api/profile", new ProfileUpdateRequest("en-US", "dark"));
        putRes.EnsureSuccessStatusCode();
        Assert.Equal("dark", (await putRes.Content.ReadFromJsonAsync<UserProfile>())!.Theme);

        var getBack = await _client.GetFromJsonAsync<UserProfile>("/api/profile");
        Assert.Equal("dark", getBack!.Theme);
    }

    // Regression: a stale client (old cached app.js) that PUTs a language-only body — the Theme
    // field entirely absent — must NOT reset a previously saved theme. The endpoint merges onto
    // the stored profile, so an omitted field keeps its stored value.
    [Fact]
    public async Task ProfileEndpoint_OmittedTheme_KeepsStoredTheme()
    {
        var seed = await _client.PutAsJsonAsync("/api/profile", new ProfileUpdateRequest("en-US", "dark"));
        seed.EnsureSuccessStatusCode();

        // Body without any Theme key, as a pre-theme client would send.
        var staleSave = await _client.PutAsJsonAsync("/api/profile", new { preferredLanguage = "fr-FR" });
        staleSave.EnsureSuccessStatusCode();

        var saved = await staleSave.Content.ReadFromJsonAsync<UserProfile>();
        Assert.Equal("fr-FR", saved!.PreferredLanguage);
        Assert.Equal("dark", saved.Theme); // kept, not silently reset to "system"
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

    // AC13: the picker reads this payload, so the wire contract is the qualified id plus the provider
    // and the capability flags — in camelCase, which is what the frontend indexes by (ADR 0013).
    [Fact]
    public async Task ModelsEndpoint_ReturnsQualifiedIdsWithProvider()
    {
        var json = await _client.GetStringAsync("/api/models");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("foundry:gpt-4o", doc.RootElement.GetProperty("default").GetString());

        var models = doc.RootElement.GetProperty("models").EnumerateArray().ToList();

        var github = models.Single(m => m.GetProperty("provider").GetString() == "github");
        Assert.Equal("github:openai/gpt-4o-mini", github.GetProperty("id").GetString());
        Assert.Equal("GPT-4o mini", github.GetProperty("displayName").GetString());
        Assert.True(github.GetProperty("supportsTools").GetBoolean());
        Assert.False(github.GetProperty("supportsVision").GetBoolean());

        var foundry = models.Single(m => m.GetProperty("provider").GetString() == "foundry");
        Assert.Equal("foundry:gpt-4o", foundry.GetProperty("id").GetString());
    }

    // ————— per-agent API key + async run endpoint (ADR 0022) —————

    // AC2: GET /api/agents never returns a raw key (its prefix must not appear anywhere in the body).
    [Fact]
    public async Task GetAgents_NeverReturnsARawKey()
    {
        var (_, apiKey) = await CreateAgentAsync("SecretHolder");

        var listRaw = await _client.GetStringAsync("/api/agents");
        Assert.DoesNotContain(apiKey, listRaw);
        Assert.DoesNotContain("sk_agent_", listRaw);
    }

    // AC3 + AC4: a valid key → 202 with a Location + conversationId; the background run persists the
    // prompt + an assistant reply into a new conversation bound to the agent's persona.
    [Fact]
    public async Task AgentRun_ValidKey_Returns202_AndRunsInBackground()
    {
        var (agentId, apiKey) = await CreateAgentAsync();

        using var res = await AgentRunAsync(apiKey, "hello agent");
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        var convId = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversationId").GetString();
        Assert.False(string.IsNullOrEmpty(convId));
        Assert.Equal($"/api/conversations/{convId}/messages", res.Headers.Location?.ToString());

        var turns = await PollMessagesAsync(convId!, t => t.Any(x => x.Role == "assistant"));
        Assert.Contains(turns, t => t.Role == "user" && t.Text == "hello agent");
        Assert.Contains(turns, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));

        // The conversation is bound to the agent persona (ADR 0003).
        var binding = await (await _client.GetAsync($"/api/conversations/{convId}/agent"))
            .Content.ReadFromJsonAsync<AgentBinding>();
        Assert.NotNull(binding);
        Assert.Equal(agentId, binding!.AgentId);
    }

    // AC5: missing or unknown X-Api-Key → 401 (fail closed).
    [Fact]
    public async Task AgentRun_MissingOrUnknownKey_Returns401()
    {
        using var noKey = await AgentRunAsync(apiKey: null, "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        using var badKey = await AgentRunAsync("sk_agent_not-a-real-key", "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, badKey.StatusCode);
    }

    // AC6: regenerating a key invalidates the old one — the new key runs (202), the old key → 401.
    [Fact]
    public async Task AgentRun_RegeneratedKey_Works_OldKeyRejected()
    {
        var (agentId, oldKey) = await CreateAgentAsync();

        var regenerated = (await (await _client.PutAsJsonAsync($"/api/agents/{agentId}",
            new AgentUpsertRequest("Runner", "", "gpt-4o", "You are a runner.", RegenerateKey: true)))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!;
        var newKey = regenerated.ApiKey;
        Assert.False(string.IsNullOrEmpty(newKey));
        Assert.NotEqual(oldKey, newKey);

        using var withNew = await AgentRunAsync(newKey!, "hi");
        Assert.Equal(HttpStatusCode.Accepted, withNew.StatusCode);

        using var withOld = await AgentRunAsync(oldKey, "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
    }

    // AC9: an image handed to agent-run rides the run and lands on the user turn in the new conversation.
    [Fact]
    public async Task AgentRun_WithImage_AttachesToTurn()
    {
        var (_, apiKey) = await CreateAgentAsync();

        using var res = await AgentRunAsync(apiKey, "describe this",
            (new byte[] { 1, 2, 3, 4 }, "image/png", "pic.png"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var convId = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversationId").GetString()!;

        var turns = await PollMessagesAsync(convId, t => t.Any(x => x.Role == "assistant"));
        var userTurn = Assert.Single(turns, t => t.Role == "user");
        Assert.NotNull(userTurn.Attachments);
        Assert.Contains(userTurn.Attachments!, a => a.FileName == "pic.png");
    }

    // AC10: a supported document handed to agent-run → 202, and its text is ingested for retrieval
    // under the run's conversation owner key (the same process-global in-memory index the grain queries).
    [Fact]
    public async Task AgentRun_WithDocument_RegistersForRag()
    {
        var (_, apiKey) = await CreateAgentAsync();

        using var res = await AgentRunAsync(apiKey, "what does the doc say",
            (TestDocuments.SampleTwoPagePdf(), DocumentMediaTypes.Pdf, "guide.pdf"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var convId = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conversationId").GetString()!;

        // Ingestion runs synchronously in the request (before the 202), so the chunk is indexed now.
        var index = new InMemoryDocumentIndex();
        var query = (await new DeterministicEmbeddingGenerator().GenerateAsync(["magrathea"]))[0].Vector;
        var hits = await index.SearchAsync($"local-dev/{convId}", query, topK: 3);
        Assert.Contains(hits, h => h.FileName == "guide.pdf");
    }

    // AC11: a batch with an unsupported (or oversize) file is rejected WHOLE (400) and no conversation
    // is created (constraint 13) — automation retries cleanly rather than getting a half-processed run.
    [Fact]
    public async Task AgentRun_BadFile_Rejects400_NoConversation()
    {
        var (_, apiKey) = await CreateAgentAsync();
        var before = (await _client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!.Count;

        using var res = await AgentRunAsync(apiKey, "hi",
            (new byte[] { 1, 2, 3 }, "application/octet-stream", "malware.exe"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var after = (await _client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!.Count;
        Assert.Equal(before, after);
    }

    // Creates an agent and returns its id + the once-shown raw API key.
    private async Task<(string Id, string ApiKey)> CreateAgentAsync(string name = "Runner")
    {
        var body = (await (await _client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest(name, "", "gpt-4o", "You are a runner.")))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!;
        return (body.Agent.Id, body.ApiKey!);
    }

    // POSTs multipart/form-data to /api/agent-run: a 'prompt' field, an optional X-Api-Key header, and
    // zero or more 'file' parts. Mirrors how external automation calls the endpoint.
    private async Task<HttpResponseMessage> AgentRunAsync(
        string? apiKey, string prompt, params (byte[] Bytes, string ContentType, string FileName)[] files)
    {
        var form = new MultipartFormDataContent { { new StringContent(prompt), "prompt" } };
        foreach (var (bytes, contentType, fileName) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, "file", fileName);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent-run") { Content = form };
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        return await _client.SendAsync(request);
    }

    // Polls a conversation's history until <paramref name="until"/> holds or a bound elapses — the
    // agent-run turn runs in the background (OneWay), so the reply is not present the instant 202 returns.
    private async Task<List<ChatTurn>> PollMessagesAsync(string convId, Func<List<ChatTurn>, bool> until)
    {
        List<ChatTurn> turns = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            turns = (await _client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{convId}/messages"))!;
            if (until(turns))
            {
                return turns;
            }

            await Task.Delay(100);
        }

        return turns;
    }
}
