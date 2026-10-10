namespace Agent42.Agents;

/// <summary>
/// The client tool calls of ONE interactive run (ADR 0089): call id → the call and the result the browser will
/// post. Created by <c>ChatAsync</c> only for an interactive turn whose browser advertised client tools, and
/// reached by the tool through <c>AgentGrain.CurrentClientToolBroker</c>, an <see cref="AsyncLocal{T}"/> set
/// around that run's own pulls. A background run in a gap of this one therefore never sees it.
///
/// <para>A call is <b>announced</b> when its <c>toolCallStart</c> is projected, before the tool is invoked: the
/// browser can answer as soon as it reads that line, which may be before the next pull invokes the tool. The tool
/// then awaits the same completion. Every completion runs its continuation asynchronously, so a result posted by
/// the interleaved <c>SubmitClientToolResultAsync</c> never resumes the turn inline inside that call.</para>
///
/// <para>Touched only on the grain's scheduler (the tool, the projection and the interleaved calls all run there),
/// so it takes no lock. Not usable from the Copilot runtime's RPC thread, which is why Copilot chats do not get
/// these tools.</para>
/// </summary>
public sealed class ClientToolBroker(IReadOnlyCollection<string> supported)
{
    private readonly HashSet<string> _supported = new(supported, StringComparer.Ordinal);

    private readonly Dictionary<string, PendingCall> _calls = new(StringComparer.Ordinal);

    private int _announced;

    /// <summary>
    /// The run's MCP elicitations (ADR 0095). Separate from the tool calls because the MCP SDK enqueues from its own
    /// thread, so it is locked; the tool calls above keep their no-lock, grain-scheduler-only invariant.
    /// </summary>
    public McpElicitationQueue Elicitations { get; } = new();

    /// <summary>True when the browser of this run said it can run <paramref name="toolName"/>.</summary>
    public bool Supports(string? toolName) => toolName is not null && _supported.Contains(toolName);

    /// <summary>The calls still waiting for the browser, oldest first.</summary>
    public IReadOnlyList<PendingClientToolCall> Pending
        => [.. _calls.Values.Where(c => !c.Result.Task.IsCompleted).OrderBy(c => c.Order).Select(c => c.Call)];

    /// <summary>Records a call the browser is about to be told about. A repeat for the same id is a no-op.</summary>
    public void Announce(string callId, string toolName, string? input) => GetOrAdd(callId, toolName, input);

    /// <summary>The result the browser posts for <paramref name="callId"/>; announces the call if it is not yet.</summary>
    public Task<string> WaitFor(string callId, string toolName, string? input)
        => GetOrAdd(callId, toolName, input).Result.Task;

    /// <summary>
    /// Completes a waiting call with the browser's result, truncated to
    /// <see cref="ClientToolLimits.MaxResultChars"/>. False when no such call is waiting. The entry stays until the
    /// tool's own wait ends (<see cref="Forget"/>), so an answer that arrives BEFORE the tool is invoked is still
    /// the one the tool reads.
    /// </summary>
    public bool TryComplete(string callId, string result)
        => _calls.TryGetValue(callId, out var call)
            && call.Result.TrySetResult(result.Length > ClientToolLimits.MaxResultChars
                ? result[..ClientToolLimits.MaxResultChars]
                : result);

    /// <summary>Forgets a call whose wait ended, answered or not; a later answer for it is then refused.</summary>
    public void Forget(string callId)
    {
        if (_calls.Remove(callId, out var call))
        {
            call.Result.TrySetCanceled();
        }
    }

    /// <summary>
    /// The run is over: every call still waiting is cancelled and forgotten, and the elicitation queue closes, so a
    /// late elicitation is answered <c>cancel</c> at once.
    /// </summary>
    public void Close()
    {
        foreach (var call in _calls.Values)
        {
            call.Result.TrySetCanceled();
        }

        _calls.Clear();
        Elicitations.Close();
    }

    private PendingCall GetOrAdd(string callId, string toolName, string? input)
    {
        if (!_calls.TryGetValue(callId, out var call))
        {
            call = new PendingCall(
                new PendingClientToolCall(callId, toolName, input),
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
                _announced++);
            _calls[callId] = call;
        }

        return call;
    }

    private sealed record PendingCall(PendingClientToolCall Call, TaskCompletionSource<string> Result, int Order);
}
