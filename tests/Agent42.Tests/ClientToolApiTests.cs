using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The client-tool routes (ADR 0089) end to end: the chat stream carries the call to the browser BEFORE the tool
/// waits, the posted result resumes the turn, and the routes are scoped to the signed-in user.
/// </summary>
public sealed class ClientToolApiTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private static readonly string[] AllClientTools =
    [
        ClientToolLimits.ConfirmAction, ClientToolLimits.ControlArtifactPane, ClientToolLimits.GetBrowserContext,
        ClientToolLimits.GetCurrentLocation,
    ];

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

    // Starts a turn whose model calls confirm_action (or the client tool `prompt` triggers), and reads the NDJSON
    // stream up to the flagged call. The response is returned still open, so the turn is genuinely waiting when the
    // test acts.
    private async Task<(HttpResponseMessage Response, StreamReader Reader, string CallId)> StartConfirmTurnAsync(
        string user, string conversation, string? prompt = null)
    {
        prompt ??= FakeChatClient.CallToolPrompt(ClientToolLimits.ConfirmAction, new { action = "Send the email" });
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

    // AC1 (ADR 0093): get_current_location streams as a client tool, and the fix the browser posts is geocoded
    // into the tool's result. "where am i" is the fake model's location trigger.
    [Fact]
    public async Task Chat_LocationToolCall_StreamsAsClientTool_AndPostedFixGeocodes()
    {
        var (response, reader, callId) = await StartConfirmTurnAsync("ct-api-location", "conv", "where am i");
        using (response)
        using (reader)
        {
            var post = await PostResult("ct-api-location", "conv", callId,
                new { latitude = 52.3676, longitude = 4.9041, accuracyMetres = 12, capturedAt = DateTimeOffset.UtcNow });
            Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);

            var rest = await reader.ReadToEndAsync();
            Assert.Contains(FakeGeocoder.Address, rest, StringComparison.Ordinal);
            Assert.Contains("52.3676", rest, StringComparison.Ordinal);
        }
    }

    // AC5 (ADR 0093): the tool-result POST is now the SSRF input of the geocode call. An out-of-range fix is
    // accepted as a post (the endpoint does not know tool shapes) but never reaches the geocoder.
    [Fact]
    public async Task PostResult_OutOfRangeFix_IsNotGeocoded()
    {
        var (response, reader, callId) = await StartConfirmTurnAsync("ct-api-badfix", "conv", "where am i");
        using (response)
        using (reader)
        {
            await PostResult("ct-api-badfix", "conv", callId, new { latitude = 999, longitude = 4.9041 });

            var rest = await reader.ReadToEndAsync();
            Assert.Contains("could not get a position", rest, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeGeocoder.Address, rest, StringComparison.Ordinal);
            Assert.Equal(0, FakeGeocoder.InvalidCalls);
        }
    }

    // ADR 0095: a browser that advertises mcp_elicitation keeps it through the endpoint's intersection; the NDJSON
    // line carries the request, and the answer rides the same tool-result POST (204, then 404 once it was taken).
    [Fact]
    public async Task AdvertisedMcpElicitation_IsKept_AndTheAnswerIs204ThenLateIs404()
    {
        var router = new McpElicitationRouter();
        var tool = AIFunctionFactory.Create(
            async (string q, CancellationToken cancellationToken) =>
            {
                Task<McpElicitationAnswer> routed;
                using (ExecutionContext.SuppressFlow())
                {
                    routed = Task.Run(() => router.RouteAsync(
                        new McpElicitationRequest(McpElicitationLimits.FormMode, "Pick", """{"type":"object","properties":{}}"""),
                        CancellationToken.None));
                }

                return (await routed.WaitAsync(cancellationToken)).Action;
            },
            "elicit_api_tool",
            "asks");
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<SingleUserMcpToolProvider>().Servers =
                [new McpServerTools("srv", [tool], [tool.Name], ServerName: "Api Server", Elicitation: router)];
        }

        var response = await Client.SendAsync(
            Request(HttpMethod.Post, "/api/chat/elicit", "ct-api-elicit", new ChatRequest(
                FakeChatClient.CallToolPrompt("elicit_api_tool", new { q = "x" }), null,
                ClientTools: [ClientToolLimits.McpElicitation, "not_a_tool"])),
            HttpCompletionOption.ResponseHeadersRead);
        using (response)
        using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync()))
        {
            string? id = null;
            while (id is null && await reader.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.GetProperty("type").GetString() == "mcpElicitation")
                {
                    Assert.Equal("Api Server", doc.RootElement.GetProperty("text").GetString());
                    using var input = JsonDocument.Parse(doc.RootElement.GetProperty("elicitation").GetString()!);
                    id = input.RootElement.GetProperty("id").GetString();
                }
            }

            Assert.NotNull(id);
            Assert.Equal(HttpStatusCode.NoContent, (await PostResult("ct-api-elicit", "elicit", id, new { action = "decline" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PostResult("ct-api-elicit", "elicit", id, new { action = "accept" })).StatusCode);
            Assert.Contains("decline", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
    }

    // A name the server does not define is dropped: advertising only that gets no broker, so a client tool is not
    // flagged for the browser and answers "unavailable" at once.
    [Fact]
    public async Task UnknownCapability_IsDropped()
    {
        var response = await Client.SendAsync(Request(HttpMethod.Post, "/api/chat/unknown-cap", "ct-api-unknown-cap",
            new ChatRequest(FakeChatClient.CallToolPrompt(ClientToolLimits.ConfirmAction, new { action = "x" }), null,
                ClientTools: ["not_a_tool"])));

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"clientTool\":true", body, StringComparison.Ordinal);
        Assert.Contains("unavailable", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostResult_UnknownCall_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostResult("ct-api-unknown", "conv", "no-such-call", new { approved = true })).StatusCode);
    }
}
