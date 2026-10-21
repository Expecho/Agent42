using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Agent42.Backend;

/// <summary>Timing knobs for <see cref="SignalDaemon"/>; tests shrink them.</summary>
public sealed record SignalDaemonTimings(
    TimeSpan ReadyTimeout,
    TimeSpan ReadyPollInterval,
    TimeSpan TerminateGrace,
    TimeSpan RestartBackoffBase,
    TimeSpan RestartBackoffMax,
    TimeSpan WatchdogCheckInterval,
    TimeSpan WatchdogMargin,
    TimeSpan HealthyRunTime)
{
    /// <summary>JVM startup is slow, so readiness gets a minute; SIGTERM gets 5 s before SIGKILL (SC3).</summary>
    public static readonly SignalDaemonTimings Default = new(
        ReadyTimeout: TimeSpan.FromSeconds(60),
        ReadyPollInterval: TimeSpan.FromMilliseconds(500),
        TerminateGrace: TimeSpan.FromSeconds(5),
        RestartBackoffBase: TimeSpan.FromSeconds(2),
        RestartBackoffMax: TimeSpan.FromSeconds(60),
        WatchdogCheckInterval: TimeSpan.FromSeconds(5),
        WatchdogMargin: TimeSpan.FromSeconds(5),
        HealthyRunTime: TimeSpan.FromMinutes(1));
}

/// <summary>
/// Runs signal-cli as a child process of the backend (ADR 0111, phase 3, S2/SC3), only while this instance holds the
/// chat gateway lease: the daemon has no HTTP auth and its store is SQLite behind Java file locks, so two daemons on one
/// store must be impossible by construction.
///
/// <list type="bullet">
/// <item>Starts lazily on <see cref="EnsureRunningAsync"/>, once (single-flight), and is ready when
/// <c>/api/v1/check</c> answers 200. A crash restarts it on the next call, with capped backoff between attempts.</item>
/// <item><see cref="OnLeaseLostAsync"/> stops it (SIGTERM, then SIGKILL after a grace period, exit awaited) and refuses
/// new starts until the next <see cref="OnLeaseRenewedAsync"/>.</item>
/// <item>A <b>watchdog</b> stops it when no renewal arrived within <see cref="WatchdogWindow"/>: a renewal failure is
/// noticed only at the next tick and <c>RenewAsync</c> has no local deadline, so a hung renewal could otherwise outlive
/// the lease while a new revision opens the same store. The window is derived from the lease's own timings.</item>
/// <item>With <see cref="SignalOptions.DaemonUrl"/> set the daemon is somebody else's: nothing is ever spawned and the
/// hooks do nothing.</item>
/// <item>Output goes to the logger at Debug with phone numbers redacted (signal-cli logs them, and <c>/logs</c> is
/// owner-visible).</item>
/// </list>
/// </summary>
public sealed partial class SignalDaemon : ISignalDaemon, IChatGatewayLeaseParticipant, IDisposable
{
    /// <summary>The named client the readiness probe uses.</summary>
    public const string CheckHttpClientName = "signal-check";

    private readonly SignalOptions _options;
    private readonly ISignalProcessLauncher _launcher;
    private readonly IHttpClientFactory _httpClients;
    private readonly Func<bool> _holdsLease;
    private readonly TimeProvider _time;
    private readonly ILogger<SignalDaemon> _logger;
    private readonly SignalDaemonTimings _timings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();

    private ISignalProcess? _process;
    private DateTimeOffset _processStartedAt;
    private bool _ready;
    private bool _startsAllowed = true;
    private CancellationTokenSource _startCancellation = new();
    private DateTimeOffset _lastRenewal;
    private DateTimeOffset _nextStartAllowedAt;
    private int _failures;
    private ITimer? _watchdog;
    private bool _disposed;

    public SignalDaemon(
        SignalOptions options,
        ISignalProcessLauncher launcher,
        IHttpClientFactory httpClients,
        Func<bool> holdsLease,
        IChatGatewayLease lease,
        TimeProvider time,
        ILogger<SignalDaemon> logger,
        SignalDaemonTimings? timings = null)
    {
        _options = options;
        _launcher = launcher;
        _httpClients = httpClients;
        _holdsLease = holdsLease;
        _time = time;
        _logger = logger;
        _timings = timings ?? SignalDaemonTimings.Default;
        _lastRenewal = time.GetUtcNow();
        WatchdogWindow = DeriveWatchdogWindow(lease, _timings.WatchdogMargin);
    }

    /// <summary>
    /// How long the daemon may run without a renewal notification: the lease's duration, minus one renew interval (the
    /// last renewal may be a whole interval old when the lease is stolen), minus a margin. Always shorter than the lease.
    /// </summary>
    public TimeSpan WatchdogWindow { get; }

    /// <summary>The window for <paramref name="lease"/>, never below one second.</summary>
    public static TimeSpan DeriveWatchdogWindow(IChatGatewayLease lease, TimeSpan margin)
    {
        var window = lease.Duration - lease.RenewInterval - margin;
        return window < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : window;
    }

    /// <summary>The loopback URL the daemon listens on (or the external one).</summary>
    public Uri BaseUri => _options.DaemonUrl ?? new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_options.Port}/"));

    /// <summary>The exact argument list signal-cli is started with (SC3). Never <c>--ignore-attachments</c>: <c>getAttachment</c> reads the file.</summary>
    public IReadOnlyList<string> BuildArguments() =>
    [
        "--config", _options.DataDir,
        "daemon",
        string.Create(CultureInfo.InvariantCulture, $"--http=127.0.0.1:{_options.Port}"),
        "--receive-mode=on-connection",
        "--ignore-stories",
        "--no-receive-stdout",
    ];

    public async Task<Uri> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (_options.DaemonUrl is { } external)
        {
            return external;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning())
            {
                return BaseUri;
            }

            RequireMayStart();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CurrentStartToken());
            await StartAndWaitUntilReadyAsync(linked.Token);
            return BaseUri;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SignalDaemonUnavailableException("Signal is unavailable: the chat gateway lease was lost while the daemon started.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task OnLeaseRenewedAsync(CancellationToken cancellationToken)
    {
        lock (_state)
        {
            _lastRenewal = _time.GetUtcNow();
            if (!_startsAllowed)
            {
                _startsAllowed = true;
                _startCancellation = new CancellationTokenSource();
            }
        }

        return Task.CompletedTask;
    }

    public async Task OnLeaseLostAsync(CancellationToken cancellationToken)
    {
        if (_options.DaemonUrl is not null)
        {
            return;
        }

        // Before waiting for the gate: a start in progress (JVM boot, up to a minute) must not delay the stop.
        lock (_state)
        {
            _startsAllowed = false;
            _startCancellation.Cancel();
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await StopProcessAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog?.Dispose();
        lock (_state)
        {
            _startCancellation.Dispose();
        }
    }

    /// <summary>Redacts E.164 numbers (<c>+31612345678</c> becomes <c>+***</c>) from a line of signal-cli output.</summary>
    public static string Redact(string line) => PhoneNumber().Replace(line, "+***");

    [GeneratedRegex(@"\+\d{6,15}")]
    private static partial Regex PhoneNumber();

    private bool IsRunning() => _process is { HasExited: false } && _ready;

    private CancellationToken CurrentStartToken()
    {
        lock (_state)
        {
            return _startCancellation.Token;
        }
    }

    private void RequireMayStart()
    {
        bool allowed;
        lock (_state)
        {
            allowed = _startsAllowed;
        }

        if (!allowed || !_holdsLease())
        {
            throw new SignalDaemonUnavailableException("Signal is unavailable: this instance does not hold the chat gateway lease.");
        }

        var now = _time.GetUtcNow();
        if (now < _nextStartAllowedAt)
        {
            throw new SignalDaemonUnavailableException(
                $"The Signal daemon is restarting; the next attempt is in {(_nextStartAllowedAt - now).TotalSeconds:0} s.");
        }
    }

    private async Task StartAndWaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        await StopProcessAsync();
        var info = new SignalProcessStartInfo(
            _options.ExecutablePath,
            BuildArguments(),
            new Dictionary<string, string> { ["JAVA_OPTS"] = _options.JavaOpts },
            ForwardLine);
        var process = _launcher.Start(info);
        lock (_state)
        {
            _process = process;
            _ready = false;
            _processStartedAt = _time.GetUtcNow();
        }

        _ = WatchExitAsync(process);
        EnsureWatchdog();
        _logger.LogInformation("Started the signal-cli daemon on 127.0.0.1:{Port}.", _options.Port);

        try
        {
            await WaitUntilReadyAsync(process, cancellationToken);
        }
        catch
        {
            RecordStartFailure(cancellationToken);
            await StopProcessAsync();
            throw;
        }

        lock (_state)
        {
            _ready = true;
        }
    }

    // A start that failed (not one the lease loss cancelled) backs off. WatchExitAsync may already have counted an exit.
    private void RecordStartFailure(CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (cancellationToken.IsCancellationRequested || _nextStartAllowedAt >= _processStartedAt)
            {
                return;
            }

            _failures++;
            _nextStartAllowedAt = _time.GetUtcNow() + Backoff(_failures);
        }
    }

    private async Task WaitUntilReadyAsync(ISignalProcess process, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _timings.ReadyTimeout;
        using var http = _httpClients.CreateClient(CheckHttpClientName);
        var check = new Uri(BaseUri, "api/v1/check");
        while (true)
        {
            if (process.HasExited)
            {
                throw new SignalDaemonUnavailableException("The Signal daemon exited while starting.");
            }

            if (await CheckAsync(http, check, cancellationToken))
            {
                return;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                throw new SignalDaemonUnavailableException("The Signal daemon did not become ready in time.");
            }

            await Task.Delay(_timings.ReadyPollInterval, _time, cancellationToken);
        }
    }

    private static async Task<bool> CheckAsync(HttpClient http, Uri check, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(check, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // Runs under the gate (or from a failed start). Idempotent; ends with the process gone and awaited.
    private async Task StopProcessAsync()
    {
        ISignalProcess? process;
        lock (_state)
        {
            process = _process;
            _process = null;
            _ready = false;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                _logger.LogInformation("Stopping the signal-cli daemon.");
                process.Terminate();
                if (!await ExitedWithinAsync(process, _timings.TerminateGrace))
                {
                    _logger.LogWarning("The signal-cli daemon ignored SIGTERM; killing it.");
                    process.Kill();
                    await ExitedWithinAsync(process, _timings.TerminateGrace);
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task<bool> ExitedWithinAsync(ISignalProcess process, TimeSpan timeout)
    {
        try
        {
            await process.Exited.WaitAsync(timeout, _time);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // A process that ends on its own (not through StopProcessAsync, which clears _process first) is a crash: back off.
    private async Task WatchExitAsync(ISignalProcess process)
    {
        await process.Exited.ConfigureAwait(false);
        lock (_state)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }

            var lived = _time.GetUtcNow() - _processStartedAt;
            _failures = lived >= _timings.HealthyRunTime ? 1 : _failures + 1;
            _ready = false;
            _nextStartAllowedAt = _time.GetUtcNow() + Backoff(_failures);
        }

        _logger.LogWarning("The signal-cli daemon exited unexpectedly; it restarts on the next use.");
    }

    private TimeSpan Backoff(int failures)
    {
        var exponential = _timings.RestartBackoffBase.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 16));
        return TimeSpan.FromMilliseconds(Math.Min(exponential, _timings.RestartBackoffMax.TotalMilliseconds));
    }

    private void ForwardLine(string line)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("signal-cli: {Line}", Redact(line));
        }
    }

    private void EnsureWatchdog()
    {
        lock (_state)
        {
            _watchdog ??= _time.CreateTimer(
                _ => _ = WatchdogTickAsync(), null, _timings.WatchdogCheckInterval, _timings.WatchdogCheckInterval);
        }
    }

    private async Task WatchdogTickAsync()
    {
        try
        {
            bool stalled;
            lock (_state)
            {
                stalled = _process is not null && _time.GetUtcNow() - _lastRenewal > WatchdogWindow;
            }

            if (stalled)
            {
                _logger.LogWarning(
                    "No chat gateway lease renewal for over {Window:0} s; stopping the signal-cli daemon.", WatchdogWindow.TotalSeconds);
                await OnLeaseLostAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The signal-cli watchdog failed.");
        }
    }
}

/// <summary>Registers the daemon host and its readiness client. The adapter registration (<c>AddSignalChat</c>) calls this.</summary>
public static class SignalDaemonRegistration
{
    /// <summary>
    /// <see cref="SignalDaemon"/> as <see cref="ISignalDaemon"/> and a lease participant, the real launcher, and the
    /// <c>signal-check</c> client (no inherited resilience pipeline, a short timeout). Expects a <see cref="SignalOptions"/>
    /// singleton. The gateway control is resolved lazily: <c>ChatGatewayService</c> depends on the participants, so a
    /// direct dependency would be a cycle.
    /// </summary>
    public static IServiceCollection AddSignalDaemon(this IServiceCollection services)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(SignalDaemon.CheckHttpClientName, http => http.Timeout = TimeSpan.FromSeconds(3))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddSingleton<ISignalProcessLauncher, SignalProcessLauncher>();
        services.AddSingleton(sp => new SignalDaemon(
            sp.GetRequiredService<SignalOptions>(),
            sp.GetRequiredService<ISignalProcessLauncher>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            () => sp.GetRequiredService<IChatGatewayControl>().HoldsLease,
            sp.GetRequiredService<IChatGatewayLease>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SignalDaemon>>()));
        services.AddSingleton<ISignalDaemon>(sp => sp.GetRequiredService<SignalDaemon>());
        services.AddSingleton<IChatGatewayLeaseParticipant>(sp => sp.GetRequiredService<SignalDaemon>());
        return services;
    }
}
