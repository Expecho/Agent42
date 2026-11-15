namespace Agent42.Agents;

/// <summary>
/// The state of ONE run (ADR 0106): a chat turn, an agent-run drain or a fired reminder. One is created at each run
/// entry, passed explicitly down the grain-side call chain, and read by tools and context providers through
/// <see cref="CurrentOrClosed"/>. It is never a grain field: two runs can be in flight on one activation (a reminder or
/// an <c>EnqueuePromptAsync</c> run in a <c>yield</c> gap of a streaming turn), and a field would be shared by both.
///
/// <para><b>Reach.</b> <see cref="RunInAsync{T}(TurnContext, Func{ValueTask{T}})"/> sets the context in its own async
/// frame, so it reaches the code the body runs (Agent Framework's tool calls, the context providers, the history
/// filters; measured on AF 1.23) and nothing after the call returns. The grain calls it around every pull and the
/// dispose of a streaming run, and around the reminder's <c>RunAsync</c>. A Copilot tool runs on the SDK's RPC thread,
/// where nothing flows, so <see cref="SchedulerBoundAIFunction"/> re-establishes the context there.</para>
///
/// <para><b>Fail closed.</b> Outside a run, <see cref="CurrentOrClosed"/> is <see cref="Closed"/>: ephemeral, no
/// broker, budgets exhausted and mutations ignored. A tool reached with no run is a bug, and refusing is the safe
/// direction. The async-local is private to this file (a source guard in <c>ProjectLayoutTests</c> pins it), so no
/// reader can skip the fallback.</para>
/// </summary>
public sealed class TurnContext
{
    private static readonly AsyncLocal<TurnContext?> Current = new();

    private TurnContext(bool ephemeral, bool interactive, ClientToolBroker? broker, bool isClosed, bool fleetResultTurn = false)
    {
        Ephemeral = ephemeral;
        Interactive = interactive;
        Broker = broker;
        IsClosed = isClosed;
        FleetResultTurn = fleetResultTurn;
    }

    /// <summary>The context a tool sees outside any run: ephemeral, not interactive, no broker, budgets exhausted.</summary>
    public static TurnContext Closed { get; } = new(ephemeral: true, interactive: false, broker: null, isClosed: true);

    /// <summary>The context of the run whose code is executing, or <see cref="Closed"/>.</summary>
    public static TurnContext CurrentOrClosed => Current.Value ?? Closed;

    /// <summary>An incognito turn (ADR 0034): nothing is persisted, and the tools that would leave a trace refuse.</summary>
    public bool Ephemeral { get; }

    /// <summary>A browser-streamed turn. Gates follow-up suggestions, the stop handle and the reauth notices.</summary>
    public bool Interactive { get; }

    /// <summary>The run's client-tool broker (ADR 0089), or null when no browser of this run runs client tools.</summary>
    public ClientToolBroker? Broker { get; }

    /// <summary>True only for <see cref="Closed"/>.</summary>
    public bool IsClosed { get; }

    /// <summary>
    /// The background turn that delivers a fleet task's result (ADR 0115, <see cref="ForFleetResult"/>). Such a turn
    /// can't start another fleet task: without this loop guard a result could start a task whose result starts a task,
    /// unbounded and steerable by the result text.
    /// </summary>
    public bool FleetResultTurn { get; }

    /// <summary>
    /// Fleet tasks started during this run (ADR 0115), bounded by <see cref="FleetToolLimits.MaxStartsPerTurn"/>.
    /// <see cref="int.MaxValue"/> (exhausted) when closed.
    /// </summary>
    public int FleetStarts => IsClosed ? int.MaxValue : _fleetStarts;

    /// <summary>
    /// Push notifications the model actually SENT during this run (ADR 0075), bounded by
    /// <see cref="PushToolLimits.MaxPushesPerTurn"/>. The away-reply push reads it after the run, so a model that
    /// already pushed is not followed by a generic notice. <see cref="int.MaxValue"/> (exhausted) when closed.
    /// </summary>
    public int PushesSent => IsClosed ? int.MaxValue : _pushes;

    /// <summary>
    /// <c>read_document</c> parts read during this run (ADR 0098), bounded by
    /// <see cref="ReadDocumentLimits.MaxPartsPerTurn"/>. <see cref="int.MaxValue"/> (exhausted) when closed.
    /// </summary>
    public int DocumentPartsRead => IsClosed ? int.MaxValue : _partsRead;

    /// <summary>
    /// True when <c>read_web_document</c> ingested a document during this run, so the agent must be rebuilt once the
    /// run is over (ADR 0066). The rebuild cannot happen inside the tool: it swaps the session out from under the
    /// framework run that is iterating it. Consumed by <see cref="TakeWebDocumentIngested"/>.
    /// </summary>
    public bool WebDocumentIngested { get; private set; }

    /// <summary>
    /// Whether an MCP tool's images may be shown to the model this run (the Chrome-extension ADR). Set once the run's
    /// model is decided, from the grain's one vision rule (<c>CanSeeImages</c>: never a copilot chat, otherwise the
    /// model's vision capability). False until then and always false when closed, so a tool image reached with no
    /// decision is omitted, which is fail-closed.
    /// </summary>
    public bool ToolImagesVisible { get; private set; }

    /// <summary>True when any tool image is waiting to be shown on this run's next model call.</summary>
    public bool HasToolImages
    {
        get
        {
            lock (_imagesLock)
            {
                return _toolImages.Count > 0;
            }
        }
    }

    /// <summary>
    /// Whether the browser streaming this run can render MCP App views (ADR 0133): an interactive run whose request
    /// advertised <see cref="ClientToolLimits.McpApp"/>. False until set and always false when closed, so every other
    /// run (reminder, agent-run, email, chat gateway, fleet, a browser without the capability) stays text-only.
    /// </summary>
    public bool AppsSupported { get; private set; }

    /// <summary>
    /// What the open MCP App views asked the model to know this turn (<c>ui/update-model-context</c>, ADR 0133), as JSON
    /// text the browser sent with the request, or null. Untrusted third-party text: <c>AppContextProvider</c> shows it as
    /// a labeled trailing message, and it is never persisted. Per run, never a grain field (ADR 0106).
    /// </summary>
    public string? AppContext { get; private set; }

    /// <summary>Applies the browser's app context, bounded. Ignored when closed or when over <see cref="McpToolLimits.MaxAppContextChars"/>.</summary>
    public void SetAppContext(string? context)
    {
        if (!IsClosed && !string.IsNullOrWhiteSpace(context) && context.Length <= McpToolLimits.MaxAppContextChars)
        {
            AppContext = context;
        }
    }

    /// <summary>The MCP App results recorded so far on this run, with their call ids, in call order (replay persists these).</summary>
    public IReadOnlyList<(string CallId, McpAppResult Result)> AppResults
    {
        get
        {
            lock (_imagesLock)
            {
                return [.. _appResults.Select(kv => (kv.Key, kv.Value))];
            }
        }
    }

    private int _pushes;

    private readonly Dictionary<string, McpAppResult> _appResults = new(StringComparer.Ordinal);

    private readonly Lock _imagesLock = new();

    private readonly List<ToolImage> _toolImages = [];

    /// <summary>Applies the run's vision decision. Ignored when closed.</summary>
    public void SetToolImagesVisible(bool visible)
    {
        if (!IsClosed)
        {
            ToolImagesVisible = visible;
        }
    }

    /// <summary>
    /// Records an image an MCP tool returned, to be shown to the model on this run's model calls, never persisted.
    /// Refused (false) when closed, when images are not visible this run, or without a call id (the Copilot path has
    /// no function-invoking client, so it has none). At most <see cref="McpToolLimits.MaxImagesPerRun"/> are kept,
    /// and the oldest is evicted.
    /// </summary>
    public bool RecordToolImage(string? callId, ReadOnlyMemory<byte> data, string mediaType)
    {
        if (IsClosed || !ToolImagesVisible || string.IsNullOrEmpty(callId))
        {
            return false;
        }

        lock (_imagesLock)
        {
            _toolImages.Add(new ToolImage(callId, data, mediaType));
            while (_toolImages.Count > McpToolLimits.MaxImagesPerRun)
            {
                _toolImages.RemoveAt(0);
            }
        }

        return true;
    }

    /// <summary>Applies the browser's MCP App capability. Ignored when closed or not interactive.</summary>
    public void SetAppsSupported(bool supported)
    {
        if (!IsClosed && Interactive)
        {
            AppsSupported = supported;
        }
    }

    /// <summary>
    /// Records the raw result of an MCP App tool call so the stream can hand it to the browser (ADR 0133). Refused
    /// (false) when closed, when the browser cannot render apps, without a call id, or when the result is over
    /// <see cref="McpToolLimits.MaxAppResultBytes"/>: the card then degrades to the plain tool chip. At most
    /// <see cref="McpToolLimits.MaxAppResultsPerRun"/> are kept per run.
    /// </summary>
    public bool RecordAppResult(string? callId, McpAppResult result)
    {
        if (IsClosed || !AppsSupported || string.IsNullOrEmpty(callId) || result.ResultJson.Length > McpToolLimits.MaxAppResultBytes)
        {
            return false;
        }

        lock (_imagesLock)
        {
            if (_appResults.Count >= McpToolLimits.MaxAppResultsPerRun && !_appResults.ContainsKey(callId))
            {
                return false;
            }

            _appResults[callId] = result;
        }

        return true;
    }

    /// <summary>The recorded app result for <paramref name="callId"/>, or null.</summary>
    public McpAppResult? AppResultFor(string? callId)
    {
        if (string.IsNullOrEmpty(callId))
        {
            return null;
        }

        lock (_imagesLock)
        {
            return _appResults.GetValueOrDefault(callId);
        }
    }

    /// <summary>The images still kept for <paramref name="callId"/>, in the order they were recorded.</summary>
    public IReadOnlyList<ToolImage> ToolImagesFor(string callId)
    {
        lock (_imagesLock)
        {
            return [.. _toolImages.Where(i => string.Equals(i.CallId, callId, StringComparison.Ordinal))];
        }
    }

    private int _partsRead;

    private int _pastChatCalls;

    private int _fleetStarts;

    /// <summary>Counts a started fleet task. Ignored when closed.</summary>
    public void RecordFleetStart()
    {
        if (!IsClosed)
        {
            _fleetStarts++;
        }
    }

    /// <summary>
    /// <c>search_past_chats</c> + <c>read_past_chat</c> calls during this run (ADR 0112), bounded by
    /// <see cref="PastChatLimits.MaxCallsPerTurn"/>. <see cref="int.MaxValue"/> (exhausted) when closed.
    /// </summary>
    public int PastChatCalls => IsClosed ? int.MaxValue : _pastChatCalls;

    /// <summary>Counts a past-chat tool call. Ignored when closed.</summary>
    public void RecordPastChatCall()
    {
        if (!IsClosed)
        {
            _pastChatCalls++;
        }
    }

    /// <summary>Counts a sent push. Ignored when closed.</summary>
    public void RecordPushSent()
    {
        if (!IsClosed)
        {
            _pushes++;
        }
    }

    /// <summary>Counts a part read. Ignored when closed.</summary>
    public void RecordDocumentPartRead()
    {
        if (!IsClosed)
        {
            _partsRead++;
        }
    }

    /// <summary>Records a mid-run ingest. Ignored when closed.</summary>
    public void RecordWebDocumentIngested()
    {
        if (!IsClosed)
        {
            WebDocumentIngested = true;
        }
    }

    /// <summary>True once if the run ingested a web document; the flag is cleared, so the rebuild runs once.</summary>
    public bool TakeWebDocumentIngested()
    {
        var ingested = WebDocumentIngested;
        WebDocumentIngested = false;
        return ingested;
    }

    /// <summary>A browser-streamed (or MCP-drained, <paramref name="interactive"/> false) chat turn.</summary>
    public static TurnContext ForChat(bool ephemeral, bool interactive, ClientToolBroker? broker)
        => new(ephemeral, interactive, broker, isClosed: false);

    /// <summary>A run nobody watches: an agent-run drain or a fired reminder. Never ephemeral, never interactive.</summary>
    public static TurnContext ForBackground() => new(ephemeral: false, interactive: false, broker: null, isClosed: false);

    /// <summary>The background turn that delivers a fleet task's result (ADR 0115): a background run marked
    /// <see cref="FleetResultTurn"/>, so it can't start another fleet task.</summary>
    public static TurnContext ForFleetResult()
        => new(ephemeral: false, interactive: false, broker: null, isClosed: false, fleetResultTurn: true);

    /// <summary>One image an MCP tool returned during this run: request-only, keyed by the tool call it answers.</summary>
    public sealed record ToolImage(string CallId, ReadOnlyMemory<byte> Data, string MediaType);

    /// <summary>Runs <paramref name="body"/> with <paramref name="context"/> current, without leaking it to the caller.</summary>
    public static async ValueTask<T> RunInAsync<T>(TurnContext context, Func<ValueTask<T>> body)
    {
        Current.Value = context;
        return await body();
    }

    /// <inheritdoc cref="RunInAsync{T}(TurnContext, Func{ValueTask{T}})"/>
    public static async ValueTask RunInAsync(TurnContext context, Func<ValueTask> body)
    {
        Current.Value = context;
        await body();
    }
}
