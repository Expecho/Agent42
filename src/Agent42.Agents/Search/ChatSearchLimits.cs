namespace Agent42.Agents;

/// <summary>
/// Bounds on the chat-search projection (ADR 0061), in the tradition of <see cref="WebToolLimits"/>
/// and <see cref="CalendarToolLimits"/> — but for a different reason, and the difference matters.
///
/// <para>Those budgets exist because a tool's return value is persisted into the conversation's
/// single 2 MB <c>agent-state</c> item (ADR 0004). Here <b>each message is its own Cosmos item</b>,
/// so the 2 MB ceiling applies per message rather than per conversation, and is far harder to
/// reach. The cap is kept well under it anyway: a single pathological message (a pasted log, a
/// large tool transcript) must not be able to fail the write for a whole turn — and because the
/// write is best-effort, such a failure would be silent.</para>
/// </summary>
public static class ChatSearchLimits
{
    /// <summary>
    /// Maximum characters indexed per message. Text beyond this is truncated for <i>search</i>
    /// only — the conversation itself is untouched, so nothing is lost from the record. Worst case
    /// on the wire is roughly 4 bytes per character, two orders of magnitude below the 2 MB item
    /// cap.
    /// </summary>
    public const int MaxIndexedTextLength = 32_768;

    /// <summary>Maximum hits returned by one search, so a common word cannot page the whole corpus.</summary>
    public const int MaxSearchResults = 50;

    /// <summary>
    /// Conversations reindexed per <c>POST /api/search/reindex</c> call. Each one costs a grain
    /// activation, and <c>OnActivateAsync</c> connects to the user's MCP servers sequentially with a
    /// 10 s timeout each (ADR 0024) — so an unbounded sweep is a multi-minute request, not a slow
    /// one. The endpoint reports whether more remain.
    /// </summary>
    public const int ReindexBatchSize = 25;

    /// <summary>
    /// Concurrent point-deletes when purging one conversation's messages. The delete runs inside a
    /// <b>non-reentrant</b> grain that <c>DELETE /api/conversations/{id}</c> awaits synchronously,
    /// so it is bounded on both sides: enough concurrency that a long chat doesn't crawl, little
    /// enough that it cannot saturate the Cosmos client.
    /// </summary>
    public const int DeleteConcurrency = 8;

    /// <summary>
    /// Truncates <paramref name="text"/> to <see cref="MaxIndexedTextLength"/>. Shared by both
    /// implementations so the in-memory twin and Cosmos cannot disagree on what was indexed.
    /// </summary>
    public static string CapText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Length <= MaxIndexedTextLength ? text : text[..MaxIndexedTextLength];
    }
}
