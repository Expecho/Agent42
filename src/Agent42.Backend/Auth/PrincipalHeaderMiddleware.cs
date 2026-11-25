using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Agent42.Backend;

/// <summary>
/// The trust boundary in <c>Auth:Mode=entra</c> (ADR 0145). Easy Auth's ingress strips inbound <c>X-MS-CLIENT-*</c> headers
/// (ADR 0002); in plain Docker nothing does, so this middleware deletes every one of them first, on every path (public
/// ones too, so a forged header on <c>/api/agent-run</c> can never reach <c>GetUserId</c>), and then sets them only from an
/// authenticated principal. <see cref="CurrentUser"/> keeps reading the same headers and is untouched.
/// <para>It protects against the network, not against a process inside the container (ADR 0121: the Copilot shell can forge
/// the header against the app's own localhost).</para>
/// </summary>
public static class PrincipalHeaderMiddleware
{
    private const string Prefix = "X-MS-CLIENT-";

    /// <summary>The claim types the stamper reads. <c>oid</c> is the user id, never <c>sub</c> (pairwise per app).</summary>
    public const string ObjectIdClaim = "oid";
    public const string NameClaim = "name";
    public const string PreferredUsernameClaim = "preferred_username";
    public const string RolesClaim = "roles";

    public static Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        Strip(context.Request.Headers);

        if (context.User.Identity?.IsAuthenticated == true && Stamp(context.Request.Headers, context.User))
        {
            // The pre-pipeline enrichment is a no-op in entra mode (it would tag spans from the forged header), so the
            // server span gets its user.* tags here, from the headers just stamped from the principal.
            Activity.Current?.ApplyUserTelemetryTags(context.Request.Headers, context.RequestServices?.GetService<UserIdAliases>());
        }

        return next(context);
    }

    /// <summary>Removes every <c>X-MS-CLIENT-*</c> header, case-insensitively (the dictionary is), however many values it has.</summary>
    public static void Strip(IHeaderDictionary headers)
    {
        foreach (var name in headers.Keys.Where(k => k.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            headers.Remove(name);
        }
    }

    /// <summary>Sets the Easy-Auth-shaped headers from <paramref name="user"/>. False when the principal has no <c>oid</c>.</summary>
    public static bool Stamp(IHeaderDictionary headers, ClaimsPrincipal user)
    {
        var oid = user.FindFirstValue(ObjectIdClaim);
        if (string.IsNullOrWhiteSpace(oid))
        {
            return false;
        }

        headers["X-MS-CLIENT-PRINCIPAL-ID"] = oid;
        headers["X-MS-CLIENT-PRINCIPAL-IDP"] = "aad";

        var username = user.FindFirstValue(PreferredUsernameClaim);
        if (!string.IsNullOrWhiteSpace(username))
        {
            headers["X-MS-CLIENT-PRINCIPAL-NAME"] = username;
        }

        headers["X-MS-CLIENT-PRINCIPAL"] = BuildBlob(user);
        return true;
    }

    // Easy Auth's shape: { "auth_typ", "name_typ", "role_typ", "claims": [ { "typ", "val" } ] }. Only the four claims
    // CurrentUser reads are emitted, which also keeps the session cookie to one chunk (ADR 0145).
    private static string BuildBlob(ClaimsPrincipal user)
    {
        var claims = new List<object>();
        foreach (var type in new[] { ObjectIdClaim, NameClaim, PreferredUsernameClaim })
        {
            if (user.FindFirstValue(type) is { Length: > 0 } value)
            {
                claims.Add(new { typ = type, val = value });
            }
        }

        foreach (var role in user.FindAll(RolesClaim).Where(c => !string.IsNullOrWhiteSpace(c.Value)))
        {
            claims.Add(new { typ = RolesClaim, val = role.Value });
        }

        var json = JsonSerializer.Serialize(new { auth_typ = "aad", name_typ = NameClaim, role_typ = RolesClaim, claims });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
