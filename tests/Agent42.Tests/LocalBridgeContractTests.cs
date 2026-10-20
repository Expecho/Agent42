using System.Text.Json;
using Agent42.Backend;
using Agent42.LocalBridge;

namespace Agent42.Tests;

/// <summary>
/// The bridge envelope exists twice — <see cref="LocalBridgeEnvelope"/> in the backend and
/// <see cref="BridgeEnvelope"/> in the helper — because one linked source file would put the same public
/// type in two assemblies this project references (CS0433). This test is what makes that duplication safe:
/// every frame kind written by either side must be read identically by the other (ADR 0076).
/// </summary>
public sealed class LocalBridgeContractTests
{
    private const string JsonRpc = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\"}";

    public static TheoryData<int, string, string?> Frames() => new()
    {
        { 1, "open", null },
        { 2, "message", JsonRpc },
        { 0, "ping", null },
        { 0, "pong", null },
        { 3, "close", null },
    };

    // AC3
    [Theory]
    [MemberData(nameof(Frames))]
    public void Envelope_WireFormatsMatch(int channel, string type, string? message)
    {
        JsonElement? msg = message is null ? null : JsonDocument.Parse(message).RootElement.Clone();

        var fromBackend = new LocalBridgeEnvelope(channel, type, msg).Serialize();
        var fromHelper = new BridgeEnvelope(channel, type, msg).Serialize();
        Assert.Equal(fromBackend, fromHelper);

        var readByHelper = BridgeEnvelope.TryParse(fromBackend);
        var readByBackend = LocalBridgeEnvelope.TryParse(fromHelper);
        Assert.NotNull(readByHelper);
        Assert.NotNull(readByBackend);
        Assert.Equal((channel, type), (readByHelper.Channel, readByHelper.Type));
        Assert.Equal((channel, type), (readByBackend.Channel, readByBackend.Type));
        Assert.Equal(message, readByHelper.Message?.GetRawText());
        Assert.Equal(message, readByBackend.Message?.GetRawText());

        // The literal wire shape, so a rename made on BOTH sides still fails here.
        Assert.StartsWith("{\"ch\":" + channel + ",\"type\":\"" + type + "\"", fromBackend, StringComparison.Ordinal);
        Assert.Equal(message is not null, fromBackend.Contains("\"msg\":", StringComparison.Ordinal));
    }

    // The close reason is part of the contract too: the helper stops on exactly this string, and a mismatch
    // would bring back two helpers displacing each other forever.
    [Fact]
    public void ReplacedCloseReason_Matches()
        => Assert.Equal(LocalBridgeLimits.ReplacedCloseReason, BridgeClient.ReplacedCloseReason);

    // The extension heartbeat rides channel 0, which is never an MCP channel: the backend numbers them from 1.
    [Fact]
    public void ControlFrames_ParseOnBothSides()
    {
        Assert.Equal(0, LocalBridgeEnvelope.ControlChannel);
        Assert.Equal((LocalBridgeEnvelope.Ping, LocalBridgeEnvelope.Pong), (BridgeEnvelope.Ping, BridgeEnvelope.Pong));
        Assert.Equal("ping", LocalBridgeEnvelope.TryParse("{\"ch\":0,\"type\":\"ping\"}")?.Type);
        Assert.Equal("pong", BridgeEnvelope.TryParse("{\"ch\":0,\"type\":\"pong\"}")?.Type);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"ch\":1,\"type\":\"bogus\"}")]
    [InlineData("{\"ch\":\"x\",\"type\":\"open\"}")]
    [InlineData("null")]
    public void Envelope_RejectsMalformedFrames(string json)
    {
        Assert.Null(BridgeEnvelope.TryParse(json));
        Assert.Null(LocalBridgeEnvelope.TryParse(json));
    }
}
