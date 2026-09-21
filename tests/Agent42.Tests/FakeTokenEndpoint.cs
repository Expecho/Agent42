using System.Net;
using System.Text;

namespace Agent42.Tests;

/// <summary>
/// A scriptable OAuth token endpoint for the ADR 0081 refresh tests. Each POST records the refresh token it was
/// sent, optionally waits on <see cref="Gate"/> (to hold a refresh in flight while a race is staged), runs
/// <see cref="BeforeRespond"/> (to land a concurrent write mid-refresh), then answers with <see cref="Respond"/>.
/// Default answer: a fresh token pair.
/// </summary>
internal sealed class FakeTokenEndpoint : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public List<string> SeenRefreshTokens { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    public Func<Task>? BeforeRespond { get; set; }

    public Func<HttpResponseMessage> Respond { get; set; } =
        () => Json(HttpStatusCode.OK, """{"access_token":"access-new","refresh_token":"refresh-2","expires_in":3600}""");

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage InvalidGrant() =>
        Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        var form = await request.Content!.ReadAsStringAsync(cancellationToken);
        var refresh = form.Split('&').Select(p => p.Split('=')).FirstOrDefault(p => p[0] == "refresh_token");
        lock (SeenRefreshTokens)
        {
            SeenRefreshTokens.Add(refresh is null ? "" : Uri.UnescapeDataString(refresh[1]));
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (BeforeRespond is { } before)
        {
            await before();
        }

        return Respond();
    }
}
