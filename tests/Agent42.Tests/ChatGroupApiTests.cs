using System.Net;
using System.Net.Http.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Custom conversation groups (ADR 0073) over the real HTTP surface and a real Orleans cluster.
///
/// <para>The listing and the group set are SHARED across this class's tests — one cluster is deployed
/// for the class (ADR 0055), so every assertion here is scoped to ids this test minted and must never
/// assume an absolute index or a total count.</para>
/// </summary>
public sealed class ChatGroupApiTests(AgentApiFixture fixture) : SharedClusterTests<AgentApiFixture>(fixture)
{
    private HttpClient Client => Fixture.Client;

    // AC1
    [Fact]
    public async Task CreateGroup_ReturnsTheGroup_AndItIsListed()
    {
        var created = await CreateGroupAsync("Work");

        // The minted id must come back on the create response: it is the only way the client can file a
        // conversation into the group it just made, without re-listing and matching on a name that is
        // not unique.
        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal("Work", created.Name);

        var listed = await GetGroupsAsync();
        Assert.Equal("Work", Assert.Single(listed, g => g.Id == created.Id).Name);
    }

    // AC2 — create
    [Fact]
    public async Task CreateGroup_BlankName_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/conversation-groups", new ChatGroupNameRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // AC2 — create
    [Fact]
    public async Task CreateGroup_TrimsAndTruncatesTheName()
    {
        var created = await CreateGroupAsync("  " + new string('g', 80) + "  ");

        // Same cap and same ellipsis as a conversation title, because they share one helper — the point
        // of NOT introducing a second constant holding the same literal.
        Assert.Equal(new string('g', 60) + "…", created.Name);
    }

    // AC3 — the cap exists so a scripted client cannot grow the state item without end (ADR 0004).
    // 409 rather than 400: the request is well-formed, it conflicts with the state.
    //
    // Driven under its OWN principal, which is load-bearing rather than tidy: the conversation index is
    // keyed by user id, the class shares one cluster (ADR 0055), and xUnit does not promise an order — so
    // filling the default principal's 50 slots here would make every other CreateGroup in this file fail
    // with a 409 depending on which test happened to run first.
    [Fact]
    public async Task CreateGroup_BeyondTheCap_Returns409()
    {
        const string capUser = "chat-group-cap-user";

        HttpResponseMessage? last = null;
        // Drives until refused rather than asserting an exact count, so the cap can move without this
        // test having to know the number.
        for (var i = 0; i <= 60; i++)
        {
            last = await PostGroupAsAsync(capUser, $"cap-{i}");
            if (last.StatusCode == HttpStatusCode.Conflict)
            {
                break;
            }
        }

        Assert.Equal(HttpStatusCode.Conflict, last!.StatusCode);

        // ...and the refusal is the cap, not a broken endpoint: the groups it did accept are all there.
        var listed = (await SendAsAsync(capUser, HttpMethod.Get, "/api/conversation-groups"))!;
        Assert.NotEmpty(await listed.Content.ReadFromJsonAsync<List<ChatGroup>>() ?? []);
    }

    // AC4
    [Fact]
    public async Task RenameGroup_ChangesTheName()
    {
        var created = await CreateGroupAsync("Before");

        var response = await Client.PutAsJsonAsync(
            $"/api/conversation-groups/{created.Id}", new ChatGroupNameRequest("After"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("After", Assert.Single(await GetGroupsAsync(), g => g.Id == created.Id).Name);
    }

    // AC2 — rename
    [Fact]
    public async Task RenameGroup_BlankName_Returns400()
    {
        var created = await CreateGroupAsync("Keeps its name");

        var response = await Client.PutAsJsonAsync(
            $"/api/conversation-groups/{created.Id}", new ChatGroupNameRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Keeps its name", Assert.Single(await GetGroupsAsync(), g => g.Id == created.Id).Name);
    }

    // AC2 — rename
    [Fact]
    public async Task RenameGroup_TruncatesTheName()
    {
        var created = await CreateGroupAsync("Short");

        await Client.PutAsJsonAsync(
            $"/api/conversation-groups/{created.Id}", new ChatGroupNameRequest(new string('r', 80)));

        Assert.Equal(new string('r', 60) + "…", Assert.Single(await GetGroupsAsync(), g => g.Id == created.Id).Name);
    }

    // AC4 — an unknown id must be indistinguishable from an unchanged one (the SetPinnedAsync rule):
    // a 404 here would disclose whether a given group id exists on some other user's account.
    [Fact]
    public async Task RenameGroup_UnknownId_Returns204()
    {
        var response = await Client.PutAsJsonAsync(
            "/api/conversation-groups/group-does-not-exist", new ChatGroupNameRequest("Nowhere"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(await GetGroupsAsync(), g => g.Id == "group-does-not-exist");
    }

    // AC4 — renaming to the name it already has earns no write. Observable only as success plus an
    // unchanged listing, but it is the discipline every mutator in this grain follows.
    [Fact]
    public async Task RenameGroup_UnchangedName_IsANoOp()
    {
        var created = await CreateGroupAsync("Same");

        var response = await Client.PutAsJsonAsync(
            $"/api/conversation-groups/{created.Id}", new ChatGroupNameRequest("Same"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(created, Assert.Single(await GetGroupsAsync(), g => g.Id == created.Id));
    }

    // AC5 — the headline safety property: deleting a GROUP must never delete a CONVERSATION.
    [Fact]
    public async Task DeleteGroup_UngroupsItsConversations_AndNeverDeletesThem()
    {
        const string conversationId = "grp-delete-conv";
        var created = await CreateGroupAsync("Doomed");
        await StartConversationAsync(conversationId, "a filed chat");
        await SetGroupAsync(conversationId, created.Id);

        var response = await Client.DeleteAsync($"/api/conversation-groups/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(await GetGroupsAsync(), g => g.Id == created.Id);

        // Still listed, and now ungrouped — it falls back into the ordinary recency walk.
        var conversation = Assert.Single(await GetConversationsAsync(), c => c.Id == conversationId);
        Assert.Null(conversation.GroupId);
    }

    // AC5
    [Fact]
    public async Task DeleteGroup_UnknownId_Returns204()
    {
        var response = await Client.DeleteAsync("/api/conversation-groups/group-does-not-exist");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // AC6
    [Fact]
    public async Task AssignConversation_SetsGroupId_AndNullClearsIt()
    {
        const string conversationId = "grp-assign-conv";
        var created = await CreateGroupAsync("Filing");
        await StartConversationAsync(conversationId, "a chat to file");

        Assert.Equal(HttpStatusCode.NoContent, (await SetGroupAsync(conversationId, created.Id)).StatusCode);
        Assert.Equal(created.Id, Assert.Single(await GetConversationsAsync(), c => c.Id == conversationId).GroupId);

        Assert.Equal(HttpStatusCode.NoContent, (await SetGroupAsync(conversationId, null)).StatusCode);
        Assert.Null(Assert.Single(await GetConversationsAsync(), c => c.Id == conversationId).GroupId);
    }

    // AC7 — an id naming no group is a NO-OP, deliberately not a silent clear. Clearing is what null
    // means; conflating the two lets a client working from a stale group list wipe a filing the user
    // still has.
    [Fact]
    public async Task AssignConversation_UnknownGroup_IsANoOp()
    {
        const string conversationId = "grp-unknown-target-conv";
        var created = await CreateGroupAsync("Survives");
        await StartConversationAsync(conversationId, "a chat that keeps its filing");
        await SetGroupAsync(conversationId, created.Id);

        var response = await SetGroupAsync(conversationId, "group-does-not-exist");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(created.Id, Assert.Single(await GetConversationsAsync(), c => c.Id == conversationId).GroupId);
    }

    // AC8
    [Fact]
    public async Task AssignConversation_UnknownConversation_Returns204()
    {
        var created = await CreateGroupAsync("Empty target");

        var response = await SetGroupAsync("conversation-does-not-exist", created.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(await GetConversationsAsync(), c => c.Id == "conversation-does-not-exist");
    }

    // AC9
    [Fact]
    public async Task ReorderGroups_AppliesThePositionalOrder()
    {
        var first = await CreateGroupAsync("order-first");
        var second = await CreateGroupAsync("order-second");

        var response = await Client.PutAsJsonAsync(
            "/api/conversation-group-order", new ReorderChatGroupsRequest([second.Id, first.Id]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Relative, not absolute: sibling tests add groups to the same shared listing.
        var listed = await GetGroupsAsync();
        Assert.True(listed.FindIndex(g => g.Id == second.Id) < listed.FindIndex(g => g.Id == first.Id));
    }

    // AC9 — the property that makes this safe against a stale client: an id the client never heard of
    // must not be dropped from the sidebar just because the client did not name it.
    [Fact]
    public async Task ReorderGroups_IgnoresUnknownIds_AndKeepsUnlistedGroups()
    {
        var named = await CreateGroupAsync("order-named");
        var omitted = await CreateGroupAsync("order-omitted");

        var response = await Client.PutAsJsonAsync(
            "/api/conversation-group-order", new ReorderChatGroupsRequest([named.Id, "group-does-not-exist"]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var listed = await GetGroupsAsync();
        Assert.DoesNotContain(listed, g => g.Id == "group-does-not-exist");
        // Still present, and after the one id the caller actually named.
        Assert.Contains(listed, g => g.Id == omitted.Id);
        Assert.True(listed.FindIndex(g => g.Id == named.Id) < listed.FindIndex(g => g.Id == omitted.Id));
    }

    // AC10 — the regression ADR 0045 established for the pin flag, now for the filing. TouchAsync rewrites
    // the entry with a `with` expression, which carries GroupId forward for free; a later refactor to an
    // explicit `new ConversationInfo(...)` would silently unfile every chat on its next turn, and nothing
    // else in the suite would see it.
    [Fact]
    public async Task TouchAsync_PreservesTheGroupAssignment()
    {
        const string conversationId = "grp-touch-conv";
        var created = await CreateGroupAsync("Sticky");
        await StartConversationAsync(conversationId, "first turn");
        await SetGroupAsync(conversationId, created.Id);

        // A second turn on the same conversation takes the TouchAsync update path.
        await StartConversationAsync(conversationId, "second turn");

        Assert.Equal(created.Id, Assert.Single(await GetConversationsAsync(), c => c.Id == conversationId).GroupId);
    }

    /* ————— helpers ————— */

    // Easy Auth's principal-id header is what CurrentUser.ResolveUserId reads, and the conversation
    // index grain is keyed by exactly that — so setting it scopes a request to its own user.
    private Task<HttpResponseMessage> PostGroupAsAsync(string userId, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/conversation-groups")
        {
            Content = JsonContent.Create(new ChatGroupNameRequest(name)),
        };
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", userId);
        return Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsAsync(string userId, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", userId);
        return Client.SendAsync(request);
    }

    private async Task<ChatGroup> CreateGroupAsync(string name)
    {
        var response = await Client.PostAsJsonAsync("/api/conversation-groups", new ChatGroupNameRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatGroup>())!;
    }

    private async Task<List<ChatGroup>> GetGroupsAsync()
        => (await Client.GetFromJsonAsync<List<ChatGroup>>("/api/conversation-groups"))!;

    private async Task<List<ConversationInfo>> GetConversationsAsync()
        => (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!;

    private Task<HttpResponseMessage> SetGroupAsync(string conversationId, string? groupId)
        => Client.PutAsJsonAsync(
            $"/api/conversations/{conversationId}/group", new SetConversationGroupRequest(groupId));

    private async Task StartConversationAsync(string conversationId, string prompt)
    {
        using var chat = await Client.PostAsJsonAsync($"/api/chat/{conversationId}", new ChatRequest(prompt, null));
        chat.EnsureSuccessStatusCode();
        await chat.Content.ReadAsStringAsync();
    }
}
