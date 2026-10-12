using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>One fleet event as the page and the log see it: a per-task sequence number, the type, the time and data.</summary>
public sealed record FleetEvent(long Seq, string Type, DateTimeOffset Time, JsonNode? Data)
{
    public string ToJson() => new JsonObject
    {
        ["seq"] = Seq,
        ["type"] = Type,
        ["time"] = Time,
        ["data"] = Data?.DeepClone(),
    }.ToJsonString(FleetTeamPlan.JsonOptions);

    /// <summary>The sequence number of a persisted line, or -1 when the line can't be read.</summary>
    public static long SeqOf(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var value) ? value : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }
}

/// <summary>
/// The event stream of ONE running fleet task (ADR 0097): it numbers events, fans them out live, and persists them to
/// the task's append blob. Streamed text is COALESCED before it is persisted (an append blob holds at most 50,000
/// blocks): a flush, at most once a second, merges consecutive <c>agent.text</c>/<c>agent.thinking</c> deltas of the
/// same agent into one line that carries the LAST delta's sequence number. Past <c>Fleet:MaxEventLogBytes</c>, text is
/// no longer persisted, but structural events and results still are. The live fan-out stays per delta.
///
/// <para><b>Replay then live, exactly once:</b> <see cref="Subscribe"/> registers the subscriber and snapshots the
/// events not yet confirmed in the store under ONE lock, so every event is either in the store, in the snapshot or in
/// the subscriber's channel. The reader skips anything at or below the last sequence number it sent.</para>
/// </summary>
public sealed class FleetEventLog(IFleetStore store, string userId, string taskId, long maxBytes, TimeProvider? time = null) : IFleetEventSink
{
    private static readonly HashSet<string> TextTypes = ["agent.text", "agent.thinking"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<FleetEvent> _pending = [];
    private readonly List<Channel<FleetEvent>> _subscribers = [];
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private long _seq;
    private long _persistedBytes;
    private bool _completed;

    public long LastSeq => Interlocked.Read(ref _seq);

    public void Publish(string type, object data)
    {
        var node = JsonSerializer.SerializeToNode(data, FleetTeamPlan.JsonOptions);
        lock (_gate)
        {
            var evt = new FleetEvent(++_seq, type, _time.GetUtcNow(), node);
            _pending.Add(evt);
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(evt);
            }
        }
    }

    /// <summary>A live subscription: the events not yet confirmed in the store, and a channel with everything after.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<FleetEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }

            return new Subscription([.. _pending], channel.Reader, () =>
            {
                lock (_gate)
                {
                    _subscribers.Remove(channel);
                }
            });
        }
    }

    /// <summary>Writes the pending events to the store, coalesced and capped. One flush at a time.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushing.WaitAsync(cancellationToken);
        try
        {
            List<FleetEvent> batch;
            lock (_gate)
            {
                batch = [.. _pending];
            }

            if (batch.Count == 0)
            {
                return;
            }

            var lines = new StringBuilder();
            foreach (var evt in Coalesce(batch))
            {
                var line = evt.ToJson() + "\n";
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (TextTypes.Contains(evt.Type) && _persistedBytes + bytes > maxBytes)
                {
                    continue;
                }

                _persistedBytes += bytes;
                lines.Append(line);
            }

            if (lines.Length > 0)
            {
                await store.AppendEventsAsync(userId, taskId, lines.ToString(), cancellationToken);
            }

            // Only this flusher removes, and publishers only append, so the batch is still the head of the list.
            lock (_gate)
            {
                _pending.RemoveRange(0, batch.Count);
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    /// <summary>No more events: live subscribers end after what they already have.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    /// <summary>
    /// The events a reader gets: the persisted <paramref name="stored"/> lines, then (while the task runs) the
    /// subscription's unconfirmed and live events, skipping anything at or below the last sequence number sent. A
    /// coalesced line carries its last delta's number, so the deltas it holds are skipped too.
    /// </summary>
    public static async IAsyncEnumerable<string> ReplayAsync(
        IReadOnlyList<string> stored, Subscription? subscription,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            long last = 0;
            foreach (var line in stored)
            {
                var seq = FleetEvent.SeqOf(line);
                if (seq > last)
                {
                    last = seq;
                    yield return line;
                }
            }

            if (subscription is null)
            {
                yield break;
            }

            foreach (var evt in subscription.Unconfirmed.Where(e => e.Seq > last))
            {
                last = evt.Seq;
                yield return evt.ToJson();
            }

            await foreach (var evt in subscription.Live.ReadAllAsync(cancellationToken))
            {
                if (evt.Seq > last)
                {
                    last = evt.Seq;
                    yield return evt.ToJson();
                }
            }
        }
        finally
        {
            subscription?.Unsubscribe();
        }
    }

    /// <summary>A live subscriber: what wasn't confirmed in the store when it subscribed, and everything after.</summary>
    public sealed record Subscription(IReadOnlyList<FleetEvent> Unconfirmed, ChannelReader<FleetEvent> Live, Action Unsubscribe);

    /// <summary>Merges consecutive text deltas of the same kind and agent; the merged event keeps the last seq.</summary>
    public static List<FleetEvent> Coalesce(IEnumerable<FleetEvent> events)
    {
        var merged = new List<FleetEvent>();
        foreach (var evt in events)
        {
            if (merged.Count > 0 && merged[^1] is var last && SameStream(last, evt))
            {
                var data = last.Data!.DeepClone().AsObject();
                data["text"] = (string?)last.Data!["text"] + (string?)evt.Data!["text"];
                merged[^1] = evt with { Data = data };
                continue;
            }

            merged.Add(evt);
        }

        return merged;
    }

    private static bool SameStream(FleetEvent a, FleetEvent b)
        => a.Type == b.Type && TextTypes.Contains(a.Type)
            && a.Data is JsonObject x && b.Data is JsonObject y
            && Key(x) == Key(y);

    private static string Key(JsonObject data) => $"{data["teamId"]}|{data["attempt"]}|{data["executorId"]}";
}
