using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>
/// The Content-Security-Policy of an MCP App frame (SEP-1865, ADR 0133), built from the origins the SERVER declared
/// for the view. Pure, so every rule is unit-testable and the header can be asserted as an exact string.
///
/// <para><b>The policy is the safety, not the iframe attribute.</b> Anyone can open the frame route as a top-level
/// page, where no <c>sandbox</c> attribute exists, so the CSP <c>sandbox</c> directive (never
/// <c>allow-same-origin</c>) gives the document an opaque origin on its own. <c>form-action 'none'</c> stops a
/// cookie-bearing form post to a same-origin endpoint, which the sandbox alone does not stop (gotcha "Proxying a
/// third-party app onto our own origin"). <c>'none'</c> and <c>data:</c> stand where the spec's own default says
/// <c>'self'</c>: <c>'self'</c> matches every same-origin URL, so it is useless here and deliberately absent.</para>
///
/// <para><b>Declared domains are untrusted input.</b> They come from a third-party server and are written into a
/// response header, so each is parsed, not escaped: only <c>https://host[:port]</c> and <c>wss://host[:port]</c>
/// (and <c>*.</c> wildcards with at least two labels behind them) survive, in a canonical lowercase form. Anything with
/// a quote, semicolon, comma, whitespace or control character, any other scheme, any IP literal, any local or internal
/// name, this app's own host, and any bare-TLD or global wildcard is DROPPED, which narrows the policy and never
/// widens it. Declaring a domain is an ADR 0013 egress extension, so the view can reach exactly what its server named
/// and what survives here.</para>
/// </summary>
public static partial class McpAppCsp
{
    /// <summary>The most entries a declared list may contribute, and the longest a single entry may be.</summary>
    public const int MaxEntries = 16;

    public const int MaxEntryLength = 256;

    /// <summary>The narrowest policy: what a view with no declared domains, or a refused one, is served under.</summary>
    public static string Default => Build(new McpAppDeclaredDomains([], [], [], []));

    /// <summary>The origins a view declared, by CSP role. Each list is raw, unvalidated server input.</summary>
    public sealed record McpAppDeclaredDomains(
        IReadOnlyList<string> Connect, IReadOnlyList<string> Resource, IReadOnlyList<string> Frame, IReadOnlyList<string> BaseUri);

    /// <summary>
    /// The policy for a view's <c>_meta.ui</c> JSON (or the narrowest policy for null, malformed or empty meta).
    /// <paramref name="ownHosts"/> are the hostnames this request was addressed to, so a view can never declare us.
    /// </summary>
    public static string Build(string? uiMetaJson, IReadOnlyCollection<string> ownHosts)
        => Build(Validate(Parse(uiMetaJson), ownHosts));

    /// <summary>The literal policy string for already-validated domains.</summary>
    public static string Build(McpAppDeclaredDomains valid)
    {
        var resource = Join(valid.Resource);
        return "sandbox allow-scripts; default-src 'none'; "
            + $"script-src 'unsafe-inline'{resource}; "
            + $"style-src 'unsafe-inline'{resource}; "
            + $"img-src data: blob:{resource}; "
            + $"font-src data:{resource}; "
            + $"media-src data: blob:{resource}; "
            + $"connect-src {OrNone(valid.Connect)}; "
            + $"frame-src {OrNone(valid.Frame)}; "
            + $"base-uri {OrNone(valid.BaseUri)}; "
            + "object-src 'none'; form-action 'none'; frame-ancestors 'self'";
    }

    /// <summary>Reads <c>csp.connectDomains</c> and friends from a view's <c>_meta.ui</c>. Malformed JSON declares nothing.</summary>
    public static McpAppDeclaredDomains Parse(string? uiMetaJson)
    {
        if (string.IsNullOrWhiteSpace(uiMetaJson))
        {
            return new McpAppDeclaredDomains([], [], [], []);
        }

        try
        {
            using var doc = JsonDocument.Parse(uiMetaJson);
            var csp = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("csp", out var c) ? c : default;
            return new McpAppDeclaredDomains(
                ReadList(csp, "connectDomains"), ReadList(csp, "resourceDomains"), ReadList(csp, "frameDomains"), ReadList(csp, "baseUriDomains"));
        }
        catch (JsonException)
        {
            return new McpAppDeclaredDomains([], [], [], []);
        }
    }

    /// <summary>Keeps only the declared entries that pass <see cref="TryNormalize"/>, within the list cap.</summary>
    public static McpAppDeclaredDomains Validate(McpAppDeclaredDomains declared, IReadOnlyCollection<string> ownHosts)
        => new(Keep(declared.Connect, ownHosts), Keep(declared.Resource, ownHosts), Keep(declared.Frame, ownHosts), Keep(declared.BaseUri, ownHosts));

    /// <summary>
    /// The canonical form of one declared origin, or null when it must be dropped (see the class remarks). Never throws.
    /// </summary>
    public static string? TryNormalize(string? entry, IReadOnlyCollection<string> ownHosts)
    {
        if (string.IsNullOrEmpty(entry) || entry.Length > MaxEntryLength || !EntryShape().IsMatch(entry))
        {
            return null;
        }

        var match = EntryShape().Match(entry);
        var host = match.Groups["host"].Value.ToLowerInvariant();
        var wildcard = match.Groups["wild"].Success;
        return IsForbiddenHost(host, wildcard, ownHosts) || !PortInRange(match.Groups["port"].Value)
            ? null
            : $"{match.Groups["scheme"].Value.ToLowerInvariant()}://{(wildcard ? "*." : "")}{host}{match.Groups["port"].Value}";
    }

    private static List<string> Keep(IReadOnlyList<string> entries, IReadOnlyCollection<string> ownHosts)
        => [.. entries.Select(e => TryNormalize(e, ownHosts)).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxEntries)];

    private static bool IsForbiddenHost(string host, bool wildcard, IReadOnlyCollection<string> ownHosts)
    {
        // A dot-less host is a bare TLD or an intranet name, and a wildcard must leave at least two labels after `*.`:
        // both mean "at least two labels in the host part". A real top-level label has a letter in it and is not a hex
        // number: a browser reads `0x7f.1`, `127.1` and `2130706433` as IPv4 addresses, so every numeric-looking host is out.
        var labels = host.Split('.');
        if (labels.Length < 2 || IPAddress.TryParse(host, out _) || !labels[^1].Any(char.IsLetter) || HexNumber().IsMatch(labels[^1]))
        {
            return true;
        }

        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".localdomain", StringComparison.Ordinal) || host.EndsWith(".lan", StringComparison.Ordinal)
            || host.EndsWith(".home.arpa", StringComparison.Ordinal))
        {
            return true;
        }

        // This app's own host, and a wildcard whose parent covers it: a third-party view must never name us.
        return ownHosts.Any(own => OwnHostMatches(own.ToLowerInvariant(), host, wildcard));
    }

    private static bool PortInRange(string port)
        => port.Length == 0 || (int.TryParse(port.AsSpan(1), out var number) && number is >= 1 and <= 65535);

    private static bool OwnHostMatches(string own, string host, bool wildcard)
        => own == host || (wildcard && own.EndsWith("." + host, StringComparison.Ordinal));

    private static List<string> ReadList(JsonElement csp, string name)
        => csp.ValueKind == JsonValueKind.Object && csp.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(MaxEntries * 4)]
            : [];

    private static string Join(IReadOnlyList<string> valid) => valid.Count == 0 ? "" : " " + string.Join(' ', valid);

    private static string OrNone(IReadOnlyList<string> valid) => valid.Count == 0 ? "'none'" : string.Join(' ', valid);

    [GeneratedRegex("^0[xX][0-9a-fA-F]*$", RegexOptions.CultureInvariant)]
    private static partial Regex HexNumber();

    // scheme://[*.]host[:port], whole string, nothing else: no path, query, userinfo, quote, semicolon, comma or space can
    // match. Hosts are ASCII labels (punycode for IDN), each 1-63 characters.
    [GeneratedRegex(
        @"^(?<scheme>https|wss)://(?<wild>\*\.)?(?<host>(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*)(?<port>:[0-9]{1,5})?\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EntryShape();
}
