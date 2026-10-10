namespace Agent42.Agents;

/// <summary>
/// Routes an MCP server's elicitation to the tool call that caused it (ADR 0095). One router per live
/// connection. The connection is shared by every conversation of a user (<c>UserMcpToolProvider</c> caches it per
/// user), so the SDK's handler cannot know which chat asked. The grain-side <c>ElicitingMcpTool</c> enters a
/// scope around each call, and the handler resolves the scope here.
///
/// <para><b>Resolution, in order.</b> (a) The scope in this router's <see cref="AsyncLocal{T}"/>, when it flowed
/// into the handler. Measured on SDK 2.2.0: it does over a stream transport (the local bridge, protocol
/// 2026-07-28) and does <b>not</b> over a stateful streamable-HTTP server (2025-11-25), where elicitation arrives
/// as a server-to-client request. (b) Otherwise the only scope in flight on this connection. (c) Otherwise, with
/// none or more than one, <c>cancel</c>. Any overlapping call to the same server (another chat, a background run,
/// a parallel tool call in the same turn) therefore cancels an elicitation from an HTTP server. A guess could hand
/// one chat's form to another chat's tab.</para>
///
/// <para>Thread-safe: the handler runs on the MCP session's thread, the scopes come and go on grain schedulers.</para>
/// </summary>
public sealed class McpElicitationRouter
{
    private readonly Lock _gate = new();

    private readonly List<Scope> _scopes = [];

    private readonly AsyncLocal<Scope?> _current = new();

    /// <summary>
    /// Opens a scope for one tool call. Set from a synchronous method, so the async local reaches the caller's own
    /// context and whatever it awaits next. Dispose it when the call ends.
    /// </summary>
    /// <param name="sink">The run's sink, or null when nobody can be asked (the router then answers <c>cancel</c>).</param>
    /// <param name="toolCallId">The model's call id of the MCP tool call, which the browser anchors the prompt to.</param>
    /// <param name="serverName">The server's display name, shown to the user as the requester.</param>
    public IDisposable Enter(IMcpElicitationSink? sink, string toolCallId, string serverName)
    {
        var scope = new Scope(this, sink, toolCallId, serverName);
        lock (_gate)
        {
            _scopes.Add(scope);
        }

        _current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Asks the scope's human, or answers <c>cancel</c> when nobody can be asked or the scope is ambiguous. A URL
    /// that fails <see cref="McpElicitationLimits.TryValidateUrl"/> is refused here, before anyone sees it.
    /// </summary>
    public Task<McpElicitationAnswer> RouteAsync(McpElicitationRequest request, CancellationToken cancellationToken)
    {
        if (request.Mode == McpElicitationLimits.UrlMode && !McpElicitationLimits.TryValidateUrl(request.Url, out _))
        {
            return Task.FromResult(McpElicitationAnswer.Cancel);
        }

        return Resolve() is { Sink: { } sink } scope
            ? sink.AskAsync(scope.ServerName, scope.ToolCallId, request, cancellationToken)
            : Task.FromResult(McpElicitationAnswer.Cancel);
    }

    /// <summary>How many calls are in flight on this connection. For tests.</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _scopes.Count;
            }
        }
    }

    private Scope? Resolve()
    {
        if (_current.Value is { Disposed: false } flowed)
        {
            return flowed;
        }

        lock (_gate)
        {
            return _scopes.Count == 1 ? _scopes[0] : null;
        }
    }

    private void Exit(Scope scope)
    {
        lock (_gate)
        {
            _scopes.Remove(scope);
        }

        if (ReferenceEquals(_current.Value, scope))
        {
            _current.Value = null;
        }
    }

    private sealed class Scope(McpElicitationRouter router, IMcpElicitationSink? sink, string toolCallId, string serverName)
        : IDisposable
    {
        public IMcpElicitationSink? Sink { get; } = sink;

        public string ToolCallId { get; } = toolCallId;

        public string ServerName { get; } = serverName;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            if (!Disposed)
            {
                Disposed = true;
                router.Exit(this);
            }
        }
    }
}
