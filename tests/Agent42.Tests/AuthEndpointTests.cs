using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Agent42.Backend;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Agent42.Tests;

public sealed class AuthEndpointTests
{
    private const string Oid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string EndSession = "https://login.test/logout";

    private readonly List<string?> _redirectTargets = [];

    private async Task<(WebApplication App, HttpClient Client)> BuildAsync()
    {
        var settings = EntraAuthSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Entra:TenantId"] = "t",
            ["Auth:Entra:ClientId"] = "c",
            ["Auth:Entra:ClientSecret"] = "s",
        }).Build());

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(UserIdAliases.Empty);
        builder.Services.AddEntraAuth(settings);
        builder.Services.Configure<OpenIdConnectOptions>(EntraAuthRegistration.OidcScheme, o =>
        {
            o.Configuration = new OpenIdConnectConfiguration
            {
                AuthorizationEndpoint = "https://login.test/authorize",
                EndSessionEndpoint = EndSession,
                Issuer = "https://login.test/",
            };
            var previous = o.Events.OnRedirectToIdentityProvider;
            o.Events.OnRedirectToIdentityProvider = async ctx =>
            {
                _redirectTargets.Add(ctx.Properties.RedirectUri);
                await previous(ctx);
            };
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.Use(PrincipalHeaderMiddleware.InvokeAsync);
        app.Use(AuthGateMiddleware.InvokeAsync);
        app.MapAuthEndpoints();
        app.MapGet("/.auth/test-signin", async (HttpContext http) =>
        {
            var principal = EntraAuthRegistration.TrimPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", Oid), new Claim("name", "Ada"), new Claim("preferred_username", "ada@example.com")], "test")))!;
            await http.SignInAsync(EntraAuthRegistration.CookieScheme, principal);
            return Results.Ok();
        });

        await app.StartAsync();
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        return (app, client);
    }

    private static async Task<string> CookieAsync(HttpClient client)
    {
        var response = await client.GetAsync("/.auth/test-signin");
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example")]
    [InlineData("http://evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("evil.example")]
    [InlineData("/ok\r\nSet-Cookie: x=1")]
    [InlineData("")]
    [InlineData(null)]
    public void PostLoginRedirect_RejectsAbsoluteAndProtocolRelative(string? value)
        => Assert.Equal("/", AuthEndpoints.SafeRelativeTarget(value));

    [Theory]
    [InlineData("/")]
    [InlineData("/c/abc?x=1")]
    [InlineData("/fleet/")]
    public void PostLoginRedirect_KeepsRelativePaths(string value)
        => Assert.Equal(value, AuthEndpoints.SafeRelativeTarget(value));

    [Fact]
    public async Task Login_ChallengesWithTheValidatedTarget_AndIsReachableSignedOut()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var good = await client.GetAsync("/.auth/login/aad?post_login_redirect_uri=/c/abc");
        var evil = await client.GetAsync("/.auth/login/aad?post_login_redirect_uri=//evil.example");

        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        Assert.StartsWith("https://login.test/authorize", good.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(["/c/abc", "/"], _redirectTargets);
        Assert.StartsWith("https://login.test/authorize", evil.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthMe_Is401Anonymous_AndJsonWhenSignedIn()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;

        var anonymous = await client.GetAsync("/.auth/me");
        var request = new HttpRequestMessage(HttpMethod.Get, "/.auth/me");
        request.Headers.Add("Cookie", await CookieAsync(client));
        var signedIn = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        using var doc = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync());
        var entry = doc.RootElement[0];
        Assert.Equal("ada@example.com", entry.GetProperty("user_id").GetString());
        Assert.Contains(entry.GetProperty("user_claims").EnumerateArray(),
            c => c.GetProperty("typ").GetString() == "oid" && c.GetProperty("val").GetString() == Oid);
    }

    [Fact]
    public async Task Logout_ClearsCookie_AndEndsTheEntraSession()
    {
        var (app, client) = await BuildAsync();
        await using var _ = app;
        var request = new HttpRequestMessage(HttpMethod.Get, "/.auth/logout?post_logout_redirect_uri=/bye");
        request.Headers.Add("Cookie", await CookieAsync(client));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(EndSession, location, StringComparison.Ordinal);
        Assert.Contains("post_logout_redirect_uri=https%3A%2F%2Flocalhost%2Fsignout-callback-oidc", location, StringComparison.Ordinal);
        var cleared = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("AppServiceAuthSession=", StringComparison.Ordinal));
        Assert.StartsWith("AppServiceAuthSession=;", cleared, StringComparison.Ordinal);
        Assert.Contains("1970", cleared, StringComparison.Ordinal);
    }
}
