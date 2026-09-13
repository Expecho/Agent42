using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Resolves the signed-in user from the headers that Container Apps built-in
/// authentication (Easy Auth) injects on every authenticated request. The
/// ingress strips X-MS-CLIENT-* headers from incoming traffic, so inside the
/// app the headers can only originate from Easy Auth itself (ADR 0002). This is
/// the single home of Easy Auth header knowledge — both grain keying
/// (<see cref="GetUserId"/>) and telemetry enrichment
/// (<see cref="GetUserTelemetryTags"/>) read the headers here.
///
/// <para>It is also where the <see cref="UserIdAliases"/> map is applied (ADR 0074), so the resolved id
/// is the object id of the signed-in account <em>unless</em> that account is aliased onto another's.
/// Identity still originates only from Easy Auth — an alias is applied after extraction and is never a
/// substitute for authentication.</para>
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

    /// <summary>
    /// The Agent42 user id for the request: the Entra ID object id of the signed-in user, unless an
    /// alias maps it onto another account's object id (ADR 0074), in which case it is that account's.
    /// This is the isolation boundary for every durable store, so the alias is applied here and only
    /// here — see <see cref="ResolveUserId"/>.
    ///
    /// <para>The map is resolved <b>optionally</b> from request services (the <c>/api/speech</c>
    /// pattern), so no host gains a mandatory registration: unregistered means zero aliases, which is
    /// exactly the pre-ADR-0074 behaviour and can never widen access. The cost of that leniency is that
    /// a missing registration is silent, which is why <c>ProjectLayoutTests</c> pins the one in
    /// <c>Program.cs</c> as source text — no test executes it.</para>
    /// </summary>
    public static string GetUserId(this HttpContext http)
        => http.Request.Headers.ResolveUserId(http.RequestServices?.GetService<UserIdAliases>());

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
    /// <remarks>
    /// <paramref name="aliases"/> is an explicit parameter rather than a DI lookup because one caller —
    /// the <c>EnrichWithHttpRequest</c> callback in <c>Program.cs</c> — receives an <c>HttpRequest</c>
    /// and runs at <c>HttpRequestIn.Start</c>, <b>before</b> the middleware pipeline, where
    /// request-scoped resolution is not safe to assume. It captures the startup-built map instead.
    ///
    /// <para><b>Only <c>user.id</c> is aliased.</b> <c>user.name</c> comes from the principal-name header
    /// and <c>user.email</c>/<c>user.full_name</c>/<c>user.roles</c> from the claims blob, so every span
    /// still names the account that actually signed in. That is what keeps attribution intact on
    /// <c>/logs</c> (ADR 0054) — and it is also why <c>/usage</c> (ADR 0031) still shows one row per
    /// account, since <c>UsageSpanProjector</c> keys on the email first.</para>
    /// </remarks>
    public static UserTelemetryContext GetUserTelemetryContext(
        this IHeaderDictionary headers, UserIdAliases? aliases)
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

        return new UserTelemetryContext(headers.ResolveUserId(aliases), name, email, fullName, roles, idp);
    }

    /// <summary>
    /// The OpenTelemetry <c>user.*</c> attributes for a request, extracted from the Easy Auth headers.
    /// A thin projection of <see cref="GetUserTelemetryContext"/> — kept as the enrichment callback's
    /// entry point (the server-span <c>EnrichWithHttpRequest</c> path) while the carrier is the single
    /// parse. Each tag is emitted only when its value is present.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, object>> GetUserTelemetryTags(
        this IHeaderDictionary headers, UserIdAliases? aliases)
        => headers.GetUserTelemetryContext(aliases).ToTags();

    /// <summary>
    /// Stamps the <c>user.*</c> attributes onto <paramref name="activity"/> via <c>SetTag</c>.
    /// This is the exact code the ASP.NET Core trace enrichment callback runs, factored out so a
    /// test can prove the tags (including <c>user.roles</c> as a <c>string[]</c>) actually land on
    /// a real <see cref="Activity"/> without booting a host.
    /// </summary>
    public static void ApplyUserTelemetryTags(
        this Activity activity, IHeaderDictionary headers, UserIdAliases? aliases)
    {
        foreach (var tag in headers.GetUserTelemetryTags(aliases))
        {
            activity.SetTag(tag.Key, tag.Value);
        }
    }

    // The principal-id read + local-dev fallback lives in one place so GetUserId (which keys every
    // grain — 15 call sites) and GetUserTelemetryTags can never drift on the fallback semantics. The
    // ADR 0074 alias is applied at this same single point, for the same reason, which is why it reaches
    // grain keys, the telemetry user.id and the dashboard owner gate alike.
    //
    // NOTE the alias is consulted ONLY on the header-present branch. The local-dev fallback is returned
    // untouched and before any lookup, so an alias can neither rewrite nor be keyed on the
    // unauthenticated identity — offline (DevHost, the AppHost) is bit-for-bit unchanged, and so is the
    // excludedPaths rule that an endpoint with no principal header must never trust GetUserId.
    private static string ResolveUserId(this IHeaderDictionary headers, UserIdAliases? aliases)
        => headers.TryGetValue(PrincipalIdHeader, out var id) && !string.IsNullOrWhiteSpace(id)
            ? (aliases ?? UserIdAliases.Empty).Resolve(id.ToString())
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
