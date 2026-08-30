using System.Net;
using System.Text;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// <see cref="SharedApiFixture"/> for the host-level half of <see cref="SearxngProxyTests"/>: the same
/// host, plus the <c>searxng-ui</c> named client pointed at a stub sidecar.
///
/// <para>It <b>creates</b> that client rather than overriding one, because <see cref="SharedApiFixture"/>
/// registers no <c>IHttpClientFactory</c> and no named clients at all — which is exactly what
/// <see cref="SearxngProxyTests.Proxy_ReturnsServiceUnavailable_WhenNoHttpClientFactory"/> relies on, and
/// why that test uses the bare fixture instead of this one. Note that <c>AddHttpClient</c> configuration
/// is additive rather than last-wins (unlike <c>AddSingleton</c>); only the primary handler is
/// effectively replaced.</para>
/// </summary>
public sealed class SearxngProxyFixture : SharedApiFixture
{
    /// <summary>The authority the stub stands in for; also what the proxy's SSRF check pins against.</summary>
    public const string UpstreamBase = "http://searxng.stub:8888/";

    /// <summary>Requests the stub received, so a test can assert on what did — and did not — reach it.</summary>
    public List<HttpRequestMessage> Received { get; } = [];

    /// <summary>Bodies the stub received, captured eagerly because the request stream is not replayable.</summary>
    public List<string> ReceivedBodies { get; } = [];

    protected override void Configure(WebApplicationBuilder builder) =>
#pragma warning disable EXTEXP0001
        builder.Services.AddHttpClient(SearxngProxy.HttpClientName, http => http.BaseAddress = new Uri(UpstreamBase))
            .ConfigurePrimaryHttpMessageHandler(() => new StubSidecarHandler(this))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

    /// <summary>
    /// Stands in for the sidecar. It is the PRIMARY handler, which is the point: the real client disables
    /// redirect-following and cookies on exactly this handler, so a stub installed anywhere else would let
    /// the redirect rules pass while being dead in production.
    /// </summary>
    private sealed class StubSidecarHandler(SearxngProxyFixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            fixture.Received.Add(request);
            fixture.ReceivedBodies.Add(
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

            var path = request.RequestUri!.PathAndQuery;
            if (path.StartsWith("/redirect-home", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                // Absolute, and on the base URL's own authority — the shape a real preferences save emits.
                response.Headers.Location = new Uri(UpstreamBase + "searxng/");
                return response;
            }

            if (path.StartsWith("/redirect-offsite", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://evil.example/steal");
                return response;
            }

            if (path.StartsWith("/set-prefs", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("saved", Encoding.UTF8, "text/plain"),
                };
                response.Headers.TryAddWithoutValidation("Set-Cookie", "theme=simple; Max-Age=157680000; Path=/");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"upstream saw {path}", Encoding.UTF8, "text/html"),
            };
        }
    }
}
