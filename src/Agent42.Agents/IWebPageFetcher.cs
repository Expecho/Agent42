namespace Agent42.Agents;

/// <summary>
/// Fetches one web page and reduces it to readable text for the model (ADR 0011).
///
/// This is a request to an <b>arbitrary, model-chosen URL made from inside the container</b>, which
/// makes it an SSRF sink: the container app's managed identity can mint tokens for Cosmos, Storage
/// and Azure OpenAI from Azure's IMDS endpoint, and anything able to issue an HTTP request from in
/// here can reach it. The production implementation therefore validates the IP it is actually
/// dialling inside <c>SocketsHttpHandler.ConnectCallback</c> (see <c>PrivateAddressGuard</c>) — a
/// resolve-then-request check would be TOCTOU-broken. Callers must additionally decide <i>which</i>
/// URLs the model may name at all: <c>AgentGrain</c> only allows hosts that came from a search
/// result or from the user's own message, which is what closes the prompt-injection exfiltration
/// channel.
///
/// Production is <c>WebPageFetcher</c>; local dev and tests use <see cref="InMemoryWebPageFetcher"/>.
/// </summary>
public interface IWebPageFetcher
{
    /// <summary>
    /// Fetches <paramref name="url"/> and returns its readable text, capped at
    /// <see cref="WebToolLimits.MaxPageTextLength"/> (the value is persisted into the session).
    /// Throws when the URL is not fetchable — a non-HTTP(S) scheme, a non-public address, a
    /// non-text content type, or a transport failure.
    /// </summary>
    Task<WebPageContent> FetchAsync(string url, CancellationToken cancellationToken = default);
}
