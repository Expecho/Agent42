using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent42.Backend;

/// <summary>
/// The NDJSON wire format of <c>POST /api/chat/{id}</c> stream events: camelCase fields, camelCase enum values
/// (e.g. "textDelta", "toolCallStart"), and null fields omitted. Public so the Android client's contract fixture is
/// pinned against the exact options the endpoint writes with, never a copy of them.
/// </summary>
public static class ChatStreamJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
