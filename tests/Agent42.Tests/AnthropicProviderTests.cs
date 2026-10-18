using Agent42.Backend;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// Pins the Anthropic SDK's <see cref="IChatClient"/> on the stub transport (ADR 0102). Nothing here talks to a
/// network.
///
/// <para>The signature tests are the plan's Step-1 gate. Within one turn, <see cref="FunctionInvokingChatClient"/>
/// re-sends the assistant message (thinking + <c>tool_use</c>) on the next request, and Claude rejects a thinking
/// block whose signature is missing or altered. While streaming, the SDK delivers the signature as a SEPARATE
/// signature-only <see cref="TextReasoningContent"/>, so whether M.E.AI's coalescing keeps it attached is what is
/// pinned here.</para>
/// </summary>
public class AnthropicProviderTests
{
    private static readonly AIFunction GetTime = AIFunctionFactory.Create(() => "12:00", "get_time");

    [Fact]
    public async Task StreamedToolLoop_ResendsThinkingSignature()
    {
        var handler = AnthropicStub.Sequence(
            () => AnthropicStub.Sse(AnthropicStub.StreamedThinkingThenToolUse),
            () => AnthropicStub.Sse(AnthropicStub.StreamedText));

        await foreach (var unused in ToolLoopClient(handler).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "What time is it?")], new ChatOptions { Tools = [GetTime] }))
        {
            // Drained for the requests it produces.
        }

        AssertSecondRequestReplaysThinking(handler);
    }

    [Fact]
    public async Task NonStreamedToolLoop_ResendsThinkingSignature()
    {
        var handler = AnthropicStub.Sequence(
            () => RecordingHttpHandler.Json(AnthropicStub.JsonThinkingThenToolUse),
            () => RecordingHttpHandler.Json(AnthropicStub.JsonText));

        var response = await ToolLoopClient(handler).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "What time is it?")], new ChatOptions { Tools = [GetTime] });

        Assert.Equal("It is noon.", response.Text);
        AssertSecondRequestReplaysThinking(handler);
    }

    /// <summary>
    /// Decision 3: the default 32 000 <c>max_tokens</c> must not be refused client-side on the non-streaming path
    /// (some Anthropic SDKs refuse a non-streaming request whose budget implies a long run).
    /// </summary>
    [Fact]
    public async Task NonStreaming_MaxTokens32k_IsSent()
    {
        var handler = AnthropicStub.Sequence(() => RecordingHttpHandler.Json(AnthropicStub.JsonText));

        var response = await SdkClient(handler).AsIChatClient("claude-test", 32_000).GetResponseAsync("hi");

        Assert.Equal("It is noon.", response.Text);
        Assert.Equal(32_000, AnthropicStub.Body(handler, 0).GetProperty("max_tokens").GetInt32());
    }

    [Fact] // AC7: the SDK's 1024 fallback would truncate replies; the provider always sends the 32 000 default.
    public async Task AnthropicRequest_DefaultsMaxTokens()
    {
        var handler = AnthropicStub.Sequence(() => AnthropicStub.Sse(AnthropicStub.StreamedText));

        await foreach (var unused in Factory(handler).GetClient("claude:claude-test").GetStreamingResponseAsync("hi"))
        {
            // Drained for the request it produces.
        }

        var body = AnthropicStub.Body(handler, 0);
        Assert.Equal(AnthropicLimits.DefaultMaxOutputTokens, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("claude-test", body.GetProperty("model").GetString());
    }

    // AC8: a declared reasoning model gets ChatClientFactory.Build's Reasoning.Output = Summary (ADR 0071), which the
    // shim must pass through to the SDK as thinking.display = "summarized" so the user sees Claude's thinking.
    [Theory]
    [InlineData(true, "summarized")]
    [InlineData(false, null)]
    public async Task AnthropicReasoningModel_RequestsSummarizedThinking(bool supportsReasoning, string? display)
    {
        var handler = AnthropicStub.Sequence(() => AnthropicStub.Sse(AnthropicStub.StreamedText));

        await foreach (var unused in Factory(handler, supportsReasoning).GetClient("claude:claude-test")
            .GetStreamingResponseAsync("hi"))
        {
            // Drained for the request it produces.
        }

        var body = AnthropicStub.Body(handler, 0);
        string? actual = body.TryGetProperty("thinking", out var thinking)
            && thinking.TryGetProperty("display", out var d) ? d.GetString() : null;
        Assert.Equal(display, actual);
    }

    /// <summary>A factory over one stub-transported Anthropic provider, built the way the builder builds it.</summary>
    public static MultiProviderChatClientFactory Factory(RecordingHttpHandler handler, bool supportsReasoning = false)
    {
        var options = new ProviderOptions
        {
            Kind = ProviderKind.Anthropic,
            Endpoint = "https://anthropic.stub",
            ApiKey = "stored-key",
            Models = [new ConfiguredModel { Id = "claude-test", SupportsTools = true, SupportsReasoning = supportsReasoning }],
        };
        var provider = MultiProviderChatClientFactory.Provider.Anthropic(
            options, new Uri(options.Endpoint), options.ApiKey, handler);
        return new MultiProviderChatClientFactory(
            new Dictionary<string, MultiProviderChatClientFactory.Provider> { ["claude"] = provider },
            "claude:claude-test", "test", NullLogger<MultiProviderChatClientFactory>.Instance);
    }

    private static void AssertSecondRequestReplaysThinking(RecordingHttpHandler handler)
    {
        Assert.Equal(2, handler.Recorded.Count);

        var assistant = AnthropicStub.Body(handler, 1).GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "assistant");
        var blocks = assistant.GetProperty("content").EnumerateArray().ToList();
        var types = blocks.Select(b => b.GetProperty("type").GetString()).ToList();

        // Exactly one thinking block, carrying the signature intact, immediately before the tool_use.
        Assert.Equal(["thinking", "tool_use"], types);
        Assert.Equal(AnthropicStub.Signature, blocks[0].GetProperty("signature").GetString());
        Assert.Equal("Let me check the time.", blocks[0].GetProperty("thinking").GetString());
    }

    private static IChatClient ToolLoopClient(RecordingHttpHandler handler) =>
        new FunctionInvokingChatClient(SdkClient(handler).AsIChatClient("claude-test", 32_000));

    private static AnthropicClient SdkClient(RecordingHttpHandler handler) => new()
    {
        ApiKey = "test-key",
        AuthToken = null,
        BaseUrl = "https://anthropic.stub",
        HttpClient = new HttpClient(handler),
    };
}
