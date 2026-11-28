namespace Agent42.Agents;

/// <summary>
/// The resumable chat turn's switches and timings (ADR 0153), bound from the <c>ChatTurns</c> section. Every default lives
/// here and nowhere else: the pump, the readers and the sweeper take their numbers from this object.
/// </summary>
public sealed class ChatTurnOptions
{
    public const string SectionName = "ChatTurns";

    /// <summary>
    /// Kill switch (D7). Off, a chat turn is the HTTP response of its own POST again: the endpoint drains the grain stream
    /// straight into the response, nothing is stored and no turn can be rejoined.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the writer flushes buffered events to the store.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>A batch this large is flushed at once, without waiting for <see cref="FlushInterval"/>.</summary>
    public int FlushBytes { get; set; } = 64 * 1024;

    /// <summary>How often a reader with no local feed re-reads the store for growth.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the owner stamps the turn's meta blob while it runs.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A running turn whose heartbeat is older than this is reported as interrupted (D4, D15).</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>The replay log's cap; past it the turn is <c>truncated</c> and the live feed carries on.</summary>
    public long MaxLogBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>The in-process ring of newest events a running turn keeps for local readers (D14).</summary>
    public long FeedMaxBytes { get; set; } = 1024 * 1024;

    /// <summary>How long a finished turn's feed stays in memory before readers fall back to the store.</summary>
    public TimeSpan FeedGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Attempts for one append batch before the turn is marked degraded and persisting stops (D12).</summary>
    public int MaxAppendAttempts { get; set; } = 3;

    /// <summary>How long a turn's blobs live, durable store and in-memory twin alike (D10).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often the sweeper looks for turns older than <see cref="Retention"/>.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The NDJSON keep-alive, the fleet's value.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
}
