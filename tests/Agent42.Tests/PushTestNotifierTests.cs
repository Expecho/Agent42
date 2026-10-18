using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

public sealed class PushTestNotifierTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private PushTestNotifier Notifier(IWebPushClient client) =>
        new(Cluster.GrainFactory, client, NullLogger<PushTestNotifier>.Instance);

    private async Task<IUserPushGrain> SeedAsync(string user, params string[] endpoints)
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserPushGrain>(user);
        foreach (var endpoint in endpoints)
        {
            await grain.AddAsync(new PushSubscription(endpoint, "p256", "auth"));
        }
        return grain;
    }

    // AC1: one push with the fixed text and an empty conversationId; outcome "sent".
    [Fact]
    public async Task SendAsync_StoredEndpoint_SendsOnceAndReportsSent()
    {
        const string user = "push-test-sent";
        await SeedAsync(user, "https://push/test-a");
        var client = new FakeWebPushClient();

        var result = await Notifier(client).SendAsync(user, "https://push/test-a", CancellationToken.None);

        Assert.Equal(new PushTestResponse(PushTestOutcomes.Sent), result);
        Assert.Equal(["https://push/test-a"], client.Sent.ToArray());
        using var payload = JsonDocument.Parse(client.Payloads["https://push/test-a"]);
        Assert.Equal("Agent 42", payload.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("body").GetString()));
        Assert.Equal("", payload.RootElement.GetProperty("conversationId").GetString());
    }

    // AC2: only the requested device, never the user's other subscriptions.
    [Fact]
    public async Task SendAsync_OnlyTargetsRequestedEndpoint()
    {
        const string user = "push-test-one";
        await SeedAsync(user, "https://push/one-a", "https://push/one-b", "https://push/one-c");
        var client = new FakeWebPushClient();

        await Notifier(client).SendAsync(user, "https://push/one-b", CancellationToken.None);

        Assert.Equal(["https://push/one-b"], client.Sent.ToArray());
    }

    // AC3: an endpoint the caller never stored is not dialled.
    [Fact]
    public async Task SendAsync_UnknownEndpoint_SendsNothing()
    {
        const string user = "push-test-unknown";
        await SeedAsync(user, "https://push/known");
        var client = new FakeWebPushClient();

        var result = await Notifier(client).SendAsync(user, "https://attacker/anything", CancellationToken.None);

        Assert.Equal(PushTestOutcomes.NotRegistered, result.Outcome);
        Assert.Empty(client.Sent);
    }

    // AC3: another user's stored endpoint is not reachable through the caller's id.
    [Fact]
    public async Task SendAsync_OtherUsersEndpoint_SendsNothing()
    {
        await SeedAsync("push-test-owner", "https://push/owned");
        var client = new FakeWebPushClient();

        var result = await Notifier(client).SendAsync("push-test-intruder", "https://push/owned", CancellationToken.None);

        Assert.Equal(PushTestOutcomes.NotRegistered, result.Outcome);
        Assert.Empty(client.Sent);
    }

    // AC4: 404/410 reports "gone" and prunes exactly like the real sender.
    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task SendAsync_Gone_PrunesAndReportsGone(int status)
    {
        var user = $"push-test-gone-{status}";
        var grain = await SeedAsync(user, "https://push/gone", "https://push/other");
        var client = new FakeWebPushClient { FailStatusFor = _ => status };

        var result = await Notifier(client).SendAsync(user, "https://push/gone", CancellationToken.None);

        Assert.Equal(new PushTestResponse(PushTestOutcomes.Gone, status), result);
        var remaining = Assert.Single(await grain.GetAllAsync());
        Assert.Equal("https://push/other", remaining.Endpoint);
    }

    // AC5: any other status is reported with that status and the subscription is kept.
    [Fact]
    public async Task SendAsync_OtherStatus_ReportsFailedAndKeeps()
    {
        const string user = "push-test-500";
        var grain = await SeedAsync(user, "https://push/flaky");
        var client = new FakeWebPushClient { FailStatusFor = _ => 500 };

        var result = await Notifier(client).SendAsync(user, "https://push/flaky", CancellationToken.None);

        Assert.Equal(new PushTestResponse(PushTestOutcomes.Failed, 500), result);
        Assert.Single(await grain.GetAllAsync());
    }

    // AC6: a send that outlives the budget reports "timeout" instead of throwing.
    [Fact]
    public async Task SendAsync_Slow_ReportsTimeout()
    {
        const string user = "push-test-slow";
        await SeedAsync(user, "https://push/slow");
        var client = new FakeWebPushClient { Delay = WebPushNotificationSender.SendBudget * 4 };

        var result = await Notifier(client).SendAsync(user, "https://push/slow", CancellationToken.None);

        Assert.Equal(new PushTestResponse(PushTestOutcomes.Timeout), result);
    }

    // AC6a: the named HttpClient's own Timeout surfaces as TaskCanceledException while the caller is live.
    [Fact]
    public async Task SendAsync_ClientTimeout_ReportsTimeout()
    {
        const string user = "push-test-client-timeout";
        await SeedAsync(user, "https://push/http-timeout");
        var client = new FakeWebPushClient { ThrowAlways = new TaskCanceledException("HttpClient.Timeout") };

        var result = await Notifier(client).SendAsync(user, "https://push/http-timeout", CancellationToken.None);

        Assert.Equal(new PushTestResponse(PushTestOutcomes.Timeout), result);
    }

    // AC6b: an untranslated transport error is "failed" with no status, not thrown, not pruned.
    [Fact]
    public async Task SendAsync_GenericError_ReportsFailed()
    {
        const string user = "push-test-dns";
        var grain = await SeedAsync(user, "https://push/unreachable");
        var client = new FakeWebPushClient { ThrowAlways = new HttpRequestException("No such host") };

        var result = await Notifier(client).SendAsync(user, "https://push/unreachable", CancellationToken.None);

        Assert.Equal(new PushTestResponse(PushTestOutcomes.Failed), result);
        Assert.Single(await grain.GetAllAsync());
    }
}
