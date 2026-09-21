using System.Net;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The MCP ClientCredentials auth mechanism (config-driven remote MCP servers, Phase 2). The real MSAL
/// token acquisition against Entra is deploy-smoke (per the plan); these tests own OUR code — the
/// <see cref="AuthHeaderHandler"/> that stamps whatever token the provider yields, and that the MSAL provider
/// constructs offline.
/// </summary>
public sealed class McpAuthTests
{
    private sealed class FakeAuthHeaderProvider(string name, string value) : IAuthHeaderProvider
    {
        public string HeaderName => name;
        public ValueTask<string> GetHeaderValueAsync(CancellationToken cancellationToken) => new(value);
    }

    private sealed class StubInnerHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    // AC11: the ClientCredentials token is stamped as an Authorization header on the outgoing request.
    [Fact]
    public async Task ClientCredentials_StampsBearer()
    {
        var inner = new StubInnerHandler();
        using var handler = new AuthHeaderHandler(new FakeAuthHeaderProvider("Authorization", "Bearer test-token"))
        {
            InnerHandler = inner,
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/mcp");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.NotNull(inner.Captured);
        Assert.Equal("Bearer test-token", inner.Captured!.Headers.GetValues("Authorization").Single());
    }

    // A pre-existing value for the same header is replaced, not duplicated (the handler removes first).
    [Fact]
    public async Task StampsHeader_ReplacesExistingValue()
    {
        var inner = new StubInnerHandler();
        using var handler = new AuthHeaderHandler(new FakeAuthHeaderProvider("Authorization", "Bearer fresh"))
        {
            InnerHandler = inner,
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/mcp");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer stale");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal("Bearer fresh", inner.Captured!.Headers.GetValues("Authorization").Single());
    }

    // A renewable provider (the InteractiveOAuth bearer, ADR 0081): the first send carries "Bearer old", its 401
    // triggers ONE renewal, and the SAME request is resent with the renewed value.
    private sealed class RenewableProvider(string first, string? renewed) : IRenewableAuthHeaderProvider
    {
        public List<string> Rejected { get; } = [];
        public string HeaderName => "Authorization";
        public ValueTask<string> GetHeaderValueAsync(CancellationToken cancellationToken) => new(first);

        public ValueTask<string?> RenewAfterUnauthorizedAsync(string rejectedValue, CancellationToken cancellationToken)
        {
            Rejected.Add(rejectedValue);
            return new(renewed);
        }
    }

    // Answers 401 unless the request carries the accepted bearer; records every Authorization it saw.
    private sealed class BearerCheckingHandler(string? accepted) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.Headers.GetValues("Authorization").Single());
            _ = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Seen[^1] == accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        }
    }

    private static async Task<HttpStatusCode> SendAsync(IAuthHeaderProvider provider, HttpMessageHandler inner)
    {
        using var handler = new AuthHeaderHandler(provider) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/mcp")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { jsonrpc = "2.0", method = "tools/call" }),
        };
        using var response = await invoker.SendAsync(request, CancellationToken.None);
        return response.StatusCode;
    }

    [Fact]
    public async Task A401_RenewsOnce_AndResendsTheSameRequest()
    {
        var provider = new RenewableProvider("Bearer old", "Bearer new");
        var inner = new BearerCheckingHandler("Bearer new");

        Assert.Equal(HttpStatusCode.OK, await SendAsync(provider, inner));
        Assert.Equal(["Bearer old", "Bearer new"], inner.Seen);
        Assert.Equal(["Bearer old"], provider.Rejected);
    }

    // Exactly one resend: a server that keeps answering 401 gets its 401 back, never a loop.
    [Fact]
    public async Task A401_AfterTheRetry_Stands()
    {
        var inner = new BearerCheckingHandler(accepted: null);

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(new RenewableProvider("Bearer old", "Bearer new"), inner));
        Assert.Equal(2, inner.Seen.Count);
    }

    // Nothing better to offer (renewal returns null) → no resend.
    [Fact]
    public async Task A401_WithNoRenewal_IsNotResent()
    {
        var inner = new BearerCheckingHandler(accepted: null);

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(new RenewableProvider("Bearer old", renewed: null), inner));
        Assert.Single(inner.Seen);
    }

    // The retry is opt-in: a plain provider (ClientCredentials) is never resent — a 401 there is configuration.
    [Fact]
    public async Task A401_OnANonRenewableProvider_IsNotResent()
    {
        var inner = new BearerCheckingHandler(accepted: null);

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(new FakeAuthHeaderProvider("Authorization", "Bearer x"), inner));
        Assert.Single(inner.Seen);
    }

    // The MSAL provider constructs offline (no network until a token is acquired) and stamps Authorization.
    // The real AcquireTokenForClient round-trip is deploy-smoke.
    [Fact]
    public void ClientCredentialsProvider_Constructs_WithAuthorizationHeader()
    {
        var provider = new ClientCredentialsTokenProvider(
            authority: "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000",
            clientId: "11111111-1111-1111-1111-111111111111",
            clientSecret: "a-secret",
            scopes: ["https://org.crm4.dynamics.com/.default"]);

        Assert.Equal("Authorization", provider.HeaderName);
    }
}
