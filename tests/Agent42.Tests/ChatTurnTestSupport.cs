using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IChatTurnBlobs"/> that can be told to fail, for the chat turn's resilience tests (ADR 0153): a number of
/// appends that throw before one works, or every append and meta write throwing as a dead store would.
/// </summary>
internal sealed class FaultyChatTurnBlobs(IChatTurnBlobs inner) : IChatTurnBlobs
{
    private int _appendFailures;

    /// <summary>The next N appends throw (the batch does not land).</summary>
    public void FailNextAppends(int count) => Volatile.Write(ref _appendFailures, count);

    /// <summary>Every append throws, for good.</summary>
    public bool AlwaysFailAppends { get; set; }

    /// <summary>Every write and append throws, for good: a store that is down.</summary>
    public bool AlwaysFailWrites { get; set; }

    /// <summary>How many appends were attempted (landed or not).</summary>
    public int AppendAttempts => Volatile.Read(ref _attempts);

    private int _attempts;

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);

    public Task<long?> GetLengthAsync(string key, CancellationToken cancellationToken = default) => inner.GetLengthAsync(key, cancellationToken);

    public Task<ChatTurnSlice?> GetRangeAsync(string key, long offset, CancellationToken cancellationToken = default)
        => inner.GetRangeAsync(key, offset, cancellationToken);

    public Task CreateAsync(string key, CancellationToken cancellationToken = default)
        => AlwaysFailWrites ? throw new IOException("store down") : inner.CreateAsync(key, cancellationToken);

    public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        => AlwaysFailWrites ? throw new IOException("store down") : inner.PutAsync(key, content, cancellationToken);

    public Task<ChatTurnAppendResult> AppendAsync(string key, byte[] content, long expectedOffset, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _attempts);
        if (AlwaysFailAppends || AlwaysFailWrites || Interlocked.Decrement(ref _appendFailures) >= 0)
        {
            throw new IOException("store down");
        }

        return inner.AppendAsync(key, content, expectedOffset, cancellationToken);
    }

    public Task<IReadOnlyList<ChatTurnBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => inner.ListAsync(prefix, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
}

/// <summary>Short intervals so a real-timer loop is observable in milliseconds.</summary>
internal static class ChatTurnTestOptions
{
    public static ChatTurnOptions Fast() => new()
    {
        FlushInterval = TimeSpan.FromMilliseconds(15),
        HeartbeatInterval = TimeSpan.FromMilliseconds(20),
        PollInterval = TimeSpan.FromMilliseconds(10),
        StaleAfter = TimeSpan.FromSeconds(5),
        FeedGrace = TimeSpan.FromMilliseconds(50),
    };
}

/// <summary>A pump over a store, wired the way the runner will wire it, for tests that drive it directly.</summary>
internal sealed class PumpHarness
{
    public const string User = "u1";
    public const string Conversation = "c1";
    public const string Turn = "t1";

    public PumpHarness(ChatTurnOptions options, IChatTurnBlobs? blobs = null, UserTelemetryContext? telemetry = null, string? application = null)
    {
        Options = options;
        Store = new ChatTurnStore(blobs ?? new InMemoryChatTurnBlobs(), options, TimeProvider.System);
        Feed = new TurnFeed(options.FeedMaxBytes);
        var meta = Store.StartAsync(User, Conversation, Turn, "replica-test").GetAwaiter().GetResult();
        var writer = new ChatTurnWriter(Store, User, Conversation, meta, options, TimeProvider.System, NullLogger.Instance);
        Pump = new ChatTurnPump(writer, Feed, NullLogger<ChatTurnPump>.Instance, telemetry, application);
    }

    public ChatTurnOptions Options { get; }

    public ChatTurnStore Store { get; }

    public TurnFeed Feed { get; }

    public ChatTurnPump Pump { get; }

    public Task<ChatTurnMeta?> MetaAsync() => Store.GetMetaAsync(User, Conversation, Turn);

    public async Task<IReadOnlyList<string>> LogLinesAsync()
    {
        var slice = await Store.ReadFromAsync(User, Conversation, Turn, 0);
        return slice is null
            ? []
            : System.Text.Encoding.UTF8.GetString(slice.Bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
