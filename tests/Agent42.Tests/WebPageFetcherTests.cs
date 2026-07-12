using System.Net;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// These tests dial a real socket on purpose (see <see cref="TestHttpServer"/>): the guard under test
/// lives in <c>SocketsHttpHandler.ConnectCallback</c>, and a stubbed transport would never invoke it.
/// </summary>
public sealed class WebPageFetcherTests
{
    // Production policy: the real guard, which refuses loopback.
    private static WebPageFetcher Guarded() =>
        new(new HttpClient(WebPageFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable)));

    // Test policy: loopback is permitted so a test server is reachable, but IMDS/link-local is still
    // refused. This swaps only the *policy*; the guard's placement (connect time) is what's under test.
    private static WebPageFetcher LoopbackAllowed() =>
        new(new HttpClient(WebPageFetcher.CreateHandler(
            ip => IPAddress.IsLoopback(ip) || PrivateAddressGuard.IsPubliclyRoutable(ip))));

    // AC8 — the guard fires on the address actually dialled. The URL names a perfectly well-formed
    // http host; only a check at connect time knows it lands on loopback.
    [Fact]
    public async Task Fetch_ValidatesAddressAtConnectTime()
    {
        await using var server = TestHttpServer.Start(
            new TestHttpServer.HttpReply(200, "text/html", "<html><body>secret</body></html>"));

        var ex = await Assert.ThrowsAsync<BlockedAddressException>(
            () => Guarded().FetchAsync($"{server.BaseUrl}/internal"));

        Assert.Contains("non-public", ex.Message);
    }

    // AC8 — the case a resolve-then-request guard gets wrong. The *first* hop is allowed, and the
    // redirect points at Azure IMDS (the managed-identity token endpoint). Validating only the URL the
    // model supplied would sail straight into it; validating at connect time catches every hop.
    [Fact]
    public async Task Fetch_RedirectToPrivateAddress_IsBlocked()
    {
        await using var server = TestHttpServer.Start(new TestHttpServer.HttpReply(
            302, "text/html", string.Empty,
            Location: "http://169.254.169.254/metadata/identity/oauth2/token"));

        var ex = await Assert.ThrowsAsync<BlockedAddressException>(
            () => LoopbackAllowed().FetchAsync($"{server.BaseUrl}/redirect"));

        Assert.Contains("169.254.169.254", ex.Message);
    }

    // A literal IMDS URL, straight from the model, is refused by the production policy.
    [Fact]
    public async Task Fetch_ImdsUrl_IsBlocked() =>
        await Assert.ThrowsAsync<BlockedAddressException>(
            () => Guarded().FetchAsync("http://169.254.169.254/metadata/instance"));

    // Non-http(s) schemes never reach the transport at all (file://, ftp://, gopher://…).
    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/x")]
    [InlineData("not a url")]
    public async Task Fetch_RejectsNonHttpScheme(string url) =>
        await Assert.ThrowsAsync<BlockedAddressException>(() => Guarded().FetchAsync(url));

    // AC9 — a response far larger than the return budget is capped, not swallowed whole. The page is
    // 3 MB; what comes back is the 8 KB the session can afford, flagged as truncated.
    [Fact]
    public async Task Fetch_CapsResponseSize()
    {
        var huge = new string('a', 3 * 1024 * 1024);
        await using var server = TestHttpServer.Start(
            new TestHttpServer.HttpReply(200, "text/plain", huge));

        var page = await LoopbackAllowed().FetchAsync($"{server.BaseUrl}/huge");

        Assert.True(page.Truncated);
        Assert.Equal(
            WebToolLimits.MaxPageTextLength + WebToolLimits.TruncationMarker.Length,
            page.Text.Length);
    }

    // AC9 — a PDF or an image is not a readable page; the document-upload path covers PDFs.
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("application/json")]
    public async Task Fetch_RejectsNonTextContentType(string contentType)
    {
        await using var server = TestHttpServer.Start(
            new TestHttpServer.HttpReply(200, contentType, "binary-ish"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LoopbackAllowed().FetchAsync($"{server.BaseUrl}/file"));

        Assert.Contains(contentType, ex.Message);
    }

    // AC10 — HTML is reduced to readable text: the title comes out, the prose comes out, and the
    // script/style/nav boilerplate the model would otherwise pay context for does not.
    [Fact]
    public async Task Fetch_ExtractsReadableText()
    {
        const string Html = """
            <html>
              <head>
                <title>Orleans overview</title>
                <style>body { color: red; }</style>
                <script>var tracking = "do-not-leak";</script>
              </head>
              <body>
                <nav>Home About Contact</nav>
                <article><p>Orleans is a framework for distributed applications.</p></article>
                <footer>Copyright 2026</footer>
              </body>
            </html>
            """;

        await using var server = TestHttpServer.Start(new TestHttpServer.HttpReply(200, "text/html", Html));

        var page = await LoopbackAllowed().FetchAsync($"{server.BaseUrl}/page");

        Assert.Equal("Orleans overview", page.Title);
        Assert.Contains("Orleans is a framework for distributed applications.", page.Text);
        Assert.DoesNotContain("do-not-leak", page.Text);
        Assert.DoesNotContain("color: red", page.Text);
        Assert.DoesNotContain("Home About Contact", page.Text);
        Assert.False(page.Truncated);
    }

    // AC10 — the return budget is what protects the 2 MB session (ADR 0004), so it is enforced on the
    // extracted text, and the model is told the page was cut rather than believing it read all of it.
    [Fact]
    public async Task Fetch_TruncatesReturnedTextTo8Kb()
    {
        var prose = string.Join(" ", Enumerable.Repeat("word", 20_000));
        await using var server = TestHttpServer.Start(
            new TestHttpServer.HttpReply(200, "text/html", $"<html><body><p>{prose}</p></body></html>"));

        var page = await LoopbackAllowed().FetchAsync($"{server.BaseUrl}/long");

        Assert.True(page.Truncated);
        Assert.EndsWith(WebToolLimits.TruncationMarker, page.Text);
        Assert.Equal(
            WebToolLimits.MaxPageTextLength + WebToolLimits.TruncationMarker.Length,
            page.Text.Length);
    }
}
