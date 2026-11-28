using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// One member of the web-search chain (ADR 0155): a named <see cref="IWebSearch"/>. A keyed provider throws
/// <see cref="WebSearchProviderException"/> to say how it failed; any other exception counts as transient.
/// </summary>
public interface IWebSearchProvider : IWebSearch
{
    /// <summary>The lower-case name used in <c>WebSearch:Providers</c>, logs and config health.</summary>
    string Name { get; }

    /// <summary>
    /// False for the keyless last resort (SearXNG): it is never benched, so the chain always has somewhere to end,
    /// and it runs under its own client timeout rather than the hosted budget.
    /// </summary>
    bool CanBeBenched => true;
}
