using System.Net;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The ADR 0081 resolver rules against a real config grain and a fake token endpoint: it re-reads the current
/// bundle, single-flights per (user, server), never clobbers a concurrent consent, and marks a server only on
/// <c>invalid_grant</c>.
/// </summary>
public sealed class McpOAuthTokenResolverTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private readonly FakeTokenEndpoint _endpoint = new();
    private readonly IMcpSecretProtector _protector =
        new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance);

    private McpOAuthTokenResolver NewResolver() =>
        new(Cluster.GrainFactory, _protector, new McpOAuthClient(new HttpClient(_endpoint)),
            NullLogger<McpOAuthTokenResolver>.Instance);

    private static McpOAuthTokens Tokens(string access = "access-old", string? refresh = "refresh-1", TimeSpan? expiresIn = null) =>
        new(access, refresh, DateTimeOffset.UtcNow.Add(expiresIn ?? TimeSpan.FromSeconds(10)),
            "https://as.example.com/token", "client-1", ["mcp.read"]);

    private StoredMcpServer Server(McpOAuthTokens? tokens) => new(
        Id: "o1",
        Name: "oauth-o1",
        Endpoint: "https://mcp.example.com/mcp",
        Auth: nameof(McpAuthMode.InteractiveOAuth),
        Enabled: true,
        TokenCiphertext: tokens is null ? null : _protector.Protect(tokens.Serialize()));

    private async Task<IUserMcpConfigGrain> SeedAsync(string user, McpOAuthTokens? tokens)
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(Server(tokens));
        return grain;
    }

    private async Task<McpOAuthTokens> StoredAsync(IUserMcpConfigGrain grain) =>
        McpOAuthTokens.Deserialize(_protector.Unprotect(Assert.Single((await grain.GetAsync()).Servers).TokenCiphertext))!;

    [Fact]
    public async Task NoStoredToken_IsNotConnected()
    {
        await SeedAsync("oauth-res-none", tokens: null);

        var result = await NewResolver().ResolveAsync("oauth-res-none", "o1");

        Assert.Equal(McpOAuthState.NotConnected, result.State);
        Assert.Equal(McpOAuthState.NotConnected, (await NewResolver().ResolveAsync("oauth-res-none", "missing")).State);
    }

    [Fact]
    public async Task AFreshToken_IsReturned_WithoutContactingTheServer()
    {
        await SeedAsync("oauth-res-fresh", Tokens(expiresIn: TimeSpan.FromHours(1)));

        var result = await NewResolver().ResolveAsync("oauth-res-fresh", "o1");

        Assert.Equal(new McpOAuthResolution(McpOAuthState.Connected, "access-old", result.ExpiresAt), result);
        Assert.Equal(0, _endpoint.Calls);
    }

    [Fact]
    public async Task ExpiredWithNoRefreshToken_IsReauthRequired()
    {
        await SeedAsync("oauth-res-norefresh", Tokens(refresh: null, expiresIn: TimeSpan.FromMinutes(-1)));

        var result = await NewResolver().ResolveAsync("oauth-res-norefresh", "o1");

        Assert.Equal(McpOAuthState.ReauthRequired, result.State);
        Assert.Equal(0, _endpoint.Calls);
    }

    // AC5 — the refresh presents the CURRENT stored refresh token, not the one the connection started with: with
    // rotating refresh tokens, a snapshot would present a rotated-away token and earn a false invalid_grant.
    [Fact]
    public async Task Refresh_UsesTheCurrentStoredBundle_NotTheSnapshot()
    {
        const string user = "oauth-res-current";
        var grain = await SeedAsync(user, Tokens(expiresIn: TimeSpan.FromHours(1)));
        var resolver = NewResolver();
        Assert.Equal("access-old", (await resolver.ResolveAsync(user, "o1")).AccessToken); // the "connect-time" read

        // Someone else rotated the bundle since (another connection's refresh, a re-consent).
        await grain.SaveAsync(Server(Tokens(access: "access-rotated", refresh: "refresh-rotated")));

        var result = await resolver.ResolveAsync(user, "o1");

        Assert.Equal(["refresh-rotated"], _endpoint.SeenRefreshTokens);
        Assert.Equal("access-new", result.AccessToken);
    }

    // AC4 — two concurrent resolutions of one (user, server) hit the token endpoint ONCE; the waiter re-reads
    // inside the gate and finds the fresh token. Two separate resolver callers stand in for two providers (two
    // retained cache versions, or the Tester) — the gate lives in the singleton resolver, not in a provider.
    [Fact]
    public async Task Resolver_SingleFlights_AcrossProviders()
    {
        const string user = "oauth-res-singleflight";
        await SeedAsync(user, Tokens());
        var resolver = NewResolver();
        _endpoint.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = resolver.ResolveAsync(user, "o1");
        var second = resolver.ResolveAsync(user, "o1");
        await WaitUntilAsync(() => _endpoint.Calls >= 1);
        _endpoint.Gate.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, _endpoint.Calls);
        Assert.All(results, r => Assert.Equal("access-new", r.AccessToken));
    }

    // AC4b — a refresh whose write loses the compare-and-swap to the OAuth callback's fresh consent keeps the
    // consent's bundle, and returns ITS token.
    [Fact]
    public async Task ARefreshLosingTheRace_KeepsTheCallbacksBundle()
    {
        const string user = "oauth-res-race";
        var grain = await SeedAsync(user, Tokens());
        _endpoint.BeforeRespond = () =>
            grain.SaveAsync(Server(Tokens(access: "access-consent", refresh: "refresh-consent", expiresIn: TimeSpan.FromHours(1))));

        var result = await NewResolver().ResolveAsync(user, "o1");

        Assert.Equal(new McpOAuthResolution(McpOAuthState.Connected, "access-consent", result.ExpiresAt), result);
        Assert.Equal("access-consent", (await StoredAsync(grain)).AccessToken);
    }

    // AC6 — invalid_grant marks the bundle; a later resolution reports ReauthRequired WITHOUT contacting the
    // authorization server again, and the version did not move (the mark is a non-bumping write).
    [Fact]
    public async Task InvalidGrant_MarksTheBundleReauthRequired()
    {
        const string user = "oauth-res-invalidgrant";
        var grain = await SeedAsync(user, Tokens());
        var version = await grain.GetVersionAsync();
        _endpoint.Respond = FakeTokenEndpoint.InvalidGrant;
        var resolver = NewResolver();

        var result = await resolver.ResolveAsync(user, "o1");

        Assert.Equal(McpOAuthState.ReauthRequired, result.State);
        Assert.Equal("access-old", result.AccessToken); // the last known token, for the per-request bearer
        Assert.NotNull((await StoredAsync(grain)).ReauthRequiredAt);
        Assert.Equal(version, await grain.GetVersionAsync());

        Assert.Equal(McpOAuthState.ReauthRequired, (await resolver.ResolveAsync(user, "o1")).State);
        Assert.Equal(1, _endpoint.Calls);
    }

    // AC7 — a transient failure (5xx, a 4xx that is not invalid_grant, a network error) marks NOTHING: nagging the
    // user to re-consent over a blip would be worse than the bug being fixed.
    [Theory]
    [InlineData("503")]
    [InlineData("invalid_client")]
    [InlineData("network")]
    public async Task ATransientRefreshFailure_NeverMarksReauth(string failure)
    {
        var user = $"oauth-res-transient-{failure}";
        var grain = await SeedAsync(user, Tokens());
        _endpoint.Respond = failure switch
        {
            "503" => () => FakeTokenEndpoint.Json(HttpStatusCode.ServiceUnavailable, "upstream down"),
            "invalid_client" => () => FakeTokenEndpoint.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""),
            _ => () => throw new HttpRequestException("name resolution failed"),
        };

        var result = await NewResolver().ResolveAsync(user, "o1");

        Assert.Equal(new McpOAuthResolution(McpOAuthState.Connected, "access-old", result.ExpiresAt), result);
        Assert.Null((await StoredAsync(grain)).ReauthRequiredAt);
    }

    // A request that just got a 401 with the stored token forces a refresh even though the token is not near
    // expiry (the server revoked it early) — the half of AC4c that lives in the resolver.
    [Fact]
    public async Task ARejectedToken_IsRefreshed_EvenBeforeItsExpiry()
    {
        const string user = "oauth-res-rejected";
        await SeedAsync(user, Tokens(expiresIn: TimeSpan.FromHours(1)));

        var result = await NewResolver().ResolveAsync(user, "o1", rejectedAccessToken: "access-old");

        Assert.Equal("access-new", result.AccessToken);
        Assert.Equal(1, _endpoint.Calls);
    }

    // A marked bundle stays marked for ordinary resolutions, but a user-pressed Test (force) retries the refresh
    // and a success clears the mark — the resolver half of AC10.
    [Fact]
    public async Task Force_RetriesAMarkedBundle_AndClearsTheMarkOnSuccess()
    {
        const string user = "oauth-res-force";
        var grain = await SeedAsync(user, Tokens() with { ReauthRequiredAt = DateTimeOffset.UtcNow });
        var resolver = NewResolver();

        Assert.Equal(McpOAuthState.ReauthRequired, (await resolver.ResolveAsync(user, "o1")).State);
        Assert.Equal(0, _endpoint.Calls);

        var forced = await resolver.ResolveAsync(user, "o1", force: true);

        Assert.Equal(new McpOAuthResolution(McpOAuthState.Connected, "access-new", forced.ExpiresAt), forced);
        Assert.Null((await StoredAsync(grain)).ReauthRequiredAt);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            await Task.Delay(10);
        }
    }
}
