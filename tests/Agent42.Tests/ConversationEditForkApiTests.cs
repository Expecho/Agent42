using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The edit/regenerate flag on <c>POST /api/chat/{id}</c> and the fork route (ADR 0092): the status codes the UI
/// acts on, the per-user key, and what a fork is listed as.
/// </summary>
public sealed class ConversationEditForkApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private HttpClient Client => Fixture.Client;

    // AC17: a stale token answers 409 with a JSON error and no NDJSON stream, and nothing is removed.
    [Fact]
    public async Task ReplaceLastTurn_Stale_Is409()
    {
        const string user = "erf-api-stale";
        await ChatAsync(user, "conv", "original question");

        var response = await ChatAsync(user, "conv", "edited", replace: true, count: 5, hash: Hash("original question"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Stale", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());
        Assert.Equal(["original question"], await UserTextsAsync(user, "conv"));
    }

    // AC17: a replace without both token parts is malformed.
    [Fact]
    public async Task ReplaceLastTurn_WithoutCount_Is400()
    {
        const string user = "erf-api-400";
        await ChatAsync(user, "conv", "original");

        Assert.Equal(HttpStatusCode.BadRequest, (await ChatAsync(user, "conv", "x", replace: true, hash: Hash("original"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChatAsync(user, "conv", "x", replace: true, count: 1)).StatusCode);
        Assert.Equal(["original"], await UserTextsAsync(user, "conv"));
    }

    // The happy path through the endpoint: the turn is replaced and the reply streams as usual.
    [Fact]
    public async Task ReplaceLastTurn_StreamsTheReplacementTurn()
    {
        const string user = "erf-api-edit";
        await ChatAsync(user, "conv", "first");
        await ChatAsync(user, "conv", "typo here");

        var response = await ChatAsync(user, "conv", "fixed here", replace: true, count: 2, hash: Hash("typo here"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["first", "fixed here"], await UserTextsAsync(user, "conv"));
    }

    // Another user's conversation id names that user's OWN (empty) grain, so the token is stale and nothing of the
    // owner's is touched.
    [Fact]
    public async Task ReplaceLastTurn_CannotReachAnotherUsersConversation()
    {
        await ChatAsync("erf-api-owner", "conv", "mine");

        var response = await ChatAsync("erf-api-intruder", "conv", "theirs", replace: true, count: 1, hash: Hash("mine"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["mine"], await UserTextsAsync("erf-api-owner", "conv"));
    }

    // AC15: an unlisted source — incognito, unknown, or another user's — is a 404.
    [Fact]
    public async Task Fork_UnlistedSource_Is404()
    {
        await ChatAsync("erf-api-404", "incognito", "secret", ephemeral: true);
        Assert.Equal(HttpStatusCode.NotFound, (await ForkAsync("erf-api-404", "incognito", 0, 1, Hash("secret"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ForkAsync("erf-api-404", "nope", 0, 0, Hash(""))).StatusCode);

        await ChatAsync("erf-api-404-owner", "conv", "owned");
        Assert.Equal(HttpStatusCode.NotFound, (await ForkAsync("erf-api-404", "conv", 0, 1, Hash("owned"))).StatusCode);
    }

    // AC15: a stale token is a 409 and creates nothing.
    [Fact]
    public async Task Fork_StaleCount_Is409()
    {
        const string user = "erf-api-fork-stale";
        await ChatAsync(user, "conv", "hello");

        var response = await ForkAsync(user, "conv", 0, 2, Hash("hello"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(await ListAsync(user));
    }

    // AC16: the fork is listed with the suffixed title, the source's group and agent label, and unpinned; it is a
    // separate conversation holding the chosen prefix.
    [Fact]
    public async Task Fork_IsListedInTheSourcesGroup()
    {
        const string user = "erf-api-fork-listed";
        var index = Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(user);
        await index.TouchAsync("conv", "Trip planning", "agent-x", "Xavier");
        await ChatAsync(user, "conv", "first leg");
        await ChatAsync(user, "conv", "second leg");
        var group = (await index.CreateGroupAsync("Travel"))!;
        await index.SetGroupAsync("conv", group.Id);
        await index.SetPinnedAsync("conv", true);

        var response = await ForkAsync(user, "conv", 0, 2, Hash("second leg"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var forkId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetString()!;
        Assert.True(Guid.TryParse(forkId, out _));
        var fork = Assert.Single(await ListAsync(user), c => c.Id == forkId);
        Assert.Equal(
            ("Trip planning (fork)", group.Id, "agent-x", "Xavier", false),
            (fork.Title, fork.GroupId, fork.AgentId, fork.AgentName, fork.Pinned));
        Assert.Equal(["first leg"], await UserTextsAsync(user, forkId));
        Assert.Equal(["first leg", "second leg"], await UserTextsAsync(user, "conv"));
    }

    // AC16: a title already at the index cap, or ending in "…", keeps its suffix.
    [Theory]
    [InlineData(80, "")]
    [InlineData(60, "")]
    [InlineData(30, "…")]
    public async Task ForkTitle_KeepsSuffixForLongTitles(int length, string ending)
    {
        var user = $"erf-api-fork-title-{length}";
        var title = new string('t', length) + ending;
        await Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(user).TouchAsync("conv", title);
        await ChatAsync(user, "conv", "x");

        var response = await ForkAsync(user, "conv", 0, 1, Hash("x"));

        var forkId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetString()!;
        var listed = Assert.Single(await ListAsync(user), c => c.Id == forkId).Title;
        Assert.EndsWith(" (fork)", listed, StringComparison.Ordinal);
        Assert.True(listed.Length <= 60, listed);
    }

    // AC9: a fork's user image, generated image and artifact are served under the FORK's id through the same
    // proxies the UI uses, and the copies are byte-identical.
    [Fact]
    public async Task Fork_AttachmentsAndArtifacts_AreServedUnderTheForkId()
    {
        const string user = "erf-api-fork-blobs";
        var upload = new MultipartFormDataContent();
        var image = new ByteArrayContent([137, 80, 78, 71, 1, 2, 3]);
        image.Headers.ContentType = new("image/png");
        upload.Add(image, "file", "cat.png");
        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "/api/conversations/conv/attachments") { Content = upload };
        uploadRequest.Headers.Add(IdHeader, user);
        var imageId = (await (await Client.SendAsync(uploadRequest)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        await ChatAsync(user, "conv", "look", attachmentIds: [imageId]);
        await ChatAsync(user, "conv", FakeChatClient.CallToolPrompt(
            "create_artifact", new { kind = "markdown", title = "Notes", content = "# forked notes" }));
        await ChatAsync(user, "conv", "draw a cat", model: TestSiloConfigurator.ImageModel);
        var history = await HistoryAsync(user, "conv");
        var generatedId = history[^1].GetProperty("attachments")[0].GetProperty("id").GetString()!;
        var artifact = (await GetJsonAsync(user, "/api/conversations/conv/artifacts"))[0];
        var artifactId = artifact.GetProperty("id").GetString()!;

        var response = await ForkAsync(user, "conv", 2, 3, Hash("draw a cat"));
        var forkId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetString()!;

        Assert.Equal(
            await GetBytesAsync(user, $"/api/conversations/conv/attachments/{imageId}"),
            await GetBytesAsync(user, $"/api/conversations/{forkId}/attachments/{imageId}"));
        Assert.NotEmpty(await GetBytesAsync(user, $"/api/conversations/{forkId}/attachments/{generatedId}"));
        var content = await GetBytesAsync(user, $"/api/conversations/{forkId}/artifacts/{artifactId}/versions/1/content");
        Assert.Contains("# forked notes", Encoding.UTF8.GetString(content), StringComparison.Ordinal);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> ChatAsync(
        string user, string conversation, string message, bool replace = false, int? count = null, string? hash = null,
        bool ephemeral = false, string[]? attachmentIds = null, string? model = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/{conversation}")
        {
            Content = JsonContent.Create(new
            {
                message,
                model,
                attachmentIds,
                ephemeral,
                replaceLastTurn = replace,
                expectedUserTurnCount = count,
                expectedLastUserTextHash = hash,
            }),
        };
        request.Headers.Add(IdHeader, user);
        var response = await Client.SendAsync(request);
        await response.Content.ReadAsStringAsync();
        return response;
    }

    private Task<HttpResponseMessage> ForkAsync(string user, string conversation, int through, int count, string hash)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversation}/fork")
        {
            Content = JsonContent.Create(new
            {
                throughUserTurn = through,
                expectedUserTurnCount = count,
                expectedLastUserTextHash = hash,
            }),
        };
        request.Headers.Add(IdHeader, user);
        return Client.SendAsync(request);
    }

    private async Task<List<JsonElement>> HistoryAsync(string user, string conversation)
        => [.. (await GetJsonAsync(user, $"/api/conversations/{conversation}/messages")).EnumerateArray()];

    private async Task<List<string>> UserTextsAsync(string user, string conversation)
        => [.. (await HistoryAsync(user, conversation))
            .Where(t => t.GetProperty("role").GetString() == "user")
            .Select(t => t.GetProperty("text").GetString()!)];

    private Task<IReadOnlyList<ConversationInfo>> ListAsync(string user)
        => Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(user).GetConversationsAsync();

    private async Task<JsonElement> GetJsonAsync(string user, string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(IdHeader, user);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<byte[]> GetBytesAsync(string user, string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(IdHeader, user);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
