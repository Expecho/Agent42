using System.Text.Json;
using Agent42.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Agent42.Backend;

/// <summary>
/// The MCP client options BOTH connection factories use (ADR 0095): declare form and URL elicitation, and route
/// each request through the connection's <see cref="McpElicitationRouter"/>. One helper, so the remote and the
/// local-bridge paths cannot drift apart (the ADR 0064 "two ways to pass one credential" lesson).
///
/// <para>Both modes must be declared: measured on 2.2.0, a server's <c>ElicitAsync</c> against a client that
/// declares no capability throws "Client does not support elicitation requests.", and the server checks each mode
/// separately.</para>
/// </summary>
public static class McpElicitationClient
{
    /// <summary>The MCP Apps extension id and the one MIME type a host that renders views declares (SEP-1865).</summary>
    public const string AppsExtension = "io.modelcontextprotocol/ui";

    public const string AppsMimeType = "text/html;profile=mcp-app";

    // An extension's value must be a JsonElement: measured on 2.2.0, an anonymous object throws
    // NotSupportedException from the source-generated context when the capabilities are serialized.
    private static readonly JsonElement AppsExtensionValue =
        JsonSerializer.SerializeToElement(new Dictionary<string, string[]> { ["mimeTypes"] = [AppsMimeType] });

    /// <summary>
    /// Client options that declare elicitation and answer it through <paramref name="router"/>. With
    /// <paramref name="appsEnabled"/> they also advertise the MCP Apps extension (ADR 0133) — only then, because a
    /// server that sees it may return UI-oriented results, and a stored server defaults to apps off.
    /// </summary>
    public static McpClientOptions Options(McpElicitationRouter router, bool appsEnabled = false) => new()
    {
        Capabilities = new ClientCapabilities
        {
            Elicitation = new ElicitationCapability { Form = new(), Url = new() },
            Extensions = appsEnabled
                ? new Dictionary<string, object> { [AppsExtension] = AppsExtensionValue }
                : null,
        },
        Handlers = new McpClientHandlers
        {
            ElicitationHandler = async (request, cancellationToken) =>
                ToResult(await router.RouteAsync(ToRequest(request), cancellationToken)),
        },
    };

    /// <summary>The SDK request as the grain sees it. A missing mode is form mode (the spec's default).</summary>
    public static McpElicitationRequest ToRequest(ElicitRequestParams? request) => new(
        request?.Mode is McpElicitationLimits.UrlMode ? McpElicitationLimits.UrlMode : McpElicitationLimits.FormMode,
        request?.Message ?? "",
        request?.RequestedSchema is { } schema ? JsonSerializer.Serialize(schema, McpJsonUtilities.DefaultOptions) : null,
        request?.Url);

    /// <summary>
    /// The answer as the SDK sends it. Content travels only with <c>accept</c>; a content string that is not a JSON
    /// object becomes <c>cancel</c>, because the grain validated it already and anything else is a bug, not data.
    /// </summary>
    public static ElicitResult ToResult(McpElicitationAnswer answer)
    {
        if (answer.Action != McpElicitationLimits.Accept || answer.ContentJson is null)
        {
            return new ElicitResult { Action = answer.Action };
        }

        try
        {
            var content = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(answer.ContentJson);
            return content is null
                ? new ElicitResult { Action = McpElicitationLimits.Cancel }
                : new ElicitResult { Action = McpElicitationLimits.Accept, Content = content };
        }
        catch (JsonException)
        {
            return new ElicitResult { Action = McpElicitationLimits.Cancel };
        }
    }

    /// <summary>The SDK's -32042 exception as the Agents layer's own, which <c>ElicitingMcpTool</c> handles.</summary>
    public static McpUrlElicitationRequiredException Translate(UrlElicitationRequiredException ex)
        => new(ex.Message, [.. ex.Elicitations.Select(ToRequest)]);
}
