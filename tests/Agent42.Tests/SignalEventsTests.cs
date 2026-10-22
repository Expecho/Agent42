using System.Text.Json;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Signal's event projection (ADR 0111, phase 3, SC4's filter), against fixtures modelled on signal-cli's documented
/// JSON-RPC field names (Step 23). Step 31 replaces them with real captures.
/// </summary>
public class SignalEventsTests
{
    private const string BotAci = "bbbbbbbb-0000-4000-8000-000000000001";
    private const string Alice = "aaaaaaaa-0000-4000-8000-000000000002";

    private static ChatInboundMessage? Parse(string json) => SignalEvents.Parse(JsonDocument.Parse(json).RootElement, BotAci);

    private static string Envelope(string body, string source = Alice, long timestamp = 1700000000123)
        => "{\"account\":\"+31600000000\",\"envelope\":{\"source\":\"" + source + "\",\"sourceUuid\":\"" + source + "\",\"sourceDevice\":1,\"timestamp\":" + timestamp + "," + body + "}}";

    [Fact]
    public void ADirectMessage_IsTheSendersChat_WithTheDedupeIdSenderColonTimestamp()
    {
        var message = Parse(Envelope("""
            "dataMessage":{"timestamp":1700000000123,"message":"hello there","expiresInSeconds":0}
            """));

        Assert.NotNull(message);
        Assert.Equal(($"{Alice}:1700000000123", Alice, ChatType.Direct, Alice, "hello there"),
            (message.PlatformMessageId, message.ChatId, message.ChatType, message.SenderId, message.Text));
        Assert.Null(message.ThreadId);
        Assert.Null(message.SenderWorkspaceId);
        Assert.False(message.SenderIsBot);
        Assert.False(message.MentionsBot);
        Assert.False(message.RepliesToBot);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000123), message.ReceivedAt);
    }

    // The placeholder U+FFFC marks the mention in the text; removing it makes "@bot /new" a command.
    [Fact]
    public void AGroupMessageMentioningTheBot_IsAGroupChat_WithThePlaceholderStripped()
    {
        var message = Parse(Envelope($$$"""
            "dataMessage":{"message":"￼ /new","groupInfo":{"groupId":"R3JvdXA=","type":"DELIVER"},"mentions":[{"name":"bot","number":null,"uuid":"{{{BotAci}}}","start":0,"length":1}]}
            """));

        Assert.NotNull(message);
        Assert.Equal((ChatType.Group, "R3JvdXA=", true, "/new"), (message.ChatType, message.ChatId, message.MentionsBot, message.Text));
    }

    [Fact]
    public void AMentionOfSomeoneElse_IsNotAMentionOfTheBot()
    {
        var message = Parse(Envelope("""
            "dataMessage":{"message":"￼ hi","groupInfo":{"groupId":"R3JvdXA="},"mentions":[{"uuid":"cccccccc-0000-4000-8000-000000000003","start":0,"length":1}]}
            """));

        Assert.False(message!.MentionsBot);
    }

    [Fact]
    public void AQuoteOfTheBot_IsAReplyToTheBot_ButAQuoteOfSomeoneElseIsNot()
    {
        var toBot = Parse(Envelope($$$"""
            "dataMessage":{"message":"yes","quote":{"id":1,"authorUuid":"{{{BotAci}}}","text":"question?"}}
            """));
        var toOther = Parse(Envelope($$$"""
            "dataMessage":{"message":"yes","quote":{"id":1,"authorUuid":"{{{Alice}}}","text":"question?"}}
            """));

        Assert.True(toBot!.RepliesToBot);
        Assert.False(toOther!.RepliesToBot);
    }

    [Fact]
    public void AnImageAttachment_IsAFile_WithItsDescription()
    {
        var message = Parse(Envelope("""
            "dataMessage":{"message":"look","attachments":[{"contentType":"image/jpeg","filename":"cat.jpg","id":"att123","size":2048}]}
            """));

        var file = Assert.Single(message!.Files);
        Assert.Equal((Alice + "|att123", "cat.jpg", "image/jpeg", 2048L, false), (file.FileRef, file.FileName, file.DeclaredMediaType, file.Size, file.IsVoiceNote));
        Assert.Equal((Alice, "att123"), SignalEvents.ParseAttachmentRef(file.FileRef));
        Assert.Null(SignalEvents.ParseAttachmentRef("att123"));
    }

    [Fact]
    public void AVoiceNote_CarriesTheFlag_AndNeedsNoText()
    {
        var message = Parse(Envelope("""
            "dataMessage":{"attachments":[{"contentType":"audio/aac","id":"voice1","size":9000,"isVoiceNote":true}]}
            """));

        Assert.Equal("", message!.Text);
        var file = Assert.Single(message.Files);
        Assert.True(file.IsVoiceNote);
        Assert.Null(file.FileName);
    }

    [Theory]
    [InlineData("""{"syncMessage":{"sentMessage":{"destinationUuid":"x","message":"echo"}}}""")]
    [InlineData("""{"storyMessage":{"textAttachment":{"text":"story"}}}""")]
    [InlineData("""{"receiptMessage":{"when":1,"isDelivery":true,"isRead":false,"timestamps":[1]}}""")]
    [InlineData("""{"typingMessage":{"action":"STARTED","timestamp":1}}""")]
    [InlineData("""{"callMessage":{"offerMessage":{"id":1}}}""")]
    [InlineData("""{"editMessage":{"targetSentTimestamp":1,"dataMessage":{"message":"edited"}}}""")]
    [InlineData("""{"dataMessage":{"reaction":{"emoji":"x","targetAuthorUuid":"y","targetSentTimestamp":1,"isRemove":false}}}""")]
    [InlineData("""{"dataMessage":{"remoteDelete":{"timestamp":1}}}""")]
    [InlineData("""{"dataMessage":{"expiresInSeconds":0}}""")]
    [InlineData("""{"dataMessage":{"message":"","attachments":[]}}""")]
    [InlineData("""{"dataMessage":{"message":"￼","mentions":[{"uuid":"bbbbbbbb-0000-4000-8000-000000000001","start":0,"length":1}]}}""")]
    public void NonMessageEnvelopes_AreDropped(string body)
        => Assert.Null(Parse(Envelope(body[1..^1])));

    [Fact]
    public void TheBotsOwnMessage_IsDropped()
        => Assert.Null(Parse(Envelope("""
            "dataMessage":{"message":"my own reply"}
            """, source: BotAci)));

    [Fact]
    public void AMissingEnvelopeOrTimestampOrSender_IsDropped()
    {
        Assert.Null(Parse("""{"account":"+31600000000"}"""));
        Assert.Null(Parse("""{"envelope":{"sourceUuid":"a","dataMessage":{"message":"x"}}}"""));
        Assert.Null(Parse("""{"envelope":{"timestamp":1,"dataMessage":{"message":"x"}}}"""));
        Assert.Null(Parse(Envelope("""
            "dataMessage":{"message":"x","groupInfo":{"type":"DELIVER"}}
            """)));
    }

    [Fact]
    public void TheSameMessage_HasOneId_ADifferentTimestampHasAnother()
    {
        const string Body = """
            "dataMessage":{"message":"x"}
            """;
        Assert.Equal(Parse(Envelope(Body))!.PlatformMessageId, Parse(Envelope(Body))!.PlatformMessageId);
        Assert.NotEqual(Parse(Envelope(Body))!.PlatformMessageId, Parse(Envelope(Body, timestamp: 1700000000124))!.PlatformMessageId);
    }
}
