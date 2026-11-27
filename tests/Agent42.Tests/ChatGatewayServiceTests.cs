using System.Collections.Concurrent;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The gateway supervisor (ADR 0111): it runs every runnable connection, but only while it holds the lease; it backs off
/// on transient failures and stops for good on an auth failure; and a restart or delete applies at once.
/// </summary>
public sealed class ChatGatewayServiceTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static readonly ChatGatewayTimings Fast = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(40), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(300));
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly IChatGatewaySecretProtector _protector = new ChatGatewaySecretProtector(
        new EphemeralDataProtectionProvider(), NullLogger<ChatGatewaySecretProtector>.Instance);

    [Fact]
    public async Task Boot_StartsEveryRunnableConnection()
    {
        var (userId, connectionId) = await NewConnectionAsync();
        var adapter = new FakeChatPlatformAdapter();
        await using var gateway = await StartAsync(adapter, new FakeLeaseAuthority());

        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));

        var session = adapter.SessionFor(connectionId)!;
        Assert.Equal(userId, session.Runtime.UserId);
        Assert.Equal("token-" + connectionId, session.Runtime.Credentials.BotToken);
    }

    [Fact]
    public async Task AnAuthFailureAtConnect_FaultsTheConnection_AndNeverRetries()
    {
        var (userId, connectionId) = await NewConnectionAsync();
        var adapter = new FakeChatPlatformAdapter { ConnectFailure = new ChatAuthException("Telegram rejected the bot token.") };
        await using var gateway = await StartAsync(adapter, new FakeLeaseAuthority());

        await TestWait.WaitUntilAsync(async () => (await Store(userId).GetAsync(connectionId))!.FaultReason is not null);
        var attempts = adapter.ConnectAttempts;
        await Task.Delay(100);

        Assert.Equal("Telegram rejected the bot token.", (await Store(userId).GetAsync(connectionId))!.FaultReason);
        Assert.Equal(attempts, adapter.ConnectAttempts);
        Assert.False(gateway.Service.IsLive(connectionId));
    }

    [Fact]
    public async Task ATransientFailure_BacksOffAndReconnects()
    {
        var (_, connectionId) = await NewConnectionAsync();
        var adapter = new FakeChatPlatformAdapter { ConnectFailure = new IOException("network down") };
        await using var gateway = await StartAsync(adapter, new FakeLeaseAuthority());

        await TestWait.WaitUntilAsync(() => adapter.ConnectAttempts >= 3);
        adapter.ConnectFailure = null;

        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));
    }

    [Fact]
    public async Task ASessionThatEndsWithAnAuthFailure_FaultsTheConnection()
    {
        var (userId, connectionId) = await NewConnectionAsync();
        var adapter = new FakeChatPlatformAdapter();
        await using var gateway = await StartAsync(adapter, new FakeLeaseAuthority());
        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));

        adapter.SessionFor(connectionId)!.Drop(new ChatAuthException("Another program is reading this bot's messages."));

        await TestWait.WaitUntilAsync(async () => (await Store(userId).GetAsync(connectionId))!.FaultReason is not null);
        Assert.False(gateway.Service.IsLive(connectionId));
    }

    [Fact]
    public async Task ADroppedSession_Reconnects()
    {
        var (_, connectionId) = await NewConnectionAsync();
        var adapter = new FakeChatPlatformAdapter();
        await using var gateway = await StartAsync(adapter, new FakeLeaseAuthority());
        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));
        var first = adapter.SessionFor(connectionId)!;

        first.Drop();

        await TestWait.WaitUntilAsync(() => adapter.SessionFor(connectionId) != first && gateway.Service.IsLive(connectionId));
        Assert.True(first.Disposed);
    }

    // AC13b: the instance without the lease connects nothing, and takes over only after the holder releases, with no
    // moment where both hold sessions.
    [Fact]
    public async Task LeaseHandover_NoOverlap()
    {
        var (_, connectionId) = await NewConnectionAsync();
        var authority = new FakeLeaseAuthority();
        var oldAdapter = new FakeChatPlatformAdapter();
        var newAdapter = new FakeChatPlatformAdapter();
        var oldGateway = await StartAsync(oldAdapter, authority);
        await TestWait.WaitUntilAsync(() => oldGateway.Service.IsLive(connectionId));
        await using var newGateway = await StartAsync(newAdapter, authority);

        await Task.Delay(100);
        Assert.Empty(newAdapter.Sessions);
        Assert.False(newGateway.Service.HoldsLease);

        await oldGateway.DisposeAsync();

        await TestWait.WaitUntilAsync(() => newGateway.Service.IsLive(connectionId));
        Assert.All(oldAdapter.Sessions, s => Assert.True(s.Disposed));
    }

    // AC13b: a lost renewal drops every session at once.
    [Fact]
    public async Task LostRenewal_DisconnectsEverything()
    {
        var (_, connectionId) = await NewConnectionAsync();
        var authority = new FakeLeaseAuthority();
        var adapter = new FakeChatPlatformAdapter();
        await using var gateway = await StartAsync(adapter, authority);
        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));

        authority.StealForever();

        await TestWait.WaitUntilAsync(() => !gateway.Service.IsLive(connectionId) && adapter.SessionFor(connectionId)!.Disposed);
        Assert.False(gateway.Service.HoldsLease);
    }

    // AC27: the participant hears about the loss BEFORE the sessions stop, so a hung session dispose can't delay the kill.
    [Fact]
    public async Task LeaseLost_NotifiesParticipants_BeforeSessionsStop()
    {
        var (_, connectionId) = await NewConnectionAsync();
        var authority = new FakeLeaseAuthority();
        var adapter = new FakeChatPlatformAdapter();
        var participant = new RecordingParticipant();
        await using var gateway = await StartAsync(adapter, authority, [participant]);
        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));
        var session = adapter.SessionFor(connectionId)!;
        var hang = new TaskCompletionSource();
        session.DisposeHang = hang;

        authority.StealForever();

        try
        {
            await TestWait.WaitUntilAsync(() => participant.Count("lost") > 0);
            Assert.False(session.Disposed);
        }
        finally
        {
            hang.SetResult();
        }

        await TestWait.WaitUntilAsync(() => session.Disposed);
    }

    [Fact]
    public async Task Renew_NotifiesParticipants()
    {
        var participant = new RecordingParticipant();
        await using var gateway = await StartAsync(new FakeChatPlatformAdapter(), new FakeLeaseAuthority(), [participant]);

        await TestWait.WaitUntilAsync(() => participant.Count("renewed") >= 3);

        Assert.Equal(0, participant.Count("lost"));
    }

    // AC27: shutdown tells the participants before the lease is released.
    [Fact]
    public async Task Stop_NotifiesBeforeRelease()
    {
        var order = new ConcurrentQueue<string>();
        var participant = new RecordingParticipant(order);
        var gateway = await StartAsync(new FakeChatPlatformAdapter(), new FakeLeaseAuthority(), [participant], order);
        await TestWait.WaitUntilAsync(() => participant.Count("renewed") > 0);

        await gateway.DisposeAsync();

        var events = order.ToArray();
        var release = Array.IndexOf(events, "release");
        Assert.True(release >= 0, string.Join(",", events));
        Assert.Contains("lost", events[..release]);
        Assert.DoesNotContain("lost", events[(release + 1)..]);
    }

    [Fact]
    public async Task StoppingTwice_NotifiesHarmlessly()
    {
        var participant = new RecordingParticipant { ThrowOnLost = true };
        var gateway = await StartAsync(new FakeChatPlatformAdapter(), new FakeLeaseAuthority(), [participant]);
        await TestWait.WaitUntilAsync(() => participant.Count("renewed") > 0);

        await gateway.Service.StopAsync(CancellationToken.None);
        await gateway.Service.StopAsync(CancellationToken.None);
        await gateway.DisposeAsync();

        Assert.True(participant.Count("lost") >= 2);
    }

    [Fact]
    public async Task AHungParticipant_DoesNotStopTheOthers()
    {
        var hung = new RecordingParticipant { HangOnLost = true };
        var fine = new RecordingParticipant();
        var (_, connectionId) = await NewConnectionAsync();
        var authority = new FakeLeaseAuthority();
        var adapter = new FakeChatPlatformAdapter();
        await using var gateway = await StartAsync(adapter, authority, [hung, fine]);
        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));

        authority.StealForever();

        await TestWait.WaitUntilAsync(() => fine.Count("lost") > 0);
        Assert.Equal(1, hung.Count("lost"));
    }

    [Fact]
    public async Task Restart_AppliesNewCredentials()
    {
        var (userId, connectionId) = await NewConnectionAsync();
        var adapter = new FakeChatPlatformAdapter();
        await using var gateway = await StartAsync(adapter, new FakeLeaseAuthority());
        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));
        var first = adapter.SessionFor(connectionId)!;

        await Store(userId).SetCredentialsAsync(connectionId, _protector.Protect(new ChatCredentials("rotated")),
            new ChatBotIdentity("1", "bot"), Now);
        await gateway.Service.RestartAsync(userId, connectionId);

        await TestWait.WaitUntilAsync(() => adapter.SessionFor(connectionId) != first && gateway.Service.IsLive(connectionId));
        Assert.True(first.Disposed);
        Assert.Equal("rotated", adapter.SessionFor(connectionId)!.Runtime.Credentials.BotToken);
    }

    // AC13c: once deleted (the grain deregisters first), a restart racing the delete finds nothing to run.
    [Fact]
    public async Task ARestartAfterDelete_RunsNothing()
    {
        var (userId, connectionId) = await NewConnectionAsync();
        var adapter = new FakeChatPlatformAdapter();
        await using var gateway = await StartAsync(adapter, new FakeLeaseAuthority());
        await TestWait.WaitUntilAsync(() => gateway.Service.IsLive(connectionId));

        await Store(userId).DeleteAsync(connectionId);
        await gateway.Service.RestartAsync(userId, connectionId);
        await Task.Delay(100);

        Assert.False(gateway.Service.IsLive(connectionId));
        Assert.True(adapter.SessionFor(connectionId)!.Disposed);
    }

    [Fact]
    public async Task UnreadableCredentials_FaultTheConnection()
    {
        var userId = "gw-user-" + Guid.NewGuid().ToString("N");
        var store = Store(userId);
        var created = await store.CreateAsync(ChatPlatformKind.Telegram, new ChatConnectionSettings { Name = "x" }, Now);
        await store.SetCredentialsAsync(created!.Id, "not-a-ciphertext", new ChatBotIdentity("1", "bot"), Now);
        await using var gateway = await StartAsync(new FakeChatPlatformAdapter(), new FakeLeaseAuthority());

        await TestWait.WaitUntilAsync(async () => (await store.GetAsync(created.Id))!.FaultReason is not null);

        Assert.Contains("Paste it again", (await store.GetAsync(created.Id))!.FaultReason);
    }

    private IUserChatConnectionsGrain Store(string userId) => Cluster.GrainFactory.GetGrain<IUserChatConnectionsGrain>(userId);

    private async Task<(string UserId, string ConnectionId)> NewConnectionAsync()
    {
        var userId = "gw-user-" + Guid.NewGuid().ToString("N");
        var store = Store(userId);
        var created = await store.CreateAsync(ChatPlatformKind.Telegram, new ChatConnectionSettings { Name = "x" }, Now);
        await store.SetCredentialsAsync(created!.Id, _protector.Protect(new ChatCredentials("token-" + created.Id)),
            new ChatBotIdentity(FakeChatPlatformAdapter.BotUserId, FakeChatPlatformAdapter.BotUserName), Now);
        return (userId, created.Id);
    }

    private async Task<RunningGateway> StartAsync(
        FakeChatPlatformAdapter adapter, FakeLeaseAuthority authority,
        IChatGatewayLeaseParticipant[]? participants = null, ConcurrentQueue<string>? leaseLog = null)
    {
        var service = new ChatGatewayService(
            Cluster.GrainFactory, [adapter], _protector, new NullInboundSink(), new FakeChatGatewayLease(authority, leaseLog),
            participants ?? [], new ChatSessionRegistry(), TimeProvider.System, NullLogger<ChatGatewayService>.Instance, Fast);
        await service.StartAsync(CancellationToken.None);
        return new RunningGateway(service);
    }

    private sealed class RunningGateway(ChatGatewayService service) : IAsyncDisposable
    {
        private bool _disposed;

        public ChatGatewayService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
        }
    }

    private sealed class RecordingParticipant(ConcurrentQueue<string>? order = null) : IChatGatewayLeaseParticipant
    {
        private readonly ConcurrentQueue<string> _events = new();

        public bool ThrowOnLost { get; init; }

        public bool HangOnLost { get; init; }

        public int Count(string name) => _events.Count(e => e == name);

        public Task OnLeaseRenewedAsync(CancellationToken cancellationToken)
        {
            Record("renewed");
            return Task.CompletedTask;
        }

        public async Task OnLeaseLostAsync(CancellationToken cancellationToken)
        {
            Record("lost");
            if (HangOnLost)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (ThrowOnLost)
            {
                throw new InvalidOperationException("participant failed");
            }
        }

        private void Record(string name)
        {
            _events.Enqueue(name);
            order?.Enqueue(name);
        }
    }

    private sealed class NullInboundSink : IChatInboundSink
    {
        public Task AcceptAsync(ChatConnectionRuntime runtime, ChatInboundMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

/// <summary>Who holds the fake lease; shared by the gateways that compete for it.</summary>
public sealed class FakeLeaseAuthority
{
    private static readonly object Thief = new();
    private readonly Lock _gate = new();
    private object? _holder;

    public bool TryTake(object claimant)
    {
        lock (_gate)
        {
            if (_holder is not null && _holder != claimant)
            {
                return false;
            }

            _holder = claimant;
            return true;
        }
    }

    public bool Holds(object claimant)
    {
        lock (_gate)
        {
            return _holder == claimant;
        }
    }

    public void Release(object claimant)
    {
        lock (_gate)
        {
            if (_holder == claimant)
            {
                _holder = null;
            }
        }
    }

    /// <summary>Another party takes the lease and never gives it back (an expired lease taken over).</summary>
    public void StealForever()
    {
        lock (_gate)
        {
            _holder = Thief;
        }
    }
}

/// <summary>A scripted <see cref="IChatGatewayLease"/> over a shared <see cref="FakeLeaseAuthority"/>.</summary>
public sealed class FakeChatGatewayLease(FakeLeaseAuthority authority, ConcurrentQueue<string>? log = null) : IChatGatewayLease
{
    public TimeSpan RenewInterval => TimeSpan.FromMilliseconds(20);

    public TimeSpan Duration => TimeSpan.FromMilliseconds(60);

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        while (!authority.TryTake(this))
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(authority.Holds(this));

    public Task ReleaseAsync(CancellationToken cancellationToken)
    {
        log?.Enqueue("release");
        authority.Release(this);
        return Task.CompletedTask;
    }
}
