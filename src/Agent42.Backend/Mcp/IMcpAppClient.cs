using System.Text.Json;
using System.Text.Json.Nodes;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agent42.Backend;

/// <summary>
/// A <c>ui://</c> resource as read from a server: the MIME type, the HTML (a blob body is decoded), and the raw
/// <c>_meta.ui</c> object (CSP, permissions) as JSON text. Plain data, no SDK types (ADR 0133).
/// </summary>
public sealed record McpAppResource(string? MimeType, string? Html, string? UiMetaJson);

/// <summary>
/// The Backend's seam to one live MCP connection for MCP Apps (ADR 0133): read a view and call a tool on behalf of
/// that view. A seam, with no SDK type in the signature, so the registry is testable over fixed data and the offline
/// DevHost stand-in can host a fixture app. It is an optional member of <see cref="McpServerConnection"/>.
/// </summary>
public interface IMcpAppClient
{
    /// <summary>Reads one resource. The caller has already restricted <paramref name="uri"/> to <c>ui://</c>.</summary>
    Task<McpAppResource> ReadResourceAsync(string uri, CancellationToken cancellationToken);

    /// <summary>Calls a tool and returns the full <c>CallToolResult</c> as JSON (content + structuredContent).</summary>
    Task<JsonElement> CallToolAsync(string toolName, JsonElement? arguments, CancellationToken cancellationToken);
}

/// <summary><see cref="IMcpAppClient"/> over the live SDK client; one instance per connection, both factories.</summary>
internal sealed class McpClientAppClient(McpClient client) : IMcpAppClient
{
    public async Task<McpAppResource> ReadResourceAsync(string uri, CancellationToken cancellationToken)
    {
        var result = await client.ReadResourceAsync(uri, cancellationToken: cancellationToken);
        var first = result.Contents.FirstOrDefault();
        return first switch
        {
            TextResourceContents text => new McpAppResource(text.MimeType, text.Text, UiMeta(text.Meta)),
            BlobResourceContents blob => new McpAppResource(
                blob.MimeType, System.Text.Encoding.UTF8.GetString(blob.DecodedData.Span), UiMeta(blob.Meta)),
            _ => new McpAppResource(null, null, null),
        };
    }

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(toolName, ToArguments(arguments), cancellationToken: cancellationToken);
        return JsonSerializer.SerializeToElement(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
    }

    /// <summary>
    /// The one place both factories turn a listed tool set into a <see cref="McpServerConnection"/>, so the remote and
    /// the local-bridge paths cannot drift (ADR 0095). With apps off nothing app-related is attached.
    /// </summary>
    internal static McpServerConnection Connection(
        McpClient client, IList<McpClientTool> tools, McpElicitationRouter router, StoredMcpServer server)
    {
        IReadOnlyList<AITool> raw = [.. tools.Cast<AITool>()];
        if (!server.AppsEnabled)
        {
            return new McpServerConnection(raw, client, router);
        }

        var apps = new Dictionary<string, McpAppToolMeta>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (McpAppMeta.Parse(tool.ProtocolTool.Meta) is { } meta)
            {
                apps[tool.Name] = meta;
            }
        }

        return new McpServerConnection(raw, client, router, new McpClientAppClient(client), apps);
    }

    private static string? UiMeta(JsonObject? meta) => (meta?["ui"] as JsonObject)?.ToJsonString();

    private static Dictionary<string, object?>? ToArguments(JsonElement? arguments)
        => arguments is { ValueKind: JsonValueKind.Object } obj
            ? obj.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal)
            : null;
}
