using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The analyzer panel's endpoints (ADR 0123): the stream for the caller's own task, a 404 for anyone else's, no listing in
/// the sidebar index or in chat search, history that reloads without the persisted open-task prefix, and Clear. The tools
/// themselves are pinned by <see cref="FleetAnalyzerToolTests"/>; the canned model makes no tool calls here.
/// </summary>
public sealed class FleetAnalyzerPanelApiTests(FleetApiFixture fixture) : SharedClusterTests<FleetApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private HttpClient Client => Fixture.Client;

    private Task<HttpResponseMessage> Send(HttpMethod method, string uri, string user, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, user);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Client.SendAsync(request);
    }

    private async Task<string> SubmitAsync(string user)
    {
        var task = FakeChatClient.FleetTask("Write a haiku about lighthouses", new([FleetRunHarness.Plan("Sequential", "Writer")]));
        var response = await Send(HttpMethod.Post, "/api/fleet/tasks", user, new { task, model = FleetRunHarness.ModelA });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetString()!;
    }

    private async Task<string> AskAsync(string user, string taskId, string message)
    {
        var response = await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/analyzer", user, new { message, model = FleetRunHarness.ModelA });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<JsonElement> HistoryAsync(string user, string taskId)
    {
        var response = await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}/analyzer", user);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Panel_StreamsForOwnTask()
    {
        const string user = "panel-own-user";
        var taskId = await SubmitAsync(user);

        var body = await AskAsync(user, taskId, "why did it stop?");

        Assert.Contains("\"type\"", body, StringComparison.Ordinal);
        Assert.Contains("help you today?", body, StringComparison.Ordinal);
        // The panel shows no suggestions, so the extra model call that makes them is skipped (ADR 0123).
        Assert.DoesNotContain("suggestions", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Panel_ForeignTask_404()
    {
        const string alice = "panel-alice";
        const string bob = "panel-bob";
        var taskId = await SubmitAsync(alice);
        await AskAsync(alice, taskId, "hello there");

        var post = await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/analyzer", bob, new { message = "hi", model = FleetRunHarness.ModelA });
        var get = await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}/analyzer", bob);
        var missing = await Send(HttpMethod.Get, "/api/fleet/tasks/no-such-task/analyzer", alice);

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        // Alice's conversation is untouched by Bob's attempts.
        Assert.Equal(2, (await HistoryAsync(alice, taskId)).GetArrayLength());
    }

    [Fact]
    public async Task Panel_NotListed()
    {
        const string user = "panel-unlisted-user";
        var taskId = await SubmitAsync(user);
        await AskAsync(user, taskId, "zebrafish analyzer needle");
        // The positive control: the same text in an ordinary chat IS searchable, so an empty result below means something.
        (await Send(HttpMethod.Post, "/api/chat/panel-control", user, new { message = "zebrafish chat needle" })).EnsureSuccessStatusCode();

        var conversations = await (await Send(HttpMethod.Get, "/api/conversations", user)).Content.ReadAsStringAsync();
        var hits = await (await Send(HttpMethod.Get, "/api/search?q=zebrafish", user)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("fleet-analyzer", conversations, StringComparison.Ordinal);
        Assert.Contains("panel-control", hits, StringComparison.Ordinal);
        Assert.DoesNotContain("fleet-analyzer", hits, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_HistoryAndClear()
    {
        const string user = "panel-history-user";
        var taskId = await SubmitAsync(user);
        await AskAsync(user, taskId, "what happened?");

        var turns = await HistoryAsync(user, taskId);
        Assert.Equal("what happened?", turns[0].GetProperty("text").GetString());
        Assert.Equal("assistant", turns[1].GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/fleet/tasks/{taskId}/analyzer", user)).StatusCode);

        Assert.Equal(0, (await HistoryAsync(user, taskId)).GetArrayLength());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-model")]
    public async Task Panel_WithoutAUsableModel_400(string? model)
    {
        var user = $"panel-model-user-{model ?? "none"}";
        var taskId = await SubmitAsync(user);

        var response = await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/analyzer", user, new { message = "hi", model });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await HistoryAsync(user, taskId)).GetArrayLength());
    }

    [Fact]
    public async Task DeletingTheTask_PurgesItsAnalyzerConversation()
    {
        const string user = "panel-delete-user";
        var taskId = await SubmitAsync(user);
        await AskAsync(user, taskId, "remember me");
        (await Send(HttpMethod.Post, $"/api/fleet/tasks/{taskId}/stop", user)).EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await (await Send(HttpMethod.Get, $"/api/fleet/tasks/{taskId}", user)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString() == FleetTaskStatus.Running)
        {
            await System.Threading.Tasks.Task.Delay(20, timeout.Token);
        }

        (await Send(HttpMethod.Delete, $"/api/fleet/tasks/{taskId}", user)).EnsureSuccessStatusCode();

        // The task is gone, so the panel GET is a 404; the grain itself was purged.
        var grain = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/{FleetAnalyzerDefaults.ConversationId(taskId)}");
        Assert.Empty(await grain.GetHistoryAsync());
    }
}
