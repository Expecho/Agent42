using System.Text.Json.Nodes;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// What a tool's <c>_meta.ui</c> says (SEP-1865, ADR 0133): the <c>ui://</c> view it renders and who may call it.
/// <see cref="Visibility"/> is the spec's <c>["model","app"]</c> when the tool declares none.
/// </summary>
public sealed record McpAppToolMeta(string? ResourceUri, IReadOnlyList<string> Visibility)
{
    public bool VisibleToModel => Visibility.Contains(McpAppMeta.ModelVisibility);

    public bool VisibleToApp => Visibility.Contains(McpAppMeta.AppVisibility);
}

/// <summary>
/// Parses a tool's <c>_meta</c> and applies the visibility rule. The rule lives here, once, because it has TWO
/// writers of tool names — <see cref="UserMcpToolProvider"/> (the chat path) and <see cref="McpConnectionTester"/>
/// (the user-pressed test) — and a tool one of them hides while the other lists is a picker checkbox the gate
/// never honors (ADR 0064, the same lesson as the discovered-names invariant).
/// </summary>
public static class McpAppMeta
{
    public const string ModelVisibility = "model";
    public const string AppVisibility = "app";

    private static readonly string[] DefaultVisibility = [ModelVisibility, AppVisibility];

    /// <summary>
    /// Reads <c>_meta.ui.resourceUri</c> / <c>_meta.ui.visibility</c>, and the legacy flat <c>ui/resourceUri</c> key
    /// older servers still send. Null when the tool declares neither a view nor a visibility.
    /// </summary>
    public static McpAppToolMeta? Parse(JsonObject? meta)
    {
        if (meta is null)
        {
            return null;
        }

        var ui = meta["ui"] as JsonObject;
        var uri = ReadString(ui?["resourceUri"]) ?? ReadString(meta["ui/resourceUri"]);
        var visibility = ReadVisibility(ui?["visibility"]);
        return uri is null && visibility is null ? null : new McpAppToolMeta(uri, visibility ?? DefaultVisibility);
    }

    /// <summary>
    /// The tools the model may see. With apps off the server was never told about the extension, so nothing is
    /// filtered and the model sees exactly what it did before. With apps on, a tool whose visibility lacks
    /// <c>"model"</c> is app-only: it must be absent from the model's tools AND from the discovered names.
    /// </summary>
    public static IReadOnlyList<AITool> ModelTools(StoredMcpServer server, McpServerConnection connection)
    {
        if (!server.AppsEnabled || connection.AppTools is not { Count: > 0 } apps)
        {
            return connection.Tools;
        }

        return [.. connection.Tools.Where(t => !apps.TryGetValue(t.Name, out var meta) || meta.VisibleToModel)];
    }

    /// <summary>The model-visible tools that declare a view, keyed by tool name — what the grain gets (<see cref="McpAppRef"/>).</summary>
    public static IReadOnlyDictionary<string, McpAppRef>? AppRefs(StoredMcpServer server, McpServerConnection connection)
    {
        if (!server.AppsEnabled || connection.AppTools is not { Count: > 0 } apps)
        {
            return null;
        }

        var refs = apps
            .Where(kv => kv.Value is { ResourceUri: not null, VisibleToModel: true })
            .ToDictionary(kv => kv.Key, kv => new McpAppRef(server.Id, kv.Key, kv.Value.ResourceUri!), StringComparer.Ordinal);
        return refs.Count == 0 ? null : refs;
    }

    /// <summary>The live handle the registry uses, or null when the server has apps off or the connection carries no client.</summary>
    public static McpAppHandle? Handle(StoredMcpServer server, McpServerConnection connection)
        => server.AppsEnabled && connection.App is { } client
            ? new McpAppHandle(
                client,
                connection.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
                connection.AppTools ?? new Dictionary<string, McpAppToolMeta>())
            : null;

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    // An absent or unreadable visibility is the default; a PRESENT one is taken literally, so an empty array hides
    // the tool from both audiences (the spec: visibility is an allow list).
    private static string[]? ReadVisibility(JsonNode? node)
        => node is JsonArray array
            ? [.. array.Select(ReadString).OfType<string>()]
            : null;
}
