using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Agent42.Backend;

/// <summary>
/// The <c>/.auth/*</c> surface in <c>Auth:Mode=entra</c> (ADR 0145), URL-compatible with Easy Auth so the sidebar's sign-out
/// link, the service worker's bypass and the Android app keep working unchanged. Mapped from Program.cs only in that mode
/// (it is not an <c>AgentApi.*</c> partial, so <c>MapAgentApi</c> does not own it); <see cref="AuthGateMiddleware"/> lets
/// <c>/.auth/*</c> through so a signed-out user can reach the login.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/.auth/login/aad", (HttpContext http, string? post_login_redirect_uri) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = SafeRelativeTarget(post_login_redirect_uri) },
                [EntraAuthRegistration.OidcScheme]));

        // Clears the cookie AND ends the Entra session (ADR 0090: the account picker must come back), then returns to the
        // validated target through the OIDC signed-out callback.
        routes.MapGet("/.auth/logout", (HttpContext http, string? post_logout_redirect_uri) =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = SafeRelativeTarget(post_logout_redirect_uri) },
                [EntraAuthRegistration.CookieScheme, EntraAuthRegistration.OidcScheme]));

        routes.MapGet("/.auth/me", (HttpContext http) =>
            http.User.Identity?.IsAuthenticated == true
                ? Results.Json(new[] { Me(http.User) })
                : Results.StatusCode(StatusCodes.Status401Unauthorized));

        return routes;
    }

    private static object Me(ClaimsPrincipal user) => new
    {
        provider_name = "aad",
        user_id = user.FindFirstValue(PrincipalHeaderMiddleware.PreferredUsernameClaim),
        user_claims = user.Claims.Select(c => new { typ = c.Type, val = c.Value }).ToArray(),
    };

    /// <summary>
    /// A same-origin relative path or <c>/</c>. Rejects absolute URLs, protocol-relative <c>//host</c>, any backslash
    /// (browsers read <c>/\host</c> as <c>//host</c>) and control characters: an open redirect after sign-in is a phishing
    /// primitive on a trusted domain.
    /// </summary>
    public static string SafeRelativeTarget(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/' || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\') || value.Any(char.IsControl))
        {
            return "/";
        }

        return Uri.IsWellFormedUriString(value, UriKind.Relative) ? value : "/";
    }
}
