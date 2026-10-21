namespace Agent42.Agents;

/// <summary>
/// The chat a conversation was started from (ADR 0111): which connection, and which route on it. Written once, by the
/// gateway, when its route mints the conversation (<c>IAgentGrain.SetChatOriginAsync</c>), so a reminder or a
/// queued run in that conversation can deliver its reply back to the same chat.
///
/// <para><b>A class with property initialisers, not a positional record</b>: on the Cosmos JSON path a record
/// parameter a stored document lacks binds to <c>null</c> (gotcha "A record parameter a Cosmos document lacks binds
/// to <c>null</c>"). It rides the conversation's grain state as a trailing <c>[Id(n)]</c> defaulting to <c>null</c>
/// (CLAUDE.md rule 2).</para>
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatOrigin")]
public sealed class ChatOrigin
{
    [Id(0)]
    public string ConnectionId { get; set; } = "";

    /// <summary>The route key (<see cref="ChatRouteKey.For"/>): the chat id, plus <c>#thread</c> where there is one.</summary>
    [Id(1)]
    public string RouteKey { get; set; } = "";

    /// <summary>
    /// Whether the route is a group or channel. A delivery there reaches everyone in it, so it is made only while the
    /// connection still has groups turned on (false by default, the Cosmos-JSON-safe shape).
    /// </summary>
    [Id(2)]
    public bool IsGroup { get; set; }
}
