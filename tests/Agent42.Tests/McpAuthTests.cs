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
