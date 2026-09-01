namespace Agent42.Agents;

/// <summary>
/// One search result (ADR 0061) — enough to render a row and open the conversation it came from.
///
/// <para><b>There is deliberately no <c>CreatedAt</c>.</b> <see cref="ChatTurn"/> carries no
/// per-message timestamp, so the backfill could not reproduce one the live path invented, and the
/// two paths would write different values for the same document id — quietly breaking the
/// idempotence the ordinal scheme exists to guarantee. A date, if ever needed, comes from the
/// conversation-level <c>UpdatedAt</c> on <c>ConversationIndexGrain</c>.</para>
/// </summary>
public sealed record ChatSearchHit(string ConversationId, string Role, string Text, int Ordinal);
