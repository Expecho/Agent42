using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The reply streamer (ADR 0111) against a fake session: placeholder then throttled plain edits, a formatted final
/// that falls back to plain, rate limits skipped mid-stream and waited out (briefly) at the end, splitting, the
/// no-edit path, and the event allow-list.
/// </summary>
public class ChatReplyStreamerTests
{
    private static readonly ChatTarget Target = new("111");

    [Fact]
    public async Task Streams_PlaceholderThenPlainEdits_ThenAFormattedFinal()
    {
        var (session, destination) = Setup();
        var buffer = new ChatReplyBuffer();

        var streaming = Streamer().StreamAsync(destination, buffer, CancellationToken.None);
        buffer.Apply(Text("Hello "));
        await Task.Delay(120);
        buffer.Apply(Text("**world**"));
        await Task.Delay(120);
        buffer.Complete();
        await streaming;

        var ops = session.Sent.Where(s => s.Op != "typing").ToList();
        Assert.Equal(("send", "…", ChatTextFormat.Plain), (ops[0].Op, ops[0].Text, ops[0].Format));
        Assert.All(ops.Skip(1).SkipLast(1), op => Assert.Equal((ChatTextFormat.Plain, true), (op.Format, op.Text.EndsWith(" ▍", StringComparison.Ordinal))));
        Assert.Equal(("edit", "Hello **world**", ChatTextFormat.Formatted), (ops[^1].Op, ops[^1].Text, ops[^1].Format));
        Assert.Equal(["Hello **world**"], session.VisibleMessages());
    }

    [Fact]
    public async Task AnUnchangedFrame_IsNotEditedAgain()
    {
        var (session, destination) = Setup();
        var buffer = new ChatReplyBuffer();

        var streaming = Streamer().StreamAsync(destination, buffer, CancellationToken.None);
        buffer.Apply(Text("same"));
        await Task.Delay(400);
        buffer.Complete();
        await streaming;

        Assert.Single(session.Sent, s => s.Op == "edit" && s.Text == "same ▍");
    }

    [Fact]
    public async Task ARejectedFormat_IsResentPlain()
    {
        var (session, destination) = Setup(supportsEdit: false);
        session.ScriptedResults.Enqueue(ChatSendResult.Fail(ChatSendErrorKind.BadFormat));

        await Streamer().StreamAsync(destination, Completed("a *broken"), CancellationToken.None);

        var sends = session.Sent.Where(s => s.Op.StartsWith("send", StringComparison.Ordinal)).ToList();
        Assert.Equal([("send-failed", ChatTextFormat.Formatted), ("send", ChatTextFormat.Plain)], sends.Select(s => (s.Op, s.Format)));
    }

    [Fact]
    public async Task TheFinal_WaitsOutAShortRateLimit_ButNotALongOne()
    {
        var (shortSession, shortDestination) = Setup(supportsEdit: false);
        shortSession.ScriptedResults.Enqueue(ChatSendResult.Fail(ChatSendErrorKind.RateLimited, TimeSpan.FromMilliseconds(20)));
        await Streamer().StreamAsync(shortDestination, Completed("hi"), CancellationToken.None);
        Assert.Equal(["hi"], shortSession.VisibleMessages());

        var (longSession, longDestination) = Setup(supportsEdit: false);
        longSession.ScriptedResults.Enqueue(ChatSendResult.Fail(ChatSendErrorKind.RateLimited, TimeSpan.FromMinutes(97)));
        var started = DateTime.UtcNow;
        await Streamer().StreamAsync(longDestination, Completed("hi"), CancellationToken.None);
        Assert.Empty(longSession.VisibleMessages());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "a 97-minute wait must never be slept");
    }

    [Fact]
    public async Task RateLimitedEdits_AreSkipped_AndThreeInARowStopTheStreaming()
    {
        var (session, destination) = Setup();
        var buffer = new ChatReplyBuffer();
        // The placeholder succeeds; the next three edits are rate limited.
        session.ScriptedResults.Enqueue(ChatSendResult.Ok());
        for (var i = 0; i < 3; i++)
        {
            session.ScriptedResults.Enqueue(ChatSendResult.Fail(ChatSendErrorKind.RateLimited, TimeSpan.FromSeconds(1)));
        }

        var streaming = Streamer().StreamAsync(destination, buffer, CancellationToken.None);
        for (var i = 0; i < 8; i++)
        {
            buffer.Apply(Text($"part {i} "));
            await Task.Delay(70);
        }

        buffer.Complete();
        await streaming;

        Assert.Equal(3, session.Sent.Count(s => s.Op == "edit-failed"));
        // After the third flood no intermediate edit is attempted; only the final.
        Assert.Single(session.Sent, s => s.Op == "edit");
        Assert.EndsWith("part 7", session.VisibleMessages().Single());
    }

    [Fact]
    public async Task AnOverLongReply_EditsThePlaceholder_ThenSendsTheRest()
    {
        var (session, destination) = Setup(maxLength: 100);
        var text = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));

        await Streamer().StreamAsync(destination, Completed(text), CancellationToken.None);

        var visible = session.VisibleMessages();
        Assert.True(visible.Count > 1);
        Assert.All(visible, m => Assert.True(m.Length <= 100));
        Assert.EndsWith($"(1/{visible.Count})", visible[0]);
    }

    [Fact]
    public async Task APlatformWithoutEdits_GetsTheFinalOnly()
    {
        var (session, destination) = Setup(supportsEdit: false);
        var buffer = new ChatReplyBuffer();

        var streaming = Streamer().StreamAsync(destination, buffer, CancellationToken.None);
        buffer.Apply(Text("all at once"));
        await Task.Delay(100);
        buffer.Complete();
        await streaming;

        Assert.Equal(["send"], session.Sent.Where(s => s.Op != "typing").Select(s => s.Op));
        Assert.Contains(session.Sent, s => s.Op == "typing");
    }

    [Fact]
    public async Task TypingStops_BeforeTheFinalSend()
    {
        var (session, destination) = Setup(supportsEdit: false, typingRefresh: TimeSpan.FromMilliseconds(10));
        var buffer = new ChatReplyBuffer();

        var streaming = Streamer().StreamAsync(destination, buffer, CancellationToken.None);
        await Task.Delay(100);
        buffer.Apply(Text("done"));
        buffer.Complete();
        await streaming;
        await Task.Delay(100);

        Assert.Equal("send", session.Sent.Last().Op);
    }

    // A complete reply (a command answer) goes straight out, with no "typing…" first.
    [Fact]
    public async Task ACompleteReply_SendsNoTyping()
    {
        var (session, destination) = Setup(supportsEdit: false, typingRefresh: TimeSpan.FromMilliseconds(10));

        await Streamer().StreamAsync(destination, Completed("Started a new conversation."), CancellationToken.None);

        Assert.Equal(["send"], session.Sent.Select(s => s.Op));
    }

    [Fact]
    public void TheBuffer_KeepsOnlyAllowListedEvents_AndHonoursTextReset()
    {
        var buffer = new ChatReplyBuffer();

        buffer.Apply(Text("draft"));
        buffer.Apply(new AgentStreamEvent(AgentStreamEventType.TextReset));
        buffer.Apply(new AgentStreamEvent(AgentStreamEventType.Reasoning, "secret reasoning"));
        buffer.Apply(new AgentStreamEvent(AgentStreamEventType.ToolCallStart, ToolName: "web_search"));
        buffer.Apply(new AgentStreamEvent(AgentStreamEventType.Todos, "todo"));
        buffer.Apply(Text("final answer"));
        buffer.Apply(new AgentStreamEvent(AgentStreamEventType.Image, AttachmentId: "img-1"));

        Assert.Equal("final answer", buffer.Snapshot().Text);
        Assert.Equal(["img-1"], buffer.ImageIds());
    }

    [Fact]
    public async Task AGeneratedImage_IsMentioned_AndAFailureReplacesTheText()
    {
        var (session, destination) = Setup(supportsEdit: false);
        var withImage = new ChatReplyBuffer();
        withImage.Apply(new AgentStreamEvent(AgentStreamEventType.Image, AttachmentId: "img-1"));
        withImage.Complete();
        await Streamer().StreamAsync(destination, withImage, CancellationToken.None);

        var (failedSession, failedDestination) = Setup(supportsEdit: false);
        var failed = new ChatReplyBuffer();
        failed.Apply(Text("half an ans"));
        failed.Fail("I couldn't answer that.");
        await Streamer().StreamAsync(failedDestination, failed, CancellationToken.None);

        Assert.Contains("An image was generated", session.VisibleMessages().Single());
        Assert.Equal(["I couldn't answer that."], failedSession.VisibleMessages());
    }

    private static ChatReplyStreamer Streamer()
        => new(TimeProvider.System, NullLogger<ChatReplyStreamer>.Instance, TimeSpan.FromSeconds(2));

    private static AgentStreamEvent Text(string text) => new(AgentStreamEventType.TextDelta, text);

    private static ChatReplyBuffer Completed(string text)
    {
        var buffer = new ChatReplyBuffer();
        buffer.Apply(Text(text));
        buffer.Complete();
        return buffer;
    }

    private static (FakeChatSession Session, ChatReplyDestination Destination) Setup(
        bool supportsEdit = true, int maxLength = 4096, TimeSpan? typingRefresh = null)
    {
        var runtime = new ChatConnectionRuntime("u", new ChatConnection { Id = "c" }, new ChatCredentials("t"), _ => Task.CompletedTask);
        var session = new FakeChatSession(runtime, null!);
        var capabilities = new ChatPlatformCapabilities(
            maxLength, 8, supportsEdit, TimeSpan.FromMilliseconds(50), typingRefresh ?? TimeSpan.FromSeconds(4));
        return (session, new ChatReplyDestination(() => session, Target, capabilities));
    }
}
