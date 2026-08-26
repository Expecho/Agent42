using System.Net;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that records every outbound request and answers from a
/// responder delegate. Shared by <see cref="GoogleOAuthClientTests"/> and
/// <see cref="GoogleCalendarServiceTests"/>.
///
/// <para>It records the whole <see cref="Recorded"/> list rather than only the last call because the
/// calendar tests assert on the <b>request line</b> — the method, the path and the query — which is
/// where <c>singleEvents=true</c>, <c>orderBy=startTime</c> and (the one irreversible outward-facing
/// parameter in the feature) <c>sendUpdates=none</c> either appear or silently do not. That is the
/// <c>GoogleGeocoderTests</c>/<c>ContextWebPageFetcherTests</c> discipline; the repo's existing
/// <c>FakeHttpHandler</c> keeps only the last body, which cannot express a refresh-then-call sequence.
/// </para>
/// </summary>
public sealed class RecordingHttpHandler : HttpMessageHandler
{
    /// <summary>One captured outbound request.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Url">The absolute request URL, including the query string.</param>
    /// <param name="Body">The request body, or an empty string when there was none.</param>
    /// <param name="Authorization">The <c>Authorization</c> header value, or null.</param>
    public sealed record Call(string Method, string Url, string Body, string? Authorization);

    /// <summary>Every request made through this handler, in order.</summary>
    public List<Call> Recorded { get; } = [];

    /// <summary>Answers a request. Defaults to 404 so an unexpected call is visible, not silently fine.</summary>
    public Func<Call, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = new Call(
            request.Method.Method,
            request.RequestUri!.AbsoluteUri,
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.Authorization?.ToString());

        Recorded.Add(call);
        return Responder(call);
    }

    /// <summary>A 200 carrying <paramref name="json"/>.</summary>
    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
