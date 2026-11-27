using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>
/// Slack's event projection (ADR 0111, old C4's filter), against fixtures built from the documented Events API field
/// names (Spike findings (b), 2026-10-21): DMs, channel mentions, threads, file shares and every echo shape.
/// </summary>
public class SlackEventsTests
{
    private const string BotId = "U0BOT42";

    [Fact]
    public void ADirectMessage_IsTheChat_WithTheTeam()
    {
        var message = Parse("""{"team_id":"T1","event":{"type":"message","channel":"D024BE91L","user":"U2147483697","text":"Hello hello can you hear me?","ts":"1355517523.000005","channel_type":"im","team":"T1"}}""");

        Assert.NotNull(message);
        Assert.Equal(("D024BE91L:1355517523.000005", "D024BE91L", ChatType.Direct, "U2147483697", "T1"),
            (message.PlatformMessageId, message.ChatId, message.ChatType, message.SenderId, message.SenderWorkspaceId));
        Assert.Null(message.ThreadId);
        Assert.Equal("Hello hello can you hear me?", message.Text);
        Assert.False(message.SenderIsBot);
    }

    // AC3 + Step 11: an app_mention at the top of a channel becomes a thread on that very message, so the reply
    // threads under it; the mention is stripped, so "<@bot> !new" is a command.
    [Fact]
    public void AChannelMention_IsAThreadOnTheMessage_WithTheMentionStripped()
    {
        var mention = Parse($$$"""{"team_id":"T1","event":{"type":"app_mention","channel":"C1","user":"U1","text":"<@{{{BotId}}}> !new","ts":"1700000000.000100"}}""");
        var plain = Parse($$$"""{"team_id":"T1","event":{"type":"message","channel":"C1","user":"U1","text":"<@{{{BotId}}}|agent42> &lt;b&gt; &amp; more","ts":"1700000000.000200","channel_type":"channel"}}""");

        Assert.Equal((ChatType.Group, "1700000000.000100", true, "!new"), (mention!.ChatType, mention.ThreadId, mention.MentionsBot, mention.Text));
        Assert.Equal((true, "<b> & more"), (plain!.MentionsBot, plain.Text));
    }

    // Both events of one mention collapse onto one dedupe id.
    [Fact]
    public void TheSameMentionAsMessageAndAppMention_HasOneId()
    {
        var asMessage = Parse($$$"""{"event":{"type":"message","channel":"C1","user":"U1","text":"<@{{{BotId}}}> hi","ts":"1.2","channel_type":"channel"}}""");
        var asMention = Parse($$$"""{"event":{"type":"app_mention","channel":"C1","user":"U1","text":"<@{{{BotId}}}> hi","ts":"1.2"}}""");

        Assert.Equal(asMessage!.PlatformMessageId, asMention!.PlatformMessageId);
    }

    [Fact]
    public void AMentionOnlyInTheBlocks_Counts_ButANameSubstringDoesNot()
    {
        var inBlocks = Parse($$$"""{"event":{"type":"message","channel":"C1","user":"U1","text":"hey agent","ts":"1.3","channel_type":"channel","blocks":[{"type":"rich_text","elements":[{"type":"rich_text_section","elements":[{"type":"user","user_id":"{{{BotId}}}"}]}]}]}}""");
        var nameOnly = Parse("""{"event":{"type":"message","channel":"C1","user":"U1","text":"ask @agent42 later","ts":"1.4","channel_type":"channel"}}""");
        var someoneElse = Parse("""{"event":{"type":"message","channel":"C1","user":"U1","text":"<@U0OTHER> hi","ts":"1.5","channel_type":"channel"}}""");

        Assert.True(inBlocks!.MentionsBot);
        Assert.False(nameOnly!.MentionsBot);
        Assert.False(someoneElse!.MentionsBot);
        Assert.Equal("<@U0OTHER> hi", someoneElse.Text);
    }

    // A follow-up in a thread the bot started (or replied in) reaches it without a mention.
    [Fact]
    public void AThreadReply_KeepsTheRoot_AndCountsAsAReplyToTheBot_WhenTheThreadIsTheBots()
    {
        const string Json = """{"event":{"type":"message","channel":"C1","user":"U1","text":"and also","ts":"1.9","thread_ts":"1.1","channel_type":"channel"}}""";

        var tracked = SlackEvents.Parse(JsonDocument.Parse(Json).RootElement, BotId, (channel, thread) => (channel, thread) == ("C1", "1.1"));
        var untracked = Parse(Json);
        var botParent = Parse($$$"""{"event":{"type":"message","channel":"C1","user":"U1","text":"x","ts":"1.9","thread_ts":"1.1","parent_user_id":"{{{BotId}}}","channel_type":"channel"}}""");
        var dmThread = Parse("""{"event":{"type":"message","channel":"D1","user":"U1","text":"x","ts":"2.2","thread_ts":"2.1","channel_type":"im"}}""");

        Assert.Equal(("1.1", true), (tracked!.ThreadId, tracked.RepliesToBot));
        Assert.False(untracked!.RepliesToBot);
        Assert.True(botParent!.RepliesToBot);
        Assert.Equal("2.1", dmThread!.ThreadId);
    }

    // Old C4: a file_share subtype is a user message (rejecting every subtype would drop every file message).
    [Fact]
    public void AFileShare_IsAccepted_WithItsFiles()
    {
        var message = Parse("""{"event":{"type":"message","subtype":"file_share","channel":"D1","user":"U1","text":"look","ts":"3.1","channel_type":"im","files":[{"id":"F1","name":"report.pdf","mimetype":"application/pdf","size":2048,"url_private_download":"https://files.slack.com/files-pri/T1-F1/download/report.pdf"},{"id":"F2","name":"clip.mp4","mimetype":"video/mp4","subtype":"slack_audio","size":10,"url_private":"https://files.slack.com/files-pri/T1-F2/clip.mp4"}]}}""");

        Assert.Equal(2, message!.Files.Count);
        Assert.Equal(new ChatInboundFile("https://files.slack.com/files-pri/T1-F1/download/report.pdf", "report.pdf", "application/pdf", 2048, false), message.Files[0]);
        Assert.True(message.Files[1].IsVoiceNote);
    }

    // AC3: our own edits come back as message_changed, our own sends as messages from our bot user; neither is a turn.
    // Another bot is flagged, so the pipeline drops it as its second line.
    [Theory]
    [InlineData("""{"event":{"type":"message","subtype":"message_changed","channel":"D1","ts":"4.1","channel_type":"im","message":{"user":"U1","text":"x"}}}""")]
    [InlineData("""{"event":{"type":"message","subtype":"bot_message","channel":"D1","bot_id":"B1","text":"x","ts":"4.2","channel_type":"im"}}""")]
    [InlineData("""{"event":{"type":"message","channel":"D1","user":"U0BOT42","bot_id":"B42","text":"my own reply","ts":"4.3","channel_type":"im"}}""")]
    [InlineData("""{"event":{"type":"message","subtype":"message_deleted","channel":"D1","ts":"4.4","channel_type":"im"}}""")]
    [InlineData("""{"event":{"type":"message","subtype":"channel_join","channel":"C1","user":"U1","ts":"4.5","channel_type":"channel"}}""")]
    [InlineData("""{"event":{"type":"reaction_added","user":"U1","item":{"type":"message","channel":"C1","ts":"4.6"}}}""")]
    [InlineData("""{"type":"event_callback"}""")]
    public void NotATurn(string json) => Assert.Null(Parse(json));

    [Fact]
    public void AnotherBotsMessage_IsFlaggedAsABot()
        => Assert.True(Parse("""{"event":{"type":"message","channel":"D1","user":"U0OTHERBOT","bot_id":"B9","text":"x","ts":"5.1","channel_type":"im"}}""")!.SenderIsBot);

    [Theory]
    [InlineData("xoxb-1-2-abc", true, false, false)]
    [InlineData("xapp-1-A1-2-abc", false, true, false)]
    [InlineData("xoxp-1-2-abc", false, false, true)]
    public void TokenShapes(string token, bool bot, bool app, bool user)
        => Assert.Equal((bot, app, user), (SlackEvents.LooksLikeBotToken(token), SlackEvents.LooksLikeAppToken(token), SlackEvents.LooksLikeUserToken(token)));

    // Step 13: the setup guide quotes the manifest from code verbatim, so the scopes and events a user installs can
    // never drift from what the parser above expects. And the manifest subscribes to every event shape it parses.
    [Fact]
    public void ChatAppsGuide_QuotesTheSlackManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        var guide = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "integrations", "chat-apps", "README.md")).ReplaceLineEndings("\n");
        var indented = string.Join('\n', SlackAdapter.AppManifest.ReplaceLineEndings("\n").Split('\n').Select(line => "   " + line));

        Assert.Contains(indented, guide, StringComparison.Ordinal);
        foreach (var required in new[] { "socket_mode_enabled: true", "messages_tab_read_only_enabled: false", "- message.im", "- app_mention", "- im:history", "- chat:write" })
        {
            Assert.Contains(required, SlackAdapter.AppManifest, StringComparison.Ordinal);
        }
    }

    private static ChatInboundMessage? Parse(string json) => SlackEvents.Parse(JsonDocument.Parse(json).RootElement, BotId);
}

/// <summary>Markdown → Slack mrkdwn (ADR 0111): escaping is what keeps model output from ever pinging a channel.</summary>
public class SlackTextTests
{
    [Theory]
    [InlineData("**bold** and *it* and _it2_", "*bold* and _it_ and _it2_")]
    [InlineData("~~gone~~", "~gone~")]
    [InlineData("# Title", "*Title*")]
    [InlineData("see [docs](https://example.com/a?b=1)", "see <https://example.com/a?b=1|docs>")]
    [InlineData("- one\n- two", "• one\n• two")]
    [InlineData("> quoted", "> quoted")]
    [InlineData("a `x<y>` b", "a `x&lt;y&gt;` b")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    public void Converts(string markdown, string mrkdwn) => Assert.Equal(mrkdwn, SlackText.ToMrkdwn(markdown));

    // The safety property: no control sequence survives, so <!channel>, <!here> and <@U…> can't be formed.
    [Theory]
    [InlineData("<!channel> wake up")]
    [InlineData("hey <!here|here>")]
    [InlineData("<@U123> look")]
    [InlineData("**<!everyone>**")]
    public void ControlSequences_AreEscaped(string markdown)
    {
        var formatted = SlackText.ToMrkdwn(markdown);
        var plain = SlackText.Escape(markdown);

        Assert.DoesNotContain("<!", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("<@", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("<", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void AFence_DropsTheLanguage_AndIsNotFormattedInside()
        => Assert.Equal("```\nvar **x** = a < b;\n```".Replace("<", "&lt;"), SlackText.ToMrkdwn("```csharp\nvar **x** = a < b;\n```"));

    [Fact]
    public void NeverThrows_OnGarbage()
    {
        foreach (var garbage in new[] { "**", "```", "[x](", "`", "> ", "\r\n\r\n", "~~a", "# ", "|", "<" })
        {
            Assert.NotNull(SlackText.ToMrkdwn(garbage));
        }
    }
}

/// <summary>
/// The Socket Mode session and the Web API (ADR 0111), against a local Kestrel stand-in for slack.com: a real websocket,
/// real acks, real reconnects.
/// </summary>
public sealed class SlackAdapterTests : IAsyncLifetime
{
    private readonly FakeSlackServer _server = new();
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _server.StartAsync();
        _services = Services(_server);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _server.DisposeAsync();
    }

    private SlackAdapter Adapter => (SlackAdapter)_services.GetServices<IChatPlatformAdapter>().Single();

    [Fact]
    public async Task Validate_ReturnsTheBot_AndChecksBothTokens()
    {
        var identity = await Adapter.ValidateAsync(Credentials, CancellationToken.None);
        Assert.Equal(new ChatBotIdentity("U0BOT42", "agent42", "T1"), identity);
        Assert.Equal(["Bearer xoxb-test", "Bearer xapp-test"], _server.Requests.Select(r => r.Authorization).ToArray());

        _server.Script("auth.test", """{"ok":false,"error":"invalid_auth"}""");
        await Assert.ThrowsAsync<ChatAuthException>(() => Adapter.ValidateAsync(Credentials, CancellationToken.None));
        _server.Script("apps.connections.open", """{"ok":false,"error":"not_allowed_token_type"}""");
        await Assert.ThrowsAsync<ChatAuthException>(() => Adapter.ValidateAsync(Credentials, CancellationToken.None));
        var user = await Assert.ThrowsAsync<ChatAuthException>(() => Adapter.ValidateAsync(new ChatCredentials("xoxp-1", "xapp-1"), CancellationToken.None));
        Assert.Contains("user token", user.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ChatAuthException>(() => Adapter.ValidateAsync(new ChatCredentials("xoxb-1"), CancellationToken.None));
    }

    // AC15: every envelope is acked, the ignored ones (our own echo, a reaction, a slash command, hello has no id)
    // included, and only the owner's message reaches the pipeline. Nothing connects before Start.
    [Fact]
    public async Task Slack_AcksEveryEnvelope()
    {
        var sink = new RecordingSink();
        await using var session = await Adapter.ConnectAsync(Runtime(), sink, CancellationToken.None);
        await Task.Delay(150);
        Assert.Equal(0, _server.Connections);
        session.Start();
        await TestWait.WaitUntilAsync(() => _server.Connections == 1);

        await _server.PushAsync("""{"type":"hello","num_connections":1,"debug_info":{"approximate_connection_time":3600}}""");
        await _server.PushAsync(EventsApi("e1", """{"type":"message","channel":"D1","user":"U1","text":"hi","ts":"1.1","channel_type":"im"}"""));
        await _server.PushAsync(EventsApi("e2", """{"type":"message","subtype":"message_changed","channel":"D1","ts":"1.2","channel_type":"im"}"""));
        await _server.PushAsync(EventsApi("e3", """{"type":"message","channel":"D1","user":"U0BOT42","bot_id":"B42","text":"echo","ts":"1.3","channel_type":"im"}"""));
        await _server.PushAsync("""{"envelope_id":"e4","type":"slash_commands","payload":{},"accepts_response_payload":true}""");
        await _server.PushAsync("""{"envelope_id":"e5","type":"events_api","payload":{"event":{"type":"message","channel":"D1"}}}""");
        await _server.PushAsync("not json");

        await TestWait.WaitUntilAsync(() => _server.Acks.Count >= 5);
        Assert.Equal(["e1", "e2", "e3", "e4", "e5"], _server.Acks.ToArray());
        Assert.Equal("hi", Assert.Single(sink.Messages).Text);
        Assert.False(session.Completion.IsCompleted);
    }

    // AC15 (critic M1): an envelope too large to read is still acked, from the id at its END (Slack's documented order
    // puts payload first), and the session carries on with the next one.
    [Fact]
    public async Task Slack_AcksAnOversizedEnvelope()
    {
        var sink = new RecordingSink();
        await using var session = await Adapter.ConnectAsync(Runtime(), sink, CancellationToken.None);
        session.Start();
        await TestWait.WaitUntilAsync(() => _server.Connections == 1);

        var huge = new string('x', SlackAdapter.MaxEnvelopeBytes + 1024);
        await _server.PushAsync($$$"""{"payload":{"event":{"type":"message","text":"{{{huge}}}"}},"type":"events_api","envelope_id":"big-1"}""");
        await _server.PushAsync(EventsApi("e2", """{"type":"message","channel":"D1","user":"U1","text":"after","ts":"9.1","channel_type":"im"}"""));

        await TestWait.WaitUntilAsync(() => _server.Acks.Count >= 2);
        Assert.Equal(["big-1", "e2"], _server.Acks.ToArray());
        Assert.Equal("after", Assert.Single(sink.Messages).Text);
    }

    // AC15: a disconnect (refresh) envelope opens a fresh socket INSIDE the session, which stays registered, so a reply
    // mid-stream keeps working; the new socket delivers too.
    [Fact]
    public async Task Slack_Disconnect_Reconnects()
    {
        var sink = new RecordingSink();
        await using var session = await Adapter.ConnectAsync(Runtime(), sink, CancellationToken.None);
        session.Start();
        await TestWait.WaitUntilAsync(() => _server.Connections == 1);

        await _server.PushAsync("""{"envelope_id":"d1","type":"disconnect","reason":"refresh_requested","debug_info":{"host":"x"}}""");
        await TestWait.WaitUntilAsync(() => _server.Connections == 2);
        await _server.PushAsync(EventsApi("e1", """{"type":"message","channel":"D1","user":"U1","text":"after","ts":"2.1","channel_type":"im"}"""));

        await TestWait.WaitUntilAsync(() => !sink.Messages.IsEmpty);
        Assert.Equal("after", Assert.Single(sink.Messages).Text);
        Assert.Equal(2, _server.Requests.Count(r => r.Method == "apps.connections.open"));
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task Slack_LinkDisabled_FaultsTheSession_AndADroppedSocketEndsItForTheSupervisor()
    {
        await using var disabled = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        disabled.Start();
        await TestWait.WaitUntilAsync(() => _server.Connections == 1);
        await _server.PushAsync("""{"type":"disconnect","reason":"link_disabled"}""");
        var fault = await Assert.ThrowsAsync<ChatAuthException>(() => disabled.Completion);
        Assert.Contains("Socket Mode was turned off", fault.Message, StringComparison.Ordinal);

        await using var dropped = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        dropped.Start();
        await TestWait.WaitUntilAsync(() => _server.Connections == 2);
        await _server.CloseCurrentAsync();
        var transient = await Assert.ThrowsAnyAsync<Exception>(() => dropped.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotType<ChatAuthException>(transient);
    }

    [Fact]
    public async Task Slack_RejectedAppToken_FaultsTheSession()
    {
        _server.Script("apps.connections.open", """{"ok":false,"error":"invalid_auth"}""");
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        session.Start();

        await Assert.ThrowsAsync<ChatAuthException>(() => session.Completion);
    }

    // Step 11: a reply in a channel threads under the mention (thread_ts), formatted as mrkdwn, and that thread is then
    // tracked so a follow-up there reaches the bot without a mention.
    [Fact]
    public async Task Send_InTheThread_AsMrkdwn_ThenEditsByTs()
    {
        var sink = new RecordingSink();
        await using var session = await Adapter.ConnectAsync(Runtime(), sink, CancellationToken.None);
        session.Start();
        await TestWait.WaitUntilAsync(() => _server.Connections == 1);

        var sent = await session.SendAsync(new ChatTarget("C1", "1.1", "C1:1.1"), "**hi** <!channel>", ChatTextFormat.Formatted, CancellationToken.None);
        var edited = await session.EditAsync(new ChatTarget("C1", "1.1"), sent.MessageId!, "plain *x*", ChatTextFormat.Plain, CancellationToken.None);

        Assert.Equal(ChatSendResult.Ok("1700000000.000900"), sent);
        Assert.True(edited.Success);
        var post = _server.Requests.Last(r => r.Method == "chat.postMessage").Body;
        Assert.Equal(("C1", "1.1", "*hi* &lt;!channel&gt;"), (Str(post, "channel"), Str(post, "thread_ts"), Str(post, "text")));
        Assert.Equal("xoxb-test", _server.Requests.Last(r => r.Method == "chat.postMessage").Authorization["Bearer ".Length..]);
        var update = _server.Requests.Last(r => r.Method == "chat.update").Body;
        Assert.Equal(("C1", "1700000000.000900", "plain *x*"), (Str(update, "channel"), Str(update, "ts"), Str(update, "text")));

        await _server.PushAsync(EventsApi("e1", """{"type":"message","channel":"C1","user":"U1","text":"follow-up","ts":"1.5","thread_ts":"1.1","channel_type":"channel"}"""));
        await TestWait.WaitUntilAsync(() => !sink.Messages.IsEmpty);
        Assert.True(Assert.Single(sink.Messages).RepliesToBot);
    }

    [Theory]
    [InlineData(200, """{"ok":false,"error":"ratelimited"}""", ChatSendErrorKind.RateLimited)]
    [InlineData(429, "", ChatSendErrorKind.RateLimited)]
    [InlineData(200, """{"ok":false,"error":"msg_too_long"}""", ChatSendErrorKind.TooLong)]
    [InlineData(200, """{"ok":false,"error":"message_not_found"}""", ChatSendErrorKind.NotFound)]
    [InlineData(200, """{"ok":false,"error":"not_in_channel"}""", ChatSendErrorKind.Forbidden)]
    [InlineData(503, "", ChatSendErrorKind.Transient)]
    [InlineData(200, """{"ok":false,"error":"something_new"}""", ChatSendErrorKind.Unknown)]
    public async Task Edit_MapsSlackErrors(int status, string json, ChatSendErrorKind kind)
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        _server.Script("chat.update", json, status, retryAfterSeconds: status == 429 ? 9 : null);

        var result = await session.EditAsync(new ChatTarget("D1"), "1.1", "x", ChatTextFormat.Plain, CancellationToken.None);

        Assert.Equal(kind, result.ErrorKind);
        if (status == 429)
        {
            Assert.Equal(TimeSpan.FromSeconds(9), result.RetryAfter);
        }
    }

    // Old C8 / AC17: a file is fetched with the bot token from files.slack.com only, never off-host, never through a
    // redirect, capped, and an HTML answer (Slack's login page) is an auth failure, not a file.
    [Fact]
    public async Task SlackWebApi_DownloadsOnlyFromThePinnedHost()
    {
        var api = _services.GetRequiredService<SlackWebApi>();
        var url = _server.BaseUrl + "files/ok.pdf";

        Assert.Equal("%PDF-1"u8.ToArray(), await api.DownloadFileAsync("xoxb-test", url, 1024, CancellationToken.None));
        Assert.Equal("Bearer xoxb-test", _server.FileRequests.Single().Authorization);
        Assert.Null(await api.DownloadFileAsync("xoxb-test", url, 3, CancellationToken.None));
        Assert.Null(await api.DownloadFileAsync("xoxb-test", _server.BaseUrl + "files/login", 1024, CancellationToken.None));
        Assert.Null(await api.DownloadFileAsync("xoxb-test", _server.BaseUrl + "files/redirect", 1024, CancellationToken.None));
        Assert.DoesNotContain(_server.FileRequests, r => r.Path.Contains("elsewhere", StringComparison.Ordinal));

        // The production pin: HTTPS, files.slack.com, default port; nothing else.
        Assert.True(SlackWebApi.IsPinnedFileUrl("https://files.slack.com/files-pri/T1-F1/x.pdf", null));
        Assert.False(SlackWebApi.IsPinnedFileUrl("http://files.slack.com/x", null));
        Assert.False(SlackWebApi.IsPinnedFileUrl("https://files.slack.com.evil.example/x", null));
        Assert.False(SlackWebApi.IsPinnedFileUrl("https://files.slack.com:8443/x", null));
        Assert.False(SlackWebApi.IsPinnedFileUrl("https://slack-files.example/x", null));
        Assert.False(SlackWebApi.IsPinnedFileUrl(url, null));
    }

    // AC17: a generated image is shared through Slack's external upload: a ticket, the bytes to the pinned upload host,
    // then the completion that posts it into the channel and thread.
    [Fact]
    public async Task UploadImage_UsesTheExternalUploadFlow_InTheThread()
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);

        var result = await session.UploadImageAsync(new ChatTarget("C1", "1700000000.000100"), [1, 2, 3], "chart.png", "image/png", CancellationToken.None);

        Assert.True(result.Success);
        var ticket = _server.Requests.Single(r => r.Method == "files.getUploadURLExternal").Body;
        Assert.Equal(("chart.png", "3"), (Str(ticket, "filename"), Str(ticket, "length")));
        Assert.Equal(("/files/upload/abc", 3L), (_server.Uploads.Single().Path, _server.Uploads.Single().Length));
        var complete = _server.Requests.Single(r => r.Method == "files.completeUploadExternal").Body;
        Assert.Equal(("C1", "1700000000.000100"), (Str(complete, "channel_id"), Str(complete, "thread_ts")));
        Assert.Contains("\"F099\"", Str(complete, "files"));
    }

    // The upload URL Slack returns must be on the pinned file host: the token and bytes go nowhere else.
    [Fact]
    public async Task UploadImage_ToAnUntrustedUploadUrl_SendsNothing()
    {
        await using var session = await Adapter.ConnectAsync(Runtime(), new RecordingSink(), CancellationToken.None);
        _server.Script("files.getUploadURLExternal", """{"ok":true,"upload_url":"https://uploads.evil.example/x","file_id":"F1"}""");

        var result = await session.UploadImageAsync(new ChatTarget("C1"), [1], "x.png", "image/png", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(_server.Uploads);
        Assert.DoesNotContain(_server.Requests, r => r.Method == "files.completeUploadExternal");
    }

    internal static ServiceProvider Services(FakeSlackServer server)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(new SlackOptions(new Uri(server.BaseUrl, "api/"), server.BaseUrl));
        services.AddSlackChat();
        return services.BuildServiceProvider();
    }

    private static ChatCredentials Credentials => new("xoxb-test", "xapp-test");

    private static ChatConnectionRuntime Runtime()
        => new("u", new ChatConnection { Id = "c", Platform = ChatPlatformKind.Slack, BotUserId = "U0BOT42" }, Credentials, _ => Task.CompletedTask);

    private static string EventsApi(string envelopeId, string evt)
        => $$$"""{"envelope_id":"{{{envelopeId}}}","type":"events_api","accepts_response_payload":false,"payload":{"team_id":"T1","type":"event_callback","event":{{{evt}}}}}""";

    private static string? Str(JsonObject body, string name) => body[name]?.GetValue<string>();

    private sealed class RecordingSink : IChatInboundSink
    {
        public ConcurrentQueue<ChatInboundMessage> Messages { get; } = new();

        public Task AcceptAsync(ChatConnectionRuntime runtime, ChatInboundMessage message, CancellationToken cancellationToken)
        {
            Messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// A local stand-in for slack.com on real Kestrel: the Web API (<c>POST /api/{method}</c>, recorded and scripted), the
/// Socket Mode websocket <c>apps.connections.open</c> points at (<c>/socket</c>), and a file host (<c>/files/…</c>).
/// </summary>
public sealed class FakeSlackServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(int Status, string Json, int? RetryAfter)>> _scripts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebApplication _app = null!;
    private WebSocket? _current;
    private TaskCompletionSource? _closeCurrent;
    private int _connections;

    public ConcurrentQueue<(string Method, string Authorization, JsonObject Body)> Requests { get; } = new();

    public ConcurrentQueue<(string Path, string Authorization)> FileRequests { get; } = new();

    public ConcurrentQueue<string> Acks { get; } = new();

    /// <summary>Each byte upload to an <c>upload_url</c>: its path, Authorization header and length.</summary>
    public ConcurrentQueue<(string Path, string Authorization, long Length)> Uploads { get; } = new();

    public int Connections => Volatile.Read(ref _connections);

    public Uri BaseUrl { get; private set; } = null!;

    public void Script(string method, string json, int status = 200, int? retryAfterSeconds = null)
        => _scripts.GetOrAdd(method, _ => new()).Enqueue((status, json, retryAfterSeconds));

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapPost("/api/{method}", HandleApiAsync);
        _app.Map("/socket", HandleSocketAsync);
        _app.MapGet("/files/{name}", HandleFile);
        _app.MapPost("/files/upload/{id}", async (string id, HttpContext http) =>
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer);
            Uploads.Enqueue((http.Request.Path, http.Request.Headers.Authorization.ToString(), buffer.Length));
            return Results.Ok();
        });
        await _app.StartAsync();
        BaseUrl = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
    }

    /// <summary>Sends one text frame on the newest socket.</summary>
    public async Task PushAsync(string json)
    {
        await _sendLock.WaitAsync();
        try
        {
            await _current!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Closes the newest socket from the server side, without a disconnect envelope.</summary>
    public Task CloseCurrentAsync()
    {
        _closeCurrent?.TrySetResult();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task HandleApiAsync(string method, HttpContext http)
    {
        // JsonContent is sent chunked, with no Content-Length, so read the body rather than trust the header.
        JsonObject body;
        if (http.Request.HasFormContentType)
        {
            body = [];
            foreach (var (name, value) in await http.Request.ReadFormAsync())
            {
                body[name] = value.ToString();
            }
        }
        else
        {
            var text = await new StreamReader(http.Request.Body).ReadToEndAsync();
            body = text.Length > 0 ? JsonNode.Parse(text)?.AsObject() ?? [] : [];
        }

        Requests.Enqueue((method, http.Request.Headers.Authorization.ToString(), body));
        var (status, json, retryAfter) = _scripts.TryGetValue(method, out var queue) && queue.TryDequeue(out var scripted)
            ? scripted
            : (200, Default(method), null);
        http.Response.StatusCode = status;
        if (retryAfter is { } seconds)
        {
            http.Response.Headers.RetryAfter = seconds.ToString();
        }

        if (json.Length > 0)
        {
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(json);
        }
    }

    private string Default(string method) => method switch
    {
        "auth.test" => """{"ok":true,"url":"https://t.slack.com/","team":"T","user":"agent42","team_id":"T1","user_id":"U0BOT42","bot_id":"B42"}""",
        "apps.connections.open" => $$$"""{"ok":true,"url":"ws://{{{BaseUrl.Authority}}}/socket"}""",
        "chat.postMessage" => """{"ok":true,"channel":"C1","ts":"1700000000.000900"}""",
        "files.getUploadURLExternal" => $$$"""{"ok":true,"upload_url":"{{{BaseUrl}}}files/upload/abc","file_id":"F099"}""",
        _ => """{"ok":true}""",
    };

    private async Task HandleSocketAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = 400;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closeCurrent = close;
        _current = socket;
        Interlocked.Increment(ref _connections);
        var receiving = ReceiveAcksAsync(socket);
        await Task.WhenAny(receiving, close.Task);
        if (close.Task.IsCompleted && socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "gone", CancellationToken.None);
        }
    }

    private async Task ReceiveAcksAsync(WebSocket socket)
    {
        try
        {
            while (await SlackSessionText.ReceiveAsync(socket) is { } text)
            {
                if (JsonNode.Parse(text)?["envelope_id"]?.GetValue<string>() is { } id)
                {
                    Acks.Enqueue(id);
                }
            }
        }
        catch (WebSocketException)
        {
            // The client went away.
        }
    }

    private IResult HandleFile(string name, HttpContext http)
    {
        FileRequests.Enqueue((http.Request.Path, http.Request.Headers.Authorization.ToString()));
        return name switch
        {
            "ok.pdf" => Results.Bytes("%PDF-1"u8.ToArray(), "application/pdf"),
            "login" => Results.Content("<html>sign in</html>", "text/html"),
            "redirect" => Results.Redirect(BaseUrl + "files/elsewhere"),
            _ => Results.NotFound(),
        };
    }

    // Reads whole text frames (the server side of the same framing the session uses).
    private static class SlackSessionText
    {
        public static async Task<string?> ReceiveAsync(WebSocket socket)
        {
            var buffer = new byte[4096];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }
    }
}
