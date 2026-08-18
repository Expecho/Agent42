using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Agents.Telemetry;

namespace Agent42.Backend;

/// <summary>
/// Resolves the signed-in user from the headers that Container Apps built-in
/// authentication (Easy Auth) injects on every authenticated request. The
/// ingress strips X-MS-CLIENT-* headers from incoming traffic, so inside the
/// app the headers can only originate from Easy Auth itself (ADR 0002). This is
/// the single home of Easy Auth header knowledge — both grain keying
/// (<see cref="GetUserId"/>) and telemetry enrichment
/// (<see cref="GetUserTelemetryTags"/>) read the headers here.
/// </summary>
public static class CurrentUser
{
    private const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string PrincipalNameHeader = "X-MS-CLIENT-PRINCIPAL-NAME";
    private const string PrincipalIdpHeader = "X-MS-CLIENT-PRINCIPAL-IDP";
    private const string PrincipalBlobHeader = "X-MS-CLIENT-PRINCIPAL";

    // The user.* attribute-key constants now live on UserTelemetryContext (the carrier), so the
    // header→carrier→tags path is the single source of the key shape (ADR 0009/0010).

    // Local development (DevHost, standalone runs) has no Easy Auth in front,
    // so every request maps to this single pseudo-user.
    private const string LocalDevelopmentUserId = "local-dev";

    // Easy Auth maps AAD claims through varying claim-type spellings (AAD v1/v2 vs Easy Auth's
    // own mapping), so each attribute is matched against a candidate list, most-preferred first.
    // These typ spellings are a prod-only fact — Easy Auth is absent locally (ADR 0002) — so a
    // wrong match here is a /gotcha to update, not a code bug (see .agentic/gotchas.md).
    private static readonly string[] EmailClaimTypes =
    [
        "preferred_username",
        "upn",
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn",
        "email",
        "emailaddress",
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
    ];

    private static readonly string[] FullNameClaimTypes =
    [
        "name",
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
    ];

    private static readonly string[] RoleClaimTypes =
    [
        "roles",
        "role",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
    ];

    /// <summary>The Entra ID object id of the signed-in user.</summary>
    public static string GetUserId(this HttpContext http)
        => http.Request.Headers.ResolveUserId();

    /// <summary>
    /// The signed-in user's identity as a serializable <see cref="UserTelemetryContext"/>, extracted
    /// from the Easy Auth headers. This is the <b>single</b> extraction path: the ASP.NET Core
    /// server-span enrichment (<see cref="GetUserTelemetryTags"/>) and the downstream-span propagation
    /// middleware (<see cref="UserTelemetrySpanProcessor"/>, via <c>RequestContext</c>) both flow
    /// through it, so the header/claims parse runs once and can never drift between the two. Receives
    /// <see cref="IHeaderDictionary"/> rather than <see cref="HttpContext"/> so it is trivially
    /// unit-testable with a plain <c>HeaderDictionary</c>. Each optional field is <c>null</c> when
    /// absent; parsing the principal blob never throws (see <see cref="ParsePrincipalClaims"/>).
    /// </summary>
    public static UserTelemetryContext GetUserTelemetryContext(this IHeaderDictionary headers)
    {
        var name = TryGetHeader(headers, PrincipalNameHeader, out var rawName) ? rawName : null;
        var idp = TryGetHeader(headers, PrincipalIdpHeader, out var rawIdp) ? rawIdp : null;

        string? email = null;
        string? fullName = null;
        string[]? roles = null;

        if (ParsePrincipalClaims(headers) is { } claims)
        {
            email = FindClaim(claims, EmailClaimTypes);
            fullName = FindClaim(claims, FullNameClaimTypes);
            // Multi-value (array) attribute: SetTag accepts string[] and OTLP exports it as an array.
            var found = FindAllClaims(claims, RoleClaimTypes);
            roles = found.Length > 0 ? found : null;
        }

        return new UserTelemetryContext(headers.ResolveUserId(), name, email, fullName, roles, idp);
    }

    /// <summary>
    /// The OpenTelemetry <c>user.*</c> attributes for a request, extracted from the Easy Auth headers.
    /// A thin projection of <see cref="GetUserTelemetryContext"/> — kept as the enrichment callback's
    /// entry point (the server-span <c>EnrichWithHttpRequest</c> path) while the carrier is the single
    /// parse. Each tag is emitted only when its value is present.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, object>> GetUserTelemetryTags(this IHeaderDictionary headers)
        => headers.GetUserTelemetryContext().ToTags();

    /// <summary>
    /// Stamps the <c>user.*</c> attributes onto <paramref name="activity"/> via <c>SetTag</c>.
    /// This is the exact code the ASP.NET Core trace enrichment callback runs, factored out so a
    /// test can prove the tags (including <c>user.roles</c> as a <c>string[]</c>) actually land on
    /// a real <see cref="Activity"/> without booting a host.
    /// </summary>
    public static void ApplyUserTelemetryTags(this Activity activity, IHeaderDictionary headers)
    {
        foreach (var tag in headers.GetUserTelemetryTags())
        {
            activity.SetTag(tag.Key, tag.Value);
        }
    }

    // The principal-id read + local-dev fallback lives in one place so GetUserId (which keys every
    // grain — 15 call sites) and GetUserTelemetryTags can never drift on the fallback semantics.
    private static string ResolveUserId(this IHeaderDictionary headers)
        => headers.TryGetValue(PrincipalIdHeader, out var id) && !string.IsNullOrWhiteSpace(id)
            ? id.ToString()
            : LocalDevelopmentUserId;

    private static bool TryGetHeader(IHeaderDictionary headers, string name, out string value)
    {
        if (headers.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    // Easy Auth's X-MS-CLIENT-PRINCIPAL header is standard base64 of the JSON
    // { "auth_typ", "name_typ", "role_typ", "claims": [ { "typ", "val" }, ... ] }.
    // Defensive by contract: a missing, non-base64, or non-JSON blob returns null (the header-only
    // attributes still emit) — it must never throw on the request path.
    private static List<KeyValuePair<string, string>>? ParsePrincipalClaims(IHeaderDictionary headers)
    {
        if (!TryGetHeader(headers, PrincipalBlobHeader, out var blob))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(blob));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claims", out var claimsElement)
                || claimsElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var claims = new List<KeyValuePair<string, string>>();
            foreach (var claim in claimsElement.EnumerateArray())
            {
                if (claim.TryGetProperty("typ", out var typ) && typ.ValueKind == JsonValueKind.String
                    && claim.TryGetProperty("val", out var val) && val.ValueKind == JsonValueKind.String)
                {
                    claims.Add(new(typ.GetString()!, val.GetString()!));
                }
            }

            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // Malformed blob (non-base64 → FormatException, non-JSON → JsonException): fall back to
            // the header-only attributes rather than failing the request.
            return null;
        }
    }

    private static string? FindClaim(List<KeyValuePair<string, string>> claims, string[] candidateTypes)
    {
        foreach (var type in candidateTypes)
        {
            foreach (var claim in claims)
            {
                if (string.Equals(claim.Key, type, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(claim.Value))
                {
                    return claim.Value;
                }
            }
        }

        return null;
    }

    private static string[] FindAllClaims(List<KeyValuePair<string, string>> claims, string[] candidateTypes)
        => [.. claims
            .Where(c => candidateTypes.Contains(c.Key, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.Value)];
}
