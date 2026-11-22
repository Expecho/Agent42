using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Agent42.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agent42.Tests;

/// <summary>
/// gpt-6.2-sol answered with output tokens billed and an empty reply: its Responses stream carried the answer only in the final
/// <c>response.completed</c> event, which the adapter reads for usage but not for text. Run over the exact client
/// <c>Provider.Foundry</c> builds, on a stub transport, so the shapes are the measured ones.
/// </summary>
public class ResponsesCompletedTextFallbackTests
{
    private const string Created = """
        event: response.created
        data: {"type":"response.created","response":{"id":"r","object":"response","created_at":0,"status":"in_progress","model":"m","output":[]}}


        """;

    private const string Msg = """{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello there","annotations":[]}]}""";

    private const string Completed = "event: response.completed\ndata: "
        + """{"type":"response.completed","response":{"id":"r","object":"response","created_at":0,"status":"completed","model":"m","output":[""" + Msg
        + """],"usage":{"input_tokens":5,"output_tokens":11,"total_tokens":16,"input_tokens_details":{"cached_tokens":0},"output_tokens_details":{"reasoning_tokens":0}}}}""" + "\n\n";

    private const string ItemDone = "event: response.output_item.done\ndata: "
        + """{"type":"response.output_item.done","output_index":0,"item":""" + Msg + "}\n\n";

    private const string Delta = """
        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"Hello there"}


        """;

    private static async Task<(string Text, int Usage)> DrainAsync(string body, bool wrapped)
    {
        var handler = new RecordingHttpHandler
        {
            Responder = _ =>
            {
                var content = new StringContent(body.Replace("\r\n", "\n"));
                content.Headers.ContentType = new("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
        };
#pragma warning disable OPENAI001, MAAI001 // The experimental Responses surface is what is under test.
        var adapter = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/openai/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        }).GetResponsesClient().AsIChatClientWithStoredOutputDisabled("m", includeReasoningEncryptedContent: false);
#pragma warning restore OPENAI001, MAAI001
        IChatClient client = wrapped ? new ResponsesCompletedTextFallbackChatClient(adapter) : adapter;

        var text = "";
        var usage = 0;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            text += string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
            usage += update.Contents.OfType<UsageContent>().Count();
        }

        return (text, usage);
    }

    // The measured defect: without the wrapper the answer is lost and only usage is left.
    [Fact]
    public async Task WithoutTheWrapper_ACompletedOnlyStream_LosesTheAnswer()
        => Assert.Equal(("", 1), await DrainAsync(Created + Completed, wrapped: false));

    [Theory]
    [InlineData("completed only")]
    [InlineData("item done, no deltas")]
    public async Task ACompletedOnlyStream_YieldsTheAnswer_AndStillReportsUsage(string shape)
    {
        var body = shape == "completed only" ? Created + Completed : Created + ItemDone + Completed;

        Assert.Equal(("Hello there", 1), await DrainAsync(body, wrapped: true));
    }

    // A model that streams deltas today must see no change: the text is never doubled by the completed event.
    [Fact]
    public async Task AStreamThatDidStreamText_IsPassedThroughUntouched()
        => Assert.Equal(("Hello there", 1), await DrainAsync(Created + Delta + ItemDone + Completed, wrapped: true));
}
