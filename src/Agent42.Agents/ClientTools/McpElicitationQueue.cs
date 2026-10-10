namespace Agent42.Agents;

/// <summary>
/// The MCP elicitations of ONE interactive run that wait for the browser (ADR 0095), owned by the run's
/// <see cref="ClientToolBroker"/>. Unlike the broker's tool calls, which are touched only on the grain's scheduler,
/// an elicitation is enqueued by the MCP SDK's handler on the session's own thread, so this queue takes a lock.
/// The grain reads it on its scheduler: <see cref="Arrived"/> wakes the stream loop while a pull is pending, and
/// <see cref="TakeNew"/> hands it the elicitations to stream.
///
/// <para><b>A closed queue refuses new elicitations at once</b> (<see cref="TryEnqueue"/> returns null, and the
/// sink answers <c>cancel</c>). A wait on the SDK's thread must never hang after its run is over.</para>
/// </summary>
public sealed class McpElicitationQueue
{
    private static readonly Task Never = new TaskCompletionSource().Task;

    private readonly Lock _gate = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly Queue<PendingMcpElicitation> _unstreamed = new();

    private TaskCompletionSource _arrived = NewSignal();

    private bool _closed;

    private int _order;

    /// <summary>
    /// Completes when an elicitation is waiting to be streamed: immediately if one already is, never once the
    /// queue is closed.
    /// </summary>
    public Task Arrived
    {
        get
        {
            lock (_gate)
            {
                return _unstreamed.Count > 0 ? Task.CompletedTask : _closed ? Never : _arrived.Task;
            }
        }
    }

    /// <summary>The elicitations still waiting for an answer, oldest first, as the recovery GET lists them.</summary>
    public IReadOnlyList<PendingClientToolCall> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values
                    .Where(e => !e.Answer.Task.IsCompleted)
                    .OrderBy(e => e.Order)
                    .Select(e => new PendingClientToolCall(
                        e.Elicitation.Id, ClientToolLimits.McpElicitation, e.Elicitation.Input))];
            }
        }
    }

    /// <summary>
    /// Adds an elicitation and returns the task the browser's answer completes. Null when the queue is closed.
    /// </summary>
    public Task<string>? TryEnqueue(PendingMcpElicitation elicitation)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            var entry = new Entry(
                elicitation, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously), _order++);
            _entries[elicitation.Id] = entry;
            _unstreamed.Enqueue(elicitation);
            _arrived.TrySetResult();
            _arrived = NewSignal();
            return entry.Answer.Task;
        }
    }

    /// <summary>The elicitations not yet streamed, in arrival order. Each is returned once.</summary>
    public IReadOnlyList<PendingMcpElicitation> TakeNew()
    {
        lock (_gate)
        {
            var taken = _unstreamed.ToList();
            _unstreamed.Clear();
            return taken;
        }
    }

    /// <summary>
    /// Completes a waiting elicitation with the browser's answer, UNtruncated: cutting JSON would corrupt it, so the
    /// sink refuses an oversized answer instead. False when no such elicitation is waiting.
    /// </summary>
    public bool TryComplete(string id, string answer)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) && entry.Answer.TrySetResult(answer);
        }
    }

    /// <summary>Forgets an elicitation whose wait ended; a later answer for it is then refused.</summary>
    public void Forget(string id)
    {
        lock (_gate)
        {
            if (_entries.Remove(id, out var entry))
            {
                entry.Answer.TrySetCanceled();
            }
        }
    }

    /// <summary>The run is over: every waiting elicitation is cancelled, and new ones are refused.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Answer.TrySetCanceled();
            }

            _entries.Clear();
            _unstreamed.Clear();
            _arrived.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Entry(PendingMcpElicitation Elicitation, TaskCompletionSource<string> Answer, int Order);
}

/// <summary>
/// One elicitation as it is streamed and listed. <see cref="Id"/> is a server-minted <c>elicit_…</c> id, never the
/// MCP server's own id. <see cref="ToolCallId"/> is the model's call id of the MCP tool that is waiting, which the
/// browser anchors the prompt to. <see cref="Input"/> is the JSON the browser renders.
/// </summary>
public sealed record PendingMcpElicitation(string Id, string ToolCallId, string ServerName, string Input);
