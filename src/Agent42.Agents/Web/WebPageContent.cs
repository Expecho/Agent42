namespace Agent42.Agents.Web;

/// <summary>
/// The readable text of one fetched web page, plus any pages reached by opt-in depth-1 link following
/// (ADR 0027). <paramref name="Text"/> is capped at <see cref="WebToolLimits.MaxPageTextLength"/> and
/// <paramref name="Truncated"/> says whether it was cut — the model is told, so it doesn't treat a
/// partial page as the whole one. The whole record is persisted into the session as a tool result;
/// following makes <see cref="WebToolLimits.MaxTotalPageTextLength"/> — not <c>MaxPageTextLength</c>
/// alone — the budget that bounds it. See <see cref="WebToolLimits"/> before widening it.
/// </summary>
public sealed record WebPageContent(
    string Url, string Title, string Text, bool Truncated, IReadOnlyList<FollowedPage> Followed)
{
    /// <summary>No links were followed. The common case: following is opt-in (ADR 0027).</summary>
    public static readonly IReadOnlyList<FollowedPage> NoFollowed = [];
}

/// <summary>
/// One page reached by following a link on the root page (ADR 0027). <paramref name="LinkText"/> is the
/// markdown link text the follow filter matched against; <paramref name="Error"/> is set (and
/// <paramref name="Text"/> empty) when that one link failed to scrape — a failed follow never fails the
/// root fetch.
///
/// <b>This is attacker-authored, allowlist-exempt content.</b> The root URL is checked against the
/// caller's allowlist, but a followed link is extracted from the (untrusted) page and is fetched without
/// that check — the whole point of the feature, and an accepted residual risk (ADR 0027). Whatever
/// serializes this for the model must label it as untrusted page-authored text.
/// </summary>
public sealed record FollowedPage(
    string LinkText, string Url, string Title, string Text, bool Truncated, string? Error);
