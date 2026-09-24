using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agent42.Tests;

/// <summary>
/// Pins how a reasoning model's "thinking" reaches us on each wire protocol (ADR 0071), against the exact
/// clients <c>ChatClientFactory</c> builds. Nothing here talks to a network; the transport is a stub.
///
/// <para>These properties were measured during the plan's Step-1 spike and are the foundation the whole
/// feature rests on: if reasoning never surfaced as a <see cref="TextReasoningContent"/>, there would be
/// nothing for <c>ProjectStreamedContent</c> to project.</para>
/// </summary>
public class ReasoningStreamTests
{
    private const string ReasoningChunk =
        """{"id":"c","object":"chat.completion.chunk","created":0,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Let me think about this."}}]}""";

    private const string ContentChunk =
        """{"id":"c","object":"chat.completion.chunk","created":0,"model":"m","choices":[{"index":0,"delta":{"content":"hi"}}]}""";

    /// <summary>
    /// AC1. A DeepSeek-style <c>delta.reasoning_content</c> must arrive as a
    /// <see cref="TextReasoningContent"/> on the pinned client stack (<c>OpenAI</c> +
    /// <c>Microsoft.Extensions.AI.OpenAI</c>; versions in <c>Directory.Packages.props</c>).
    ///
    /// <para>This is the affirmative twin of <c>StreamingEmptyChoicesTests</c>, and the distinction matters:
    /// that test's <c>found=False</c> was measured against an EMPTY-choices chunk, where absence is correct.
    /// It says nothing about a chunk that really carries reasoning, which is what this one pins.</para>
    /// </summary>
    [Fact]
    public async Task Reasoning_SurfacesFromAChatCompletionsStream()
    {
        var reasoning = new List<string>();
        var text = "";

        await foreach (var update in ChatCompletionsClient(ReasoningChunk, ContentChunk)
            .GetStreamingResponseAsync("hi"))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent r: reasoning.Add(r.Text); break;
                    case TextContent t: text += t.Text; break;
                }
            }
        }

        Assert.Equal("Let me think about this.", Assert.Single(reasoning));
        // The reply is a SEPARATE content part — reasoning must not be concatenated into it.
        Assert.Equal("hi", text);
    }

    /// <summary>
    /// The Responses path (ADR 0014's Foundry transport) surfaces a reasoning SUMMARY the same way, and —
    /// the Step-1(b) finding that decided the design — it does so with
    /// <c>includeReasoningEncryptedContent</c> left <b>false</b>.
    ///
    /// <para>That flag was the suspected mechanism and is not one: it only adds
    /// <c>include:["reasoning.encrypted_content"]</c>, which exists to carry encrypted reasoning ITEMS
    /// across turns. Setting it true would push reasoning into our own session (ADR 0004's 2 MB item),
    /// against the live-only decision, and buys nothing here. Both values are asserted so a future change
    /// to the default cannot quietly make visible reasoning depend on it.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reasoning_SurfacesOnTheResponsesPath_RegardlessOfEncryptedContent(bool includeEncrypted)
    {
        var handler = ResponsesHandler();
        var reasoning = new List<string>();
        var text = "";

        await foreach (var update in ResponsesChatClient(handler, includeEncrypted)
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], SummaryReasoning()))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent r: reasoning.Add(r.Text); break;
                    case TextContent t: text += t.Text; break;
                }
            }
        }

        Assert.Equal("Let me think about this.", Assert.Single(reasoning));
        Assert.Equal("hi", text);

        // The flag's ONLY effect, pinned so the claim above stays falsifiable.
        var body = handler.Recorded[0].Body;
        Assert.Equal(includeEncrypted, body.Contains("reasoning.encrypted_content", StringComparison.Ordinal));
    }

    /// <summary>
    /// What actually turns reasoning ON at the provider: <see cref="ReasoningOutput.Summary"/> on
    /// <see cref="ChatOptions.Reasoning"/> becomes a <c>reasoning.summary</c> field in the request body.
    /// A reasoning model emits no summary unless asked, so without this the feature would be invisible in
    /// production however well the client rendered it.
    /// </summary>
    [Fact]
    public async Task ReasoningSummary_IsRequested_WhenReasoningOutputIsSummary()
    {
        var handler = ResponsesHandler();

        await foreach (var unused in ResponsesChatClient(handler, includeEncrypted: false)
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], SummaryReasoning()))
        {
            // Drained for the request it produces.
        }

        var request = JsonSerializer.Deserialize<JsonElement>(handler.Recorded[0].Body);
        Assert.True(request.TryGetProperty("reasoning", out var reasoning),
            "A summary-requesting ChatOptions.Reasoning should produce a `reasoning` request field.");
        Assert.False(string.IsNullOrEmpty(reasoning.GetProperty("summary").GetString()));

        // Stateless stays mandatory (ADR 0014) — asking for reasoning must not have relaxed it.
        Assert.False(request.GetProperty("store").GetBoolean());
    }

    /// <summary>
    /// The negative control for the test above: with no reasoning asked for, no <c>reasoning</c> field is
    /// sent. Without this, a body that always carried the field would satisfy the assertion above and the
    /// per-model gate could be wired to a constant without any test noticing.
    /// </summary>
    [Fact]
    public async Task NoReasoningField_IsSent_WhenReasoningIsNotRequested()
    {
        var handler = ResponsesHandler();

        await foreach (var unused in ResponsesChatClient(handler, includeEncrypted: false)
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            // Drained for the request it produces.
        }

        var request = JsonSerializer.Deserialize<JsonElement>(handler.Recorded[0].Body);
        Assert.False(request.TryGetProperty("reasoning", out _));
    }

    /// <summary>Asking the provider for a reasoning summary, the way <c>Provider.Foundry</c> does.</summary>
    private static ChatOptions SummaryReasoning() =>
        new() { Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary } };

    /// <summary>An <see cref="IChatClient"/> over a stub transport replaying the chunks as SSE.</summary>
    private static IChatClient ChatCompletionsClient(params string[] chunks)
    {
        var body = string.Concat(chunks.Select(c => "data: " + c + "\n\n")) + "data: [DONE]\n\n";

        return new ChatClient("m", new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(Sse(body))),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        }).AsIChatClient();
    }

    /// <summary>The Responses client built exactly as <c>Provider.Foundry</c>'s Responses branch builds it.</summary>
    private static IChatClient ResponsesChatClient(RecordingHttpHandler handler, bool includeEncrypted)
    {
#pragma warning disable OPENAI001, MAAI001 // The experimental Responses surface is what is under test.
        return new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/openai/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        }).GetResponsesClient().AsIChatClientWithStoredOutputDisabled(
            "m", includeReasoningEncryptedContent: includeEncrypted);
#pragma warning restore OPENAI001, MAAI001
    }

    private static RecordingHttpHandler ResponsesHandler() => Sse(ReasoningResponsesStream);

    private static RecordingHttpHandler Sse(string body) => new()
    {
        Responder = _ =>
        {
            var content = new StringContent(body.Replace("\r\n", "\n"));
            content.Headers.ContentType = new("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        },
    };

    /// <summary>A Responses stream carrying a reasoning summary item followed by the message text.</summary>
    private const string ReasoningResponsesStream = """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp_1","object":"response","created_at":0,"status":"in_progress","model":"m","output":[]}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[]}}

        event: response.reasoning_summary_part.added
        data: {"type":"response.reasoning_summary_part.added","item_id":"rs_1","output_index":0,"summary_index":0,"part":{"type":"summary_text","text":""}}

        event: response.reasoning_summary_text.delta
        data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"Let me think about this."}

        event: response.reasoning_summary_text.done
        data: {"type":"response.reasoning_summary_text.done","item_id":"rs_1","output_index":0,"summary_index":0,"text":"Let me think about this."}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"Let me think about this."}]}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":1,"item":{"id":"msg_1","type":"message","role":"assistant","status":"in_progress","content":[]}}

        event: response.content_part.added
        data: {"type":"response.content_part.added","item_id":"msg_1","output_index":1,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":1,"content_index":0,"delta":"hi"}

        event: response.output_text.done
        data: {"type":"response.output_text.done","item_id":"msg_1","output_index":1,"content_index":0,"text":"hi"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":1,"item":{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hi","annotations":[]}]}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp_1","object":"response","created_at":0,"status":"completed","model":"m","output":[{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"Let me think about this."}]},{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hi","annotations":[]}]}]}}


        """;
}
