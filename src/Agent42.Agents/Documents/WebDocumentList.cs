namespace Agent42.Agents;

/// <summary>
/// The narrow port onto the conversation's web-fetched document lists (ADR 0066, plan cc-12):
/// <see cref="AgentGrainState.WebDocumentNames"/> and <see cref="AgentGrainState.WebDocumentUrls"/>, which this class
/// appends and trims <b>together and only here</b>, so they stay index-aligned: the name at position i belongs to the
/// URL at position i. Nothing reads them pairwise today, but a drift would make the eviction evict a name and a
/// different document's URL.
///
/// <para>It reads state through a <c>Func</c> on every call, never a captured list (ADR 0148): <c>ClearStateAsync</c>
/// replaces the state object. Built with <c>new</c> in <c>OnActivateAsync</c> (ADR 0147).</para>
/// </summary>
public sealed class WebDocumentList(Func<AgentGrainState> state)
{
    /// <summary>True when this normalized URL was already read into the conversation.</summary>
    public bool ContainsUrl(string normalizedUrl) =>
        state().WebDocumentUrls.Contains(normalizedUrl, StringComparer.Ordinal);

    /// <summary>
    /// Appends one document, then trims oldest-first to <see cref="WebDocumentLimits.MaxWebDocuments"/>. The list is
    /// rendered into the instructions on EVERY turn and lives in the 2 MB session item (ADR 0004).
    /// </summary>
    public void Remember(string fileName, string normalizedUrl)
    {
        var current = state();
        current.WebDocumentNames.Add(fileName);
        current.WebDocumentUrls.Add(normalizedUrl);

        while (current.WebDocumentNames.Count > WebDocumentLimits.MaxWebDocuments)
        {
            current.WebDocumentNames.RemoveAt(0);
            current.WebDocumentUrls.RemoveAt(0);
        }
    }

    /// <summary>How many web documents the conversation holds.</summary>
    public int Count => state().WebDocumentNames.Count;

    /// <summary>Forgets every web document (purge). Cleared together, for the reason in <c>PurgeAsync</c>.</summary>
    public void Clear()
    {
        var current = state();
        current.WebDocumentNames.Clear();
        current.WebDocumentUrls.Clear();
    }
}
