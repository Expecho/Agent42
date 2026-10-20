using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Bounds an MCP tool's return value before it is persisted into the 2 MB session (ADR 0004), and takes any image
/// out of it (the Chrome-extension ADR). <see cref="TruncatingMcpTool"/> runs every MCP result through here.
///
/// <para><b>What the SDK hands back</b> (measured, ModelContextProtocol 2.2.0): a text-only result is a
/// <see cref="TextContent"/>, whose <c>ToString()</c> is its text. A result with more than one block is an
/// <see cref="AIContent"/> array, whose <c>ToString()</c> is its type name. The old "a JsonElement, measure its
/// <c>ToString()</c>" rule therefore never truncated a multi-part result, and an image block passed through as a
/// <see cref="DataContent"/> whose base64 the session then persisted. Both are fixed here: a multi-part result
/// becomes one bounded string.</para>
///
/// <para><b>Images</b> are request-only. A valid one (an allowed type whose magic bytes match, at most
/// <see cref="McpToolLimits.MaxImageBytes"/>) is recorded in the run's <see cref="TurnContext"/> under the tool call
/// id, which <see cref="ToolImageChatClient"/> shows the model on this run's model calls. The persisted result keeps
/// only a placeholder, worded from what will actually happen, so a later turn never reads a false "shown". No call
/// id, a closed context or a run whose model cannot see images (a copilot chat, a no-vision model, a fleet run) all
/// mean "omitted".</para>
/// </summary>
public static class McpToolResultShaper
{
    public const string ImageOmitted = "[image omitted: this model cannot be shown images here; use a text tool such as snapshot or read_text instead]";

    /// <summary>The bounded, image-free value to persist and show as the tool result.</summary>
    public static object? Shape(object? raw, TurnContext turn, string? callId)
    {
        IReadOnlyList<AIContent>? parts = raw switch
        {
            IEnumerable<AIContent> many => [.. many],
            DataContent single => [single],
            _ => null,
        };
        if (parts is not null)
        {
            return Truncate(Flatten(parts, turn, callId));
        }

        // A single TextContent, a JsonElement or a string: unchanged since ADR 0024, raw when it fits.
        var text = raw as string ?? raw?.ToString() ?? "";
        return text.Length <= McpToolLimits.MaxToolResultLength ? raw : Truncate(text);
    }

    private static string Flatten(IReadOnlyList<AIContent> parts, TurnContext turn, string? callId)
    {
        var lines = new List<string>(parts.Count);
        var imageNumber = 0;
        foreach (var part in parts)
        {
            switch (part)
            {
                case TextContent text:
                    lines.Add(text.Text);
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                    lines.Add(ImagePlaceholder(data, ++imageNumber, turn, callId));
                    break;
                case DataContent data:
                    lines.Add($"[{data.MediaType} content omitted]");
                    break;
                case UriContent link:
                    lines.Add($"[{link.MediaType}: {link.Uri}]");
                    break;
                default:
                    // Not a type name (ToString() of most AIContent is just that): its JSON, bounded with the rest below.
                    lines.Add(JsonSerializer.Serialize(part, AIJsonUtilities.DefaultOptions));
                    break;
            }
        }

        return string.Join("\n", lines);
    }

    private static string ImagePlaceholder(DataContent image, int number, TurnContext turn, string? callId)
    {
        var mediaType = image.MediaType.ToLowerInvariant();
        if (!McpToolLimits.ImageMediaTypes.Contains(mediaType) || !MagicBytesMatch(image.Data.Span, mediaType))
        {
            return $"[image {number} dropped: {mediaType} is not a supported image]";
        }

        if (image.Data.Length > McpToolLimits.MaxImageBytes)
        {
            return $"[image {number} dropped: {image.Data.Length / 1024} KB is over the {McpToolLimits.MaxImageBytes / 1024} KB limit]";
        }

        return turn.RecordToolImage(callId, image.Data, mediaType)
            ? $"[image {number}: {mediaType}, {Math.Max(1, image.Data.Length / 1024)} KB, attached for this turn only; " +
              $"only the latest {McpToolLimits.MaxImagesPerRun} images of a turn are shown]"
            : ImageOmitted;
    }

    // The declared type is the server's claim; the bytes are checked so a mislabeled blob never reaches a provider.
    private static bool MagicBytesMatch(ReadOnlySpan<byte> data, string mediaType) => mediaType switch
    {
        "image/png" => data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]),
        "image/jpeg" => data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        "image/gif" => data.StartsWith("GIF8"u8),
        "image/webp" => data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    private static string Truncate(string text)
        => text.Length <= McpToolLimits.MaxToolResultLength
            ? text
            : text[..McpToolLimits.MaxToolResultLength] + McpToolLimits.TruncationMarker;
}
