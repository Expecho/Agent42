using System.Net;
using System.Text.Json;

namespace Agent42.Tests;

/// <summary>
/// Canned Anthropic Messages-API responses for the stub transport (ADR 0103). Nothing here talks to a
/// network: a <see cref="RecordingHttpHandler"/> records each request body and answers the Nth request with
/// the Nth canned response, so a test can drive a whole tool loop and then read what went back on the wire.
/// </summary>
public static class AnthropicStub
{
    public const string Signature = "SIG-abc123";

    /// <summary>A handler that answers request N with <paramref name="responses"/>[N] (the last one repeats).</summary>
    public static RecordingHttpHandler Sequence(params Func<HttpResponseMessage>[] responses)
    {
        var handler = new RecordingHttpHandler();
        handler.Responder = _ => responses[Math.Min(handler.Recorded.Count - 1, responses.Length - 1)]();
        return handler;
    }

    /// <summary>An SSE response; the body uses LF line endings whatever the source file uses.</summary>
    public static HttpResponseMessage Sse(string body)
    {
        var content = new StringContent(body.Replace("\r\n", "\n"));
        content.Headers.ContentType = new("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Streamed: a thinking block (text + a separate signature delta), then a <c>get_time</c> tool call.</summary>
    public const string StreamedThinkingThenToolUse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me check "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"the time."}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"SIG-abc123"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_time","input":{}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":20}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    /// <summary>Streamed: a plain text reply that ends the turn.</summary>
    public const string StreamedText = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","model":"claude-test","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"It is noon."}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    /// <summary>Non-streamed: a thinking block with its signature, then a <c>get_time</c> tool call.</summary>
    public const string JsonThinkingThenToolUse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-test","content":[{"type":"thinking","thinking":"Let me check the time.","signature":"SIG-abc123"},{"type":"tool_use","id":"toolu_1","name":"get_time","input":{}}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":20}}
        """;

    /// <summary>Non-streamed: a plain text reply that ends the turn.</summary>
    public const string JsonText = """
        {"id":"msg_2","type":"message","role":"assistant","model":"claude-test","content":[{"type":"text","text":"It is noon."}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    /// <summary>The recorded request body <paramref name="index"/>, parsed.</summary>
    public static JsonElement Body(RecordingHttpHandler handler, int index) =>
        JsonSerializer.Deserialize<JsonElement>(handler.Recorded[index].Body);
}
