using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// ADR 0153, D5: <c>seq</c> is a trailing nullable field on the NDJSON event. Omitted when null (so Android's golden file,
/// pinned by <see cref="AndroidStreamContractTests"/>, does not change) and present when the pump stamped it.
/// </summary>
public sealed class ChatTurnWireTests
{
    [Fact]
    public void Seq_IsOmittedWhenNull_AndAndroidGoldenUnchanged()
    {
        var json = JsonSerializer.Serialize(new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: "Hi"), ChatStreamJson.Options);

        Assert.DoesNotContain("seq", json);
        Assert.Equal("""{"type":"textDelta","text":"Hi"}""", json);
    }

    [Fact]
    public void Seq_IsWritten_WhenSet_AndRoundTrips()
    {
        var json = JsonSerializer.Serialize(new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: "Hi", Seq: 7), ChatStreamJson.Options);

        Assert.Contains("\"seq\":7", json);
        Assert.Equal(7, JsonSerializer.Deserialize<AgentStreamEvent>(json, ChatStreamJson.Options)!.Seq);
    }
}
