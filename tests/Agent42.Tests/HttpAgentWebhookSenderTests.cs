using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The HTTP side of the outbound webhook (ADR 0094): signing headers, the bounded retry, the never-throw contract, and
/// the guard. Status sequences use a stub handler; the guard test goes through the REAL registration against a
/// loopback listener, because a guard that is built but not wired ships green otherwise.
/// </summary>
public sealed class HttpAgentWebhookSenderTests
{
    private const string Url = "https://hooks.example.com/agent42";
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private static readonly AgentWebhookEvent Turn = new(
        AgentWebhookEvent.TurnCompleted, "agent-1", "Hooky", DateTimeOffset.UtcNow, "conv-1",
        AgentWebhookEvent.Triggers.Interactive, Prompt: "hi", Reply: "hello");

    // AC10: the three Standard Webhooks headers are present, and the signature verifies over the body as sent.
    [Fact]
    public async Task Send_SetsVerifiableSignatureHeaders()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await Sender(handler).SendAsync(new AgentWebhookTarget(Url, "protected:" + Secret), "d-1", Turn);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("d-1", request.Headers["webhook-id"]);
        var timestamp = long.Parse(request.Headers["webhook-timestamp"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(timestamp, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal(WebhookSigner.Sign(Secret, "d-1", timestamp, request.Body), request.Headers["webhook-signature"]);

        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("agent.turn.completed", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello", json.RootElement.GetProperty("reply").GetString());
        Assert.Equal("Hooky", json.RootElement.GetProperty("agent").GetProperty("name").GetString());
        Assert.False(json.RootElement.GetProperty("replacesLastTurn").GetBoolean());
    }

    // AC18: the test event's body carries the agent and nothing from a conversation.
    [Fact]
    public async Task Send_TestEvent_BodyHasNoConversationFields()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var test = new AgentWebhookEvent(AgentWebhookEvent.Test, "agent-1", "Hooky", DateTimeOffset.UtcNow);

        await Sender(handler).SendAsync(new AgentWebhookTarget(Url, "protected:" + Secret), "d-1", test);

        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(["type", "id", "timestamp", "agent"], json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    // AC11: a 503 then a 200 is two attempts and a success.
    [Fact]
    public async Task Send_RetriesTransientThenSucceeds()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var result = await Sender(handler).SendAsync(new AgentWebhookTarget(Url, "protected:" + Secret), "d-1", Turn);

        Assert.Equal((200, 2), (result.StatusCode, result.Attempts));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Headers["webhook-id"], handler.Requests[1].Headers["webhook-id"]);
    }

    // AC11: a 429 is retried like a 5xx.
    [Fact]
    public async Task Send_RetriesTooManyRequests()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.NoContent);

        var result = await Sender(handler).SendAsync(new AgentWebhookTarget(Url, "protected:" + Secret), "d-1", Turn);

        Assert.Equal((204, 2), (result.StatusCode, result.Attempts));
    }

    // AC11: any other 4xx is final.
    [Fact]
    public async Task Send_DoesNotRetryClientError()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, HttpStatusCode.OK);

        var result = await Sender(handler).SendAsync(new AgentWebhookTarget(Url, "protected:" + Secret), "d-1", Turn);

        Assert.Equal((400, 1), (result.StatusCode, result.Attempts));
        Assert.False(result.Succeeded);
    }

    // AC11: three failures are a failed result, never an exception — transport errors included.
    [Fact]
    public async Task Send_ExhaustedRetries_ReturnsFailureWithoutThrowing()
    {
        var fivexx = new StubHandler(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway);
        var down = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var failed = await Sender(fivexx).SendAsync(new AgentWebhookTarget(Url, "protected:" + Secret), "d-1", Turn);
        var unreachable = await Sender(down).SendAsync(new AgentWebhookTarget(Url, "protected:" + Secret), "d-2", Turn);

        Assert.Equal((502, 3), (failed.StatusCode, failed.Attempts));
        Assert.Equal(((int?)null, "network", 3), (unreachable.StatusCode, unreachable.Error, unreachable.Attempts));
    }

    // AC11: a receiver that never answers times out per attempt, and is retried.
    [Fact]
    public async Task Send_Timeout_IsRetriedAndReported()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out", new TimeoutException()));

        var result = await Sender(handler).SendAsync(new AgentWebhookTarget(Url, "protected:" + Secret), "d-1", Turn);

        Assert.Equal(("timeout", 3), (result.Error, result.Attempts));
    }

    // AC13: a secret the key ring cannot decrypt sends NOTHING — never an unsigned POST.
    [Fact]
    public async Task Send_UndecryptableSecret_SendsNothing()
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        var result = await Sender(handler).SendAsync(new AgentWebhookTarget(Url, "garbage"), "d-1", Turn);

        Assert.Equal((WebhookLimits.SecretUnavailableError, 0), (result.Error, result.Attempts));
        Assert.Empty(handler.Requests);
    }

    // AC12: the REAL registration refuses a loopback destination at connect time, and does not retry the refusal.
    [Fact]
    public async Task Send_LoopbackDestination_IsBlockedAndNotRetried()
    {
        var hits = 0;
        await using var server = TestHttpServer.Start(_ =>
        {
            Interlocked.Increment(ref hits);
            return new TestHttpServer.HttpReply(200, "text/plain", "ok");
        });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddAgentWebhooks();
        await using var provider = services.BuildServiceProvider();
        var protector = provider.GetRequiredService<IWebhookSecretProtector>();
        var sender = provider.GetRequiredService<IAgentWebhookSender>();

        var result = await sender.SendAsync(new AgentWebhookTarget(server.BaseUrl + "/hook", protector.Protect(Secret)), "d-1", Turn);

        Assert.Equal(("blocked-address", 1), (result.Error, result.Attempts));
        Assert.Null(result.StatusCode);
        Assert.Equal(0, hits);
    }

    // AC12: a redirect is not followed — the 302 itself is the (final) answer. The handler is built the way the
    // registration builds it, with the guard opened only so the loopback listener is reachable.
    [Fact]
    public async Task Send_DoesNotFollowRedirect()
    {
        var hits = new ConcurrentQueue<string>();
        await using var server = TestHttpServer.Start(line =>
        {
            hits.Enqueue(line);
            return line.Contains("/hook", StringComparison.Ordinal)
                ? new TestHttpServer.HttpReply(302, "text/plain", "", Location: "/elsewhere")
                : new TestHttpServer.HttpReply(200, "text/plain", "ok");
        });
        var handler = GuardedDocumentFetcher.CreateHandler(_ => true);
        handler.AllowAutoRedirect = false;

        var result = await Sender(handler).SendAsync(
            new AgentWebhookTarget(server.BaseUrl + "/hook", "protected:" + Secret), "d-1", Turn);

        Assert.Equal((302, 1), (result.StatusCode, result.Attempts));
        Assert.DoesNotContain(hits, l => l.Contains("/elsewhere", StringComparison.Ordinal));
    }

    // AC12: the wiring itself, as source text — the ADR 0066 lesson that a guard built but not wired ships green.
    [Fact]
    public void Registration_WiresGuardNoRedirectAndNoResilience()
    {
        var registration = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Agent42.Backend", "Webhooks", "WebhookRegistration.cs"));
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Agent42.Backend", "Program.cs"));

        Assert.Contains("ConfigurePrimaryHttpMessageHandler", registration, StringComparison.Ordinal);
        Assert.Contains("GuardedDocumentFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable)", registration, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", registration, StringComparison.Ordinal);
        Assert.Contains("UseProxy = false", registration, StringComparison.Ordinal);
        Assert.Contains("RemoveAllResilienceHandlers", registration, StringComparison.Ordinal);
        Assert.Contains("http.Timeout", registration, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddAgentWebhooks();", program, StringComparison.Ordinal);
    }

    private static HttpAgentWebhookSender Sender(HttpMessageHandler handler)
        => new(new SingleClientFactory(handler), new PrefixProtector(), NullLogger<HttpAgentWebhookSender>.Instance)
        {
            RetryDelays = [TimeSpan.Zero],
        };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    // "Protects" by prefixing, so a test can hand the sender a ciphertext it will decrypt — and anything else is
    // undecryptable, like a rotated key ring.
    private sealed class PrefixProtector : IWebhookSecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;

        public string? Unprotect(string? ciphertext)
            => ciphertext?.StartsWith("protected:", StringComparison.Ordinal) == true ? ciphertext["protected:".Length..] : null;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record SeenRequest(Dictionary<string, string?> Headers, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpStatusCode> _respond;

        public StubHandler(params HttpStatusCode[] statuses)
            => _respond = i => statuses[Math.Min(i, statuses.Length - 1)];

        public StubHandler(Func<int, HttpStatusCode> respond) => _respond = respond;

        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => (string?)string.Join(",", h.Value));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var index = Requests.Count;
            Requests.Add(new SeenRequest(headers, body));
            return new HttpResponseMessage(_respond(index));
        }
    }
}
