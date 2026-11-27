using System.Text;

namespace Agent42.Agents;

/// <summary>
/// The budgets the <c>read_web_document</c> path runs inside (ADR 0066), plus the one piece of policy
/// both fetcher implementations must share.
///
/// <para>Three independent budgets live here and must not be conflated. The <b>transport</b> cap
/// (<see cref="MaxDownloadBytes"/>) and the <b>deadline</b> (<see cref="DownloadDeadline"/>) bound what
/// we pull off the wire and how long a non-reentrant grain can be stalled doing it. The <b>ingestion</b>
/// cap (<see cref="MaxChunks"/>) bounds the embedding work, so a document that is small in bytes but
/// enormous in extracted text cannot stall the turn either. The <b>return</b> caps
/// (<see cref="MaxExcerpts"/>, <see cref="MaxReturnTextLength"/>) bound what the tool hands back to the
/// model — which is persisted into the single 2 MB Cosmos item every turn (ADR 0004), re-sent to the
/// model on every later turn, and rendered into the browser DOM as a tool chip.</para>
///
/// <para><b><see cref="SanitizeFileName"/> lives here, and not in the production fetcher, for a reason
/// worth stating:</b> the name it produces is attacker-influenced text that lands in the system prompt
/// (<c>DocumentContext.WithAwareness</c>) and in every citation's <c>SourceName</c>. It must therefore be one
/// implementation shared by <i>every</i> <see cref="IWebDocumentFetcher"/>, and the only assembly both
/// the production (Backend) and offline (Agents) fetchers can reach is this one.</para>
/// </summary>
public static class WebDocumentLimits
{
    /// <summary>
    /// Transport guard: refuse a document larger than this, and stop reading a body that grows past it.
    /// 10 MB — large enough for a real specification PDF, small enough that a hostile or merely huge
    /// response cannot be pulled into a single grain's memory.
    /// </summary>
    public const int MaxDownloadBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Ingestion guard: at most this many chunks are embedded and indexed from one document, with the
    /// tail dropped and the truncation reported to the model rather than silently discarded.
    ///
    /// <para>Bytes alone do not bound the stall: a 2 MB text file extracts to far more chunks than a
    /// 9 MB scanned PDF, and every chunk is an embedding round trip on a <b>non-reentrant</b> grain.
    /// This is the cap that actually bounds that work.</para>
    /// </summary>
    public const int MaxChunks = 200;

    /// <summary>Maximum number of excerpts the tool returns inline for the current turn.</summary>
    public const int MaxExcerpts = 5;

    /// <summary>
    /// Maximum total characters of excerpt text the tool returns, across all excerpts. Counted against
    /// the 2 MB session budget (ADR 0004) — see the class remarks.
    ///
    /// <para><b>It does not bind at today's numbers, and that is worth knowing rather than discovering.</b>
    /// <see cref="MaxExcerpts"/> (5) × <c>DocumentChunker.ChunkSize</c> (1 000) is 5 000 characters, so
    /// <see cref="MaxExcerpts"/> is the cap that actually decides the return size and the truncation
    /// branch guarding this one is currently unreachable. It is kept as the invariant that survives a
    /// change to either of those two numbers — raise <c>ChunkSize</c> and this becomes the binding cap
    /// with no further edit — but do not read it as the value in force.</para>
    /// </summary>
    public const int MaxReturnTextLength = 8 * 1024;

    /// <summary>
    /// Wall-clock ceiling on one document download, covering the request AND the body read.
    ///
    /// <para><b>This is not redundant with <c>HttpClient.Timeout</c>.</b> The fetcher reads with
    /// <c>HttpCompletionOption.ResponseHeadersRead</c> so it can stop at
    /// <see cref="MaxDownloadBytes"/> instead of buffering first — and under that option
    /// <c>HttpClient.Timeout</c> stops applying once the headers arrive. Without this deadline a
    /// slow-drip body would stall a non-reentrant grain indefinitely.</para>
    /// </summary>
    public static readonly TimeSpan DownloadDeadline = TimeSpan.FromSeconds(30);

    /// <summary>Maximum length of the URL-derived part of a document's display name, in characters.</summary>
    public const int MaxFileNameLength = 64;

    /// <summary>Maximum length of the host qualifier appended to a display name, in characters.</summary>
    public const int MaxHostLength = 40;

    /// <summary>
    /// Maximum number of web-fetched documents one conversation keeps, oldest dropped first.
    ///
    /// <para>Bounded for the reason <c>AgentGrain.MaxFetchableUrls</c> is: the list is rendered into the
    /// agent's instructions on <b>every</b> turn and persisted in the same 2 MB Cosmos item (ADR 0004).
    /// It matters more here than on the browser-upload path because the trigger is <b>model-driven</b>
    /// and reachable from <c>/api/agent-run</c> (publicly reachable with an API key, ADR 0022) and from
    /// a <b>repeating reminder</b> — neither of which has a human deciding to attach one more file.</para>
    ///
    /// <para>Eviction drops the name, not the indexed chunks: those are removed with the conversation
    /// (<c>AgentGrain.PurgeAsync</c>). An evicted URL can be read again, which re-ingests it and, since
    /// ADR 0098, replaces its chunks rather than duplicating them.</para>
    /// </summary>
    public const int MaxWebDocuments = 10;

    /// <summary>Appended to the tool's excerpt text when it was cut at <see cref="MaxReturnTextLength"/>.</summary>
    public const string TruncationMarker = "\n\n[truncated]";

    // Everything outside this set is dropped. An allowlist, never a denylist: the value ends up inside
    // the system prompt, so "which characters are dangerous" is the wrong question to be answering.
    private static bool IsAllowedFileNameChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ';

    /// <summary>
    /// Derives a safe, <b>self-describing</b> display name for a fetched document from its URL and
    /// resolved media type — of the form <c>path-segments.pdf (web: host)</c>.
    ///
    /// <para><b>The name is attacker-influenced text that reaches the model.</b> It is folded into the
    /// agent's instructions and into every citation <c>SourceName</c>, so a URL whose path is
    /// <c>Ignore previous instructions and…</c> would otherwise write that straight into the system
    /// prompt. Sanitization is therefore an allowlist plus a hard length cap, not an escaping pass:
    /// path separators and control characters cannot survive, and neither can anything else outside
    /// <c>[A-Za-z0-9.-_ ]</c>.</para>
    ///
    /// <para><b>The <c>(web: host)</c> qualifier is a provenance control, not decoration.</b> The
    /// instructions distinguish uploaded from web-fetched documents, but the always-on retrieval
    /// provider injects excerpts from both as <c>ChatRole.User</c> messages whose only per-excerpt
    /// metadata is this name (<c>DocumentChunk</c> carries no provenance field). Without the qualifier a
    /// URL ending <c>/handbook.pdf</c> would produce a citation indistinguishable from an uploaded
    /// <c>handbook.pdf</c> in the same conversation — and would name-collide with it outright.</para>
    ///
    /// <para><b>The WHOLE path is used, not just the last segment</b>, so the name is near-unique per
    /// URL: <c>/2024/report.pdf</c> and <c>/2025/report.pdf</c> must not produce one name, because the
    /// citations would be ambiguous. (Exact re-read identity is a separate mechanism — see
    /// <c>AgentGrainState.WebDocumentUrls</c>; this only has to make citations readable.)</para>
    ///
    /// <para>When nothing usable remains — a bare host, a query-only URL, a path that sanitizes to
    /// empty — the fallback is an extension implied by the media type, with the host qualifier still
    /// attached. A document with no name at all is worse: the model cannot cite it.</para>
    /// </summary>
    public static string SanitizeFileName(Uri url, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(url);

        var derived = Sanitize(FlattenedPath(url), MaxFileNameLength);
        var host = Sanitize(url.Host, MaxHostLength);

        return (derived.Length > 0 ? derived : "document" + ExtensionFor(mediaType))
            + $" (web: {(host.Length > 0 ? host : "unknown")})";
    }

    // The whole path, percent-decoded, with separators flattened to '-'. Decoding happens BEFORE the
    // flatten so an encoded '/' or newline is judged as the character it decodes to, rather than as a
    // literal '%2f' that sanitizes cleanly and decodes later.
    private static string FlattenedPath(Uri url)
    {
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            // Relative segments carry no information into a flat display name, and leaving them in
            // produces names that READ like traversal ("-..-etc-passwd") even though nothing here ever
            // touches a file system. Drop them.
            .Where(s => s is not ("." or ".."));

        return string.Join('-', segments);
    }

    private static string Sanitize(string raw, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(raw.Length, maxLength));
        foreach (var c in raw)
        {
            if (builder.Length == maxLength)
            {
                break;
            }

            if (IsAllowedFileNameChar(c))
            {
                builder.Append(c);
            }
        }

        // A name of only dots ("..", ".") sanitizes "cleanly" and is still a path traversal spelling;
        // trim them at the edges rather than admitting one.
        return builder.ToString().Trim('.', ' ');
    }

    // The extension implied by a resolved media type, for the host fallback only. Unknown types get
    // none — a wrong extension would be a worse lie than no extension.
    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        DocumentMediaTypes.Pdf => ".pdf",
        DocumentMediaTypes.Docx => ".docx",
        DocumentMediaTypes.Pptx => ".pptx",
        DocumentMediaTypes.Xlsx => ".xlsx",
        DocumentMediaTypes.Markdown => ".md",
        DocumentMediaTypes.PlainText => ".txt",
        _ => "",
    };
}
