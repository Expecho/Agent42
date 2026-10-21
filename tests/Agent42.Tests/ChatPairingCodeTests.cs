using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The pairing code (ADR 0111) and the chat conversation-id shape. A code is read off a screen and retyped on a phone,
/// so matching forgives case, spaces and hyphens and nothing else.
/// </summary>
public class ChatPairingCodeTests
{
    [Fact]
    public void Generate_IsTenUnambiguousCharacters_AndUnique()
    {
        var code = ChatPairingCode.Generate();

        Assert.Matches("^[ABCDEFGHJKMNPQRSTUVWXYZ2-9]{10}$", code);
        Assert.NotEqual(code, ChatPairingCode.Generate());
    }

    [Theory]
    [InlineData("ABCDE23456", true)]
    [InlineData("abcde23456", true)]
    [InlineData("  ABCDE-23456 ", true)]
    [InlineData("ABCDE 23456", true)]
    [InlineData("ABCDE2345", false)]
    [InlineData("ABCDE234567", false)]
    [InlineData("ABCDE23457", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Matches_ForgivesCaseSpacesAndHyphensOnly(string? candidate, bool expected)
        => Assert.Equal(expected, ChatPairingCode.Matches(candidate, "ABCDE23456"));

    [Fact]
    public void Matches_NoCode_NeverMatches()
    {
        Assert.False(ChatPairingCode.Matches("anything", null));
        Assert.False(ChatPairingCode.Matches("", ""));
    }

    [Fact]
    public void ConversationId_IsExactlyTheMintedShape()
    {
        var minted = ChatGatewayLimits.NewConversationId();

        Assert.True(ChatGatewayLimits.IsValidConversationId(minted));
        Assert.False(ChatGatewayLimits.IsValidConversationId(null));
        Assert.False(ChatGatewayLimits.IsValidConversationId("chat-"));
        Assert.False(ChatGatewayLimits.IsValidConversationId("chat-a/../b"));
        Assert.False(ChatGatewayLimits.IsValidConversationId(minted.ToUpperInvariant()));
        Assert.False(ChatGatewayLimits.IsValidConversationId("mcp-" + Guid.NewGuid().ToString("d")));
        Assert.False(ChatGatewayLimits.IsValidConversationId("agent-" + Guid.NewGuid().ToString("d")));
        Assert.False(ChatGatewayLimits.IsValidConversationId("chat-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void PlatformKinds_AreKnownOrdinally()
    {
        Assert.True(ChatPlatformKind.IsKnown("Telegram"));
        Assert.False(ChatPlatformKind.IsKnown("telegram"));
        Assert.False(ChatPlatformKind.IsKnown(null));
        Assert.Equal(5, ChatPlatformKind.All.Count);
    }
}
