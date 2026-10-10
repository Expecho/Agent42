namespace Agent42.Agents;

/// <summary>
/// One MCP elicitation (<c>elicitation/create</c>, ADR 0095) as the grain sees it: the SDK's
/// <c>ElicitRequestParams</c> mapped by the Backend, because this layer has no MCP package reference (see
/// <see cref="IUserMcpToolProvider"/>).
/// </summary>
/// <param name="Mode"><see cref="McpElicitationLimits.FormMode"/> or <see cref="McpElicitationLimits.UrlMode"/>.</param>
/// <param name="Message">The server's text for the user. Untrusted: the browser sets it with <c>textContent</c>.</param>
/// <param name="SchemaJson">Form mode: the requested schema (flat object of primitives), as the SDK serializes it.</param>
/// <param name="Url">URL mode: the page the user is asked to open. Validated by <see cref="McpElicitationLimits.TryValidateUrl"/>.</param>
/// <remarks>The server's own elicitation id is deliberately not carried: it is attacker-chosen, and the grain mints its own.</remarks>
public sealed record McpElicitationRequest(
    string Mode,
    string Message,
    string? SchemaJson = null,
    string? Url = null);

/// <summary>The answer that goes back to the server: an action and, for an accepted form, its content as JSON.</summary>
public sealed record McpElicitationAnswer(string Action, string? ContentJson = null)
{
    /// <summary>
    /// Nobody answered, or nobody could be asked. Deliberately not <c>decline</c>: the spec reserves that for an
    /// explicit refusal by the user, which a headless run never makes.
    /// </summary>
    public static McpElicitationAnswer Cancel { get; } = new(McpElicitationLimits.Cancel);
}

/// <summary>
/// Where an elicitation is shown to a human: the interactive run's <see cref="ClientToolBroker"/>. A run with no
/// browser that advertised <see cref="ClientToolLimits.McpElicitation"/> has no sink, and the router answers
/// <see cref="McpElicitationAnswer.Cancel"/> for it.
/// </summary>
public interface IMcpElicitationSink
{
    Task<McpElicitationAnswer> AskAsync(
        string serverName, string toolCallId, McpElicitationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A tool call failed with MCP error -32042 (<c>UrlElicitationRequired</c>): the server needs the user to visit
/// one or more URLs first. The Backend translates the SDK's exception into this one, so the grain-side
/// <c>ElicitingMcpTool</c> can handle it without an MCP package reference.
/// </summary>
public sealed class McpUrlElicitationRequiredException(string message, IReadOnlyList<McpElicitationRequest> elicitations)
    : Exception(message)
{
    public IReadOnlyList<McpElicitationRequest> Elicitations { get; } = elicitations;
}

/// <summary>The constants and the URL rule of MCP elicitation (ADR 0095).</summary>
public static class McpElicitationLimits
{
    public const string FormMode = "form";

    public const string UrlMode = "url";

    public const string Accept = "accept";

    public const string Decline = "decline";

    public const string Cancel = "cancel";

    /// <summary>
    /// The largest request (message plus schema, as the browser receives it) that is shown at all. The text is the
    /// server's, unbounded by the protocol, and it rides the stream and the pending-calls list; a bigger one is
    /// answered <c>cancel</c> without asking. How long a human gets is <c>ClientToolTimeouts.For("mcp_elicitation")</c>.
    /// </summary>
    public const int MaxRequestChars = 16 * 1024;

    /// <summary>
    /// The only URLs an elicitation may show: absolute <c>https:</c>, or <c>http:</c> to a loopback host (the
    /// local bridge's case, ADR 0076). Anything else (<c>javascript:</c>, <c>data:</c>, a relative URL) is refused
    /// before any event is emitted, and the server gets <c>cancel</c>.
    /// </summary>
    public static bool TryValidateUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        var allowed = parsed.Scheme == Uri.UriSchemeHttps
            || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback);
        uri = allowed ? parsed : null;
        return allowed;
    }
}
