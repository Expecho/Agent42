using System.Net;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>A host whose GitHub OAuth App is configured and whose token endpoint is a fake that knows one code.</summary>
public sealed class GitHubSignInApiFixture : SharedApiFixture
{
    public const string GoodCode = "good-code";

    public const string IssuedToken = "gho_issued-by-the-fake";

    public RecordingHttpHandler GitHub { get; } = new()
    {
        Responder = call => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // GitHub answers a bad code with 200 and an error member, so the fake does too.
            Content = new StringContent(
                call.Body.Contains($"code={GoodCode}", StringComparison.Ordinal)
                    ? $$"""{"access_token":"{{IssuedToken}}","token_type":"bearer","scope":""}"""
                    : """{"error":"bad_verification_code","error_description":"<script>reflected?</script>"}""",
                Encoding.UTF8, "application/json"),
        },
    };

    protected override void Configure(WebApplicationBuilder builder)
        => builder.Services.AddSingleton(new GitHubOAuthClient(
            new HttpClient(GitHub), new GitHubOAuthOptions { ClientId = "Iv1.client", ClientSecret = "client-secret" }));
}

/// <summary>
/// "Sign in with GitHub" (ADR 0108): the start redirect (state + PKCE), the one-time state, the code exchange, the token
/// stored encrypted as the GitHub Copilot provider, and an upstream error never reflected.
/// </summary>
public sealed class GitHubSignInTests(GitHubSignInApiFixture fixture) : SharedClusterTests<GitHubSignInApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private readonly HttpClient _client = fixture.CreateNonRedirectingClient();

    private async Task<HttpResponseMessage> Get(string uri, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(IdHeader, user);
        return await _client.SendAsync(request);
    }

    private async Task<string> StartAsync(string user)
    {
        var start = await Get("/api/providers/github/start", user);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        return QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();
    }

    // AC3: the browser goes to GitHub with the app's client id, the callback on this host, a state and an S256 challenge.
    [Fact]
    public async Task Start_RedirectsToGitHub_WithStateAndPkce()
    {
        var start = await Get("/api/providers/github/start", $"user-{Guid.NewGuid():N}");

        var location = start.Headers.Location!;
        Assert.StartsWith(GitHubOAuthClient.AuthorizeEndpoint, location.AbsoluteUri, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("Iv1.client", query["client_id"]);
        Assert.Equal($"http://localhost{AgentApi.GitHubCallbackPath}", query["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
    }

    // AC2/AC3: a valid callback stores the token ENCRYPTED as the user's GitHub Copilot provider; GET never shows it; the
    // exchange sends the verifier that matches the challenge.
    [Fact]
    public async Task Callback_StoresTokenEncrypted_AsTheGitHubCopilotProvider()
    {
        var user = $"user-{Guid.NewGuid():N}";
        var state = await StartAsync(user);

        var callback = await Get($"{AgentApi.GitHubCallbackPath}?code={GitHubSignInApiFixture.GoodCode}&state={state}", user);

        Assert.Equal("/?github=connected", callback.Headers.Location!.OriginalString);
        var provider = Assert.Single((await Cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>(user).GetAsync()).Providers);
        Assert.Equal((GitHubCopilotProvider.Key, GitHubCopilotProvider.Kind), (provider.Key, provider.Kind));
        Assert.NotNull(provider.ApiKeyCiphertext);
        Assert.DoesNotContain(GitHubSignInApiFixture.IssuedToken, provider.ApiKeyCiphertext, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", Fixture.GitHub.Recorded[^1].Body, StringComparison.Ordinal);

        using var providers = new HttpRequestMessage(HttpMethod.Get, "/api/providers");
        providers.Headers.Add(IdHeader, user);
        var raw = await (await _client.SendAsync(providers)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(GitHubSignInApiFixture.IssuedToken, raw, StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(raw).RootElement.GetProperty("gitHubSignIn").GetBoolean());
    }

    // AC3: a state from nowhere, a replayed one, and another user's state are all refused, and nothing is stored.
    [Fact]
    public async Task Callback_UnknownReplayedOrForeignState_IsRefused()
    {
        var alice = $"user-{Guid.NewGuid():N}";
        var bob = $"user-{Guid.NewGuid():N}";
        var state = await StartAsync(alice);

        var foreign = await Get($"{AgentApi.GitHubCallbackPath}?code={GitHubSignInApiFixture.GoodCode}&state={state}", bob);
        var unknown = await Get($"{AgentApi.GitHubCallbackPath}?code={GitHubSignInApiFixture.GoodCode}&state=forged", alice);
        (await Get($"{AgentApi.GitHubCallbackPath}?code={GitHubSignInApiFixture.GoodCode}&state={state}", alice)).EnsureRedirect("/?github=connected");
        var replay = await Get($"{AgentApi.GitHubCallbackPath}?code={GitHubSignInApiFixture.GoodCode}&state={state}", alice);

        foreach (var refused in new[] { foreign, unknown, replay })
        {
            Assert.Equal("/?github=error&reason=unknown_state", refused.Headers.Location!.OriginalString);
        }

        Assert.Empty((await Cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>(bob).GetAsync()).Providers);
    }

    // GitHub's error body is never reflected into the redirect, and nothing is stored.
    [Fact]
    public async Task Callback_ExchangeError_IsGeneric_AndStoresNothing()
    {
        var user = $"user-{Guid.NewGuid():N}";
        var state = await StartAsync(user);

        var callback = await Get($"{AgentApi.GitHubCallbackPath}?code=bad&state={state}", user);

        Assert.Equal("/?github=error&reason=token_exchange_failed", callback.Headers.Location!.OriginalString);
        Assert.Empty((await Cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>(user).GetAsync()).Providers);
    }

    // The user cancelling on GitHub comes back as a fixed reason, never GitHub's own text.
    [Fact]
    public async Task Callback_UserDenied_IsAFixedReason()
    {
        var callback = await Get($"{AgentApi.GitHubCallbackPath}?error=access_denied&error_description=evil", $"user-{Guid.NewGuid():N}");

        Assert.Equal("/?github=error&reason=denied", callback.Headers.Location!.OriginalString);
    }
}

/// <summary>AC4: a host without the OAuth App (DevHost, a deploy without the secret) answers 503 and says so on GET.</summary>
public sealed class GitHubSignInUnconfiguredTests(SharedApiFixture fixture) : SharedClusterTests<SharedApiFixture>(fixture)
{
    [Fact]
    public async Task Start_IsUnavailable_AndProvidersSaySo()
    {
        using var start = new HttpRequestMessage(HttpMethod.Get, "/api/providers/github/start");
        start.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "user-unconfigured");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Fixture.Client.SendAsync(start)).StatusCode);

        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/providers");
        get.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", "user-unconfigured");
        var raw = await (await Fixture.Client.SendAsync(get)).Content.ReadAsStringAsync();
        Assert.False(JsonDocument.Parse(raw).RootElement.GetProperty("gitHubSignIn").GetBoolean());
    }
}

internal static class RedirectAssertions
{
    public static void EnsureRedirect(this HttpResponseMessage response, string location)
        => Assert.Equal(location, response.Headers.Location!.OriginalString);
}
