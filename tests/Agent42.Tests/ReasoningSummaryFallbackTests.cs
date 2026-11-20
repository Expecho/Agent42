using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Agent42.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agent42.Tests;

/// <summary>
/// A model that rejects <c>reasoning.summary</c> (gpt-6.2-astra) must cost one retry, not a broken chat, and must be
/// remembered so it is one retry per model, not per turn. Run against the exact Responses client
/// <c>Provider.Foundry</c> builds, over a stub transport, so both failure shapes are the measured ones: an HTTP 400
/// (<see cref="ClientResultException"/>) and a Responses <c>error</c> event (an <see cref="ErrorContent"/> update).
/// </summary>
public class ReasoningSummaryFallbackTests : IDisposable
{
    private const string Rejection =
        "Unsupported parameter: 'reasoning.summary' is not supported with the 'm' model";

    private const string OkStream = """
        event: response.created
        data: {"type":"response.created","response":{"id":"r","object":"response","created_at":0,"status":"in_progress","model":"m","output":[]}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","status":"in_progress","content":[]}}

        event: response.content_part.added
        data: {"type":"response.content_part.added","item_id":"msg_1","output_index":0,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"hi"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hi","annotations":[]}]}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"r","object":"response","created_at":0,"status":"completed","model":"m","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hi","annotations":[]}]}]}}


        """;

    private readonly string _key = ReasoningSummaryFallbackChatClient.KeyFor(
        ProviderKind.AzureOpenAI, "https://example.invalid/", "m-" + Guid.NewGuid());

    public void Dispose() => ReasoningSummaryFallbackChatClient.ForgetAll();

    [Fact]
    public async Task Http400OnTheSummary_IsRetriedWithoutIt_AndRemembered()
    {
        var handler = Handler(rejectWith: Reject400);
        var client = Wrap(handler);

        Assert.Equal("hi", await DrainTextAsync(client, Summary()));

        Assert.Equal(2, handler.Recorded.Count);
        Assert.True(HasSummary(handler.Recorded[0].Body), "the first attempt asks for the summary");
        Assert.False(HasSummary(handler.Recorded[1].Body), "the retry must not");

        // The next turn goes straight to the plain request: ONE call, not two.
        Assert.Equal("hi", await DrainTextAsync(client, Summary()));
        Assert.Equal(3, handler.Recorded.Count);
        Assert.False(HasSummary(handler.Recorded[2].Body));
        Assert.True(ReasoningSummaryFallbackChatClient.IsRemembered(_key));
    }

    [Fact]
    public async Task ErrorEventOnTheSummary_IsRetriedWithoutIt()
    {
        var handler = Handler(rejectWith: RejectAsErrorEvent);

        Assert.Equal("hi", await DrainTextAsync(Wrap(handler), Summary()));

        Assert.Equal(2, handler.Recorded.Count);
        Assert.False(HasSummary(handler.Recorded[1].Body));
    }

    [Fact]
    public async Task NonStreamingCall_IsRetriedToo()
    {
        var handler = Handler(rejectWith: Reject400, streamBody: false);

        var response = await Wrap(handler).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], Summary());

        Assert.Equal("hi", response.Text);
        Assert.Equal(2, handler.Recorded.Count);
        Assert.False(HasSummary(handler.Recorded[1].Body));
    }

    [Fact]
    public async Task TheEffortTheCallerSet_SurvivesTheRetry()
    {
        var handler = Handler(rejectWith: Reject400);
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High, Output = ReasoningOutput.Summary },
        };

        await DrainTextAsync(Wrap(handler), options);

        var retry = JsonSerializer.Deserialize<JsonElement>(handler.Recorded[1].Body);
        Assert.Equal("high", retry.GetProperty("reasoning").GetProperty("effort").GetString());
        // The caller's options are never mutated: the factory shares one instance across calls.
        Assert.Equal(ReasoningOutput.Summary, options.Reasoning!.Output);
    }

    [Fact]
    public async Task AnyOtherFailure_PropagatesAndRemembersNothing()
    {
        var handler = Handler(rejectWith: _ => Json400("Invalid value for 'input': nope.", param: "input", code: "invalid_value"));
        var client = Wrap(handler);

        await Assert.ThrowsAsync<ClientResultException>(() => DrainTextAsync(client, Summary()));

        Assert.Single(handler.Recorded);
        Assert.False(ReasoningSummaryFallbackChatClient.IsRemembered(_key));
    }

    [Fact]
    public async Task ACallThatNeverAsksForASummary_IsPassedThroughUntouched()
    {
        var handler = Handler(rejectWith: null);

        Assert.Equal("hi", await DrainTextAsync(Wrap(handler), options: null));

        Assert.Single(handler.Recorded);
    }

    [Fact]
    public void OnlyTheSummaryRefusal_IsRecognised()
    {
        Assert.True(ReasoningSummaryFallbackChatClient.IsSummaryRejection(Rejection));
        Assert.False(ReasoningSummaryFallbackChatClient.IsSummaryRejection("Unsupported parameter: 'temperature'"));
        Assert.False(ReasoningSummaryFallbackChatClient.IsSummaryRejection("rate limited"));
        Assert.False(ReasoningSummaryFallbackChatClient.IsSummaryRejection(null));
    }

    private IChatClient Wrap(RecordingHttpHandler handler) =>
#pragma warning disable OPENAI001, MAAI001 // The experimental Responses surface is what is under test.
        new ReasoningSummaryFallbackChatClient(
            new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
            {
                Endpoint = new Uri("https://example.invalid/openai/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            }).GetResponsesClient().AsIChatClientWithStoredOutputDisabled("m", includeReasoningEncryptedContent: false),
            _key);
#pragma warning restore OPENAI001, MAAI001

    private static ChatOptions Summary() => new() { Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary } };

    private static bool HasSummary(string body) =>
        JsonSerializer.Deserialize<JsonElement>(body) is { } json
        && json.TryGetProperty("reasoning", out var reasoning)
        && reasoning.TryGetProperty("summary", out _);

    private static async Task<string> DrainTextAsync(IChatClient client, ChatOptions? options)
    {
        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
            text += string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
        }

        return text;
    }

    private static HttpResponseMessage Json400(string message, string param = "reasoning.summary", string code = "unsupported_parameter") => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { error = new { message, type = "invalid_request_error", param, code } }),
            System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Reject400(RecordingHttpHandler.Call _) => Json400(Rejection);

    private static HttpResponseMessage RejectAsErrorEvent(RecordingHttpHandler.Call _)
    {
        var body = """
            event: response.created
            data: {"type":"response.created","response":{"id":"r","object":"response","created_at":0,"status":"in_progress","model":"m","output":[]}}

            event: error
            data: {"type":"error","code":"unsupported_parameter","message":"Unsupported parameter: 'reasoning.summary' is not supported with the 'm' model","param":"reasoning.summary","sequence_number":1}


            """;
        return Sse(body);
    }

    private static HttpResponseMessage Sse(string body)
    {
        var content = new StringContent(body.Replace("\r\n", "\n"));
        content.Headers.ContentType = new("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // Rejects any request that carries a reasoning summary, answers the plain one. `rejectWith` null accepts everything.
    private static RecordingHttpHandler Handler(Func<RecordingHttpHandler.Call, HttpResponseMessage>? rejectWith, bool streamBody = true) => new()
    {
        Responder = call =>
        {
            if (rejectWith is not null && HasSummary(call.Body))
            {
                return rejectWith(call);
            }

            if (streamBody)
            {
                return Sse(OkStream);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"r","object":"response","created_at":0,"status":"completed","model":"m","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hi","annotations":[]}]}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        },
    };
}
