using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The client-tool routes (ADR 0089) end to end: the chat stream carries the call to the browser BEFORE the tool
/// waits, the posted result resumes the turn, and the routes are scoped to the signed-in user.
/// </summary>
public sealed class ClientToolApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private static readonly string[] AllClientTools =
        [ClientToolLimits.ConfirmAction, ClientToolLimits.ControlArtifactPane, ClientToolLimits.GetBrowserContext];

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

    private Task<HttpResponseMessage> PostResult(string user, string conversation, string callId, object result)
        => Client.SendAsync(Request(HttpMethod.Post, $"/api/chat/{conversation}/tool-result", user,
            new { toolCallId = callId, result }));

    // Starts a turn whose model calls confirm_action, and reads the NDJSON stream up to the flagged call. The
    // response is returned still open, so the turn is genuinely waiting when the test acts.
    private async Task<(HttpResponseMessage Response, StreamReader Reader, string CallId)> StartConfirmTurnAsync(
        string user, string conversation)
    {
        var prompt = FakeChatClient.CallToolPrompt(ClientToolLimits.ConfirmAction, new { action = "Send the email" });
        var response = await Client.SendAsync(
            Request(HttpMethod.Post, $"/api/chat/{conversation}", user, new ChatRequest(prompt, null, ClientTools: AllClientTools)),
            HttpCompletionOption.ResponseHeadersRead);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        while (await reader.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("clientTool", out var flag) && flag.GetBoolean())
            {
                return (response, reader, doc.RootElement.GetProperty("toolCallId").GetString()!);
            }
        }

        Assert.Fail("The stream ended without a client tool call.");
        return default;
    }

    // AC1 + AC16b: the flagged line is READ from the HTTP stream while the turn waits, the POST answers it, and the
    // rest of the stream carries the model's use of that answer. The ordering is proven, not assumed.
    [Fact]
    public async Task Stream_DeliversTheClientToolCallBeforeWaiting_ThenResumesOnPost()
    {
        var (response, reader, callId) = await StartConfirmTurnAsync("ct-api-user", "stream");
        using (response)
        using (reader)
        {
            var post = await PostResult("ct-api-user", "stream", callId, new { approved = true });
            Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);

            var rest = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement).ToList();
            var text = string.Concat(rest
                .Where(e => e.GetProperty("type").GetString() == "textDelta")
                .Select(e => e.GetProperty("text").GetString()));
            Assert.Contains("\"approved\":true", text, StringComparison.Ordinal);
            Assert.Equal("done", rest[^1].GetProperty("type").GetString());
        }
    }

    // AC5: a browser that lost its stream finds the waiting call, with what it needs to answer it.
    [Fact]
    public async Task PendingToolCalls_ListsTheWaitingCall()
    {
        var (response, reader, callId) = await StartConfirmTurnAsync("ct-api-pending", "conv");
        using (response)
        using (reader)
        {
            var pending = await (await Client.SendAsync(Request(HttpMethod.Get, "/api/chat/conv/pending-tool-calls", "ct-api-pending")))
                .Content.ReadFromJsonAsync<List<PendingClientToolCall>>(JsonSerializerOptions.Web);

            var call = Assert.Single(pending!);
            Assert.Equal((callId, ClientToolLimits.ConfirmAction), (call.ToolCallId, call.ToolName));
            Assert.Contains("Send the email", call.ToolInput, StringComparison.Ordinal);

            await PostResult("ct-api-pending", "conv", callId, new { approved = false });
            await reader.ReadToEndAsync();
        }
    }

    // AC8: the key is per user, so another user cannot answer (or list) someone else's waiting call.
    [Fact]
    public async Task PostResult_OtherUsersConversation_Is404()
    {
        var (response, reader, callId) = await StartConfirmTurnAsync("ct-api-owner", "conv");
        using (response)
        using (reader)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await PostResult("ct-api-intruder", "conv", callId, new { approved = true })).StatusCode);
            var listed = await (await Client.SendAsync(Request(HttpMethod.Get, "/api/chat/conv/pending-tool-calls", "ct-api-intruder")))
                .Content.ReadFromJsonAsync<List<PendingClientToolCall>>(JsonSerializerOptions.Web);
            Assert.Empty(listed!);

            await PostResult("ct-api-owner", "conv", callId, new { approved = true });
            await reader.ReadToEndAsync();
        }
    }

    // AC16c: once the wait has timed out, a late answer is a 404, which the browser swallows.
    [Fact]
    public async Task PostResult_AfterTimeout_Is404()
    {
        var (response, reader, callId) = await StartConfirmTurnAsync("ct-api-late", "conv");
        using (response)
        using (reader)
        {
            Assert.Contains("timeout", await reader.ReadToEndAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, (await PostResult("ct-api-late", "conv", callId, new { approved = true })).StatusCode);
        }
    }

    // AC8: a body over the cap is refused before it is read whole; a malformed one is a 400.
    [Fact]
    public async Task PostResult_Oversized_Is413_AndMalformedIs400()
    {
        var huge = new string('x', ClientToolLimits.MaxResultRequestBytes + 1);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await PostResult("ct-api-size", "conv", "call", huge)).StatusCode);

        var malformed = Request(HttpMethod.Post, "/api/chat/conv/tool-result", "ct-api-size");
        malformed.Content = new StringContent("""{"result":1}""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.SendAsync(malformed)).StatusCode);
    }

    [Fact]
    public async Task PostResult_UnknownCall_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostResult("ct-api-unknown", "conv", "no-such-call", new { approved = true })).StatusCode);
    }
}
