using System.Net;
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
}
