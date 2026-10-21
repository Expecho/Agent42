namespace Agent42.Agents;

/// <summary>
/// The budgets the past-chat tools run inside (ADR 0111), and the fixed text compaction relies on.
///
/// <para>A tool result is persisted into the single 2 MB Cosmos session item (ADR 0004) and re-sent every later
/// turn. Three things bound that: the per-result caps below, <see cref="MaxCallsPerTurn"/> for one turn's
/// in-context cost, and post-turn compaction, which replaces every successful result with a stub before the
/// session is serialized (the <see cref="ReadDocumentLimits"/> shape).</para>
/// </summary>
public static class PastChatLimits
{
    /// <summary>The search tool's protocol name.</summary>
    public const string SearchToolName = "search_past_chats";

    /// <summary>The read tool's protocol name.</summary>
    public const string ReadToolName = "read_past_chat";

    /// <summary>
    /// The search tool's description: the rule the model reads (rule 11). When to use it, that results are the
    /// user's OTHER conversations, how to phrase the query, the follow-up with <see cref="ReadToolName"/>, the cap.
    /// </summary>
    public static readonly string SearchDescription =
        "Search the user's other conversations with you, to recall what was said or decided earlier: use it when "
        + "the user refers to a previous chat, or asks what was discussed or decided about something before. Search "
        + "with a few short, distinctive keywords, not a sentence. Results are snippets from other conversations, "
        + "most relevant first, each with a conversationId and ordinal: call read_past_chat with those to read the "
        + "turns around a result before you rely on it. The current conversation is never included. At most "
        + $"{MaxCallsPerTurn} past-chat calls per turn.";

    /// <summary>The read tool's description.</summary>
    public static readonly string ReadDescription =
        "Read the turns around one search_past_chats result, from one of the user's other conversations. Pass the "
        + "conversationId and ordinal exactly as the search result gave them. Returns a few turns before and after "
        + "that ordinal. The text of earlier results is not kept for later turns, so note what you need.";

    /// <summary>Most keywords one search uses; the rest are ignored. Each is a Cosmos literal (ADR 0111).</summary>
    public const int MaxTerms = 8;

    /// <summary>Longest keyword kept; a longer one is cut.</summary>
    public const int MaxTermLength = 64;

    /// <summary>Shortest keyword kept. A single character matches nearly every message.</summary>
    public const int MinTermLength = 2;

    /// <summary>Hits one search returns to the model.</summary>
    public const int MaxHits = 8;

    /// <summary>
    /// Hits per conversation. Without it one chat with many matching turns (a cron agent) fills every slot, and
    /// recall across conversations is the point. <c>read_past_chat</c> reads deeper into one.
    /// </summary>
    public const int MaxHitsPerConversation = 2;

    /// <summary>Most characters of a search result. Defence in depth: <see cref="MaxHits"/> snippets fit under it.</summary>
    public const int MaxSearchTextLength = 4 * 1024;

    /// <summary>Turns a read returns before the given ordinal.</summary>
    public const int ReadTurnsBefore = 2;

    /// <summary>Turns a read returns after the given ordinal.</summary>
    public const int ReadTurnsAfter = 3;

    /// <summary>The turns one read can return: the ordinal itself plus both sides.</summary>
    public const int ReadWindow = ReadTurnsBefore + 1 + ReadTurnsAfter;

    /// <summary>Most characters of one turn in a read; a longer turn is cut with <see cref="TruncationMarker"/>.</summary>
    public const int MaxReadTurnTextLength = 2 * 1024;

    /// <summary>
    /// The most turn text one read can carry. Not enforced separately: <see cref="ReadWindow"/> ×
    /// <see cref="MaxReadTurnTextLength"/> fits under it by construction, and a test pins that, so changing either
    /// number cannot silently grow a read past it.
    /// </summary>
    public const int MaxReadTextLength = 12 * 1024;

    /// <summary>Calls of either tool one turn may make. Outside a run the budget reads exhausted (ADR 0106).</summary>
    public const int MaxCallsPerTurn = 10;

    /// <summary>Appended to a turn cut at <see cref="MaxReadTurnTextLength"/>.</summary>
    public const string TruncationMarker = " [turn truncated]";

    /// <summary>The prefix every <b>successful</b> search result starts with, and nothing else does.</summary>
    public const string SearchHeaderPrefix = "Past chats matching ";

    /// <summary>The prefix every <b>successful</b> read result starts with, and nothing else does.</summary>
    public const string ReadHeaderPrefix = "Past chat '";

    /// <summary>The prefix of a compacted search, so compaction recognises its own stub and stays idempotent.</summary>
    public const string SearchStubPrefix = "[search_past_chats result omitted: ";

    /// <summary>The prefix of a compacted read.</summary>
    public const string ReadStubPrefix = "[read_past_chat result omitted: ";

    /// <summary>The text a successful search is replaced with after its turn.</summary>
    public static string SearchStub(string query)
        => $"{SearchStubPrefix}the search for '{query}' ran in an earlier turn; call search_past_chats again to see it]";

    /// <summary>The text a successful read is replaced with after its turn.</summary>
    public static string ReadStub(string conversationId, int ordinal)
        => $"{ReadStubPrefix}turns around ordinal {ordinal} of conversation {conversationId} were read in an "
            + "earlier turn; call read_past_chat again to see them]";
}
