using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agent42.Tests;

/// <summary>
/// Guards the <c>OpenAI</c> × <c>Microsoft.Extensions.AI.OpenAI</c> pairing on the <b>Responses</b> path — how
/// prod Foundry runs (ADR 0014) — whenever a turn carries tools, which is nearly every turn.
///
/// <para><c>Microsoft.Extensions.AI.OpenAI</c> 10.10.0 against <c>OpenAI</c> 2.14.0 threw
/// <c>TypeLoadException: Could not load type 'OpenAI.Responses.GlobalMcpToolCallApprovalPolicy'</c> out of
/// <c>OpenAIResponsesChatClient.ToResponseTool</c> — before any byte hit the wire, for ANY tool, because the
/// JIT resolves the whole method. 10.10.1 fixes it. Every other adapter test here sends no tools, so none of
/// them could see it; <c>LiveModelChatTests</c> found it against a real model.</para>
///
/// <para>Nothing here talks to a network; the transport is a stub.</para>
/// </summary>
public class ResponsesToolMappingTests
{
    [Fact]
    public async Task ResponsesRequest_WithFunctionAndHostedTools_ReachesTheWire()
    {
        var handler = new RecordingHttpHandler
        {
            Responder = _ =>
            {
                var content = new StringContent(CompletedStream.Replace("\r\n", "\n"));
                content.Headers.ContentType = new("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
        };
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "sunny", "get_weather"), new HostedWebSearchTool()],
        };

        var text = "";
        await foreach (var update in ResponsesChatClient(handler)
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
            text += update.Text;
        }

        Assert.Equal("hi", text);
        var tools = JsonSerializer.Deserialize<JsonElement>(Assert.Single(handler.Recorded).Body).GetProperty("tools");
        var types = tools.EnumerateArray().Select(t => t.GetProperty("type").GetString()).ToList();
        Assert.Contains("function", types);
        Assert.Contains(types, t => t!.StartsWith("web_search", StringComparison.Ordinal));
    }

    /// <summary>The Responses client built exactly as <c>Provider.Foundry</c>'s Responses branch builds it.</summary>
    private static IChatClient ResponsesChatClient(RecordingHttpHandler handler)
    {
#pragma warning disable OPENAI001, MAAI001 // The experimental Responses surface is what is under test.
        return new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/openai/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        }).GetResponsesClient().AsIChatClientWithStoredOutputDisabled("m", includeReasoningEncryptedContent: false);
#pragma warning restore OPENAI001, MAAI001
    }

    private const string CompletedStream = """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp_1","object":"response","created_at":0,"status":"in_progress","model":"m","output":[]}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"hi"}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp_1","object":"response","created_at":0,"status":"completed","model":"m","output":[]}}

        """;
}
