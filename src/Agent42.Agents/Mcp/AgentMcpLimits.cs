namespace Agent42.Agents;

/// <summary>
/// The budgets and the conversation-id shape the server-side MCP surface runs inside (ADR 0070) — the
/// inverse direction of every other file in this folder, which is the client side (ADR 0024 onwards).
///
/// <para>Three independent concerns live here and must not be conflated. The <b>inbound</b> cap
/// (<see cref="MaxPromptChars"/>) bounds what an external caller can push into the owner's 2 MB Cosmos
/// session (ADR 0004). The <b>outbound</b> cap (<see cref="MaxReplyChars"/>) bounds what we hand back
/// into the caller's context and makes the tool's contract a bounded one. The <b>wait</b>
/// (<see cref="RunTimeout"/>) bounds how long the caller's JSON-RPC request blocks — deliberately not
/// how long the turn runs, which continues to completion so it still persists (see the C6 gotcha).</para>
///
/// <para><b><see cref="IsValidConversationId"/> is a security control, not a tidiness check.</b> The
/// grain key is raw string concatenation of <c>{userId}/{conversationId}</c>, and that same string is
/// also an attachment blob prefix and a document-index partition. A caller supplies this value and the
/// owner is resolved from the API key, so an unvalidated id reaches the owner's other conversations and
/// their blobs. It therefore validates the <b>exact</b> minted shape rather than a prefix.</para>
/// </summary>
public static class AgentMcpLimits
{
    /// <summary>
    /// The route the MCP surface is served under. Also the prefix <c>AgentMcpKeyGate</c> covers and the
    /// stem of both <c>excludedPaths</c> entries.
    /// </summary>
    /// <remarks>
    /// Measured against <c>ModelContextProtocol.AspNetCore</c> 2.2.0: <c>MapMcp(RoutePrefix)</c>
    /// registers exactly <b>one</b> endpoint, <c>POST /api/agent-mcp/</c>, which matches both
    /// <c>/api/agent-mcp</c> and <c>/api/agent-mcp/</c> (both answered 405 for a GET, so the route
    /// matched and only the verb was wrong). There is no <c>/sse</c> or <c>/message</c> sub-route —
    /// those belong to the legacy SSE transport, and <c>EnableLegacySse</c> is false by default. Both
    /// reachable forms still need their own <c>excludedPaths</c> entry, because ADR 0042 measured a
    /// bare entry as matching <i>exactly</i>.
    /// </remarks>
    public const string RoutePrefix = "/api/agent-mcp";

    /// <summary>The prefix every conversation this surface mints carries.</summary>
    /// <remarks>
    /// Chosen to be distinguishable from ADR 0016's <c>agent-</c> continuous-chat prefix, which
    /// <see cref="IsValidConversationId"/> rejects outright: those ids collapse every chat onto one
    /// non-reentrant grain, so an agent reachable at its own continuous grain could block on itself
    /// until <see cref="RunTimeout"/> expired. It is also the second prefix ADR 0068's 24-hour
    /// retention gate detects, for the reason in that gate's own remarks.
    /// </remarks>
    public const string ConversationIdPrefix = "mcp-";

    /// <summary>
    /// How long the caller's JSON-RPC request waits for the reply before it is answered with a timeout.
    ///
    /// <para><b>This bounds the WAIT, never the turn.</b> The turn keeps running on a detached task and
    /// persists normally; abandoning it would lose it outright, because the session write sits after the
    /// streaming loop inside the iterator body.</para>
    /// </summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Maximum characters of prompt an external caller may push in. It lands verbatim in the owner's
    /// single 2 MB Cosmos session item (ADR 0004) and is re-sent to the model on every later turn of a
    /// reused conversation, so this is the owner's budget being spent, not the caller's.
    /// </summary>
    public const int MaxPromptChars = 32_000;

    /// <summary>
    /// Maximum characters of reply handed back to the caller. Over-long replies are truncated rather
    /// than refused: the turn already ran and is already persisted, so refusing would bill the owner for
    /// work and return nothing.
    /// </summary>
    public const int MaxReplyChars = 64_000;

    /// <summary>Mints a fresh conversation id in the shape <see cref="IsValidConversationId"/> accepts.</summary>
    public static string NewConversationId() => ConversationIdPrefix + Guid.NewGuid().ToString("d");

    /// <summary>
    /// Whether <paramref name="conversationId"/> is <b>exactly</b> a minted id: the prefix followed by a
    /// canonical lowercase GUID, nothing else.
    ///
    /// <para><b>Validates the whole string, never a prefix.</b> A <c>StartsWith</c> check accepts
    /// <c>mcp-a/../b</c>, which escapes the grain key's <c>{userId}/</c> segment and the matching blob
    /// prefix. Parsing the remainder as a GUID admits no separator, no traversal and no empty
    /// remainder, and the round-trip compare rejects the brace, parenthesis and hyphenless formats
    /// <see cref="Guid.TryParse(string, out Guid)"/> otherwise accepts — so exactly one spelling of a
    /// given id exists and two callers cannot address one conversation by two different keys.</para>
    ///
    /// <para>An ADR 0016 <c>agent-</c> id fails this by construction, which is the third of the three
    /// defences: the other two are this shape check and the runner's binding-owner comparison.</para>
    /// </summary>
    public static bool IsValidConversationId(string? conversationId)
    {
        if (conversationId is null || !conversationId.StartsWith(ConversationIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = conversationId[ConversationIdPrefix.Length..];
        return Guid.TryParse(remainder, out var parsed)
            && string.Equals(parsed.ToString("d"), remainder, StringComparison.Ordinal);
    }

    /// <summary>
    /// Truncates a reply to <see cref="MaxReplyChars"/>, marking it so the caller knows.
    ///
    /// <para>The marker is counted <b>inside</b> the cap, not appended past it. This file is the one
    /// place these numbers are meant to be authoritative, so a method documented as capping at N must
    /// not return N + 13.</para>
    /// </summary>
    public static string CapReply(string reply)
    {
        const string Marker = "\n\n[truncated]";

        return reply.Length <= MaxReplyChars
            ? reply
            : reply[..(MaxReplyChars - Marker.Length)] + Marker;
    }
}
