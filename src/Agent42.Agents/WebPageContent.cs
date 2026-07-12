namespace Agent42.Agents;

/// <summary>
/// The readable text of one fetched web page. <paramref name="Text"/> is capped at
/// <see cref="WebToolLimits.MaxPageTextLength"/> and <paramref name="Truncated"/> says whether it was
/// cut — the model is told, so it doesn't treat a partial page as the whole one. Persisted into the
/// session as a tool result; see <see cref="WebToolLimits"/> before widening it.
/// </summary>
public sealed record WebPageContent(string Url, string Title, string Text, bool Truncated);
