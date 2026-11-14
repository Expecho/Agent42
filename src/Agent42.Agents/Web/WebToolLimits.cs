namespace Agent42.Agents;

/// <summary>
/// The budget the web search tool's <b>return value</b> must fit in. These are not transport limits
/// — they cap what the tool hands back to the model, which is the number that actually matters here.
///
/// Why: a tool's <c>FunctionResultContent</c> is <b>persisted</b>. It stays in the Agent Framework
/// chat history, and <c>AgentGrain</c> re-serializes the whole session into a single
/// <c>agent-state</c> Cosmos item every turn — an item Cosmos hard-caps at <b>2 MB</b> (ADR 0004).
/// So an unbudgeted tool result would not merely bloat one reply: it would sit in history forever,
/// be re-sent to the model every turn, and eventually make <c>WriteStateAsync</c> fail <i>after</i>
/// the model had already answered — bricking the conversation, in production only. The same values
/// are streamed to the browser as a tool chip, so these caps protect the DOM too.
///
/// Anything added to the web search return payload must be counted against this budget.
/// </summary>
public static class WebToolLimits
{
    /// <summary>Maximum number of search results returned to the model.</summary>
    public const int MaxSearchResults = 5;

    /// <summary>Maximum length of a single search result's snippet, in characters.</summary>
    public const int MaxSnippetLength = 300;

    /// <summary>
    /// Backstop, in characters, on one serialized search payload where a caller has no budget of its own
    /// (the fleet's <c>web_search</c> cuts to this). <c>IWebSearch</c> already budgets results to
    /// <see cref="MaxSearchResults"/> × <see cref="MaxSnippetLength"/>, so this should never bind; it only
    /// guarantees a misbehaving implementation cannot push an unbounded value into a persisted session.
    /// </summary>
    public const int MaxSearchPayloadLength = 24 * 1024;
}
