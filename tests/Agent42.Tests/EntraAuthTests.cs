using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Agent42.Backend;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Agent42.Tests;

/// <summary>
/// The <c>Auth:Mode=entra</c> pipeline (ADR 0145) on a TestServer with the REAL registration, stamper and gate, in the order
/// Program.cs uses. The OIDC metadata is a static stub, so a challenge builds its redirect with no network.
/// </summary>
public sealed class EntraAuthTests : IDisposable
{
    private const string Oid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Authorize = "https://login.test/authorize";

    private readonly string _wwwroot = Directory.CreateTempSubdirectory("entra-www").FullName;

    public void Dispose() => Directory.Delete(_wwwroot, recursive: true);

    private async Task<(WebApplication App, HttpClient Client)> BuildAsync(string? publicBaseUrl = null)
    {
        Directory.CreateDirectory(Path.Combine(_wwwroot, "shared"));
        File.WriteAllText(Path.Combine(_wwwroot, "index.html"), "<html/>");
        File.WriteAllText(Path.Combine(_wwwroot, "sw.js"), "//sw");
        File.WriteAllText(Path.Combine(_wwwroot, "logs.html"), "<html/>");
        File.WriteAllText(Path.Combine(_wwwroot, "shared", "index.html"), "<html/>");

        var settings = EntraAuthSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Entra:TenantId"] = "tenant-1",
            ["Auth:Entra:ClientId"] = "client-1",
            ["Auth:Entra:ClientSecret"] = "secret",
            ["Auth:PublicBaseUrl"] = publicBaseUrl,
        }).Build());

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(UserIdAliases.Empty);
        builder.Services.AddEntraAuth(settings);
        builder.Services.Configure<OpenIdConnectOptions>(EntraAuthRegistration.OidcScheme, o =>
            o.Configuration = new OpenIdConnectConfiguration { AuthorizationEndpoint = Authorize, Issuer = "https://login.test/" });

        var app = builder.Build();
        app.UseAuthentication();
        app.Use(PrincipalHeaderMiddleware.InvokeAsync);
        app.Use(AuthGateMiddleware.InvokeAsync);
        var files = new PhysicalFileProvider(_wwwroot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        // Under /.auth/* so the gate lets a signed-out test client reach it, like the real login.
        app.MapGet("/.auth/test-signin", async (HttpContext http, int? groups) =>
        {
            var claims = new List<Claim> { new("oid", Oid), new("sub", "sub-1"), new("name", "Ada"), new("preferred_username", "ada@example.com"), new("roles", "Admin") };
            claims.AddRange(Enumerable.Range(0, groups ?? 0).Select(i => new Claim("groups", Guid.NewGuid().ToString())));
            var principal = EntraAuthRegistration.TrimPrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")))!;
            await http.SignInAsync(EntraAuthRegistration.CookieScheme, principal, new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(EntraAuthRegistration.SessionLifetime),
            });
            return Results.Ok();
        });
        app.MapGet("/api/whoami", (HttpContext http) => http.GetUserId());
        app.MapPost("/api/write", (HttpContext http) => http.GetUserId());
        app.MapGet("/api/share/{**rest}", (HttpContext http) => http.GetUserId());
        app.MapGet("/api/agent-run", (HttpContext http) => http.GetUserId());

        await app.StartAsync();
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        return (app, client);
    }

    private static HttpRequestMessage Req(HttpMethod method, string path, string? accept = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (accept is not null)
        {
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return request;
    }

    private static async Task<string> SignInCookieAsync(HttpClient client, int groups = 0)
    {
        var response = await client.GetAsync($"/.auth/test-signin?groups={groups}");
        return SessionCookie(response).Split(';')[0];
    }

    private static string SessionCookie(HttpResponseMessage response)
        => response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(EntraAuthRegistration.SessionCookieName + "=", StringComparison.Ordinal));

    [Fact]
    public async Task Anonymous_Browser_Redirects()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var response = await client.SendAsync(Req(HttpMethod.Get, "/api/whoami?x=1", accept: "text/html"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(Authorize, location, StringComparison.Ordinal);
        Assert.Contains("client_id=client-1", location, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=https%3A%2F%2Flocalhost%2Fsignin-oidc", location, StringComparison.Ordinal);
        Assert.Contains("code_challenge=", location, StringComparison.Ordinal);
        Assert.DoesNotContain("form_post", location, StringComparison.Ordinal); // query is the code flow default, so the parameter is omitted
    }

    [Fact]
    public async Task Anonymous_Api_401()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var response = await client.SendAsync(Req(HttpMethod.Get, "/api/whoami", accept: "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Anonymous_Post_401()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        // Even with a browser-like Accept: a redirected POST would lose its body, so it is never redirected.
        var response = await client.SendAsync(Req(HttpMethod.Post, "/api/write", accept: "text/html"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Android_Request_NoAccept_Gets401()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var response = await client.SendAsync(Req(HttpMethod.Get, "/api/whoami"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("/logs.html")]
    [InlineData("/sw.js")]
    [InlineData("/")]
    public async Task Anonymous_StaticFile_Challenged(string path)
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var browser = await client.SendAsync(Req(HttpMethod.Get, path, accept: "text/html"));
        var fetch = await client.SendAsync(Req(HttpMethod.Get, path));

        Assert.Equal(HttpStatusCode.Redirect, browser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fetch.StatusCode);
    }

    [Fact]
    public async Task PublicPaths_PassThrough_AnonymouslyWithNoPrincipal()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var shared = await client.SendAsync(Req(HttpMethod.Get, "/shared/", accept: "text/html"));
        var share = await client.SendAsync(Req(HttpMethod.Get, "/api/share/shr_x"));
        var forged = Req(HttpMethod.Get, "/api/agent-run");
        forged.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "attacker");
        var run = await client.SendAsync(forged);

        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        Assert.Equal("local-dev", await share.Content.ReadAsStringAsync());
        Assert.Equal("local-dev", await run.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SessionCookie_RoundTrips_AndUsesOidNotSub()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var cookie = await SignInCookieAsync(client);
        var response = await client.SendAsync(Req(HttpMethod.Get, "/api/whoami", cookie: cookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Oid, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SessionCookie_Contract()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var setCookie = SessionCookie(await client.GetAsync("/.auth/test-signin"));

        Assert.StartsWith("AppServiceAuthSession=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);

        var expires = setCookie.Split(';').Select(p => p.Trim()).Single(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        var at = DateTimeOffset.Parse(expires["expires=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange((at - DateTimeOffset.UtcNow).TotalDays, 13.99, 14.01);

        var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(EntraAuthRegistration.OidcScheme);
        Assert.False(options.SaveTokens);
        Assert.False(options.UseTokenLifetime);
        Assert.False(options.MapInboundClaims);
        Assert.Equal("/signout-callback-oidc", options.SignedOutCallbackPath.Value);
        Assert.Equal("/signout-oidc", options.RemoteSignOutPath.Value);
    }

    [Fact]
    public async Task Cookie_StaysSingleChunk()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var response = await client.GetAsync("/.auth/test-signin?groups=400");
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();

        Assert.Single(cookies);
        Assert.DoesNotContain(cookies, c => c.StartsWith("AppServiceAuthSessionC1", StringComparison.Ordinal));
        Assert.True(cookies[0].Length < 4000, $"cookie is {cookies[0].Length} chars");
    }

    [Fact]
    public async Task CrossSiteWrite_Rejected()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;
        var cookie = await SignInCookieAsync(client);

        async Task<HttpStatusCode> Post(Action<HttpRequestMessage>? tweak)
        {
            var request = Req(HttpMethod.Post, "/api/write", cookie: cookie);
            tweak?.Invoke(request);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Post(null));
        Assert.Equal(HttpStatusCode.OK, await Post(r => r.Headers.Add("Origin", "https://localhost")));
        Assert.Equal(HttpStatusCode.OK, await Post(r => r.Headers.Add("Sec-Fetch-Site", "same-origin")));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(r => r.Headers.Add("Origin", "https://evil.example")));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(r => r.Headers.Add("Origin", "null")));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(r => r.Headers.Add("Sec-Fetch-Site", "cross-site")));
    }

    [Fact]
    public async Task Gate_ReturnTarget_CannotBeAnOpenRedirect()
    {
        var captured = new List<string?>();
        var (app, client) = await BuildAsync();
        await using var _ = app;
        var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(EntraAuthRegistration.OidcScheme);
        var previous = options.Events.OnRedirectToIdentityProvider;
        options.Events.OnRedirectToIdentityProvider = async ctx =>
        {
            captured.Add(ctx.Properties.RedirectUri);
            await previous(ctx);
        };

        await client.SendAsync(Req(HttpMethod.Get, "//evil.example/x", accept: "text/html"));
        await client.SendAsync(Req(HttpMethod.Get, "/api/whoami?a=1", accept: "text/html"));

        // TestServer normalises a leading // away, so this pins the property: every target is same-origin relative.
        Assert.All(captured, t => Assert.Equal(t, AuthEndpoints.SafeRelativeTarget(t)));
        Assert.Equal("/api/whoami?a=1", captured[1]);
    }

    [Fact]
    public async Task PublicBaseUrl_IsUsedForTheRedirectUri()
    {
        var (app, client) = await BuildAsync("https://chat.example.com/");
        await using var _ = app;

        var response = await client.SendAsync(Req(HttpMethod.Get, "/", accept: "text/html"));

        Assert.Contains("redirect_uri=https%3A%2F%2Fchat.example.com%2Fsignin-oidc", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_WithoutOid_Rejected()
    {
        var (app, _) = await BuildAsync();
        await using var __ = app;
        var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(EntraAuthRegistration.OidcScheme);
        var scheme = new AuthenticationScheme(EntraAuthRegistration.OidcScheme, null, typeof(OpenIdConnectHandler));

        TokenValidatedContext Ctx(params Claim[] claims) => new(
            new DefaultHttpContext(), scheme, options,
            new ClaimsPrincipal(new ClaimsIdentity(claims, "x")), new AuthenticationProperties());

        var noOid = Ctx(new Claim("sub", "s"), new Claim("name", "n"));
        await options.Events.TokenValidated(noOid);
        var withOid = Ctx(new Claim("oid", Oid), new Claim("groups", "g"));
        await options.Events.TokenValidated(withOid);

        Assert.NotNull(noOid.Result?.Failure);
        Assert.Null(withOid.Result?.Failure);
        Assert.Equal(Oid, withOid.Principal!.FindFirstValue("oid"));
        Assert.Null(withOid.Principal.FindFirst("groups"));
    }
}
