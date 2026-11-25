using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.Extensions;

namespace Agent42.Backend;

/// <summary>
/// Registers the native Entra sign-in (<c>Auth:Mode=entra</c>, ADR 0145): an OIDC authorization-code flow that signs into a
/// cookie whose name, lifetime and URLs match Easy Auth's, so the sidebar link, the service worker and the Android app keep
/// working unchanged. Its own folder, as <c>HindsightRegistration</c> does, to keep Program.cs under the S3776 limit.
/// </summary>
public static class EntraAuthRegistration
{
    /// <summary>The Easy Auth cookie name; the Android app reads exactly this cookie from the WebView jar.</summary>
    public const string SessionCookieName = "AppServiceAuthSession";

    /// <summary>Fixed, not sliding (ADR 0090): a returning user is not sent back to Entra on every open.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    public const string CallbackPath = "/signin-oidc";
    public const string SignedOutCallbackPath = "/signout-callback-oidc";
    public const string RemoteSignOutPath = "/signout-oidc";

    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;

    public static IServiceCollection AddEntraAuth(this IServiceCollection services, EntraAuthSettings settings)
    {
        services.AddSingleton(settings);
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultChallengeScheme = OidcScheme;
            })
            .AddCookie(CookieScheme, ConfigureCookie)
            .AddOpenIdConnect(OidcScheme, options => ConfigureOidc(options, settings));
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = SessionCookieName;
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
        // Secure follows the effective scheme (UseForwardedHeaders runs first), so http://localhost still works.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = SessionLifetime;
        options.SlidingExpiration = false;
        // The gate decides redirect-or-401; these must never answer with a redirect an API client cannot follow.
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    private static void ConfigureOidc(OpenIdConnectOptions options, EntraAuthSettings settings)
    {
        options.SignInScheme = CookieScheme;
        options.Authority = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.ResponseType = "code";
        // Query, not form_post: the callback is then a same-site GET, so the correlation cookie can stay Lax and work on
        // plain http://localhost (a SameSite=None cookie must be Secure).
        options.ResponseMode = "query";
        options.UsePkce = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.CallbackPath = CallbackPath;
        options.SignedOutCallbackPath = SignedOutCallbackPath;
        options.RemoteSignOutPath = RemoteSignOutPath;
        // The app never reads an access or refresh token, so none is stored (also keeps the cookie to one chunk).
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.UseTokenLifetime = false;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        options.Events.OnRedirectToIdentityProvider = ctx =>
        {
            ctx.ProtocolMessage.RedirectUri = settings.BaseUrl(ctx.Request) + CallbackPath;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToIdentityProviderForSignOut = ctx =>
        {
            ctx.ProtocolMessage.PostLogoutRedirectUri = settings.BaseUrl(ctx.Request) + SignedOutCallbackPath;
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = ctx =>
        {
            if (TrimPrincipal(ctx.Principal) is { } trimmed)
            {
                ctx.Principal = trimmed;
            }
            else
            {
                ctx.Fail("The ID token has no 'oid' claim.");
            }

            return Task.CompletedTask;
        };
        options.Events.OnTicketReceived = ctx =>
        {
            ctx.Properties ??= new AuthenticationProperties();
            ctx.Properties.IsPersistent = true;
            ctx.Properties.AllowRefresh = false;
            ctx.Properties.ExpiresUtc = DateTimeOffset.UtcNow.Add(SessionLifetime);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// The one principal the cookie stores: only the claims <c>CurrentUser</c> reads, so the ticket stays one cookie chunk
    /// (Android sends a single cookie; a chunked ticket would be an endless sign-in loop). Null when the token has no
    /// <c>oid</c>: the user id is the object id, never <c>sub</c>, which is pairwise per app and would orphan stored data.
    /// </summary>
    public static ClaimsPrincipal? TrimPrincipal(ClaimsPrincipal? source)
    {
        var oid = source?.FindFirstValue(PrincipalHeaderMiddleware.ObjectIdClaim);
        if (source is null || string.IsNullOrWhiteSpace(oid))
        {
            return null;
        }

        var claims = new List<Claim> { new(PrincipalHeaderMiddleware.ObjectIdClaim, oid) };
        foreach (var type in new[] { PrincipalHeaderMiddleware.NameClaim, PrincipalHeaderMiddleware.PreferredUsernameClaim })
        {
            if (source.FindFirstValue(type) is { Length: > 0 } value)
            {
                claims.Add(new Claim(type, value));
            }
        }

        claims.AddRange(source.FindAll(PrincipalHeaderMiddleware.RolesClaim)
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => new Claim(PrincipalHeaderMiddleware.RolesClaim, c.Value)));

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims, "Entra", PrincipalHeaderMiddleware.NameClaim, PrincipalHeaderMiddleware.RolesClaim));
    }

    /// <summary>
    /// The origin the browser used: <c>Auth:PublicBaseUrl</c> when set (a proxy that rewrites Host), else the request's.
    /// <c>XForwardedHost</c> is deliberately not trusted (ADR 0042), which is why this setting exists.
    /// </summary>
    public static string BaseUrl(this EntraAuthSettings settings, HttpRequest request)
        => settings.PublicBaseUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>The request's own path and query, as a same-origin relative return target for the challenge.</summary>
    internal static string ReturnTarget(HttpRequest request)
        => AuthEndpoints.SafeRelativeTarget(
            request.PathBase.Add(request.Path).ToUriComponent() + request.QueryString.ToUriComponent());
}
