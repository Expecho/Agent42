namespace Agent42.Backend;

/// <summary>One search result as the sidebar renders it (ADR 0061).</summary>
public sealed record SearchHitResponse(string ConversationId, string Role, string Text, int Ordinal);

/// <summary>The <c>GET /api/search</c> payload.</summary>
public sealed record SearchResponse(IReadOnlyList<SearchHitResponse> Hits);

/// <summary>
/// The <c>POST /api/search/reindex</c> payload. <paramref name="More"/> reports whether the sweep
/// stopped on its batch cap rather than because it ran out of conversations, so the caller can call
/// again with a higher <c>skip</c> instead of silently believing the backfill finished.
/// </summary>
public sealed record ReindexResponse(int Conversations, int Turns, bool More);
