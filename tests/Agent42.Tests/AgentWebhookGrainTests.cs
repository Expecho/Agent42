using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The webhook grain on its own (ADR 0094): configuration, the delivery log, and the reentrancy that keeps the config
/// calls answering while a ~40 s delivery is in flight. Deliveries are <c>[OneWay]</c>, so every assertion on them
/// polls.
/// </summary>
public sealed class AgentWebhookGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    // AC14: the grain stores ciphertext only, and GetAsync never hands it back.
    [Fact]
    public async Task GetAsync_NeverReturnsCiphertext()
    {
        var (webhook, url) = Webhook();
        await webhook.SaveAsync(url, "CIPHERTEXT-abc");

        var status = await webhook.GetAsync();

        Assert.Equal(url, status.Url);
        Assert.True(status.HasSecret);
        Assert.DoesNotContain("CIPHERTEXT", JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_WithNullSecret_KeepsTheStoredOne()
    {
        var (webhook, url) = Webhook();
        await webhook.SaveAsync(url, "first");
        await webhook.SaveAsync(url + "/other", null);

        await webhook.SendTestAsync("Agent");

        var send = Assert.Single(SendsTo(url + "/other"));
        Assert.Equal("first", send.Target.SecretCiphertext);
    }

    [Fact]
    public async Task Deliver_WithoutUrl_IsANoOp()
    {
        var (webhook, url) = Webhook();

        await webhook.DeliverAsync(Event("a"));
        await Task.Delay(200);

        Assert.Empty(SendsTo(url));
        Assert.Empty((await webhook.GetAsync()).Deliveries);
    }

    // AC16: the log keeps the last 20, newest (by completion) first. Orleans does not order messages, so which 20
    // survive is not asserted — only the cap and the order.
    [Fact]
    public async Task Deliver_LogCappedAt20()
    {
        var (webhook, url) = Webhook();
        await webhook.SaveAsync(url, "secret");

        // One at a time: fired all at once, the backlog cap (tested below) would drop the excess.
        for (var i = 0; i < 25; i++)
        {
            await webhook.DeliverAsync(Event($"conv-{i}"));
            var sent = i + 1;
            await TestWait.WaitUntilAsync(() => SendsTo(url).Count == sent);
        }

        var deliveries = await PollDeliveriesAsync(webhook, d => d.Count == WebhookLimits.MaxDeliveryLog);
        await Task.Delay(200);
        deliveries = (await webhook.GetAsync()).Deliveries;

        Assert.Equal(WebhookLimits.MaxDeliveryLog, deliveries.Count);
        Assert.Equal(deliveries.OrderByDescending(d => d.At).Select(d => d.DeliveryId), deliveries.Select(d => d.DeliveryId));
        Assert.All(deliveries, d => Assert.Equal(200, d.StatusCode));
    }

    [Fact]
    public async Task Clear_RemovesUrlSecretAndLog()
    {
        var (webhook, url) = Webhook();
        await webhook.SaveAsync(url, "secret");
        await webhook.SendTestAsync("Agent");

        await webhook.ClearAsync();

        var status = await webhook.GetAsync();
        Assert.Null(status.Url);
        Assert.False(status.HasSecret);
        Assert.Empty(status.Deliveries);
        Assert.Null(await webhook.SendTestAsync("Agent"));
    }

    // AC16: a delivery can outlast the 30 s call timeout, so the config calls must not queue behind it.
    [Fact]
    public async Task ConfigCalls_AnswerDuringSlowDelivery()
    {
        var (webhook, url) = Webhook();
        await webhook.SaveAsync(url, "secret");
        HoldAll();

        await webhook.DeliverAsync(Event("slow"));
        await TestWait.WaitUntilAsync(() => Waiting() == 1);

        var within = TimeSpan.FromSeconds(5);
        await webhook.GetAsync().WaitAsync(within);
        await webhook.SaveAsync(url, null).WaitAsync(within);
        await webhook.ClearAsync().WaitAsync(within);

        ReleaseAll();
    }

    // AC16: a clear that interleaves with a delivery wins — the delivery finishing afterwards writes no log entry,
    // even once a new webhook is saved over it.
    [Fact]
    public async Task ClearDuringDelivery_DropsTheLogWrite()
    {
        var (webhook, url) = Webhook();
        await webhook.SaveAsync(url, "secret");
        HoldAll();

        await webhook.DeliverAsync(Event("in-flight"));
        await TestWait.WaitUntilAsync(() => Waiting() == 1);
        await webhook.ClearAsync();
        await webhook.SaveAsync("https://new.example.com/hook", "new-secret");

        ReleaseAll();
        await TestWait.WaitUntilAsync(() => Waiting() == 0);
        await Task.Delay(200);

        Assert.Empty((await webhook.GetAsync()).Deliveries);
    }

    // AC16: deliveries beyond MaxPendingDeliveries are dropped and logged, not queued without bound.
    [Fact]
    public async Task Deliver_BeyondBacklog_IsDroppedAndLogged()
    {
        var (webhook, url) = Webhook();
        await webhook.SaveAsync(url, "secret");
        HoldAll();

        for (var i = 0; i < WebhookLimits.MaxPendingDeliveries + 2; i++)
        {
            await webhook.DeliverAsync(Event($"conv-{i}"));
        }

        var dropped = await PollDeliveriesAsync(
            webhook, d => d.Count(r => r.Error == WebhookLimits.DroppedBacklogError) == 2);
        ReleaseAll();

        Assert.Equal(2, dropped.Count(r => r.Error == WebhookLimits.DroppedBacklogError));
        var sent = await PollDeliveriesAsync(webhook, d => d.Count(r => r.StatusCode == 200) == WebhookLimits.MaxPendingDeliveries);
        Assert.Equal(WebhookLimits.MaxPendingDeliveries, sent.Count(r => r.StatusCode == 200));
    }

    // AC16: the record has nowhere to put a response body.
    [Fact]
    public void DeliveryRecord_HasNoResponseBody()
    {
        var names = typeof(WebhookDeliveryRecord).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Body", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Response", StringComparison.OrdinalIgnoreCase));
    }

    // AC18: the test event carries no conversation content, and its result is logged and returned.
    [Fact]
    public async Task SendTest_CarriesNoConversationContent()
    {
        var (webhook, url) = Webhook(agentId: "agent-test");
        await webhook.SaveAsync(url, "secret");

        var result = await webhook.SendTestAsync("Test Agent");

        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
        var evt = Assert.Single(SendsTo(url)).Event;
        Assert.Equal(AgentWebhookEvent.Test, evt.Type);
        Assert.Equal(("agent-test", "Test Agent"), (evt.AgentId, evt.AgentName));
        Assert.Null(evt.ConversationId);
        Assert.Null(evt.Prompt);
        Assert.Null(evt.Reply);
        Assert.Equal(AgentWebhookEvent.Test, Assert.Single((await webhook.GetAsync()).Deliveries).Type);
    }

    [Fact]
    public async Task Configuration_SurvivesReactivation()
    {
        const string url = "https://hooks.example.com/reactivated";
        var key = AgentWebhookKeys.KeyFor($"wh-user-{Guid.NewGuid():N}", "agent-1");
        await Cluster.GrainFactory.GetGrain<IAgentWebhookGrain>(key).SaveAsync(url, "secret");

        await Cluster.GrainFactory.GetGrain<IAgentWebhookGrain>(key)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();

        var status = await Cluster.GrainFactory.GetGrain<IAgentWebhookGrain>(key).GetAsync();
        Assert.Equal((url, true), (status.Url, status.HasSecret));
    }

    // A fresh grain and a URL no other test uses: a OneWay delivery of an earlier test can still be sending after the
    // per-test reset, so every assertion on sends filters by this URL.
    private (IAgentWebhookGrain Grain, string Url) Webhook(string agentId = "agent-1")
    {
        var id = Guid.NewGuid().ToString("N");
        return (Cluster.GrainFactory.GetGrain<IAgentWebhookGrain>(AgentWebhookKeys.KeyFor($"wh-user-{id}", agentId)),
            $"https://hooks.example.com/{id}");
    }

    private static AgentWebhookEvent Event(string conversationId)
        => new(AgentWebhookEvent.TurnCompleted, "agent-1", "Agent", DateTimeOffset.UtcNow, conversationId,
            AgentWebhookEvent.Triggers.Interactive, Prompt: "hi", Reply: "hello");

    private IEnumerable<RecordingAgentWebhookSender> Recorders()
        => Cluster.Silos.Select(s => Cluster.GetSiloServiceProvider(s.SiloAddress)
            .GetRequiredService<RecordingAgentWebhookSender>());

    private List<(AgentWebhookTarget Target, string DeliveryId, AgentWebhookEvent Event)> SendsTo(string url)
        => [.. Recorders().SelectMany(r => r.Sends).Where(s => s.Target.Url == url)];

    private int Waiting() => Recorders().Sum(r => r.Waiting);

    private void HoldAll()
    {
        foreach (var recorder in Recorders())
        {
            recorder.Hold();
        }
    }

    private void ReleaseAll()
    {
        foreach (var recorder in Recorders())
        {
            recorder.Release();
        }
    }

    private static async Task<IReadOnlyList<WebhookDeliveryRecord>> PollDeliveriesAsync(
        IAgentWebhookGrain webhook, Func<IReadOnlyList<WebhookDeliveryRecord>, bool> until)
    {
        return await TestWait.WaitForAsync(async () => (await webhook.GetAsync()).Deliveries, until, "the delivery log to reach the expected state");
    }

}
