using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Agent42.Tests;

/// <summary>
/// Guards against the OpenAI 2.13.0 empty-<c>choices</c> regression, which held <c>OpenAI</c> at 2.12.0
/// until 2.14.0 passed these tests. Nothing checks this at restore time now, so this test is the only
/// guard when <c>OpenAI</c> is bumped.
///
/// <para>A streaming chunk may legitimately carry an <b>empty <c>choices</c> array</b>: Azure sends one
/// as the leading <c>prompt_filter_results</c> chunk, and another as the trailing usage-only chunk.
/// <c>Microsoft.Extensions.AI.OpenAI</c> probes <c>"$.choices[0].delta.reasoning_content"</c> on
/// <b>every</b> streaming update (it exists to surface DeepSeek-style <c>reasoning_content</c>), so
/// anything that makes that probe throw kills the whole stream rather than one chunk.</para>
///
/// <para>OpenAI 2.13.0 made it throw: <c>StreamingChatCompletionUpdate.PropagateGet</c> indexes
/// <c>Choices[index]</c> with no bounds check, and 2.13.0 routes the read through that indexer instead
/// of resolving it from stored JSON. Measured against the two versions with everything else held equal:
/// 2.12.0 returns <c>found=False</c> and the stream completes; 2.13.0 throws
/// <c>ArgumentOutOfRangeException</c> out of <c>List&lt;T&gt;.get_Item</c>.</para>
///
/// <para>This bites the <b>Chat Completions</b> path only — <c>Provider.OpenAICompatible</c>, and Foundry
/// when <c>UseResponsesApi</c> is false (ADR 0013/0014/0018). The Responses path builds a different
/// client and never reaches this code, which is why prod Foundry did not see it.</para>
///
/// <para>Nothing here talks to a network; the transport is a stub handler.</para>
/// </summary>
public class StreamingEmptyChoicesTests
{
    /// <summary>The probe path, UTF-8 — <c>JsonPatch</c> takes a byte span, so this cannot be a const.</summary>
    private static ReadOnlySpan<byte> ReasoningProbePath => "$.choices[0].delta.reasoning_content"u8;

    private const string EmptyChoicesChunk =
        """{"id":"c","object":"chat.completion.chunk","created":0,"model":"m","choices":[]}""";

    private const string ContentChunk =
        """{"id":"c","object":"chat.completion.chunk","created":0,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"}}]}""";

    /// <summary>
    /// The two frames at the top of the production stack trace, in isolation: the reasoning probe
    /// against a chunk whose <c>choices</c> array is empty. It must report "absent", not throw.
    /// </summary>
    [Fact]
    public void ReasoningProbe_OnAnEmptyChoicesChunk_ReportsAbsentRatherThanThrowing()
    {
#pragma warning disable OPENAI001, SCME0001 // JsonPatch is the very surface under test.
        var update = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(
            BinaryData.FromString(EmptyChoicesChunk))!;

        var found = update.Patch.TryGetValue(ReasoningProbePath, out string? reasoning);
#pragma warning restore OPENAI001, SCME0001

        Assert.False(found);
        Assert.Null(reasoning);
    }

    /// <summary>
    /// The whole failure end to end: an empty-choices chunk ahead of a normal one, drained through the
    /// same <c>AsIChatClient()</c> adapter <c>ChatClientFactory</c> builds. The reply must survive.
    /// </summary>
    [Fact]
    public async Task Streaming_SurvivesAnEmptyChoicesChunkAheadOfTheReply()
    {
        var text = "";

        await foreach (var update in StreamingClient(EmptyChoicesChunk, ContentChunk)
            .GetStreamingResponseAsync("hi"))
        {
            text += update.Text;
        }

        Assert.Equal("hi", text);
    }

    /// <summary>
    /// The same drain with the empty chunk trailing, which is the shape Azure sends when usage is
    /// requested — the reply is already complete, so a throw there loses an answer that fully arrived.
    /// </summary>
    [Fact]
    public async Task Streaming_SurvivesAnEmptyChoicesChunkAfterTheReply()
    {
        var text = "";

        await foreach (var update in StreamingClient(ContentChunk, EmptyChoicesChunk)
            .GetStreamingResponseAsync("hi"))
        {
            text += update.Text;
        }

        Assert.Equal("hi", text);
    }

    /// <summary>
    /// An <see cref="IChatClient"/> over a stub transport that replays <paramref name="chunks"/> as SSE.
    /// Retries are off so a failure surfaces as itself rather than as three of itself.
    /// </summary>
    private static IChatClient StreamingClient(params string[] chunks)
    {
        var body = string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";
        var handler = new RecordingHttpHandler
        {
            Responder = _ =>
            {
                var content = new StringContent(body);
                content.Headers.ContentType = new("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
        };

        return new ChatClient("m", new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        }).AsIChatClient();
    }
}
