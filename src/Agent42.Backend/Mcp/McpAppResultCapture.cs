using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Turns what an MCP tool invocation returned into the <c>CallToolResult</c> JSON an MCP App view receives
/// (ADR 0133). The view wants the whole result (<c>content</c> and <c>structuredContent</c>), which the model's shaped
/// text deliberately is not.
///
/// <para><b>What the SDK hands back</b> (measured, ModelContextProtocol 2.2.0, Step 0 spike): a result with
/// <c>structuredContent</c> is the WHOLE result as a <see cref="JsonElement"/>, and we pass that through untouched. A
/// result without it is a <see cref="TextContent"/>, an <see cref="AIContent"/> array or a string, from which the
/// <c>content</c> blocks are rebuilt here so a view always sees one shape.</para>
/// </summary>
public static class McpAppResultCapture
{
    public static string ToResultJson(object? raw) => raw switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } whole when IsCallToolResult(whole) => whole.GetRawText(),
        IEnumerable<AIContent> many => Blocks([.. many.Select(Block).OfType<JsonNode>()]),
        AIContent single => Blocks([Block(single)]),
        _ => Blocks([TextBlock(raw as string ?? raw?.ToString() ?? "")]),
    };

    private static bool IsCallToolResult(JsonElement element)
        => element.TryGetProperty("content", out _) || element.TryGetProperty("structuredContent", out _);

    private static string Blocks(IEnumerable<JsonNode?> blocks)
        => new JsonObject { ["content"] = new JsonArray([.. blocks.OfType<JsonNode>()]) }.ToJsonString();

    private static JsonNode? Block(AIContent part) => part switch
    {
        TextContent text => TextBlock(text.Text),
        DataContent data when data.HasTopLevelMediaType("image") => new JsonObject
        {
            ["type"] = "image",
            ["data"] = Convert.ToBase64String(data.Data.Span),
            ["mimeType"] = data.MediaType,
        },
        UriContent link => new JsonObject { ["type"] = "resource_link", ["uri"] = link.Uri.ToString(), ["name"] = link.Uri.ToString() },
        _ => null,
    };

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };
}
