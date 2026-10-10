using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The /api/agents/{id}/webhook contract (ADR 0094), hosted in-memory against a real cluster: the secret is shown
/// once and never again, the URL is validated, and another user's agent is a 404.
/// </summary>
public sealed class WebhookApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string Url = "https://hooks.example.com/agent42";

    private HttpClient Client => Fixture.Client;

    // AC14: the minting PUT returns the secret once; GET never carries it or its ciphertext.
    [Fact]
    public async Task Put_MintsSecretOnce_GetNeverReturnsIt()
    {
        var (user, agentId) = await NewAgentAsync();

        var saved = await PutAsync(user, agentId, new { url = Url });
        var secret = saved.GetProperty("secret").GetString();
        Assert.StartsWith(WebhookSigner.SecretPrefix, secret, StringComparison.Ordinal);

        var get = await Client.SendAsync(Request(HttpMethod.Get, $"/api/agents/{agentId}/webhook", user));
        get.EnsureSuccessStatusCode();
        var raw = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret!, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext", raw, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(["url", "hasSecret", "deliveries"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Url, json.RootElement.GetProperty("url").GetString());
        Assert.True(json.RootElement.GetProperty("hasSecret").GetBoolean());
    }

    // AC14: a plain PUT keeps the secret (none in the response); rotateSecret returns a new one.
    [Fact]
    public async Task Put_RotateSecret_ReturnsNewSecret()
    {
        var (user, agentId) = await NewAgentAsync();
        var first = (await PutAsync(user, agentId, new { url = Url })).GetProperty("secret").GetString();

        var plain = await PutAsync(user, agentId, new { url = Url + "/v2" });
        var rotated = await PutAsync(user, agentId, new { url = Url, rotateSecret = true });

        Assert.Equal(JsonValueKind.Null, plain.GetProperty("secret").ValueKind);
        var second = rotated.GetProperty("secret").GetString();
        Assert.StartsWith(WebhookSigner.SecretPrefix, second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    // AC15: only an absolute https URL with no userinfo and no private IP literal is accepted.
    [Theory]
    [InlineData("http://hooks.example.com/a")]
    [InlineData("https://user:pw@hooks.example.com/a")]
    [InlineData("https://127.0.0.1/a")]
    [InlineData("https://169.254.169.254/metadata")]
    [InlineData("https://10.0.0.5/a")]
    [InlineData("https://[::1]/a")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Put_RejectsInvalidUrls(string url)
    {
        var (user, agentId) = await NewAgentAsync();

        var response = await Client.SendAsync(Request(HttpMethod.Put, $"/api/agents/{agentId}/webhook", user, new { url }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC15: another user's agent id is a 404 on every route — the grain key is never reached for it.
    [Fact]
    public async Task Put_OtherUsersAgent_Returns404()
    {
        var (_, agentId) = await NewAgentAsync();
        const string intruder = "webhook-intruder";

        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.SendAsync(Request(HttpMethod.Put, $"/api/agents/{agentId}/webhook", intruder, new { url = Url }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.SendAsync(Request(HttpMethod.Get, $"/api/agents/{agentId}/webhook", intruder))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.SendAsync(Request(HttpMethod.Delete, $"/api/agents/{agentId}/webhook", intruder))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.SendAsync(Request(HttpMethod.Post, $"/api/agents/{agentId}/webhook/test", intruder))).StatusCode);
    }

    // AC18: the test endpoint returns what the delivery got, and logs it; 409 before anything is configured.
    [Fact]
    public async Task PostTest_ReturnsDeliveryResult()
    {
        var (user, agentId) = await NewAgentAsync();
        Assert.Equal(HttpStatusCode.Conflict,
            (await Client.SendAsync(Request(HttpMethod.Post, $"/api/agents/{agentId}/webhook/test", user))).StatusCode);
        await PutAsync(user, agentId, new { url = Url });

        var response = await Client.SendAsync(Request(HttpMethod.Post, $"/api/agents/{agentId}/webhook/test", user));

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(200, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("attempts").GetInt32());
        var get = await Client.SendAsync(Request(HttpMethod.Get, $"/api/agents/{agentId}/webhook", user));
        using var status = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(AgentWebhookEvent.Test,
            status.RootElement.GetProperty("deliveries").EnumerateArray().Single().GetProperty("type").GetString());
    }

    [Fact]
    public async Task Delete_ClearsTheWebhook()
    {
        var (user, agentId) = await NewAgentAsync();
        await PutAsync(user, agentId, new { url = Url });

        var deleted = await Client.SendAsync(Request(HttpMethod.Delete, $"/api/agents/{agentId}/webhook", user));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var get = await Client.SendAsync(Request(HttpMethod.Get, $"/api/agents/{agentId}/webhook", user));
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("url").ValueKind);
        Assert.False(json.RootElement.GetProperty("hasSecret").GetBoolean());
    }

    // AC14: what reaches the grain is ciphertext — the protector's output, never the secret the response showed.
    [Fact]
    public async Task Put_StoresOnlyCiphertext()
    {
        var (user, agentId) = await NewAgentAsync();
        var secret = (await PutAsync(user, agentId, new { url = Url })).GetProperty("secret").GetString()!;

        await Fixture.Cluster.GrainFactory.GetGrain<IAgentWebhookGrain>(AgentWebhookKeys.KeyFor(user, agentId))
            .SendTestAsync("Hooky");

        var stored = Fixture.Cluster.Silos
            .SelectMany(s => Fixture.Cluster.GetSiloServiceProvider(s.SiloAddress).GetRequiredService<RecordingAgentWebhookSender>().Sends)
            .Single(s => s.Target.Url == Url && s.Event.AgentId == agentId).Target.SecretCiphertext;
        Assert.NotEqual(secret, stored);
        Assert.Equal(secret, Fixture.App.Services.GetRequiredService<IWebhookSecretProtector>().Unprotect(stored));
    }

    // AC22: the chat endpoint tells the grain when a turn replaces the last one (ADR 0092), so the event can say so.
    [Fact]
    public void Chat_ReplaceLastTurn_PassesFlagToGrain()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Agent42.Backend", "Conversations", "AgentApi.Conversations.cs"));

        Assert.Contains("replacesLastTurn: request.ReplaceLastTurn", source, StringComparison.Ordinal);
    }

    private async Task<(string User, string AgentId)> NewAgentAsync()
    {
        var user = $"webhook-api-{Guid.NewGuid():N}";
        var response = await Client.SendAsync(Request(HttpMethod.Post, "/api/agents", user, new
        {
            name = "Hooky",
            description = "",
            model = "fake-model",
            systemPrompt = "You are Hooky.",
        }));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (user, json.RootElement.GetProperty("agent").GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> PutAsync(string user, string agentId, object body)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Put, $"/api/agents/{agentId}/webhook", user, body));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string principalId, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, principalId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
