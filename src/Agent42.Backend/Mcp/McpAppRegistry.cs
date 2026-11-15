using System.Collections.Concurrent;
using System.Text.Json;

namespace Agent42.Backend;

/// <summary>
/// One server's live MCP Apps handle (ADR 0133): the connection's <see cref="IMcpAppClient"/>, every tool name the
/// server lists, and the <c>_meta.ui</c> of the ones that declare any. A NEW instance exists per connection, which is
/// what the registry's cache keys on: a reconnect (config save, restart) yields a new handle and so drops stale entries.
/// </summary>
public sealed record McpAppHandle(
    IMcpAppClient Client, IReadOnlySet<string> ToolNames, IReadOnlyDictionary<string, McpAppToolMeta> Tools);

/// <summary>
/// Where the registry gets a live handle. <see cref="UserMcpToolProvider"/> implements it over its version cache and
/// connects on demand (a cache miss after a restart or a config save goes through the same
/// <c>ConnectAllAsync</c> path, bounded and capped like a chat activation, ADR 0064), never opening a second socket.
/// Null means the server is unknown, disabled, has apps off or is unreachable: the registry fails closed.
/// </summary>
public interface IMcpAppHandleSource
{
    Task<McpAppHandle?> GetAppHandleAsync(string userId, string serverId, CancellationToken cancellationToken);
}

/// <summary>Why the registry refused (the endpoints map each to an HTTP status).</summary>
public enum McpAppError
{
    None,

    /// <summary>No such server, a server with apps off, or one that could not be reached (fail closed).</summary>
    Unavailable,

    /// <summary>The request itself is malformed: not a <c>ui://</c> uri, wrong MIME, unknown tool.</summary>
    BadRequest,

    /// <summary>The tool exists but is not visible to a view.</summary>
    Forbidden,

    /// <summary>An argument, result or view exceeded its bound.</summary>
    TooLarge,

    /// <summary>The server itself failed the call.</summary>
    Failed,
}

/// <summary>A registry outcome: a value, or the reason there is none.</summary>
public readonly record struct McpAppOutcome<T>(T? Value, McpAppError Error)
{
    public bool Ok => Error == McpAppError.None;

    public static McpAppOutcome<T> Success(T value) => new(value, McpAppError.None);

    public static McpAppOutcome<T> Fail(McpAppError error) => new(default, error);
}

/// <summary>A view as read from its server: the HTML and the raw <c>_meta.ui</c> JSON (declared CSP, permissions).</summary>
public sealed record McpAppView(string Html, string? UiMetaJson);

/// <summary>
/// The Backend's access to a user's live MCP connections on behalf of a rendered view (ADR 0133): read the view's
/// <c>ui://</c> resource, and run a tool the view asks for. Every check that protects the user lives here, in one
/// place, so the endpoints stay thin and the rules are unit-testable without HTTP.
/// </summary>
public interface IMcpAppRegistry
{
    Task<McpAppOutcome<McpAppView>> ReadUiResourceAsync(string userId, string serverId, string uri, CancellationToken cancellationToken);

    Task<McpAppOutcome<JsonElement>> CallToolAsync(
        string userId, string serverId, string tool, JsonElement? arguments, CancellationToken cancellationToken);
}

/// <summary>The bounds of <see cref="McpAppRegistry"/>.</summary>
public static class McpAppLimits
{
    /// <summary>A view is one bundled HTML file; past this we refuse it rather than serve megabytes from a third party.</summary>
    public const int MaxViewBytes = 1024 * 1024;

    /// <summary>What a view may send in a <c>tools/call</c>.</summary>
    public const int MaxCallArgumentBytes = 64 * 1024;

    /// <summary>What a view may receive back from a <c>tools/call</c> (JSON text length).</summary>
    public const int MaxCallResultBytes = 256 * 1024;

    /// <summary>Resource cache size; a view is a few hundred KB at most, so the cap is a memory bound.</summary>
    public const int MaxCachedViews = 32;
}

public sealed class McpAppRegistry(IMcpAppHandleSource handles, ILogger<McpAppRegistry> logger) : IMcpAppRegistry
{
    private const string UiScheme = "ui://";

    // Cached per (user, server, uri) AND the handle instance it was read through: a reconnect makes a new handle,
    // so a changed config version can never serve a view read through the old connection.
    private readonly ConcurrentDictionary<string, (McpAppHandle Handle, McpAppView View)> _views = new(StringComparer.Ordinal);

    public async Task<McpAppOutcome<McpAppView>> ReadUiResourceAsync(
        string userId, string serverId, string uri, CancellationToken cancellationToken)
    {
        // A view may read only ui:// resources: anything else would turn this into a general resource proxy.
        if (!uri.StartsWith(UiScheme, StringComparison.OrdinalIgnoreCase) || uri.Length > 2048)
        {
            return McpAppOutcome<McpAppView>.Fail(McpAppError.BadRequest);
        }

        var handle = await handles.GetAppHandleAsync(userId, serverId, cancellationToken);
        if (handle is null)
        {
            return McpAppOutcome<McpAppView>.Fail(McpAppError.Unavailable);
        }

        var key = $"{userId}\n{serverId}\n{uri}";
        if (_views.TryGetValue(key, out var hit) && ReferenceEquals(hit.Handle, handle))
        {
            return McpAppOutcome<McpAppView>.Success(hit.View);
        }

        var outcome = await ReadAsync(handle, serverId, uri, cancellationToken);
        if (outcome.Ok)
        {
            if (_views.Count >= McpAppLimits.MaxCachedViews)
            {
                _views.Clear(); // a memory bound, not an LRU: views are cheap to re-read
            }

            _views[key] = (handle, outcome.Value!);
        }

        return outcome;
    }

    public async Task<McpAppOutcome<JsonElement>> CallToolAsync(
        string userId, string serverId, string tool, JsonElement? arguments, CancellationToken cancellationToken)
    {
        if (arguments is { } args && args.GetRawText().Length > McpAppLimits.MaxCallArgumentBytes)
        {
            return McpAppOutcome<JsonElement>.Fail(McpAppError.TooLarge);
        }

        // The server comes from the REQUEST route and the handle is resolved for THIS server only, so a view can
        // never reach another server's tools: there is no code path that takes a handle for a different id.
        var handle = await handles.GetAppHandleAsync(userId, serverId, cancellationToken);
        if (handle is null)
        {
            return McpAppOutcome<JsonElement>.Fail(McpAppError.Unavailable);
        }

        if (!handle.ToolNames.Contains(tool))
        {
            return McpAppOutcome<JsonElement>.Fail(McpAppError.BadRequest);
        }

        // Absent `_meta.ui` means the spec default, ["model","app"]; a declared visibility is an allow list.
        if (handle.Tools.TryGetValue(tool, out var meta) && !meta.VisibleToApp)
        {
            return McpAppOutcome<JsonElement>.Fail(McpAppError.Forbidden);
        }

        JsonElement result;
        try
        {
            result = await handle.Client.CallToolAsync(tool, arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A view's call of MCP tool {Tool} on server {Server} failed.", tool, serverId);
            return McpAppOutcome<JsonElement>.Fail(McpAppError.Failed);
        }

        return result.GetRawText().Length > McpAppLimits.MaxCallResultBytes
            ? McpAppOutcome<JsonElement>.Fail(McpAppError.TooLarge)
            : McpAppOutcome<JsonElement>.Success(result);
    }

    private async Task<McpAppOutcome<McpAppView>> ReadAsync(
        McpAppHandle handle, string serverId, string uri, CancellationToken cancellationToken)
    {
        McpAppResource resource;
        try
        {
            resource = await handle.Client.ReadResourceAsync(uri, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading MCP App resource {Uri} for server {Server} failed.", uri, serverId);
            return McpAppOutcome<McpAppView>.Fail(McpAppError.Failed);
        }

        if (!string.Equals(resource.MimeType, McpElicitationClient.AppsMimeType, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(resource.Html))
        {
            return McpAppOutcome<McpAppView>.Fail(McpAppError.BadRequest);
        }

        return System.Text.Encoding.UTF8.GetByteCount(resource.Html) > McpAppLimits.MaxViewBytes
            ? McpAppOutcome<McpAppView>.Fail(McpAppError.TooLarge)
            : McpAppOutcome<McpAppView>.Success(new McpAppView(resource.Html, resource.UiMetaJson));
    }
}
