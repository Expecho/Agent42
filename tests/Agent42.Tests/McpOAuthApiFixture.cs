using System.Net;
using System.Text;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> for <see cref="McpOAuthEndpointTests"/>: the same host, but with the
/// <see cref="McpOAuthClient"/> pointed at a fake authorization server instead of the base fixture's inert
/// one, so discovery and the code exchange actually complete.
/// </summary>
public sealed class McpOAuthApiFixture : SharedApiFixture
{
    protected override void Configure(WebApplicationBuilder builder) =>
        builder.Services.AddSingleton(new McpOAuthClient(new HttpClient(new FakeAuthServerHandler())));

    // A fake authorization server: serves PRM + AS metadata for discovery and a token endpoint for exchange.
    private sealed class FakeAuthServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            HttpResponseMessage Json(string json) =>
                new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

            var response = url switch
            {
                "https://mcp.example.com/.well-known/oauth-protected-resource" =>
                    Json("""{"authorization_servers":["https://as.example.com"],"scopes_supported":["mcp.read"]}"""),
                _ when url.StartsWith("https://as.example.com/.well-known/oauth-authorization-server") =>
                    Json("""{"authorization_endpoint":"https://as.example.com/authorize","token_endpoint":"https://as.example.com/token"}"""),
                "https://as.example.com/token" =>
                    Json("""{"access_token":"at-123","refresh_token":"rt-456","expires_in":3600}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
        }
    }
}
