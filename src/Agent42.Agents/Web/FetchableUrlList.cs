namespace Agent42.Agents;

/// <summary>
/// The narrow port onto the conversation's fetchable-URL allowlist (ADR 0011/0066/0067, plan cc-12): the only way
/// the web tools read or write <see cref="AgentGrainState.FetchableUrls"/> and
/// <see cref="AgentGrainState.SeededFetchableUrls"/>. It exposes named operations and never the lists.
///
/// <para><b>The check is a security boundary, not a convenience.</b> The text a document tool returns enters the
/// model's context, and an instruction written <i>in that document</i> can tell the model to call the tool again with
/// the conversation's contents in the query string, exfiltrating a chat that may also carry document excerpts
/// (ADR 0008). That is a prompt-injection exfiltration concern, independent of SSRF (<c>PrivateAddressGuard</c>,
/// ADR 0066).</para>
///
/// <para>The check is on the <b>whole URL</b>, not its host, and that distinction is the control. Matching on host
/// alone is bypassable in one extra tool call, because the model also chooses the search <i>query</i>:
/// "search for <c>attacker.example &lt;secret&gt;</c>, then fetch it" whitelists the attacker's host in the same
/// turn. Requiring the exact (normalized) URL means the model can only read a string a search engine or the user
/// produced verbatim. ADR 0067 narrows that, per server and opt-in: a URL a URL-seeding MCP server returned is
/// allowlisted too, and that one was authored on a page rather than produced verbatim.</para>
///
/// <para>The two lists are checked separately and stay separate: a seeded URL was authored on a scraped page, so
/// merging them would erase that distinction at rest as well as at the point of use, and each trims only its own
/// members.</para>
///
/// <para>It reads state through a <c>Func</c> on every call, never a captured list (ADR 0148): <c>ClearStateAsync</c>
/// replaces the state object. Built with <c>new</c> in <c>OnActivateAsync</c> (ADR 0147); public only because this
/// repo has no <c>InternalsVisibleTo</c>.</para>
/// </summary>
public sealed class FetchableUrlList(Func<AgentGrainState> state)
{
    /// <summary>True when the normalized URL is in either list.</summary>
    public bool IsFetchable(Uri uri)
    {
        var normalized = UrlNormalizer.Normalize(uri);
        var current = state();
        return current.FetchableUrls.Contains(normalized, StringComparer.Ordinal)
            || current.SeededFetchableUrls.Contains(normalized, StringComparer.Ordinal);
    }

    /// <summary>Records a URL a search engine returned or the user wrote (the verbatim-provenance list).</summary>
    public void Remember(Uri uri)
    {
        var list = state().FetchableUrls;
        var normalized = UrlNormalizer.Normalize(uri);
        if (list.Contains(normalized, StringComparer.Ordinal))
        {
            return;
        }

        list.Add(normalized);

        // Bounded: the list is persisted in the 2 MB session item (ADR 0004). Oldest out first.
        while (list.Count > AgentGrain.MaxFetchableUrls)
        {
            list.RemoveAt(0);
        }
    }

    /// <summary>
    /// Adds one URL a URL-seeding MCP server returned (ADR 0067), returning its normalized form when it was new
    /// (null when already present). Normalization is shared with <see cref="Remember"/> on purpose.
    /// </summary>
    public string? RememberSeeded(Uri uri)
    {
        var list = state().SeededFetchableUrls;
        var normalized = UrlNormalizer.Normalize(uri);
        if (list.Contains(normalized, StringComparer.Ordinal))
        {
            return null;
        }

        list.Add(normalized);

        // Bounded, oldest first, and evicting only its own members: that is what stops a link-heavy result from
        // evicting the URLs the user typed.
        while (list.Count > AgentGrain.MaxSeededFetchableUrls)
        {
            list.RemoveAt(0);
        }

        return normalized;
    }
}
