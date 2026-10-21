namespace Agent42.Backend;

/// <summary>
/// Signal's settings (ADR 0111, phase 3), bound from <c>ChatGateway:Signal</c>. Signal ships dormant: with
/// <see cref="Enabled"/> false the adapter is not registered, no daemon ever starts and the panel does not offer Signal.
/// </summary>
public sealed class SignalOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "ChatGateway:Signal";

    /// <summary>Whether Signal is offered at all. Off until the store's Azure Files measurement passed (plan Step 24).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// An already running daemon (DevHost's stand-in, tests). When set, nothing is ever spawned and the lease hook is a
    /// no-op: the daemon's lifetime is somebody else's.
    /// </summary>
    public Uri? DaemonUrl { get; set; }

    /// <summary>signal-cli's <c>--config</c> directory: the account store (keys, <c>account.db</c>, attachments).</summary>
    public string DataDir { get; set; } = "/mnt/signal";

    /// <summary>The signal-cli launcher script inside the image.</summary>
    public string ExecutablePath { get; set; } = "/opt/signal-cli/bin/signal-cli";

    /// <summary>The loopback port the spawned daemon listens on. Never bound to anything but <c>127.0.0.1</c>.</summary>
    public int Port { get; set; } = 8086;

    /// <summary>Passed to the JVM as <c>JAVA_OPTS</c>; caps the heap (Step 24b measured 186 MiB idle).</summary>
    public string JavaOpts { get; set; } = "-Xmx256m";
}

/// <summary>
/// The signal-cli daemon, as the adapter and the registration endpoints see it: where to reach it, started on demand.
/// The implementation runs it only while this instance holds the gateway lease (S2), so callers never start a second
/// daemon on the shared store.
/// </summary>
public interface ISignalDaemon
{
    /// <summary>
    /// The daemon's base URL (<c>http://127.0.0.1:&lt;port&gt;/</c>), after it answered <c>/api/v1/check</c>. Throws
    /// <see cref="SignalDaemonUnavailableException"/> when this instance does not hold the lease or the daemon did not
    /// come up.
    /// </summary>
    Task<Uri> EnsureRunningAsync(CancellationToken cancellationToken);
}

/// <summary>The daemon can't be used here right now: no lease, or it failed to start. Transient for the supervisor.</summary>
public sealed class SignalDaemonUnavailableException(string message) : Exception(message);
