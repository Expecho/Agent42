using System.Diagnostics;
using System.Text;
using System.Text.Json;

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

    // OpenTelemetry semantic-convention user.* attribute keys. user.idp is a deliberate
    // deviation — there is no semconv key for the identity provider (ADR 0009).
    private const string UserIdTag = "user.id";
    private const string UserNameTag = "user.name";
    private const string UserEmailTag = "user.email";
    private const string UserFullNameTag = "user.full_name";
    private const string UserRolesTag = "user.roles";
    private const string UserIdpTag = "user.idp";

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
    /// The OpenTelemetry <c>user.*</c> attributes for a request, extracted from the Easy Auth
    /// headers. Receives <see cref="IHeaderDictionary"/> rather than <see cref="HttpContext"/>
    /// (the shape <see cref="GetUserId"/> uses) so it is trivially unit-testable with a plain
    /// <c>HeaderDictionary</c> and reusable from an enrichment callback that only holds the
    /// request headers — do not "align" it back to an HttpContext receiver. Each tag is emitted
    /// only when its value is present; parsing the principal blob never throws (see
    /// <see cref="ParsePrincipalClaims"/>).
    /// </summary>
    public static IEnumerable<KeyValuePair<string, object>> GetUserTelemetryTags(this IHeaderDictionary headers)
    {
        yield return new(UserIdTag, headers.ResolveUserId());

        if (TryGetHeader(headers, PrincipalNameHeader, out var name))
        {
            yield return new(UserNameTag, name);
        }

        if (TryGetHeader(headers, PrincipalIdpHeader, out var idp))
        {
            yield return new(UserIdpTag, idp);
        }

        var claims = ParsePrincipalClaims(headers);
        if (claims is null)
        {
            yield break;
        }

        if (FindClaim(claims, EmailClaimTypes) is { } email)
        {
            yield return new(UserEmailTag, email);
        }

        if (FindClaim(claims, FullNameClaimTypes) is { } fullName)
        {
            yield return new(UserFullNameTag, fullName);
        }

        // Multi-value (array) attribute: SetTag accepts string[] and OTLP exports it as an array.
        var roles = FindAllClaims(claims, RoleClaimTypes);
        if (roles.Length > 0)
        {
            yield return new(UserRolesTag, roles);
        }
    }

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
