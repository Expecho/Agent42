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
}
