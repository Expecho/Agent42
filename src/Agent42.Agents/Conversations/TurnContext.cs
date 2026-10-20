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

    private TurnContext(bool ephemeral, bool interactive, ClientToolBroker? broker, bool isClosed)
    {
        Ephemeral = ephemeral;
        Interactive = interactive;
        Broker = broker;
        IsClosed = isClosed;
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

    private int _pushes;

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

    /// <summary>The images still kept for <paramref name="callId"/>, in the order they were recorded.</summary>
    public IReadOnlyList<ToolImage> ToolImagesFor(string callId)
    {
        lock (_imagesLock)
        {
            return [.. _toolImages.Where(i => string.Equals(i.CallId, callId, StringComparison.Ordinal))];
        }
    }

    private int _partsRead;

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
