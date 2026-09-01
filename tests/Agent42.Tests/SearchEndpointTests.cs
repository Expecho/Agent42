using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// AC3, AC8 and AC9 (ADR 0061): the <c>GET /api/search</c> and <c>POST /api/search/reindex</c>
/// contract, hosted in-memory (TestServer) against a real Orleans test cluster, so owner scoping and
/// the backfill are exercised end to end rather than against the index in isolation.
///
/// <para>Owner scoping is the one that matters: the endpoint takes its userId from
/// <c>GetUserId()</c> and never from the query string, so a caller cannot ask for someone else's
/// corpus. <see cref="Search_IsOwnerScoped_AcrossUsers"/> is what pins that.</para>
/// </summary>
public sealed class SearchEndpointTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private HttpClient Client => Fixture.Client;

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

    private async Task<JsonElement[]> SearchAsync(string principalId, string query)
    {
        var response = await Client.SendAsync(
            Request(HttpMethod.Get, $"/api/search?q={Uri.EscapeDataString(query)}", principalId));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. doc.RootElement.GetProperty("hits").EnumerateArray().Select(e => e.Clone())];
    }

    // Sends a chat turn through the API so the conversation is both indexed AND listed in the
    // conversation index — the backfill walks the latter, so a grain-only turn would not be found.
    private async Task SendTurnAsync(string principalId, string conversationId, string prompt)
    {
        var response = await Client.SendAsync(
            Request(HttpMethod.Post, $"/api/chat/{conversationId}", principalId, new { message = prompt }));
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync();
    }

    // AC1 end to end: a turn sent through the API is searchable through the API.
    [Fact]
    public async Task Search_FindsMessagesFromTheCallersOwnConversations()
    {
        const string user = "search-api-basic-user";
        await SendTurnAsync(user, "search-api-basic-conv", "endpoint reachable needle");

        var hits = await SearchAsync(user, "endpoint reachable needle");

        Assert.NotEmpty(hits);
        Assert.Equal("search-api-basic-conv", hits[0].GetProperty("conversationId").GetString());
    }

    // AC3: the load-bearing security property. Two users index the same needle; each sees only their
    // own. The endpoint never accepts a userId from the caller, so there is no parameter to tamper
    // with — this pins that it stays that way.
    [Fact]
    public async Task Search_IsOwnerScoped_AcrossUsers()
    {
        await SendTurnAsync("search-api-alice", "alice-conv", "shared secret needle from alice");
        await SendTurnAsync("search-api-bob", "bob-conv", "shared secret needle from bob");

        var alicesHits = await SearchAsync("search-api-alice", "shared secret needle");

        Assert.NotEmpty(alicesHits);
        Assert.All(alicesHits, hit =>
            Assert.Equal("alice-conv", hit.GetProperty("conversationId").GetString()));
        Assert.DoesNotContain(alicesHits, hit =>
            hit.GetProperty("text").GetString()!.Contains("from bob"));
    }

    [Fact]
    public async Task Search_WithNoQuery_ReturnsNoHits()
    {
        const string user = "search-api-blank-user";
        await SendTurnAsync(user, "search-api-blank-conv", "some indexed content");

        var response = await Client.SendAsync(Request(HttpMethod.Get, "/api/search", user));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("hits").EnumerateArray());
    }

    // AC8: the backfill makes a pre-existing conversation searchable — the criterion that justified
    // rejecting "new messages onward" in the planning interview.
    //
    // The index is DELETED between the turn and the reindex, which is what makes this test mean
    // anything. Written the obvious way — send a turn, reindex, assert hits — it passes even if
    // ReindexAsync never writes at all, because the live path already indexed the turn. Deleting
    // first simulates the real case (history that predates the feature) and fails if the backfill is
    // a no-op.
    [Fact]
    public async Task Reindex_MakesExistingConversationsSearchable()
    {
        const string user = "search-api-reindex-user";
        await SendTurnAsync(user, "search-api-reindex-conv", "backfilled needle");

        await new InMemoryChatSearchIndex().DeleteConversationAsync(user, "search-api-reindex-conv");
        Assert.Empty(await SearchAsync(user, "backfilled needle"));

        var response = await Client.SendAsync(Request(HttpMethod.Post, "/api/search/reindex", user));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("conversations").GetInt32() >= 1);
        Assert.True(doc.RootElement.GetProperty("turns").GetInt32() >= 1);
        Assert.False(doc.RootElement.GetProperty("more").GetBoolean());

        // Only the backfill can have put these back.
        Assert.NotEmpty(await SearchAsync(user, "backfilled needle"));
    }

    // AC9: running the backfill twice does not duplicate anything. Both runs derive ordinals from the
    // same GetHistoryAsync() projection, so the second overwrites the first's documents.
    [Fact]
    public async Task Reindex_IsIdempotent()
    {
        const string user = "search-api-idempotent-user";
        await SendTurnAsync(user, "search-api-idempotent-conv", "idempotent endpoint needle");

        await Client.SendAsync(Request(HttpMethod.Post, "/api/search/reindex", user));
        var afterFirst = (await SearchAsync(user, "idempotent endpoint needle")).Length;

        await Client.SendAsync(Request(HttpMethod.Post, "/api/search/reindex", user));
        var afterSecond = (await SearchAsync(user, "idempotent endpoint needle")).Length;

        Assert.Equal(afterFirst, afterSecond);
    }

    // The backfill is per-user: it walks the CALLER's conversation index, so it can never touch — or
    // report — another user's conversations.
    [Fact]
    public async Task Reindex_NeverTouchesAnotherUsersConversations()
    {
        await SendTurnAsync("search-api-sweep-owner", "owner-conv", "sweepable needle");
        await SendTurnAsync("search-api-sweep-other", "other-conv", "sweepable needle");

        var response = await Client.SendAsync(
            Request(HttpMethod.Post, "/api/search/reindex", "search-api-sweep-owner"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("conversations").GetInt32());
    }
}
