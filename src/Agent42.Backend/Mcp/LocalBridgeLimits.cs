namespace Agent42.Backend;

/// <summary>
/// Every bound on the local-bridge socket (ADR 0076), in one place. A bridge key holder gets a socket
/// into our single replica, so these are what one socket can cost.
/// </summary>
public static class LocalBridgeLimits
{
    /// <summary>Open MCP channels per bridge. Overflow closes the new channel, never the bridge.</summary>
    public const int MaxChannels = 16;

    /// <summary>
    /// Largest single envelope, either direction. A bigger frame closes its channel, never the bridge.
    /// Tool results are cut to 8 KiB later, by <c>TruncatingMcpTool</c>; this bounds what we buffer first.
    /// </summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>WebSocket keep-alive ping interval (both sides) — the ACA ingress idle timeout is unmeasured.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a ping may go unanswered before the socket is aborted. Without it a keep-alive only SENDS —
    /// a helper whose laptop slept (no FIN, no RST) would leave the socket "open" until TCP gives up, and a
    /// pending <c>tools/call</c> would hold the non-reentrant grain turn that long. Aborting runs the
    /// session's fault-all, which is what releases that turn.
    /// </summary>
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The close description a socket gets when a second helper for the same server takes over. The helper
    /// recognises it and STOPS instead of reconnecting — otherwise two helpers on one key would displace each
    /// other every second, failing every in-flight call each time.
    /// </summary>
    public const string ReplacedCloseReason = "replaced by a newer connection";

    /// <summary>
    /// The Agent42 Chrome extension's origin. Its id is derived from the public <c>key</c> in
    /// <c>tools/Agent42.ChromeExtension/manifest.json</c>, so it is the same on every machine (a test pins the
    /// two together). <b>Not a credential:</b> the key is public and any non-browser client can send this
    /// header. It only keeps <i>web pages</i> out, which cannot forge <c>Origin</c>. The bridge key stays the
    /// credential.
    /// </summary>
    public const string ExtensionOrigin = "chrome-extension://ddfhnmhjjdmlfpidpampclimjhnmmnmm";

    /// <summary>
    /// The subprotocol the extension offers next to its key. A browser WebSocket cannot set <c>X-Api-Key</c>,
    /// and declarativeNetRequest does not modify an extension's own socket (measured), so the extension sends
    /// <c>Sec-WebSocket-Protocol: agent42-bridge.v1, &lt;key&gt;</c>. The server echoes only this name, never
    /// the key, which must not land in response headers or ingress logs.
    /// </summary>
    public const string ExtensionSubprotocol = "agent42-bridge.v1";

    /// <summary>
    /// Minimum gap between two tool-cache invalidations for one server on connect. Each is a Cosmos write,
    /// and with two retained versions two bumps tear down the user's OTHER warm MCP sockets.
    /// </summary>
    public static readonly TimeSpan InvalidateMinInterval = TimeSpan.FromMinutes(5);
}
