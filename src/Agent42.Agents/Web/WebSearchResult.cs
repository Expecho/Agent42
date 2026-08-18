namespace Agent42.Agents;

/// <summary>
/// One ranked web search result. Deliberately three short strings: this record is handed to the
/// model as a tool result and therefore <b>persisted into the session</b> — see
/// <see cref="WebToolLimits"/> before adding a field to it.
/// </summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet);
