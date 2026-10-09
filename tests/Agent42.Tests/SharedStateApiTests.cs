using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The shared-state routes (ADR 0089 AC15/AC16): the wire shape, the status codes the UI acts on, and the
/// per-user key.
/// </summary>
public sealed class SharedStateApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private HttpClient Client => Fixture.Client;

    private static HttpRequestMessage Request(HttpMethod method, string uri, string user, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, user);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async Task SaveConversationAsync(string user, string conversation)
    {
        await foreach (var _ in Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/{conversation}").ChatAsync("hi", null))
        {
        }
    }

    private async Task<JsonElement> GetState(string user, string conversation)
    {
        var response = await Client.SendAsync(Request(HttpMethod.Get, $"/api/conversations/{conversation}/state", user));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task<HttpResponseMessage> Put(string user, string conversation, object state, long baseVersion)
        => Client.SendAsync(Request(HttpMethod.Put, $"/api/conversations/{conversation}/state", user,
            new { state, baseVersion }));

    // The state is a real JSON object on the wire in both directions, versioned.
    [Fact]
    public async Task GetAndPut_RoundTripTheStateAsAnObject()
    {
        await SaveConversationAsync("ss-api-user", "conv");
        var empty = await GetState("ss-api-user", "conv");
        Assert.Equal((JsonValueKind.Null, 0L), (empty.GetProperty("state").ValueKind, empty.GetProperty("version").GetInt64()));

        var put = await Put("ss-api-user", "conv", new { checklist = new[] { new { id = "a", title = "T", done = true } } }, 0);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(1, (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());

        var state = await GetState("ss-api-user", "conv");
        Assert.True(state.GetProperty("state").GetProperty("checklist")[0].GetProperty("done").GetBoolean());
        Assert.Equal(1, state.GetProperty("version").GetInt64());
    }

    // AC15: a stale base is a 409 carrying the current version, so the UI can re-fetch.
    [Fact]
    public async Task Put_StaleBaseVersion_Is409()
    {
        await SaveConversationAsync("ss-api-stale", "conv");
        await Put("ss-api-stale", "conv", new { a = 1 }, 0);

        var stale = await Put("ss-api-stale", "conv", new { a = 2 }, 0);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
    }

    // AC16: no saved record, no state.
    [Fact]
    public async Task Put_OnAnUnsavedConversation_Is409()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Put("ss-api-unsaved", "conv", new { a = 1 }, 0)).StatusCode);
    }

    [Theory]
    [InlineData("""{"state":[1,2],"baseVersion":0}""")]
    [InlineData("""{"state":{"a":1}}""")]
    [InlineData("""{"state":{"a":1},"baseVersion":"zero"}""")]
    [InlineData("not json")]
    public async Task Put_InvalidBody_Is400(string body)
    {
        await SaveConversationAsync("ss-api-invalid", "conv");
        var request = Request(HttpMethod.Put, "/api/conversations/conv/state", "ss-api-invalid");
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, (await Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Put_Oversized_Is413_AndOverTheCap_Is400()
    {
        await SaveConversationAsync("ss-api-size", "conv");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await Put("ss-api-size", "conv", new { big = new string('x', 2 * SharedStateLimits.MaxBytes) }, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Put("ss-api-size", "conv", new { big = new string('x', SharedStateLimits.MaxBytes) }, 0)).StatusCode);
    }

    // The key is per user: another user's request reaches a different (empty, unsaved) conversation.
    [Fact]
    public async Task OtherUser_CannotReadOrWriteTheState()
    {
        await SaveConversationAsync("ss-api-owner", "conv");
        await Put("ss-api-owner", "conv", new { secret = 1 }, 0);

        Assert.Equal(JsonValueKind.Null, (await GetState("ss-api-intruder", "conv")).GetProperty("state").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await Put("ss-api-intruder", "conv", new { a = 1 }, 0)).StatusCode);
    }
}
