using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Agent42.Agents;

/// <summary>
/// The pure half of the past-chat tools (ADR 0112): keywords from the model's query, which hits survive, and the
/// text the model reads. Split from the grain so each rule is testable without a silo and the tool methods stay
/// under the cognitive-complexity gate.
/// </summary>
public static partial class PastChatQuery
{
    /// <summary>
    /// The keywords of <paramref name="query"/>: split on anything that is not a letter or digit, lower-cased,
    /// deduplicated, without single characters, at most <see cref="PastChatLimits.MaxTerms"/>, each at most
    /// <see cref="PastChatLimits.MaxTermLength"/>. Empty when nothing usable is left.
    /// </summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return [.. NonWord().Split(query)
            .Where(term => term.Length >= PastChatLimits.MinTermLength)
            .Select(term => term.ToLowerInvariant())
            .Select(term => term.Length <= PastChatLimits.MaxTermLength ? term : term[..PastChatLimits.MaxTermLength])
            .Distinct(StringComparer.Ordinal)
            .Take(PastChatLimits.MaxTerms)];
    }

    /// <summary>
    /// The hits the model sees, in the index's relevance order: only conversations the user still has
    /// (<paramref name="known"/>, which also drops orphans a failed delete left behind, ADR 0068), never the
    /// current one, at most <see cref="PastChatLimits.MaxHitsPerConversation"/> per conversation and
    /// <see cref="PastChatLimits.MaxHits"/> in all.
    /// </summary>
    public static IReadOnlyList<ChatSearchHit> Select(
        IEnumerable<ChatSearchHit> ranked, IReadOnlyDictionary<string, ConversationInfo> known, string currentConversationId)
    {
        var perConversation = new Dictionary<string, int>(StringComparer.Ordinal);
        var selected = new List<ChatSearchHit>();
        foreach (var hit in ranked)
        {
            if (selected.Count == PastChatLimits.MaxHits)
            {
                break;
            }

            if (hit.ConversationId == currentConversationId || !known.ContainsKey(hit.ConversationId))
            {
                continue;
            }

            var count = perConversation.GetValueOrDefault(hit.ConversationId);
            if (count < PastChatLimits.MaxHitsPerConversation)
            {
                perConversation[hit.ConversationId] = count + 1;
                selected.Add(hit);
            }
        }

        return selected;
    }

    /// <summary>
    /// The search result: a header starting with <see cref="PastChatLimits.SearchHeaderPrefix"/>, then one line per
    /// hit with the conversation's title, last-activity date, id and ordinal (what <c>read_past_chat</c> takes),
    /// and a snippet around the first matching keyword. Capped at <see cref="PastChatLimits.MaxSearchTextLength"/>.
    /// </summary>
    public static string FormatSearch(
        string query, IReadOnlyList<string> terms, IReadOnlyList<ChatSearchHit> hits,
        IReadOnlyDictionary<string, ConversationInfo> known)
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture,
                $"{PastChatLimits.SearchHeaderPrefix}'{query}' ({hits.Count} results, most relevant first). ")
            .Append("Call read_past_chat with a conversationId and ordinal to read the turns around a result.\n");

        foreach (var hit in hits)
        {
            var info = known[hit.ConversationId];
            var term = terms.FirstOrDefault(t => hit.Text.Contains(t, StringComparison.OrdinalIgnoreCase)) ?? "";
            text.Append(CultureInfo.InvariantCulture,
                $"- '{info.Title}' ({info.UpdatedAt:yyyy-MM-dd}) conversationId={hit.ConversationId} "
                + $"ordinal={hit.Ordinal} {hit.Role}: {ChatSearchLimits.SnippetAround(hit.Text, term)}\n");
        }

        return Cap(text.ToString(), PastChatLimits.MaxSearchTextLength);
    }

    /// <summary>
    /// The inclusive ordinal window a read of <paramref name="ordinal"/> covers. The ordinal comes from the model, so
    /// it is clamped into a range where neither bound can overflow.
    /// </summary>
    public static (int From, int To) Window(int ordinal)
    {
        var at = Math.Clamp(ordinal, 0, int.MaxValue - PastChatLimits.ReadTurnsAfter);
        return (Math.Max(0, at - PastChatLimits.ReadTurnsBefore), at + PastChatLimits.ReadTurnsAfter);
    }

    /// <summary>
    /// The read result: a header starting with <see cref="PastChatLimits.ReadHeaderPrefix"/>, then each turn with its
    /// ordinal and role. When the index lacks an ordinal inside the window the header says so: the index is
    /// best-effort (ADR 0061), so a window must never pass for complete when it is not.
    /// </summary>
    public static string FormatRead(ConversationInfo info, int from, IReadOnlyList<ChatSearchTurn> turns)
    {
        // Every ordinal from the window's start to the last returned turn exists (the conversation reached it), so
        // fewer turns than that span means the index dropped some. Past the last turn the chat may simply end.
        var missing = turns[^1].Ordinal - from + 1 > turns.Count;
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture,
                $"{PastChatLimits.ReadHeaderPrefix}{info.Title}' ({info.UpdatedAt:yyyy-MM-dd}), conversationId={info.Id}, "
                + $"turns {turns[0].Ordinal}-{turns[^1].Ordinal}")
            .Append(missing ? " (some turns in this range are missing from the search index)" : "")
            .Append(":\n");

        foreach (var turn in turns)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"[{turn.Ordinal}] {turn.Role}: {Cap(turn.Text, PastChatLimits.MaxReadTurnTextLength, PastChatLimits.TruncationMarker)}\n");
        }

        // No whole-result cap: the window holds at most ReadWindow turns of at most MaxReadTurnTextLength each,
        // which fits MaxReadTextLength by construction (pinned by a test).
        return text.ToString();
    }

    private static string Cap(string text, int max, string marker = "")
        => text.Length <= max ? text : text[..max] + marker;

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();
}
