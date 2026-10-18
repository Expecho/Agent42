using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

public sealed class WebPushNotificationSenderTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private WebPushNotificationSender Sender(IWebPushClient client) =>
        new(Cluster.GrainFactory, client, NullLogger<WebPushNotificationSender>.Instance);

    private async Task<IUserPushGrain> SeedAsync(string user, params string[] endpoints)
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserPushGrain>(user);
        foreach (var endpoint in endpoints)
        {
            await grain.AddAsync(new PushSubscription(endpoint, "p256", "auth"));
        }
        return grain;
    }

    private static PushMessage Message() => new("Agent Bob", "replied while you were away", "conv-1");

    [Fact]
    public async Task SendsToEverySubscription()
    {
        const string user = "push-send-all";
        await SeedAsync(user, "https://push/a", "https://push/b", "https://push/c");
        var client = new FakeWebPushClient();

        await Sender(client).SendAsync(user, Message());

        Assert.Equal(
            ["https://push/a", "https://push/b", "https://push/c"],
            client.Sent.OrderBy(e => e).ToArray());
    }

    // AC6: a 410 prunes exactly that subscription; the others survive.
    [Fact]
    public async Task Prunes410Subscription()
    {
        const string user = "push-prune-410";
        var grain = await SeedAsync(user, "https://push/gone", "https://push/live");
        var client = new FakeWebPushClient
        {
            FailStatusFor = sub => sub.Endpoint == "https://push/gone" ? 410 : null,
        };

        await Sender(client).SendAsync(user, Message());

        var remaining = Assert.Single(await grain.GetAllAsync());
        Assert.Equal("https://push/live", remaining.Endpoint);
    }

    // A 404 prunes too (the other "subscription is gone" status).
    [Fact]
    public async Task Prunes404Subscription()
    {
        const string user = "push-prune-404";
        var grain = await SeedAsync(user, "https://push/gone");
        var client = new FakeWebPushClient { FailStatusFor = _ => 404 };

        await Sender(client).SendAsync(user, Message());

        Assert.Empty(await grain.GetAllAsync());
    }

    // AC6: a non-gone failure (500) is swallowed and does NOT prune — a transient outage must not
    // silently discard a still-valid subscription.
    [Fact]
    public async Task TransientFailure_DoesNotPrune_AndDoesNotThrow()
    {
        const string user = "push-transient";
        var grain = await SeedAsync(user, "https://push/flaky");
        var client = new FakeWebPushClient { FailStatusFor = _ => 500 };

        await Sender(client).SendAsync(user, Message());

        Assert.Single(await grain.GetAllAsync());
    }

    // AC6: an arbitrary throw from the client never escapes into the (already-persisted) turn.
    [Fact]
    public async Task ArbitraryThrow_DoesNotEscape()
    {
        const string user = "push-throw";
        await SeedAsync(user, "https://push/boom");
        var client = new FakeWebPushClient { ThrowGenericFor = _ => true };

        // No assertion needed beyond "this await completes without throwing".
        await Sender(client).SendAsync(user, Message());
    }

    [Fact]
    public async Task NoSubscriptions_IsANoOp()
    {
        var client = new FakeWebPushClient();
        await Sender(client).SendAsync("push-none", Message());
        Assert.Empty(client.Sent);
    }

    // The no-op sender (unconfigured prod + offline) does nothing and never throws.
    [Fact]
    public async Task NullSender_IsASilentNoOp()
    {
        await new NullPushNotificationSender().SendAsync("any-user", Message());
    }
}
