namespace Agent42.Agents.Web;

/// <summary>
/// The budget the web tools' <b>return values</b> must fit in. These are not transport limits
/// (the fetcher separately caps how much it will read off the wire) — they cap what the tools
/// hand back to the model, which is the number that actually matters here.
///
/// Why: a tool's <c>FunctionResultContent</c> is <b>persisted</b>. It stays in the Agent Framework
/// chat history, and <c>AgentGrain</c> re-serializes the whole session into a single
/// <c>agent-state</c> Cosmos item every turn — an item Cosmos hard-caps at <b>2 MB</b> (ADR 0004).
/// So an unbudgeted fetch result would not merely bloat one reply: it would sit in history forever,
/// be re-sent to the model every turn, and eventually make <c>WriteStateAsync</c> fail <i>after</i>
/// the model had already answered — bricking the conversation, in production only. The same values
/// are streamed to the browser as a tool chip, so these caps protect the DOM too.
///
/// Anything added to a web tool's return payload must be counted against this budget.
/// </summary>
public static class WebToolLimits
{
    /// <summary>Maximum number of search results returned to the model.</summary>
    public const int MaxSearchResults = 5;

    /// <summary>Maximum length of a single search result's snippet, in characters.</summary>
    public const int MaxSnippetLength = 300;

    /// <summary>Maximum length of the readable text returned for one fetched page, in characters.</summary>
    public const int MaxPageTextLength = 8 * 1024;

    /// <summary>Appended to a page's text when it was cut off at <see cref="MaxPageTextLength"/>.</summary>
    public const string TruncationMarker = "\n\n[truncated]";

    /// <summary>
    /// Server-side hard ceiling on how many links one fetch may follow (ADR 0027). This is a cost bound
    /// (context.dev bills one credit per followed scrape) as much as a security one (each followed page
    /// is an attacker-authored page pulled in through the allowlist exemption). A model-supplied
    /// <c>maxFollowedLinks</c> is clamped to this — selection can only narrow it, never raise it.
    /// </summary>
    public const int MaxFollowedLinks = 3;

    /// <summary>
    /// Total budget, in characters, across a fetch's root text <b>and</b> every followed page's text
    /// (ADR 0027). Following turns one page into <c>1 + MaxFollowedLinks</c> pages, so
    /// <see cref="MaxPageTextLength"/> alone is no longer the ceiling on what gets persisted into the
    /// 2 MB session item (ADR 0004). Deliberately smaller than <c>(1 + MaxFollowedLinks) ×
    /// MaxPageTextLength</c> so it actually binds: the root keeps its full per-page budget and followed
    /// pages share whatever remains, truncated to fit.
    /// </summary>
    public const int MaxTotalPageTextLength = 24 * 1024;
}
