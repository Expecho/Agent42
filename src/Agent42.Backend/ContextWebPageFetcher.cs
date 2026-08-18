using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agent42.Agents;
using Agent42.Agents.Web;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IWebPageFetcher"/> over the hosted <b>context.dev</b> scrape API (ADR 0027): fetches one
/// model-chosen URL as clean markdown, and optionally follows depth-1 links whose text matches a filter.
///
/// <para><b>This is not an SSRF sink, and the reason replaces ADR 0011's guard rather than duplicating
/// it.</b> The outbound request that reads the page is made by <b>context.dev</b>, not by us — our
/// container never opens a socket to the model's URL — so there is no in-container request to an
/// attacker-chosen address, and therefore no <c>PrivateAddressGuard</c> / connect-time check here (it
/// was deleted with the old direct-fetch path). The call this class <i>does</i> make is to context.dev's
/// own <b>config-pinned</b> host, carrying the model's URL as a query parameter — an ordinary API call,
/// not an SSRF vector.</para>
///
/// <para><b>The allowlist is still the caller's job.</b> Refusing SSRF is not the same as refusing
/// exfiltration: an instruction on a fetched page can still tell the model to fetch the conversation out
/// through an attacker URL. That is closed by <c>AgentGrain</c>'s whole-URL allowlist on the <b>root</b>
/// URL (ADR 0011, unchanged). Followed links are deliberately exempt from it — an accepted residual risk
/// (ADR 0027) — which is why following is opt-in and hard-capped.</para>
///
/// <para>Two budgets, deliberately different numbers — do not conflate them: each page's text is capped
/// at <see cref="WebToolLimits.MaxPageTextLength"/>, and the root plus every followed page together at
/// <see cref="WebToolLimits.MaxTotalPageTextLength"/>, which is the value that gets persisted into the
/// 2 MB session every turn (ADR 0004). The <see cref="WebToolLimits.MaxFollowedLinks"/> ceiling is a
/// cost bound too: context.dev bills one credit per scrape.</para>
///
/// The <see cref="HttpClient"/> is a named client with base address + bearer auth, an explicit timeout
/// and <b>no</b> resilience pipeline (see Program.cs): <c>AgentGrain</c> is non-reentrant, so a retried
/// scrape multiplies the stall on the user's turn.
/// </summary>
public sealed partial class ContextWebPageFetcher(HttpClient httpClient, ILogger<ContextWebPageFetcher> logger)
    : IWebPageFetcher
{
    /// <summary>Name of the DI-registered <see cref="HttpClient"/> this service resolves.</summary>
    public const string HttpClientName = "context.dev";

    /// <summary>The scrape API's base address. Config-pinned — half of the "not an SSRF sink" argument.</summary>
    public const string DefaultBaseAddress = "https://api.context.dev/";

    /// <summary>
    /// Configures the named client's base address and bearer auth. Extracted so the blank-key branch is
    /// testable without a running host (AC14): <c>new AuthenticationHeaderValue("Bearer", "")</c> throws
    /// <see cref="FormatException"/>, so a missing key must be <b>branched on</b>, not passed through —
    /// and a missing key must not prevent startup (ADR 0027, unlike <c>SEARXNG_SECRET</c>). With no key
    /// the client is still registered; calls simply 401 and the tool tells the model the page could not
    /// be read.
    /// </summary>
    public static void ConfigureClient(HttpClient http, string? apiKey)
    {
        http.BaseAddress = new Uri(DefaultBaseAddress);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    public async Task<WebPageContent> FetchAsync(
        string url,
        string? followLinkFilter = null,
        int maxFollowedLinks = 1,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"'{url}' is not an absolute http(s) URL, so there is no page to read.", nameof(url));
        }

        // Following is OPT-IN: with no filter, nothing is followed. This deliberately INVERTS the spike's
        // rule (where a null/empty filter meant "follow every link") — an unfiltered default would spend a
        // credit per link and pull an attacker-authored page into the session on every ordinary fetch. Do
        // not "fix" this back to follow-all. (ADR 0027.)
        var following = !string.IsNullOrWhiteSpace(followLinkFilter);

        // includeLinks must be TRUE whenever we intend to follow, or the links never survive into the
        // markdown and following is a silent no-op in production while every stub test still passes. When
        // not following, links are noise we do not need. useMainContentOnly is TRUE in both cases: it
        // strips nav/boilerplate the model would otherwise pay context for, and — verified against the
        // live API on 2026-07-21 — it does NOT strip the motivating "Se komplett salgsoppgave" CTA link,
        // which lives inside the main listing content, so following still fires with it on.
        var root = await ScrapeOneAsync(url, includeLinks: following, cancellationToken);

        if (!following || root.Markdown is null)
        {
            var (rootTextOnly, rootTruncatedOnly) = Cap(root.Markdown, WebToolLimits.MaxPageTextLength);
            return new WebPageContent(
                url, TitleOf(root, uri), rootTextOnly, rootTruncatedOnly, WebPageContent.NoFollowed);
        }

        var baseUri = new Uri(root.Metadata?.FinalUrl is { Length: > 0 } final ? final : url);
        var candidates = ExtractLinks(root.Markdown, baseUri, followLinkFilter!);

        // maxFollowedLinks is model-supplied, so it can only NARROW toward the ceiling, never raise it —
        // the ceiling is the cost and bandwidth bound.
        var take = Math.Clamp(maxFollowedLinks, 0, WebToolLimits.MaxFollowedLinks);
        var chosen = candidates.Take(take).ToList();
        if (candidates.Count > chosen.Count)
        {
            logger.LogInformation(
                "context.dev fetch of '{Url}' matched {Matched} link(s) for filter '{Filter}'; following {Followed} (ceiling {Ceiling}).",
                url, candidates.Count, followLinkFilter, chosen.Count, WebToolLimits.MaxFollowedLinks);
        }

        // Depth-1: the followed scrapes never request links of their own, and their links are never
        // expanded. Run them concurrently (bounded by the ceiling), capturing — never throwing — a
        // per-link failure, so one bad link cannot fail the root fetch.
        var followedResults = await Task.WhenAll(chosen.Select(async link =>
        {
            try
            {
                var page = await ScrapeOneAsync(link.Url, includeLinks: false, cancellationToken);
                return new FollowedPage(link.Text, link.Url, TitleOf(page, new Uri(link.Url)),
                    page.Markdown ?? string.Empty, Truncated: false, Error: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "context.dev failed to follow '{Url}'.", link.Url);
                return new FollowedPage(link.Text, link.Url, string.Empty, string.Empty,
                    Truncated: false, Error: ex.Message);
            }
        }));

        var (rootText, rootTruncated) = Cap(root.Markdown, WebToolLimits.MaxPageTextLength);

        // Total budget across root + followed: the followed pages share whatever the root left of
        // MaxTotalPageTextLength, each still individually capped, applied in order so the sum is bounded.
        var remaining = WebToolLimits.MaxTotalPageTextLength - rootText.Length;
        var followed = new List<FollowedPage>(followedResults.Length);
        foreach (var page in followedResults)
        {
            if (page.Error is not null)
            {
                followed.Add(page);
                continue;
            }

            var limit = Math.Min(WebToolLimits.MaxPageTextLength, Math.Max(0, remaining));
            var (text, truncated) = Cap(page.Text, limit);
            remaining -= text.Length;
            followed.Add(page with { Text = text, Truncated = truncated });
        }

        return new WebPageContent(url, TitleOf(root, uri), rootText, rootTruncated, followed);
    }

    private async Task<ContextScrapeResult> ScrapeOneAsync(
        string url, bool includeLinks, CancellationToken cancellationToken)
    {
        // The API key rides in the Authorization header (set on the named client), NOT in this query
        // string, so the request URI is safe to surface. The scraped URL itself does ride here; OTel's
        // query redaction is a library default we do not own, and a root URL is far less sensitive than
        // the coordinates that concern was written for — accepted (ADR 0027).
        var requestUri =
            $"v1/web/scrape/markdown?url={Uri.EscapeDataString(url)}" +
            $"&includeLinks={(includeLinks ? "true" : "false")}" +
            "&useMainContentOnly=true";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Never include the key: it is not in `body` (it is a request header), but keep the message
            // to the status + the API's own error text.
            throw new HttpRequestException($"context.dev returned HTTP {(int)response.StatusCode}: {body}");
        }

        var result = JsonSerializer.Deserialize(body, ContextScrapeJsonContext.Default.ContextScrapeResult)
            ?? throw new HttpRequestException("context.dev returned an empty or unparseable response.");

        // A 200 can still carry success:false (a scrape that failed at their end). Treat it as a failure
        // rather than letting an empty/null markdown flow back as a blank page — the root fetch then
        // surfaces "couldn't read" to the model, and a followed link is captured as a per-link error.
        if (!result.Success)
        {
            throw new HttpRequestException("context.dev could not scrape the page (success: false).");
        }

        return result;
    }

    // Pulls followable links out of the markdown. Ported from the spike; the two subtleties are
    // load-bearing: (?<!! ) keeps ![alt](img) image links from being followed, and the fragment-
    // insensitive dedupe key means the same page reached via two #anchors is one scrape, one credit.
    private static List<(string Text, string Url)> ExtractLinks(string markdown, Uri baseUri, string filter)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();

        foreach (Match match in MarkdownLink().Matches(markdown))
        {
            var text = match.Groups["text"].Value.Trim();
            if (!text.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, match.Groups["url"].Value, out var abs) ||
                (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            // Dedupe ignoring the #fragment — same page, same scrape, same credit.
            if (seen.Add(abs.GetLeftPart(UriPartial.Query)))
            {
                result.Add((text, abs.ToString()));
            }
        }

        return result;
    }

    private static (string Text, bool Truncated) Cap(string? text, int limit)
    {
        text ??= string.Empty;
        if (limit <= 0)
        {
            return (string.Empty, text.Length > 0);
        }

        return text.Length <= limit
            ? (text, false)
            : (text[..limit] + WebToolLimits.TruncationMarker, true);
    }

    private static string TitleOf(ContextScrapeResult result, Uri uri) =>
        string.IsNullOrWhiteSpace(result.Metadata?.Title) ? uri.Host : result.Metadata!.Title!.Trim();

    // (?<!!) so ![alt](img) image links are not treated as followable; the optional trailing "title"
    // group matches the markdown [text](url "title") form.
    [GeneratedRegex(@"(?<!!)\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLink();
}

// Source-generated JSON: the backend runs with reflection-based serialization available, but a pinned
// context keeps the shape explicit and the trimming story clean. Only the fields we read are declared.
internal sealed record ContextScrapeResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("markdown")] string? Markdown,
    [property: JsonPropertyName("contentLength")] int ContentLength,
    [property: JsonPropertyName("metadata")] ContextScrapeMetadata? Metadata);

internal sealed record ContextScrapeMetadata(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("finalUrl")] string? FinalUrl,
    [property: JsonPropertyName("description")] string? Description);

[JsonSerializable(typeof(ContextScrapeResult))]
internal sealed partial class ContextScrapeJsonContext : JsonSerializerContext;
