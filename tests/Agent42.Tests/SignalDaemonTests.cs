using System.Collections.Concurrent;
using System.Net;
using Agent42.Backend;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>
/// The Signal daemon host (ADR 0111, phase 3, S2/SC3, AC27): a fake launcher, a manual clock and a stub readiness
/// endpoint, so no JVM and no real waiting. The daemon must run only while the lease is held, die with it, and restart
/// after a crash with backoff.
/// </summary>
public sealed class SignalDaemonTests
{
    private readonly ManualTime _time = new();
    private readonly FakeLauncher _launcher = new();
    private readonly StubCheck _check = new();
    private readonly StubLease _lease = new();
    private readonly CapturingLogger<SignalDaemon> _logger = new();
    private bool _holdsLease = true;

    [Fact]
    public async Task NoStartWithoutTheLease()
    {
        _holdsLease = false;
        using var daemon = NewDaemon();

        await Assert.ThrowsAsync<SignalDaemonUnavailableException>(() => daemon.EnsureRunningAsync(CancellationToken.None));

        Assert.Empty(_launcher.Processes);
    }

    [Fact]
    public async Task StartsOnLoopbackWithOnConnectionReceive_AndNeverIgnoresAttachments()
    {
        using var daemon = NewDaemon(new SignalOptions { DataDir = "/data/signal", Port = 9123, JavaOpts = "-Xmx99m" });

        var url = await Ensure(daemon);

        var info = Assert.Single(_launcher.Infos);
        Assert.Equal("http://127.0.0.1:9123/", url.AbsoluteUri);
        Assert.Equal(
            ["--config", "/data/signal", "daemon", "--http=127.0.0.1:9123", "--receive-mode=on-connection", "--ignore-stories", "--no-receive-stdout"],
            info.Arguments);
        Assert.Contains(info.Arguments, a => a.Contains("127.0.0.1"));
        Assert.DoesNotContain("--ignore-attachments", info.Arguments);
        Assert.Equal("-Xmx99m", info.Environment["JAVA_OPTS"]);
        Assert.Equal("/opt/signal-cli/bin/signal-cli", info.FileName);
    }

    [Fact]
    public async Task WaitsForTheCheckEndpoint()
    {
        _check.Statuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        _check.Statuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        _check.Down = false;
        using var daemon = NewDaemon();

        await Ensure(daemon);

        Assert.True(_check.Requests >= 3);
        Assert.All(_check.Uris, u => Assert.Equal("http://127.0.0.1:8086/api/v1/check", u));
    }

    [Fact]
    public async Task NeverReady_TimesOutAndStopsTheProcess()
    {
        _check.Default = HttpStatusCode.ServiceUnavailable;
        using var daemon = NewDaemon();

        await Assert.ThrowsAsync<SignalDaemonUnavailableException>(() => Ensure(daemon));

        var process = Assert.Single(_launcher.Processes);
        Assert.True(process.Terminated);
        Assert.True(process.Exited.IsCompleted);
    }

    [Fact]
    public async Task ConcurrentCallers_StartItOnce()
    {
        using var daemon = NewDaemon();

        var calls = await Pump(Task.WhenAll(daemon.EnsureRunningAsync(CancellationToken.None), daemon.EnsureRunningAsync(CancellationToken.None)), daemon);

        Assert.Equal(2, calls.Length);
        Assert.Single(_launcher.Processes);
    }

    [Fact]
    public async Task ACrash_RestartsOnTheNextCall_WithGrowingBackoff()
    {
        using var daemon = NewDaemon();
        await Ensure(daemon);

        _launcher.Processes[0].Crash();
        await CrashObservedAsync(daemon);
        Assert.Single(_launcher.Processes);

        _time.Advance(TimeSpan.FromSeconds(2));
        await Ensure(daemon);
        Assert.Equal(2, _launcher.Processes.Count);

        // Crashed again straight away: the backoff doubled.
        _launcher.Processes[1].Crash();
        await CrashObservedAsync(daemon);
        _time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<SignalDaemonUnavailableException>(() => daemon.EnsureRunningAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromSeconds(2));
        await Ensure(daemon);
        Assert.Equal(3, _launcher.Processes.Count);
    }

    // The daemon learns of a crash on its own exit watcher, asynchronously. Under a loaded full-suite run a fixed delay was
    // not always enough, so wait until it refuses (it is in backoff) instead of sleeping.
    private static async Task CrashObservedAsync(SignalDaemon daemon)
    {
        await TestWait.WaitUntilAsync(async () =>
        {
            try
            {
                await daemon.EnsureRunningAsync(CancellationToken.None);
                return false;
            }
            catch (SignalDaemonUnavailableException)
            {
                return true;
            }
        }, "the daemon to notice the crash");
    }

    [Fact]
    public async Task LeaseLost_TerminatesThenKillsAfterTheGrace_AndAwaitsExit()
    {
        using var daemon = NewDaemon();
        await Ensure(daemon);
        var process = _launcher.Processes[0];
        process.IgnoreTerminate = true;

        var stopping = daemon.OnLeaseLostAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.True(process.Terminated);
        Assert.False(process.Killed);
        Assert.False(stopping.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(5));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(process.Killed);
        Assert.True(process.Exited.IsCompleted);
    }

    [Fact]
    public async Task LeaseLost_WithAGracefulExit_NeverKills()
    {
        using var daemon = NewDaemon();
        await Ensure(daemon);

        await daemon.OnLeaseLostAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var process = _launcher.Processes[0];
        Assert.True(process.Terminated);
        Assert.False(process.Killed);
    }

    [Fact]
    public async Task LeaseLostTwice_IsHarmless()
    {
        using var daemon = NewDaemon();
        await Ensure(daemon);

        await daemon.OnLeaseLostAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await daemon.OnLeaseLostAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _launcher.Processes[0].TerminateCalls);
    }

    [Fact]
    public async Task AfterLeaseLost_NothingRestartsUntilTheNextRenewal()
    {
        using var daemon = NewDaemon();
        await Ensure(daemon);
        await daemon.OnLeaseLostAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<SignalDaemonUnavailableException>(() => daemon.EnsureRunningAsync(CancellationToken.None));
        Assert.Single(_launcher.Processes);

        await daemon.OnLeaseRenewedAsync(CancellationToken.None);
        await Ensure(daemon);
        Assert.Equal(2, _launcher.Processes.Count);
    }

    [Fact]
    public async Task LeaseLost_DuringAStart_CancelsItAtOnce()
    {
        _check.Default = HttpStatusCode.ServiceUnavailable;
        using var daemon = NewDaemon();
        var starting = daemon.EnsureRunningAsync(CancellationToken.None);
        await Task.Delay(50);

        await daemon.OnLeaseLostAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<SignalDaemonUnavailableException>(() => starting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(_launcher.Processes[0].Exited.IsCompleted);
    }

    [Fact]
    public void TheWatchdogWindow_IsDerivedFromTheLease_AndShorterThanIt()
    {
        using var daemon = NewDaemon();

        Assert.Equal(_lease.Duration - _lease.RenewInterval - SignalDaemonTimings.Default.WatchdogMargin, daemon.WatchdogWindow);
        Assert.True(daemon.WatchdogWindow < _lease.Duration);
        Assert.True(daemon.WatchdogWindow > _lease.RenewInterval);
    }

    [Fact]
    public async Task TheWatchdog_StopsTheDaemon_WhenRenewalsStall()
    {
        using var daemon = NewDaemon();
        await Ensure(daemon);
        var process = _launcher.Processes[0];

        _time.Advance(daemon.WatchdogWindow - TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        Assert.False(process.Terminated);

        _time.Advance(TimeSpan.FromSeconds(11));
        await TestWait.WaitUntilAsync(() => process.Exited.IsCompleted);

        Assert.True(process.Terminated);
        await Assert.ThrowsAsync<SignalDaemonUnavailableException>(() => daemon.EnsureRunningAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ARenewal_ResetsTheWatchdog()
    {
        using var daemon = NewDaemon();
        await Ensure(daemon);
        var process = _launcher.Processes[0];

        // Three times the window passes, but a renewal arrives every 20 s, the way the lease does.
        for (var i = 0; i < 15; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            await daemon.OnLeaseRenewedAsync(CancellationToken.None);
        }

        await Task.Delay(100);
        Assert.False(process.Terminated);
        Assert.False(process.Exited.IsCompleted);
    }

    [Fact]
    public async Task AnExternalDaemonUrl_NeverSpawns_AndTheHooksDoNothing()
    {
        var external = new Uri("http://stand-in:8085/");
        using var daemon = NewDaemon(new SignalOptions { DaemonUrl = external });

        var url = await daemon.EnsureRunningAsync(CancellationToken.None);
        await daemon.OnLeaseLostAsync(CancellationToken.None);
        await daemon.OnLeaseRenewedAsync(CancellationToken.None);
        url = await daemon.EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(external, url);
        Assert.Empty(_launcher.Processes);
    }

    [Fact]
    public async Task ForwardedOutput_HasPhoneNumbersRedacted()
    {
        using var daemon = NewDaemon();
        await Ensure(daemon);

        _launcher.Infos[0].OnOutputLine("INFO Registered +31612345678 and +4915112345678 ok");

        var line = Assert.Single(_logger.Entries, e => e.Contains("signal-cli:", StringComparison.Ordinal));
        Assert.Equal("signal-cli: INFO Registered +*** and +*** ok", line);
        Assert.DoesNotContain("3161234", string.Join("\n", _logger.Entries));
        Assert.Equal("Short +12345 stays", SignalDaemon.Redact("Short +12345 stays"));
    }

    private SignalDaemon NewDaemon(SignalOptions? options = null)
        => new(options ?? new SignalOptions(), _launcher, new StubHttpClientFactory(_check), () => _holdsLease, _lease, _time, _logger);

    // EnsureRunningAsync waits on the fake clock (readiness polling), so it needs the clock pumped. Renewing as it
    // goes keeps the watchdog quiet, the way the real lease does.
    private Task<Uri> Ensure(SignalDaemon daemon) => Pump(daemon.EnsureRunningAsync(CancellationToken.None), daemon);

    private async Task<T> Pump<T>(Task<T> task, SignalDaemon daemon)
    {
        for (var i = 0; i < 4000 && !task.IsCompleted; i++)
        {
            await Task.Delay(1);
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await daemon.OnLeaseRenewedAsync(CancellationToken.None);
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class StubLease : IChatGatewayLease
    {
        public TimeSpan RenewInterval => TimeSpan.FromSeconds(20);

        public TimeSpan Duration => TimeSpan.FromSeconds(60);

        public Task AcquireAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeLauncher : ISignalProcessLauncher
    {
        public List<FakeProcess> Processes { get; } = [];

        public List<SignalProcessStartInfo> Infos { get; } = [];

        public ISignalProcess Start(SignalProcessStartInfo info)
        {
            var process = new FakeProcess();
            Processes.Add(process);
            Infos.Add(info);
            return process;
        }
    }

    private sealed class FakeProcess : ISignalProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _terminateCalls;

        public Task Exited => _exited.Task;

        public bool HasExited => _exited.Task.IsCompleted;

        public bool IgnoreTerminate { get; set; }

        public bool Terminated { get; private set; }

        public bool Killed { get; private set; }

        public int TerminateCalls => _terminateCalls;

        public void Terminate()
        {
            Interlocked.Increment(ref _terminateCalls);
            Terminated = true;
            if (!IgnoreTerminate)
            {
                _exited.TrySetResult();
            }
        }

        public void Kill()
        {
            Killed = true;
            _exited.TrySetResult();
        }

        public void Crash() => _exited.TrySetResult();

        public void Dispose()
        {
        }
    }

    private sealed class StubCheck
    {
        private int _requests;

        public ConcurrentQueue<HttpStatusCode> Statuses { get; } = new();

        public ConcurrentQueue<string> Uris { get; } = new();

        public HttpStatusCode Default { get; set; } = HttpStatusCode.OK;

        public bool Down { get; set; }

        public int Requests => _requests;

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Interlocked.Increment(ref _requests);
            Uris.Enqueue(request.RequestUri!.AbsoluteUri);
            if (Down)
            {
                throw new HttpRequestException("connection refused");
            }

            return new HttpResponseMessage(Statuses.TryDequeue(out var status) ? status : Default);
        }
    }

    private sealed class StubHttpClientFactory(StubCheck check) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(SignalDaemon.CheckHttpClientName, name);
            return new HttpClient(new StubHandler(check), disposeHandler: true);
        }
    }

    private sealed class StubHandler(StubCheck check) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(check.Respond(request));
    }

    /// <summary>The captured log lines, formatted (<c>signal-cli: &lt;line&gt;</c>).</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Queue { get; } = new();

        public IReadOnlyCollection<string> Entries => Queue;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Queue.Enqueue(formatter(state, exception));
    }

    /// <summary>A clock a test moves by hand: timers fire, in order, when <see cref="Advance"/> passes their due time.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override long GetTimestamp() => GetUtcNow().Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target;
            lock (_gate)
            {
                target = _now + by;
            }

            while (true)
            {
                ManualTimer? next;
                lock (_gate)
                {
                    _timers.RemoveAll(t => t.Disposed);
                    next = _timers.Where(t => t.Due is not null && t.Due <= target).MinBy(t => t.Due);
                    if (next is null)
                    {
                        _now = target;
                        return;
                    }

                    _now = next.Due!.Value > _now ? next.Due.Value : _now;
                }

                next.Fire();
            }
        }

        internal DateTimeOffset Now()
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now() + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;
            callback(state);
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
