using System.Security.Cryptography;
using System.Text;

namespace Agent42.Agents;

/// <summary>
/// One chat route (a DM, a group, or a thread in either) on one connection, and the Agent42 conversation it currently
/// talks to (ADR 0111). Keyed by <see cref="ChatRouteKey.GrainKey"/>.
///
/// <para><b>The conversation id is stored, not derived.</b> A deterministic id (hash of connection + chat + thread)
/// would need no state, but a DM on a platform with no threads (Telegram, WhatsApp, Signal) has no root to hash, so
/// <c>/new</c> could never start a fresh conversation there. The route mints a <see cref="ChatGatewayLimits"/> id on
/// first use, on <c>/new</c>, on <c>/agent</c> and on rollover.</para>
/// </summary>
[Alias("Agent42.Agents.IChatRouteGrain")]
public interface IChatRouteGrain : IGrainWithStringKey
{
    [Alias("GetAsync")]
    Task<ChatRouteSnapshot> GetAsync();

    /// <summary>
    /// Counts a user turn on this route and returns the conversation to run it in, minting one when the route has
    /// none, when <paramref name="forceNew"/> is set (the caller found the current conversation deleted), or when the
    /// current one reached <see cref="ChatGatewayLimits.MaxTurnsPerConversation"/> (a rollover).
    /// </summary>
    [Alias("BeginTurnAsync")]
    Task<ChatRouteTurn> BeginTurnAsync(bool forceNew);

    /// <summary><c>/new</c>: forget the current conversation, so the next turn mints one.</summary>
    [Alias("StartNewAsync")]
    Task StartNewAsync();

    /// <summary><c>/model</c>: the qualified model id later turns run on, or <c>null</c> for the user's default.</summary>
    [Alias("SetModelAsync")]
    Task SetModelAsync(string? model);

    /// <summary>
    /// <c>/agent</c>: the stored agent the route's NEXT conversation binds to. <c>""</c> means plain chat, <c>null</c>
    /// means the connection's default target. Implies <see cref="StartNewAsync"/>: an agent is bound only to a fresh
    /// conversation, never under an existing history.
    /// </summary>
    [Alias("SetAgentAsync")]
    Task SetAgentAsync(string? agentId);
}

/// <summary>The route's current state.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatRouteSnapshot")]
public sealed record ChatRouteSnapshot(
    [property: Id(0)] string? ConversationId,
    [property: Id(1)] int TurnCount,
    [property: Id(2)] string? ModelOverride,
    [property: Id(3)] string? TargetAgentOverride);

/// <summary>The conversation one turn runs in, and whether the route just minted it.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatRouteTurn")]
public sealed record ChatRouteTurn(
    [property: Id(0)] string ConversationId,
    [property: Id(1)] bool Minted,
    [property: Id(2)] bool RolledOver,
    [property: Id(3)] string? ModelOverride,
    [property: Id(4)] string? TargetAgentOverride);

/// <summary>Builds route keys and the route grain key in one place.</summary>
public static class ChatRouteKey
{
    /// <summary>
    /// The route key for a chat and an optional thread root. Platform ids are opaque strings; the separator cannot
    /// occur in any of the platforms' chat ids (numeric, or Slack/Discord alphanumerics).
    /// </summary>
    public static string For(string chatId, string? threadId)
        => string.IsNullOrEmpty(threadId) ? chatId : $"{chatId}#{threadId}";

    /// <summary>
    /// <c>{userId}/{connectionId}/{hash}</c>. The route key is hashed so arbitrary platform text never lands in a grain
    /// key; the user id leads so one user's routes can never be addressed through another's connection id.
    /// </summary>
    public static string GrainKey(string userId, string connectionId, string routeKey)
        => $"{userId}/{connectionId}/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(routeKey)))[..32]}";
}
