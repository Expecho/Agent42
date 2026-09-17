using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent42.Backend;

/// <summary>
/// One frame on the bridge socket (ADR 0076) — the backend's copy of the helper's
/// <c>BridgeEnvelope</c> in the helper project. One envelope per WebSocket text message.
/// <see cref="Channel"/> names one
/// MCP session; <see cref="Type"/> is <c>open</c>, <c>message</c> or <c>close</c>; <see cref="Message"/>
/// is the verbatim JSON-RPC message on a <c>message</c> frame. Change the wire shape here and in the
/// helper together — <c>Envelope_WireFormatsMatch</c> fails otherwise.
/// </summary>
public sealed record LocalBridgeEnvelope(
    [property: JsonPropertyName("ch")] int Channel,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("msg")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Message = null)
{
    public const string Open = "open";
    public const string MessageType = "message";
    public const string Close = "close";

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>Parses a frame, or returns null for anything that is not a well-formed envelope.</summary>
    public static LocalBridgeEnvelope? TryParse(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<LocalBridgeEnvelope>(json);
            return envelope is { Type: Open or MessageType or Close } ? envelope : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
