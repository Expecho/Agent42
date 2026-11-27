using Agent42.Backend;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// <see cref="AnthropicCompatibilityChatClient"/> (ADR 0103): each rewrite against a recording inner client, then the
/// whole thing through the real SDK on the stub transport, with the shim's <c>RawRepresentationFactory</c> active.
/// That factory is the riskiest SDK assumption (a <c>MessageCreateParams</c> overrides <c>ChatOptions</c>), so the wire
/// test pins every field it could swallow.
/// </summary>
public class AnthropicCompatibilityChatClientTests
{
    private static readonly AIFunction GetTime = AIFunctionFactory.Create(() => "12:00", "get_time");

    [Theory]
    [InlineData("any")]
    [InlineData("specific")]
    public async Task RequiredToolMode_BecomesAuto(string mode)
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync("hi", new ChatOptions
        {
            ToolMode = mode == "any" ? ChatToolMode.RequireAny : ChatToolMode.RequireSpecific("get_time"),
        });

        Assert.Same(ChatToolMode.Auto, inner.Options!.ToolMode);
    }

    [Fact]
    public async Task NoneToolMode_IsLeftAlone()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync("hi", new ChatOptions { ToolMode = ChatToolMode.None });

        Assert.Same(ChatToolMode.None, inner.Options!.ToolMode);
    }

    [Fact]
    public async Task SamplingParams_AreDropped()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync("hi", new ChatOptions { Temperature = 0.2f, TopP = 0.9f, TopK = 40 });

        Assert.Null(inner.Options!.Temperature);
        Assert.Null(inner.Options.TopP);
        Assert.Null(inner.Options.TopK);
    }

    [Fact]
    public async Task EffortNone_BecomesLow_KeepingOutput()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync("hi", new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None, Output = ReasoningOutput.None },
        });

        Assert.Equal(ReasoningEffort.Low, inner.Options!.Reasoning!.Effort);
        Assert.Equal(ReasoningOutput.None, inner.Options.Reasoning.Output);
    }

    // ADR 0071's "show thinking" flag rides Reasoning.Output; the shim must never touch it.
    [Fact]
    public async Task ReasoningOutput_PassesThrough()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync("hi", new ChatOptions
        {
            Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary },
        });

        Assert.Equal(ReasoningOutput.Summary, inner.Options!.Reasoning!.Output);
        Assert.Null(inner.Options.Reasoning.Effort);
    }

    [Fact]
    public async Task TrailingAssistant_GetsContinue()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi"), new ChatMessage(ChatRole.Assistant, "Here is")]);

        var last = inner.Messages![^1];
        Assert.Equal(ChatRole.User, last.Role);
        Assert.Equal(AnthropicCompatibilityChatClient.ContinuePrompt, last.Text);
        Assert.Equal(3, inner.Messages.Count);
    }

    // A pending tool round: appending a user turn there would break the tool_use/tool_result pairing.
    [Fact]
    public async Task TrailingAssistantToolCall_GetsNoContinue()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "hi"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("toolu_1", "get_time")]),
        ]);

        Assert.Equal(2, inner.Messages!.Count);
        Assert.Equal(ChatRole.Assistant, inner.Messages[^1].Role);
    }

    [Fact]
    public async Task TrailingUser_GetsNoContinue()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Single(inner.Messages!);
    }

    [Fact]
    public async Task MessagesTheSdkWouldEmpty_AreDropped()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "hi"),
            new ChatMessage(ChatRole.Assistant, "   "),
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "audio/wav")]),
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
        ]);

        Assert.Equal(["hi", ""], inner.Messages!.Select(m => m.Text));
        Assert.IsType<DataContent>(Assert.Single(inner.Messages![1].Contents));
    }

    // The dropped message is the one that ended the request: the check sees the assistant turn now last on the wire.
    [Fact]
    public async Task DroppedTrailingUser_ExposesAssistant_WhichGetsContinue()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "hi"),
            new ChatMessage(ChatRole.Assistant, "Hello"),
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1 }, "audio/wav")]),
        ]);

        Assert.Equal(["hi", "Hello", AnthropicCompatibilityChatClient.ContinuePrompt], inner.Messages!.Select(m => m.Text));
    }

    // An orphaned tool_result is a 400, so a message carrying tool content survives however blank its text is.
    [Fact]
    public async Task ToolContentMessage_IsNeverDropped()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "hi"),
            new ChatMessage(ChatRole.Assistant, [new TextContent(" "), new FunctionCallContent("toolu_1", "get_time")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("toolu_1", "")]),
        ]);

        Assert.Equal(3, inner.Messages!.Count);
    }

    [Fact]
    public async Task Shim_DoesNotMutateCallerState()
    {
        var (shim, _) = Shim();
        var reasoning = new ReasoningOptions { Effort = ReasoningEffort.None, Output = ReasoningOutput.Summary };
        var options = new ChatOptions
        {
            ToolMode = ChatToolMode.RequireAny,
            Temperature = 0.5f,
            Reasoning = reasoning,
            Tools = [GetTime],
        };
        var assistant = new ChatMessage(ChatRole.Assistant, "Here is");
        var blank = new ChatMessage(ChatRole.Assistant, " ");
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "hi"), assistant, blank];

        await shim.GetResponseAsync(messages, options);

        Assert.Same(ChatToolMode.RequireAny, options.ToolMode);
        Assert.Equal(0.5f, options.Temperature);
        Assert.Same(reasoning, options.Reasoning);
        Assert.Equal(ReasoningEffort.None, reasoning.Effort);
        Assert.Null(options.RawRepresentationFactory);
        Assert.Equal(3, messages.Count);
        Assert.Same(assistant, messages[1]);
        Assert.Same(blank, messages[2]);
        Assert.Single(assistant.Contents);
    }

    [Fact]
    public async Task StreamingPath_IsRewrittenToo()
    {
        var (shim, inner) = Shim();

        await foreach (var unused in shim.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.Assistant, "Here is")],
            new ChatOptions { ToolMode = ChatToolMode.RequireAny, Temperature = 1f }))
        {
            // Drained for the call it produces.
        }

        Assert.Same(ChatToolMode.Auto, inner.Options!.ToolMode);
        Assert.Null(inner.Options.Temperature);
        Assert.Equal(AnthropicCompatibilityChatClient.ContinuePrompt, inner.Messages![^1].Text);
    }

    [Fact]
    public async Task ShimFactory_CarriesModelMaxTokensAndCaching()
    {
        var (shim, inner) = Shim();

        await shim.GetResponseAsync("hi", new ChatOptions { MaxOutputTokens = 400 });

        var raw = Assert.IsType<MessageCreateParams>(inner.Options!.RawRepresentationFactory!(shim));
        Assert.Equal("claude-test", raw.Model.Raw());
        Assert.Equal(400, raw.MaxTokens);
        Assert.NotNull(raw.CacheControl);
    }

    // A caller's own params keep every field it set; only the absent cache_control is filled in.
    [Fact]
    public async Task CallerFactory_KeepsItsParams_GainsCaching()
    {
        var (shim, inner) = Shim();
        var callerParams = new MessageCreateParams { Model = "caller-model", MaxTokens = 7, Messages = [] };

        await shim.GetResponseAsync("hi", new ChatOptions { RawRepresentationFactory = _ => callerParams });

        var raw = Assert.IsType<MessageCreateParams>(inner.Options!.RawRepresentationFactory!(shim));
        Assert.Equal("caller-model", raw.Model.Raw());
        Assert.Equal(7, raw.MaxTokens);
        Assert.NotNull(raw.CacheControl);
        Assert.Null(callerParams.CacheControl);
    }

    [Fact]
    public async Task ForeignFactory_PassesThrough()
    {
        var (shim, inner) = Shim();
        var foreign = new object();

        await shim.GetResponseAsync("hi", new ChatOptions { RawRepresentationFactory = _ => foreign });

        Assert.Same(foreign, inner.Options!.RawRepresentationFactory!(shim));
    }

    // A fleet agent's YAML model hint is the QUALIFIED catalog id and ends up in ChatOptions.ModelId; Anthropic 404s on
    // 'Claude:claude-test', so the client's own bare model id must be what goes on the wire.
    [Fact]
    public async Task Wire_QualifiedModelIdInOptions_SendsTheBareModel()
    {
        var handler = AnthropicStub.Sequence(() => RecordingHttpHandler.Json(AnthropicStub.JsonText));

        await WireClient(handler).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "Claude:claude-test" });

        Assert.Equal("claude-test", AnthropicStub.Body(handler, 0).GetProperty("model").GetString());
    }

    // AC11: through the real SDK with the shim's factory active, nothing the factory could swallow is lost.
    [Fact]
    public async Task Wire_UnderFactory_KeepsEveryField()
    {
        var handler = AnthropicStub.Sequence(() => AnthropicStub.Sse(AnthropicStub.StreamedText));

        await foreach (var unused in WireClient(handler).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions
            {
                Instructions = "You are Agent 42.",
                Tools = [GetTime],
                ToolMode = ChatToolMode.RequireAny,
                Temperature = 0.3f,
                Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary },
            }))
        {
            // Drained for the request it produces.
        }

        var body = AnthropicStub.Body(handler, 0);
        Assert.Equal("claude-test", body.GetProperty("model").GetString());
        Assert.Equal(AnthropicLimits.DefaultMaxOutputTokens, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.True(!body.TryGetProperty("tool_choice", out var choice)
            || choice.GetProperty("type").GetString() == "auto");
        Assert.Equal("get_time", body.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Contains("You are Agent 42.", body.GetProperty("system").GetRawText(), StringComparison.Ordinal);
        Assert.Equal("summarized", body.GetProperty("thinking").GetProperty("display").GetString());
    }

    // The shim's drop rule mirrors the SDK. This pins the SDK half: an audio-only message reaches the wire as no
    // message at all, and a trailing assistant (with the shim) is followed by the Continue turn.
    [Fact]
    public async Task Wire_EmptiedMessageIsSkipped_AndTrailingAssistantContinues()
    {
        var handler = AnthropicStub.Sequence(() => RecordingHttpHandler.Json(AnthropicStub.JsonText));

        await WireClient(handler).GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "hi"),
            new ChatMessage(ChatRole.Assistant, "Hello"),
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1 }, "audio/wav")]),
        ]);

        var roles = AnthropicStub.Body(handler, 0).GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToList();
        Assert.Equal(["user", "assistant", "user"], roles);
        Assert.Contains(AnthropicCompatibilityChatClient.ContinuePrompt, handler.Recorded[0].Body, StringComparison.Ordinal);
    }

    private static IChatClient WireClient(RecordingHttpHandler handler) => new AnthropicCompatibilityChatClient(
        new AnthropicClient
        {
            ApiKey = "test-key",
            AuthToken = null,
            BaseUrl = "https://anthropic.stub",
            HttpClient = new HttpClient(handler),
        }.AsIChatClient("claude-test", AnthropicLimits.DefaultMaxOutputTokens),
        "claude-test");

    private static (AnthropicCompatibilityChatClient Shim, RecordingChatClient Inner) Shim()
    {
        var inner = new RecordingChatClient();
        return (new AnthropicCompatibilityChatClient(inner, "claude-test"), inner);
    }
}
