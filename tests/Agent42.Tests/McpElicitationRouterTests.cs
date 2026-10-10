using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The router that ties an MCP server's elicitation to the tool call that caused it (ADR 0095): the async-local
/// scope wins, the single in-flight scope is the fallback, and anything ambiguous or unattended answers cancel.
/// </summary>
public sealed class McpElicitationRouterTests
{
    private static readonly McpElicitationRequest Form = new(McpElicitationLimits.FormMode, "Your name?", """{"type":"object"}""");

    private sealed class RecordingSink(string tag) : IMcpElicitationSink
    {
        public List<(string Server, string CallId, McpElicitationRequest Request)> Asked { get; } = [];

        public Task<McpElicitationAnswer> AskAsync(
            string serverName, string toolCallId, McpElicitationRequest request, CancellationToken cancellationToken)
        {
            Asked.Add((serverName, toolCallId, request));
            return Task.FromResult(new McpElicitationAnswer(McpElicitationLimits.Accept, $$"""{"by":"{{tag}}"}"""));
        }
    }

    // Runs the route on a fresh execution context, as the SDK's handler does on a stateful HTTP server (measured:
    // the caller's async local does not flow there).
    private static Task<McpElicitationAnswer> RouteFromSdkThread(McpElicitationRouter router, McpElicitationRequest request)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => router.RouteAsync(request, CancellationToken.None));
        }
    }

    [Fact]
    public async Task SingleScope_RoutesThere()
    {
        var router = new McpElicitationRouter();
        var sink = new RecordingSink("a");
        using var scope = router.Enter(sink, "call_1", "Calendar");

        var answer = await RouteFromSdkThread(router, Form);

        Assert.Equal(McpElicitationLimits.Accept, answer.Action);
        var asked = Assert.Single(sink.Asked);
        Assert.Equal(("Calendar", "call_1"), (asked.Server, asked.CallId));
    }

    [Fact]
    public async Task TwoScopes_AnswerCancel()
    {
        var router = new McpElicitationRouter();
        var a = new RecordingSink("a");
        var b = new RecordingSink("b");
        using var first = router.Enter(a, "call_1", "Calendar");
        using var second = router.Enter(b, "call_2", "Calendar");

        var answer = await RouteFromSdkThread(router, Form);

        Assert.Equal(McpElicitationLimits.Cancel, answer.Action);
        Assert.Empty(a.Asked);
        Assert.Empty(b.Asked);
    }

    [Fact]
    public async Task NoScope_AnswersCancel()
    {
        var answer = await RouteFromSdkThread(new McpElicitationRouter(), Form);

        Assert.Equal(McpElicitationLimits.Cancel, answer.Action);
    }

    [Fact]
    public async Task NullSink_AnswersCancel()
    {
        var router = new McpElicitationRouter();
        using var scope = router.Enter(null, "call_1", "Calendar");

        var answer = await router.RouteAsync(Form, CancellationToken.None);

        Assert.Equal(McpElicitationLimits.Cancel, answer.Action);
    }

    [Fact]
    public async Task DisposedScope_NoLongerRoutes()
    {
        var router = new McpElicitationRouter();
        var sink = new RecordingSink("a");
        router.Enter(sink, "call_1", "Calendar").Dispose();

        var flowed = await router.RouteAsync(Form, CancellationToken.None);
        var fallback = await RouteFromSdkThread(router, Form);

        Assert.Equal(McpElicitationLimits.Cancel, flowed.Action);
        Assert.Equal(McpElicitationLimits.Cancel, fallback.Action);
        Assert.Empty(sink.Asked);
        Assert.Equal(0, router.InFlight);
    }

    // The local bridge's case (stream transport): the async local flows into the handler, so a call is routed
    // exactly even while another call to the same server is in flight.
    [Fact]
    public async Task AsyncLocalScope_WinsOverFallback()
    {
        var router = new McpElicitationRouter();
        var other = new RecordingSink("other");
        var mine = new RecordingSink("mine");
        Task<IDisposable> otherCall;
        using (ExecutionContext.SuppressFlow())
        {
            // Another chat's call, entered on its own context.
            otherCall = Task.Run(() => router.Enter(other, "call_other", "Calendar"));
        }

        using var otherScope = await otherCall;
        McpElicitationAnswer answer;
        using (router.Enter(mine, "call_mine", "Calendar"))
        {
            answer = await Task.Run(() => router.RouteAsync(Form, CancellationToken.None));
        }

        Assert.Equal(1, router.InFlight);
        Assert.Equal("""{"by":"mine"}""", answer.ContentJson);
        Assert.Empty(other.Asked);
    }

    [Fact]
    public async Task InvalidUrl_IsCancelledWithoutAsking()
    {
        var router = new McpElicitationRouter();
        var sink = new RecordingSink("a");
        using var scope = router.Enter(sink, "call_1", "Calendar");

        var answer = await router.RouteAsync(
            new McpElicitationRequest(McpElicitationLimits.UrlMode, "Go", Url: "javascript:alert(1)"), CancellationToken.None);

        Assert.Equal(McpElicitationLimits.Cancel, answer.Action);
        Assert.Empty(sink.Asked);
    }

    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,<b>x</b>", false)]
    [InlineData("http://example.com/auth", false)]
    [InlineData("/relative/path", false)]
    [InlineData("ftp://example.com/x", false)]
    [InlineData(null, false)]
    [InlineData("https://example.com/auth?x=1", true)]
    [InlineData("http://localhost:5000/cb", true)]
    [InlineData("http://127.0.0.1:5000/cb", true)]
    public void TryValidateUrl_RejectsNonHttpsSchemes(string? url, bool expected)
    {
        Assert.Equal(expected, McpElicitationLimits.TryValidateUrl(url, out var uri));
        Assert.Equal(expected, uri is not null);
    }
}
