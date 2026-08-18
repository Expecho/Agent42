using System.Collections.Concurrent;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;
using Agent42.Agents;

namespace Agent42.Tests;

public sealed class WebPushNotificationSenderTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    private WebPushNotificationSender Sender(IWebPushClient client) =>
        new(_cluster.GrainFactory, client, NullLogger<WebPushNotificationSender>.Instance);

    private async Task<IUserPushGrain> SeedAsync(string user, params string[] endpoints)
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserPushGrain>(user);
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

    private sealed class FakeWebPushClient : IWebPushClient
    {
        public ConcurrentBag<string> Sent { get; } = [];

        /// <summary>Returns an HTTP status to throw as <see cref="PushDeliveryException"/> for a sub, else null.</summary>
        public Func<PushSubscription, int?> FailStatusFor { get; init; } = _ => null;

        /// <summary>When true for a sub, throws a non-delivery exception to prove nothing escapes.</summary>
        public Func<PushSubscription, bool> ThrowGenericFor { get; init; } = _ => false;

        public Task SendAsync(PushSubscription subscription, string payload, CancellationToken cancellationToken)
        {
            Sent.Add(subscription.Endpoint);
            if (ThrowGenericFor(subscription))
            {
                throw new InvalidOperationException("boom");
            }
            if (FailStatusFor(subscription) is int status)
            {
                throw new PushDeliveryException(status, new HttpRequestException());
            }
            return Task.CompletedTask;
        }
    }
}
