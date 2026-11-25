using Microsoft.AspNetCore.Authentication;

namespace Agent42.Backend;

/// <summary>
/// The gate in <c>Auth:Mode=entra</c> (ADR 0145). Easy Auth gated every path at the ingress, static files included. In the
/// app, <c>UseDefaultFiles</c>/<c>UseStaticFiles</c> are terminal middleware, not endpoints, so a fallback authorization
/// policy would never see <c>/index.html</c>, <c>/logs</c> or <c>/sw.js</c>. This middleware therefore sits ABOVE them and
/// answers anything that is neither a <see cref="PublicPaths"/> entry nor <c>/.auth/*</c> and has no signed-in user.
///
/// <para>Only a GET whose <c>Accept</c> contains <c>text/html</c> (a browser or WebView navigation) is redirected to Entra.
/// Everything else (no <c>Accept</c> as Android sends, a fetch, SSE, any non-GET) gets a bare 401, so a POST is never
/// redirected (its body would be lost) and Android's "401 or any redirect or HTML means sign in again" path fires.</para>
///
/// <para>CSRF: Easy Auth rejects a write whose <c>Origin</c> is <c>null</c>; a cookie has only SameSite=Lax. So a non-safe
/// request from another origin is rejected 403 here.</para>
/// </summary>
public static class AuthGateMiddleware
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (PublicPaths.IsPublic(request.Path) || PublicPaths.IsAuthSurface(request.Path))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await DenyAnonymousAsync(context);
            return;
        }

        if (!IsSafeMethod(request.Method) && IsCrossSite(request, context.RequestServices?.GetService<EntraAuthSettings>()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static Task DenyAnonymousAsync(HttpContext context)
    {
        if (!WantsLoginRedirect(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        var properties = new AuthenticationProperties { RedirectUri = EntraAuthRegistration.ReturnTarget(context.Request) };
        return context.ChallengeAsync(EntraAuthRegistration.OidcScheme, properties);
    }

    /// <summary>A browser navigation: GET with <c>Accept: text/html</c>. Nothing else is ever redirected.</summary>
    public static bool WantsLoginRedirect(HttpRequest request)
        => HttpMethods.IsGet(request.Method)
           && request.Headers.Accept.Any(a => a?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsSafeMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool IsCrossSite(HttpRequest request, EntraAuthSettings? settings)
    {
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site.Count > 0
            && !site.ToString().Equals("same-origin", StringComparison.OrdinalIgnoreCase)
            && !site.ToString().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!request.Headers.TryGetValue("Origin", out var origin) || origin.Count == 0)
        {
            return false;
        }

        if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var originUri))
        {
            return true; // "null" and anything unparseable
        }

        var own = request.Host.ToUriComponent();
        var originHost = originUri.IsDefaultPort ? originUri.Host : $"{originUri.Host}:{originUri.Port}";
        if (originHost.Equals(own, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return settings?.PublicBaseUrl is not { } baseUrl
               || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
               || !baseUri.Authority.Equals(originUri.Authority, StringComparison.OrdinalIgnoreCase);
    }
}
