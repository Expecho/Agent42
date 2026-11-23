using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// An Orleans grain wrapping a Microsoft Agent Framework <see cref="AIAgent"/>.
/// Streams replies and persists the conversation session in the "agent-state"
/// storage provider (Cosmos DB in production). Implements <see cref="IRemindable"/>
/// so the agent can schedule durable reminders via the <c>schedule_prompt</c> tool.
///
/// <para>This is the CORE part of a class split across feature folders (ADR 0039). It holds every
/// dependency (the search and research slices keep their own feature-only fields, ADR 0061/0082), plus
/// activation, the run entry points (<see cref="ChatAsync"/>, <see cref="EnqueuePromptAsync"/> and
/// <see cref="ReceiveReminder"/>) and the persist pipeline. The feature slices live beside their feature
/// (<c>Web/AgentGrain.WebTools.cs</c>, <c>Documents/AgentGrain.Documents.cs</c>, and so on) but all
/// declare this one namespace: a partial class cannot span namespaces, which is why
/// <c>AgentGrain.*.cs</c> is exempted from the IDE0130 folder/namespace rule in
/// <c>.editorconfig</c>.</para>
///
/// <para><b>A value of one run is never a field here</b> (ADR 0106). Each run entry creates its
/// <see cref="TurnContext"/> and passes it down; tools read <see cref="TurnContext.CurrentOrClosed"/>. The
/// fields that remain name running turns (<c>_turnCts</c>, ADR 0078, published by identity) or are
/// activation-scoped by design. Read <c>_turnCts</c>'s remarks before adding a field of either kind.</para>
/// </summary>
public sealed partial class AgentGrain : Grain, IAgentGrain, IRemindable
{
    public const string StorageProviderName = "agent-state";

    /// <summary>
    /// Cap on <see cref="AgentGrainState.FetchableUrls"/> — the URLs this conversation may fetch.
    /// Bounded because the list lives in the session's Cosmos item, which is capped at 2 MB (ADR 0004);
    /// the oldest URL is dropped when the cap is reached.
    /// </summary>
    public const int MaxFetchableUrls = 50;

    /// <summary>
    /// Cap on how many URLs ONE seeding MCP tool result may contribute to
    /// <see cref="AgentGrainState.SeededFetchableUrls"/> (ADR 0067), in document order.
    ///
    /// <para><b>This is not belt-and-braces.</b> An MCP result is capped at
    /// <see cref="McpToolLimits.MaxToolResultLength"/> (8 KB), which at ~40 characters a URL is 150–250 of
    /// them. Without a per-result cap, one scrape of a link-heavy page fills the seeded list on its own and
    /// evicts the very link the feature exists to deliver — the same defect the separate list was introduced
    /// to prevent, reproduced one level down. 20 comfortably covers a filtered <c>find_links</c>.</para>
    /// </summary>
    public const int MaxSeededUrlsPerResult = 20;

    /// <summary>
    /// Cap on <see cref="AgentGrainState.SeededFetchableUrls"/> — the URLs seeded from MCP tool results
    /// (ADR 0067). Bounded for the same reason as <see cref="MaxFetchableUrls"/> (the 2 MB session item,
    /// ADR 0004); oldest dropped first. 100 URLs is roughly 8 KB at ~80 characters each — the same order as
    /// one MCP result's own budget.
    ///
    /// <para>It is a SEPARATE list, and separately bounded, so seeded URLs can only ever evict each other:
    /// harvesting into <see cref="MaxFetchableUrls"/> would let one link-heavy result silently evict every
    /// URL the user typed. The cost of the split — a poisoned seed is harder to flush, because only other
    /// seeds evict it — is recorded in ADR 0067 rather than left implicit.</para>
    /// </summary>
    public const int MaxSeededFetchableUrls = 100;

    private const string Instructions = """
        You are Agent 42, a friendly and helpful assistant.
        Answer the user's questions concisely and accurately.
        When the user has uploaded documents to this conversation, relevant excerpts are retrieved
        and added to your context automatically before you answer — ground your answer in those
        excerpts and cite the source file and page (or chunk index) shown with each.
        """;

    private readonly IPersistentState<AgentGrainState> _state;

    private readonly IUserChatClientFactoryProvider _chatClientFactoryProvider;

    // The Auto router port (ADR 0137), null when the host registers none (a test silo): then there is no Auto. Resolved per turn,
    // never held as per-run state (ADR 0106).
    private readonly IUserModelRouterProvider? _modelRouterProvider;

    private readonly IUserMcpToolProvider _mcpToolProvider;

    private readonly IUserSkillProvider _userSkillProvider;

    private readonly IAttachmentStore _attachments;

    private readonly IDocumentIndex _documentIndex;

    private readonly IChatSearchIndex _chatSearchIndex;

    private readonly IWebSearch _webSearch;

    // read_web_document (ADR 0066). It dials the model-chosen URL from OUR container (so it carries the
    // SSRF guard), and it returns bytes rather than scraped text.
    private readonly IWebDocumentFetcher _webDocumentFetcher;

    private readonly IDocumentIngestor _documentIngestor;

    private readonly IGeocoder _geocoder;

    private readonly HindsightIntegration _memory;

    private readonly IPushNotificationSender _pushSender;

    private readonly IChatDeliverySender _chatDelivery;
    private readonly IFleetLauncher _fleet;

    private readonly ILogger<AgentGrain> _logger;

    /// <summary>
    /// The clock every retention decision reads (ADR 0016's window). Injected rather than
    /// <c>DateTimeOffset.UtcNow</c> for one blunt reason: without a seam no test can produce a turn
    /// older than the window, so the whole prune would ship unverified.
    ///
    /// <para>The parameter is OPTIONAL and falls back to <see cref="TimeProvider.System"/>, so —
    /// unlike <c>IPushNotificationSender</c> or <c>IUserMcpToolProvider</c> — a silo host that does
    /// not register one still activates this grain. That is deliberate: this dependency has a
    /// correct default, and the "every grain host must register X" hazard is worth avoiding when it
    /// buys nothing. Tests register a controllable one and get it injected as normal.</para>
    ///
    /// <para><c>GetDate()</c> — the model-facing <c>get_date</c> tool — deliberately still reads
    /// <c>DateTime.Now</c>. It answers a question about the user's wall clock, not about retention.</para>
    /// </summary>
    private readonly TimeProvider _timeProvider;

    // How long a client tool waits for the browser (ADR 0089). Optional with a production default, exactly like
    // _timeProvider, so no silo host has to register it; the test silo shortens it.
    private readonly ClientToolTimeouts _clientToolTimeouts;

    private AIAgent _agent = null!;

    private AgentSession _session = null!;

    private string _modelId = null!;

    // The user's effective chat-client factory, resolved once in OnActivateAsync from this grain's
    // userId (the overlay of ADR 0018). Not readonly and not ctor-injected: a user's configured
    // providers are per-user, so this cannot be a process singleton — it is resolved per activation.
    private IChatClientFactory _chatClientFactory = null!;

    // This grain's userId, retained from OnActivateAsync (ADR 0019). The factory is resolved once per
    // activation, so a user who added their FIRST provider after this grain activated would still hold the
    // empty factory — the no-model path in ChatCoreAsync/ReceiveReminder re-resolves GetForUserAsync(_userId)
    // to pick it up without waiting for a reactivation.
    private string _userId = null!;

    // The user's DEFAULT selectable-tools set (ADR 0021), read ONCE on activation from IUserProfileGrain —
    // null means "all selectable tools" (the null==all sentinel). It is the middle layer of the resolution
    // override ?? default ?? all (see EffectiveEnabledTools): a per-conversation override or an agent snapshot
    // in _state.State.EnabledTools beats it. Read on activation (before the first BuildAgent), mirroring how
    // the default model resolves on activation — so a Preferences change reflects in a warm regular chat only
    // on its next activation (accepted staleness, ADR 0021).
    private IReadOnlyList<string>? _userDefaultEnabledTools;

    // Whether follow-up suggestion prompts are enabled for this user, read ONCE on activation from the same
    // IUserProfileGrain fetch as _userDefaultEnabledTools (null-sentinel: null/true = ON, false = OFF). A
    // Preferences change reflects in a warm chat only on its next activation — accepted staleness, matching
    // _userDefaultEnabledTools and the default-model resolution.
    private bool _suggestFollowUpsEnabled;

    // This user's remote-MCP-server tools grouped per server (config-driven remote MCP servers; per-server
    // grouping added by ADR 0026), resolved ONCE on activation from this grain's userId — BEFORE the first
    // BuildAgent, the same ordering hazard as _chatClientFactory and _userDefaultEnabledTools. Each group's
    // id also feeds the `mcp` wildcard expansion in GetEnabledToolsAsync. The provider is a singleton holding
    // the live MCP connections these tools reference; it is version-cached and best-effort, so a dead server
    // never breaks activation. A user's MCP config change is picked up on the next activation of each of
    // their conversation grains.
    private IReadOnlyList<McpServerTools> _mcpServers = [];

    // The overlay version _mcpServers was resolved from (ADR 0081). The mid-turn re-resolve compares against it,
    // so a reconnect elsewhere (the OAuth callback's save bumps the version) brings a dead server's tools back on
    // the user's next message without a reactivation.
    private int _mcpVersion;

    // The servers already shown a Reconnect notice on this ACTIVATION (ADR 0081) — at most once per server, so a
    // user who ignores the button is not nagged on every turn. Deliberately activation-scoped and NOT a member of
    // the turn-scoped clear ritual; cleared only when the MCP tool set is re-resolved. Only an INTERACTIVE run adds
    // to it: a background drain that consumed it would hide the notice from the user's next real turn. Which
    // servers 401'd during a run is a LOCAL (McpReauthTurnObserver), never a field — two runs can be in flight.
    private readonly HashSet<string> _mcpReauthNotified = new(StringComparer.Ordinal);

    // This user's UPLOADED skills (ADR 0049), resolved ONCE on activation from this grain's userId — BEFORE
    // the first BuildAgent, the same ordering hazard as _mcpServers, _chatClientFactory and
    // _userDefaultEnabledTools. BuildAgent merges these with SkillCatalog into one catalog (upload wins on a
    // name clash) for BOTH the provider gate and the `skills` wildcard expansion in GetEnabledToolsAsync, and
    // ReceiveReminder reuses that same _agent — so resolving later would have the first chat turn AND every
    // reminder turn on this activation silently ignore the user's uploads.
    //
    // NOTE this is precisely why the skills expansion (ToolSelection.ExpandedEnabledTools) reads the user's uploads
    // from its context, built from this field. ADR 0048 could say the skills
    // expansion "needs no grain field ... there is no OnActivateAsync ordering hazard here" because
    // SkillCatalog is static and process-wide; making skills per-user re-opens the hazard the MCP expansion
    // already had.
    //
    // Best-effort: a missing or corrupt package costs that one skill, never the activation.
    private IReadOnlyList<SkillInfo> _userSkills = [];

    /// <summary>
    /// The stop handle for the turn a browser is currently streaming (ADR 0078): the
    /// <see cref="CancellationTokenSource"/> whose token that turn's model run is bound to.
    /// <see cref="StopAsync"/> cancels it; everything else leaves it alone.
    ///
    /// <para><b>It names a running turn; it does not carry a value.</b> A value of one run belongs in its
    /// <see cref="TurnContext"/> (ADR 0106), never in a field. This handle is a field because
    /// <see cref="StopAsync"/>, a separate grain call, must find the turn. Two turns can be in flight on one
    /// activation: <see cref="ChatAsync"/> suspends between <c>yield return</c>s, so a fired reminder or an
    /// <see cref="EnqueuePromptAsync"/> run executes in the gap. Assign it blindly and an
    /// <see cref="EnqueuePromptAsync"/> run — which drains the same core, so it reaches the publish —
    /// steals the handle, and the user's stop cancels <i>that</i> turn instead; clear it blindly and one
    /// turn's disposal faults another turn's live run. (A fired reminder cannot steal it: it never comes
    /// through that core. It can still interfere, which is what the next paragraph is about.)</para>
    ///
    /// <para>So it is published <b>by identity</b> and only by an interactive turn — <c>_turnCts ??=
    /// mine</c> on the way in, and <c>if (ReferenceEquals(_turnCts, mine))</c> on the way out — and
    /// <see cref="ReceiveReminder"/> deliberately has <b>no</b> reset line for it. Adding one would let
    /// a reminder firing mid-stream silently disarm the user's stop button. The publish reads the turn's own
    /// <see cref="TurnContext.Interactive"/>, which a background turn starting in one of those gaps cannot
    /// change.</para>
    /// </summary>
    private CancellationTokenSource? _turnCts;

    public AgentGrain(
        [PersistentState("agent", StorageProviderName)] IPersistentState<AgentGrainState> state,
        IUserChatClientFactoryProvider chatClientFactoryProvider,
        IUserMcpToolProvider mcpToolProvider,
        IUserSkillProvider userSkillProvider,
        IAttachmentStore attachments,
        IDocumentIndex documentIndex,
        IChatSearchIndex chatSearchIndex,
        IWebSearch webSearch,
        IWebDocumentFetcher webDocumentFetcher,
        IDocumentIngestor documentIngestor,
        IGeocoder geocoder,
        HindsightIntegration memory,
        IPushNotificationSender pushSender,
        IChatDeliverySender chatDelivery,
        IFleetLauncher fleet,
        ILogger<AgentGrain> logger,
        TimeProvider? timeProvider = null,
        ClientToolTimeouts? clientToolTimeouts = null,
        IUserModelRouterProvider? modelRouterProvider = null)
    {
        _modelRouterProvider = modelRouterProvider;
        _state = state;
        _chatClientFactoryProvider = chatClientFactoryProvider;
        _mcpToolProvider = mcpToolProvider;
        _userSkillProvider = userSkillProvider;
        _attachments = attachments;
        _documentIndex = documentIndex;
        _chatSearchIndex = chatSearchIndex;
        _webSearch = webSearch;
        _webDocumentFetcher = webDocumentFetcher;
        _documentIngestor = documentIngestor;
        _geocoder = geocoder;
        _memory = memory;
        _pushSender = pushSender;
        _chatDelivery = chatDelivery;
        _fleet = fleet;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientToolTimeouts = clientToolTimeouts ?? ClientToolTimeouts.Default;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Resolve THIS user's effective provider set (baseline ⊕ overlay, ADR 0018) FIRST: every use of
        // _chatClientFactory below — the DefaultModelId read on the next line, BuildAgent, and the ~8
        // sync call sites — dereferences it, so resolving it later would NRE (m4). The userId is
        // everything before the first '/' of the grain key ({userId}/{conversationId}, see AgentApi.AgentKey).
        _userId = UserIdFromGrainKey(this.GetPrimaryKeyString());
        _search = CreateSearchProjection();
        _retention = new HistoryRetention(_timeProvider, _logger, this.GetPrimaryKeyString());
        _forking = new ConversationForking(_attachments, _logger);
        _chatClientFactory = await _chatClientFactoryProvider.GetForUserAsync(_userId, cancellationToken);

        // An empty factory (the user has configured no chat provider — ADR 0019) has DefaultModelId "".
        // _modelId then stays "" REGARDLESS of a persisted DeploymentName: with no providers there is no
        // client to run, so the no-model guards in ChatCoreAsync/ReceiveReminder must fire. BuildAgent still
        // runs — against the no-op NoModelChatClient — so _agent/_session are always valid and every
        // non-inference path (history, bind, register, reminder, rehydrate) keeps working unguarded.
        _modelId = string.IsNullOrEmpty(_chatClientFactory.DefaultModelId)
            ? ""
            : (_state.State.DeploymentName ?? _chatClientFactory.DefaultModelId);

        // Resolve the user's DEFAULT selectable-tools set (ADR 0021) BEFORE the first BuildAgent below —
        // BuildAgent reads it (via EffectiveEnabledTools) and ReceiveReminder reuses that same _agent, so
        // fetching it later would have the first chat turn AND every reminder turn on this activation
        // silently register all tools and ignore the user's default. Same ordering hazard the
        // _chatClientFactory resolution above guards against for a different field.
        var profile = await GrainFactory.GetGrain<IUserProfileGrain>(_userId).GetAsync();
        _userDefaultEnabledTools = profile.EnabledTools;
        // Null-sentinel: null (never saved / legacy) and true both mean ON; only an explicit false disables.
        _suggestFollowUpsEnabled = profile.SuggestFollowUps != false;

        // Resolve this user's MCP-server tools (config-driven remote MCP servers) BEFORE the first BuildAgent
        // below — BuildAgent merges them into its tool list, and ReceiveReminder reuses that same _agent, so
        // connecting later would have the first chat turn AND every reminder turn on this activation silently
        // omit the user's MCP tools. Same ordering hazard the two resolutions above guard against. Best-effort:
        // the provider connects with a timeout and skips dead servers, so this never fails activation.
        var mcp = await _mcpToolProvider.GetForUserAsync(_userId, cancellationToken);
        _mcpServers = mcp.Servers;
        _mcpVersion = mcp.Version;

        // Resolve this user's UPLOADED skills (ADR 0049) BEFORE the first BuildAgent below, for exactly the
        // reason above — BuildAgent merges them with the shipped catalog and ReceiveReminder reuses the same
        // _agent, so materializing later would have the first chat turn AND every reminder turn on this
        // activation silently ignore them. Best-effort: a missing or corrupt package costs that one skill.
        _userSkills = (await _userSkillProvider.GetForUserAsync(_userId, cancellationToken)).Skills;

        _agent = BuildAgent(_modelId);

        _session = _state.State.SerializedSession is { Length: > 0 } serialized
            ? await _agent.DeserializeSessionAsync(
                JsonSerializer.Deserialize<JsonElement>(serialized), cancellationToken: cancellationToken)
            : await _agent.CreateSessionAsync(cancellationToken);

        _logger.LogInformation("Agent grain {GrainKey} activated on model {Model} (existing session: {HasSession})",
            this.GetPrimaryKeyString(), _modelId, _state.State.SerializedSession is not null);

        await base.OnActivateAsync(cancellationToken);
    }

    // The userId is everything before the FIRST '/' of the grain key ({userId}/{conversationId} — see
    // AgentApi.AgentKey). The userId is an opaque Easy-Auth principal id with no '/', so a first-'/'
    // split is safe even if a conversation id ever contained one. A key with no '/' (defensive) is
    // treated wholly as the userId.
    internal static string UserIdFromGrainKey(string grainKey)
    {
        var slash = grainKey.IndexOf('/');
        return slash < 0 ? grainKey : grainKey[..slash];
    }

    // The conversationId is everything after the FIRST '/' of the grain key ({userId}/{conversationId}).
    // A key with no '/' (defensive) has no conversation part, so returns the whole key — the push then
    // opens "/" rather than a specific conversation, which is a harmless degradation.
    internal static string ConversationIdFromGrainKey(string grainKey)
    {
        var slash = grainKey.IndexOf('/');
        return slash < 0 ? grainKey : grainKey[(slash + 1)..];
    }

    public async Task BindAgentAsync(AgentBinding binding, bool ephemeral = false)
    {
        // Immutable once bound: a conversation keeps the first persona it was given, so a
        // repeat bind (e.g. a retried first turn) can never flip it mid-conversation.
        if (!string.IsNullOrEmpty(_state.State.AgentId))
        {
            return;
        }

        // `auto` exists only on the chat wire (ADR 0137): an agent is pinned to a real model, and this persists binding.Model.
        ThrowIfAuto(binding.Model);

        _state.State.AgentId = binding.AgentId;
        _state.State.AgentName = binding.Name;
        _state.State.SystemPrompt = binding.SystemPrompt;
        _state.State.DeploymentName = binding.Model;
        // Persona, so snapshotted with the rest (ADR 0082 amends ADR 0003): BuildAgent below composes the research
        // loop from it, and editing the source agent later never changes this chat.
        _state.State.AgentKind = binding.Kind;
        // Snapshot like the rest of the persona (ADR 0140 amends 0068/0003): editing the agent never changes this chat.
        _state.State.RetentionMinutes = binding.RetentionMinutes;

        // Snapshot the agent's tools as a CONCRETE set (ADR 0021/0003): a null binding materializes to
        // all-selectable, so the persona is self-contained and never inherits the USER's per-chat default.
        // Sanitized so a stale/garbage key stored on an old agent can't reach state.
        _state.State.EnabledTools =
            [.. ToolCatalog.Sanitize(binding.EnabledTools ?? ToolCatalog.AllSelectableToolKeys)!];

        // Rebuild the agent so the pinned persona + model take effect, mirroring the
        // model-switch path in ChatAsync: snapshot the session, rebuild, restore. Binding
        // runs on a brand-new empty session, but copying that shape keeps the rebuild path
        // uniform and avoids relying on an empty session being portable across instances.
        var snapshot = await _agent.SerializeSessionAsync(_session);
        _modelId = binding.Model;
        _agent = BuildAgent(binding.Model);
        _session = await _agent.DeserializeSessionAsync(snapshot);

        // This is the SECOND path that changes the conversation's model, so it needs the same downgrade
        // strip as the picker path in ChatCoreAsync — an agent's Model is a qualified id since ADR 0013
        // and may name a no-tools model. The comment above says binding runs on an empty session, and
        // today it does (the client binds on a new conversation's first send), but that is an assumption
        // about a caller, not an invariant of this grain — and if it ever stops holding, the failure is a
        // conversation that cannot be reopened. The strip is a no-op on an empty session.
        if (!_chatClientFactory.GetCapabilities(binding.Model).SupportsTools)
        {
            StripToolHistory();
        }

        // An incognito chat (ADR 0034/0134) binds in memory only: the rebuild above applies the persona for this
        // activation, but a stored record would break "not persisted" and flip RecordExists. The client resends
        // agentId every incognito turn, so a deactivated grain rebinds instead of running as a plain chat.
        if (!ephemeral)
        {
            await _state.WriteStateAsync();
            await RegisterRetentionSweepAsync();
        }

        _logger.LogInformation("Agent grain {GrainKey} bound to agent {AgentId} ({AgentName}) on model {Model}",
            this.GetPrimaryKeyString(), binding.AgentId, binding.Name, binding.Model);
    }

    public Task<AgentBinding?> GetAgentBindingAsync()
        => Task.FromResult(_state.State.AgentId is { Length: > 0 } id
            ? new AgentBinding(
                id, _state.State.AgentName ?? "", _state.State.SystemPrompt ?? "", _modelId,
                _state.State.EnabledTools, _state.State.AgentKind, _state.State.RetentionMinutes)
            : null);

    public async IAsyncEnumerable<AgentStreamEvent> ChatAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments = null,
        bool ephemeral = false, bool interactive = true,
        IReadOnlyList<string>? clientTools = null, bool replacesLastTurn = false, string? appContext = null)
    {
        // This turn's client-tool broker (ADR 0089), or null when no browser of this turn runs client tools. Owned
        // by THIS call and registered by identity, never a turn-scoped field: see AgentGrain.ClientTools.cs.
        var broker = OpenClientToolBroker(interactive, clientTools);

        // This run's context (ADR 0106). A caller that DRAINS this stream rather than streaming it to a browser
        // passes interactive false: the MCP surface (ADR 0070) takes the reply text and discards every other
        // event, so a follow-up call would be a second model call whose result nothing can ever read.
        var turn = TurnContext.ForChat(ephemeral, interactive, broker);
        // A browser that advertised the mcp_app capability (ADR 0133) is sent each app tool's raw result beside the
        // text; every other run (and every browser without it) stays text-only.
        turn.SetAppsSupported(broker?.Supports(ClientToolLimits.McpApp) == true);
        turn.SetAppContext(appContext);

        try
        {
            await foreach (var evt in ChatCoreAsync(message, model, attachments, turn, replacesLastTurn))
            {
                yield return evt;
            }
        }
        finally
        {
            CloseClientToolBroker(broker);
        }
    }

    public async Task StopAsync()
    {
        // Read the handle once: [AlwaysInterleave] means this call can land at any await point of the
        // turn, including the moment ChatCoreAsync's finally is unpublishing it.
        var cts = _turnCts;
        if (cts is null)
        {
            // No browser turn in flight on this activation (or the only turn running is a background
            // one, which never publishes). Silently nothing — the client cannot know, and a 404/409
            // would only tempt it into retrying a race it cannot win.
            return;
        }

        try
        {
            // CancelAsync, and guarded, because Cancel() is NOT the inert call it reads as: it throws
            // ObjectDisposedException if the owning turn has since disposed the source, and it runs
            // registered callbacks INLINE on this grain's thread — an AggregateException from any of
            // them (HttpClient and the framework both register on the run token) would otherwise
            // surface as a failed grain call for a button press that did its job.
            await cts.CancelAsync();
            _logger.LogInformation(
                "Agent grain {GrainKey} stopped the turn in flight at the user's request",
                this.GetPrimaryKeyString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Agent grain {GrainKey} could not cancel the turn in flight", this.GetPrimaryKeyString());
        }
    }

    // An agent-run conversation is always listed and persisted (it is opened later by its owner), and no live
    // client watches the stream, so its run is never ephemeral and never interactive (ADR 0106).
    public Task EnqueuePromptAsync(string message, IReadOnlyList<AttachmentRef>? attachments)
        => RunBackgroundTurnAsync(message, attachments, TurnContext.ForBackground());

    public async Task ReceiveFleetResultAsync(string taskId, string message)
    {
        // The conversation was deleted while its fleet task ran (PurgeAsync clears the record): drop the result.
        // Running the turn would write a new record and bring the conversation back.
        if (!_state.RecordExists)
        {
            _logger.LogInformation(
                "Agent grain {GrainKey} dropped the result of fleet task {TaskId}: the conversation no longer exists",
                this.GetPrimaryKeyString(), taskId);
            return;
        }

        // The loop guard (ADR 0115): this turn is marked, so start_fleet_task refuses in it.
        await RunBackgroundTurnAsync(message, attachments: null, TurnContext.ForFleetResult());
    }

    // The background turn shared by agent-run (EnqueuePromptAsync) and a fleet result (ReceiveFleetResultAsync): the
    // drained chat core, then the away push, then the chat-app delivery. One body, so the two can't drift apart.
    private async Task RunBackgroundTurnAsync(string message, IReadOnlyList<AttachmentRef>? attachments, TurnContext turn)
    {
        try
        {
            // Reuse the full chat turn core by DRAINING it. ChatCoreAsync already does the no-model guard,
            // the image-generation branch (ADR 0020 — so an image model edits the attachment), the vision
            // gate + attachment→UriContent materialization (ADR 0013/0004), document retrieval, the model
            // run and the session persist — everything a browser turn does. The streamed events have no
            // listener here (this is OneWay/202), so they are simply discarded; the side effects — the
            // persisted turn — are the point. model: null keeps the bound/current model.
            var reply = new AwayReplyText();
            await foreach (var evt in ChatCoreAsync(message, model: null, attachments, turn))
            {
                reply.Observe(evt);
            }

            // Away-reply push (ADR 0028): an agent-run turn completed with a real reply and no live client
            // was watching. INSIDE the try, after the drain — so a faulted turn (caught below) never
            // notifies. Gated on _modelId so the no-model path (which drains normally after emitting the
            // "configure a provider" notice, ChatCoreAsync) does NOT fire a false "your agent replied";
            // _modelId is set by ChatCoreAsync's re-resolve/rebind if a provider appeared mid-flight.
            //
            // Also suppressed when the model already pushed this turn (ADR 0075): the service worker sets
            // no `tag`, so a model-authored "Build finished" followed by the generic "Replied while you
            // were away." would stack as two entries in the shade for one reply. The model's own text is
            // the better of the two, so this generic one yields. The _modelId gate above is untouched.
            if (!string.IsNullOrEmpty(_modelId) && turn.PushesSent == 0)
            {
                await SendAwayReplyPushAsync();
            }

            // Chat delivery (ADR 0111), beside the push and at this site and the reminder's only: the reply text
            // goes to the origin chat (or the home chat). Same _modelId gate, so the "configure a provider" notice
            // is never delivered as a reply.
            if (!string.IsNullOrEmpty(_modelId))
            {
                await DeliverAwayReplyToChatAsync(turn, reply.Text);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: no caller to surface this to (the 202 was already returned). Log and move on,
            // rather than faulting a OneWay grain call into the void.
            _logger.LogError(
                ex, "Agent grain {GrainKey} failed a background (agent-run or fleet result) turn", this.GetPrimaryKeyString());
        }
    }

    // Publish this turn's usage-dashboard "application" dimension (ADR 0031). An agent chat groups under
    // the agent's name; a regular chat leaves it unset so the ingestion span processor falls back to the
    // service constant. Rides Orleans RequestContext like the user.* identity (ADR 0010) — the processor
    // stamps it onto the in-grain chat/tool spans at OnStart. RequestContext is per-call, so this is
    // naturally turn-scoped (no clear-on-exit hazard).
    private void PublishUsageApplication()
    {
        if (_state.State.AgentName is { Length: > 0 } name)
        {
            RequestContext.Set(UsageSpanProjector.ApplicationContextKey, name);
        }
    }

    // The shared core of a chat run, drained by ChatAsync and EnqueuePromptAsync. This wrapper only counts the run
    // (ADR 0089): a user edit of the shared state may write at once only while no run is in flight, because a write
    // serialises the whole grain state (see AgentGrain.SharedState.cs). ReceiveReminder, the other run site, counts
    // itself.
    private async IAsyncEnumerable<AgentStreamEvent> ChatCoreAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments, TurnContext turn,
        bool replacesLastTurn = false)
    {
        _runsInFlight++;
        try
        {
            await foreach (var evt in ChatTurnAsync(message, model, attachments, turn, replacesLastTurn))
            {
                yield return evt;
            }
        }
        finally
        {
            await EndRunAsync();
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> ChatTurnAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments, TurnContext turn,
        bool replacesLastTurn)
    {
        PublishUsageApplication();

        var interactive = turn.Interactive;

        _logger.LogInformation(
            "Agent grain {GrainKey} received a message ({Length} chars, {Attachments} attachment(s))",
            this.GetPrimaryKeyString(), message.Length, attachments?.Count ?? 0);

        // No model configured (ADR 0019): see EnsureModelBoundAsync. A false result means still none, so tell the user and stop.
        var (bound, requestedModel) = await EnsureModelBoundAsync(model);
        if (!bound)
        {
            yield return new AgentStreamEvent(
                AgentStreamEventType.TextDelta,
                // GitHub Copilot alone is not enough (ADR 0108): its models run only in Copilot agents.
                Text: "_No model provider is configured. Add one in Settings → model providers to "
                    + "start chatting. GitHub Copilot needs an ordinary provider alongside it._\n\n");
            yield return new AgentStreamEvent(AgentStreamEventType.Done);
            yield break;
        }

        model = requestedModel;

        var routing = await ResolveTurnModelAsync(model, message, attachments, turn);
        await SwitchModelAsync(routing.Model);
        if (routing.Routed is { } routed)
        {
            yield return routed;
        }

        // A copilot chat that cannot run this turn (no agent, or an incognito turn) is refused here, before anything
        // is written or sent: nothing is persisted and Copilot is never called. After the switch, so a switch onto a
        // no-tools model (a plain chat) is not refused.
        if (CopilotTurnRefusal(turn) is { } refusal)
        {
            yield return new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: refusal);
            yield return new AgentStreamEvent(AgentStreamEventType.Done);
            yield break;
        }

        // Image-generation model (ADR 0020): the turn produces an IMAGE, not a chat reply. Branch BEFORE the
        // chat vision-gate below — an image model CONSUMES images (it edits them), so the "withhold from a
        // no-vision model" gate must not fire here, and it runs a different API entirely
        // (IImageGenerationService, not the model's IChatClient). The no-op NoModelChatClient path never
        // reaches here: _modelId is non-empty by now (guarded above).
        //
        // The model is the router's one-off pick under Auto (ADR 0138), else the conversation's own image model.
        var imageModel = ImageModelForTurn(routing);
        if (imageModel is not null)
        {
            var webhookTurn = new WebhookTurn(
                ChatTrigger(interactive), message, "", ImageCount: 1, ReplacesLastTurn: replacesLastTurn);
            await foreach (var evt in GenerateImageTurnAsync(message, attachments, webhookTurn, turn, imageModel, routing.ImageModel is not null))
            {
                yield return evt;
            }

            yield break;
        }

        // A dead-grant MCP server (ADR 0081): first pick up a reconnect made elsewhere, then tell the user about any
        // server this conversation uses that still needs one — before the model runs, so the button is there even
        // if the model never tries the missing tools.
        await ReResolveMcpAfterReconnectAsync(interactive);
        foreach (var notice in TakeMcpReauthNotices(McpServersNeedingReauth(), interactive))
        {
            yield return notice;
        }

        // Images are gated on the model's VISION capability, and this gate is not merely about a turn
        // that would fail anyway (ADR 0013). An image reaches the model as a SAS URL into our PRIVATE
        // attachments container (ADR 0004), and the model's host really does fetch that URL server-side
        // (the ADR 0013 spike confirmed it) — so handing it to a third-party endpoint that cannot even
        // read images leaks a live credential-bearing URL for nothing. Gate it HERE, in the grain, not
        // only in the UI: the UI is not a security boundary. A copilot chat never sees images (CanSeeImages).
        var canSeeImages = CanSeeImages();

        // The same rule decides whether an MCP tool's images reach the model this run (the Chrome-extension ADR).
        turn.SetToolImagesVisible(canSeeImages);

        // Refresh the image URLs on PRIOR turns before this model call. Azure OpenAI re-fetches
        // a prior turn's image URL on every subsequent turn (Step 1 spike), and a blob SAS is
        // short-lived — but a warm grain serves many turns without reactivating, so this must run
        // per turn, not only on activation, or a long-lived conversation 400s once a SAS expires.
        // It also applies the vision gate to history — see the method.
        await RehydrateAttachmentUrlsAsync(canSeeImages);

        // Build the user turn (images, withholding, the TurnAttachments 1:1 join — see the method).
        var (userMessage, skippedImages) = await BuildUserTurnAsync(message, attachments, canSeeImages);

        // Tell the user their image was withheld, rather than letting the model silently ignore it.
        // This is streamed to the CLIENT and never added to the user's message: a note pushed into
        // `contents` would be concatenated into that turn's text by GetHistoryAsync and render inside
        // the user's own chat bubble on every later reload.
        if (skippedImages > 0)
        {
            yield return new AgentStreamEvent(
                AgentStreamEventType.TextDelta,
                Text: $"_The selected model cannot see images, so {skippedImages} attachment(s) were "
                    + "not sent to it. Switch to a vision-capable model to ask about them._\n\n");
        }

        // Token usage arrives as UsageContent updates, one per LLM turn (a tool-calling
        // run has several); accumulate them and report the totals on the Done event. The pricing identity is resolved
        // once, here, so the Done event prices the model this turn ran on (ADR 0110).
        var tokens = new TurnTokenTotals { Pricing = _chatClientFactory.GetPricingIdentity(_modelId) };

        // Accumulate the reply text (only) so we can feed the last exchange to the follow-up
        // suggestion call after the turn — cheaper than re-reading the serialized session.
        //
        // NOT the persistence path on a normal turn: the framework fills the session from its own response
        // message. The one exception is a STOPPED turn (ADR 0078), where the framework persists nothing and
        // this accumulator is what AppendStoppedTurnToSession writes.
        var reply = new StringBuilder();

        // This turn's stop handle (ADR 0078). EVERY turn gets a token — that costs nothing — but only an
        // interactive one publishes the handle (from the flag captured at the top, never re-read), and
        // publishing never steals one that is already there — see the _turnCts remarks for why identity
        // rather than assignment is the whole safety argument.
        using var turnCts = new CancellationTokenSource();
        PublishStopHandle(turnCts, interactive);

        // A research-kind chat runs the loop (ADR 0082): this resets its todos and hides the turn from history until
        // it ends. Null for every other chat, and every research call below is then a no-op.
        var research = BeginResearchRun(userMessage);

        var outcome = new TurnOutcome();
        var reauth = new McpReauthTurnObserver(_mcpServers);
        var streamEnded = false;
        try
        {
            await foreach (var evt in StreamModelReplyAsync(
                userMessage, reply, tokens, outcome, research, turn, turnCts.Token))
            {
                reauth.Observe(evt);
                yield return evt;
            }

            streamEnded = true;
        }
        finally
        {
            // Before the persist, not after: once the last event is out there is nothing left to stop,
            // and leaving the handle up would let a late press cancel nothing while logging that it did.
            UnpublishStopHandle(turnCts);
            // Unhidden here so an abandoned stream cannot leave history hidden for the rest of the activation. On
            // the normal path the fold below runs before the next await, so no reader sees the unfolded turn. A
            // research turn that FAILED or was ABANDONED never reaches that fold, yet LoopAgent has already stored
            // every iteration that finished (ADR 0082), so it is folded here instead.
            ReleaseResearchRun(research, foldFirst: !streamEnded);
        }

        // A stopped turn is persisted like any other — but the framework hands us NOTHING to persist.
        // Measured on Agent Framework 1.19: a cancelled RunStreamingAsync notifies the history provider
        // of a failure and rethrows, so the session is left EMPTY — not partial, empty: no user message
        // and no reply. Persisting at this point without appending would therefore write away the whole
        // turn, and since the title is stamped by the endpoint BEFORE the turn runs, the result is a
        // conversation listed with a title and no messages (the ADR 0070 failure shape). So the turn is
        // appended by hand, and this is the ONE branch on which `reply` is the persistence path.
        var prepared = TryPrepareStoppedTurn(outcome, userMessage, reply, research);

        // After the stopped-turn append, so a stopped research turn folds its tail in with the rest.
        FoldResearchRun(research);

        if (!prepared)
        {
            // The history was unreachable, so there is nothing coherent to write. Skip the persist rather
            // than commit an empty session over a conversation that had one — and note this also skips the
            // ADR 0066 rebuild below, so a web document ingested during this turn is dropped. Both are
            // acceptable: every session here has in-memory history from the start (an InMemoryChatHistoryProvider, or
            // the copilot mirror, which CopilotChatAgent seeds on create), so reaching this branch at all means
            // something about the session is already broken.
            yield return tokens.ToDoneEvent(stopped: true);
            yield break;
        }

        var persisted = await PersistTurnUnlessEphemeralAsync(turn);

        // Outbound webhook (ADR 0094), fire site 1 of 3: gated on what the persist reported.
        await NotifyAgentWebhookAsync(persisted, new WebhookTurn(
            ChatTrigger(interactive), message, reply.ToString(), outcome.Stopped, ReplacesLastTurn: replacesLastTurn));

        // Consume site 1 of 2 (ADR 0066): if read_web_document ingested during this run, rebuild now that
        // the run is over — the tool could not, because a rebuild swaps _session out from under the
        // framework run iterating it. Covers the live chat path AND /api/agent-run, which drains this
        // method. Site 2 is in ReceiveReminder, which does NOT drain this method.
        await RebuildForIngestedWebDocumentAsync(turn);

        foreach (var evt in await TrailingEventsAsync(message, reply, outcome, reauth, interactive))
        {
            yield return evt;
        }

        yield return tokens.ToDoneEvent(outcome.Stopped);
    }

    // The live-only events between the fully streamed reply and Done. Extracted as one seam so ChatCoreAsync stays
    // under the S3776 gate (ADR 0050), and in this order:
    //
    // 1. Reconnect notices for the servers whose tool call was refused with 401 during this run (ADR 0081) — under
    //    the answer that explains why the tool did not run.
    // 2. Follow-up suggestions (interactive path only, opt-out via Preferences). Best-effort: the helper owns its
    //    try/catch and returns null on any failure, so a slow/failing suggestion call never breaks the turn.
    //    Skipped on the agent-run/reminder drain (not interactive) and on an empty reply. The
    //    generator uses a throwaway message list and never touches _session, so nothing is persisted. A stopped
    //    turn is gated inside the helper too, beside the other three conditions.
    private async Task<List<AgentStreamEvent>> TrailingEventsAsync(
        string message, StringBuilder reply, TurnOutcome outcome, McpReauthTurnObserver reauth, bool interactive)
    {
        var events = TakeMcpReauthNotices(reauth.ServerIds, interactive);
        if (EmptyReplyNotice(outcome) is { } notice)
        {
            events.Add(notice);
        }

        if (await SuggestFollowUpsAsync(message, reply, outcome.Stopped, interactive) is { Count: > 0 } suggestions)
        {
            events.Add(new AgentStreamEvent(AgentStreamEventType.Suggestions, Suggestions: suggestions));
        }

        return events;
    }

    // A turn that ended with nothing to show, and no error either: a model that answered 200 with no output item we map.
    // Said out loud rather than left as an empty bubble the user cannot tell from a hang. Not for a stopped turn (the user
    // cut it short) and not when the provider already reported an error (that event is the explanation).
    private const int ErrorTextLimit = 500;

    private static AgentStreamEvent? EmptyReplyNotice(TurnOutcome outcome)
        => outcome is { Stopped: false, ProducedOutput: false, ReportedError: false }
            ? new AgentStreamEvent(AgentStreamEventType.Error, Text: "The model returned an empty reply.")
            : null;

    // Makes this turn stoppable from outside, for a browser turn only. `??=` rather than `=`: if a turn is
    // already published, THIS turn is the one that started while that one was suspended at a yield, and
    // overwriting would hand the user's stop button to the wrong turn.
    private void PublishStopHandle(CancellationTokenSource turnCts, bool interactive)
    {
        if (interactive)
        {
            _turnCts ??= turnCts;
        }
    }

    // Clears the handle only if it is still OURS. A turn that never published (background) or whose handle
    // was never taken up leaves the field exactly as it found it — so one turn ending can never disarm
    // another turn's stop button. Runs before the `using` disposes the source, which is the order that
    // keeps StopAsync from ever seeing a disposed one through the field.
    private void UnpublishStopHandle(CancellationTokenSource turnCts)
    {
        if (ReferenceEquals(_turnCts, turnCts))
        {
            _turnCts = null;
        }
    }

    // The model run, as a separate iterator so the cancellation can be CAUGHT: C# forbids a `yield return`
    // inside a `try` that has a `catch` (CS1626), so the catch has to wrap MoveNextAsync alone with every
    // yield outside it. That is also why this cannot stay an `await foreach` in ChatCoreAsync, and it is the
    // same shape Agent Framework's own ChatClientAgent.RunCoreStreamingAsync uses, for the same reason.
    //
    // The outcome rides a class rather than an out/ref parameter for the same reason TurnTokenTotals is
    // one — an iterator method cannot hand out a `ref`.
    private async IAsyncEnumerable<AgentStreamEvent> StreamModelReplyAsync(
        ChatMessage userMessage, StringBuilder reply, TurnTokenTotals tokens, TurnOutcome outcome,
        ResearchRun? research, TurnContext turn, [EnumeratorCancellation] CancellationToken turnToken)
    {
        var broker = turn.Broker;
        var updates = _agent
            .RunStreamingAsync([userMessage], _session, null, turnToken)
            .GetAsyncEnumerator(turnToken);

        // The shared-state version this run last showed the browser (ADR 0089); a change is streamed after the tool
        // result that carried it.
        var stateCursor = new SharedStateCursor(_state.State.SharedStateVersion);
        Task<bool>? pull = null;
        var copilotRun = OpenCopilotRun(turn);

        try
        {
            while (true)
            {
                // Through PullAsync so this run's context (ADR 0106) is visible to the tools and providers this
                // pull invokes, and to nothing else. Started once and awaited as a task: an MCP elicitation (ADR
                // 0095) arrives while the pull is pending, and is streamed before the pull is awaited again.
                pull = PullAsync(updates, turn).AsTask();
                while (await ElicitationArrivedFirstAsync(pull, broker))
                {
                    foreach (var elicitation in broker!.Elicitations.TakeNew())
                    {
                        yield return ElicitationEvent(elicitation);
                    }
                }

                AgentResponseUpdate update;
                try
                {
                    if (!await pull)
                    {
                        break;
                    }

                    update = updates.Current;
                }
                catch (OperationCanceledException)
                {
                    // The user pressed stop (ADR 0078) — surfaced as a TaskCanceledException, hence the
                    // base type. This is NOT a failure and NOT the end of the turn: the caller still
                    // appends and persists what streamed, so the partial reply outlives the cancellation.
                    outcome.Stopped = true;
                    break;
                }

                // The projection is a plain iterator because it must not be an ASYNC one; usage updates
                // produce no event at all, they only accumulate into `tokens`. A research turn adds its
                // TextReset and checklist events around it (ADR 0082), which is why this call is async.
                foreach (var evt in await ProjectUpdateAsync(update, reply, tokens, research))
                {
                    outcome.Observe(evt);
                    yield return AttachMcpApp(MarkClientToolCall(evt, broker), turn);
                }

                if (StateSnapshotAfter(update, stateCursor) is { } snapshot)
                {
                    yield return snapshot;
                }
            }
        }
        finally
        {
            await SettlePendingPullAsync(pull, broker);
            await DisposeRunAsync(updates, turn);
            CloseCopilotRun(copilotRun);
        }
    }

    // True when the turn may be persisted: it ran to completion (nothing to prepare), or it was stopped and
    // its partial turn has been appended to the session. False only in the one case where persisting would
    // destroy rather than record — see AppendStoppedTurnToSession.
    private bool TryPrepareStoppedTurn(
        TurnOutcome outcome, ChatMessage userMessage, StringBuilder reply, ResearchRun? research)
        => !outcome.Stopped || AppendStoppedTurnToSession(userMessage, reply, research);

    // Writes a stopped turn into the session by hand, because the framework wrote nothing (see the call
    // site). Returns false when the history cannot be reached, which is the one caller for which the usual
    // harmless early-return would be harmful.
    //
    // Appending here bypasses both storage filters (Tools/AgentGrain.BuildAgent.cs), and that is safe
    // rather than lucky: `userMessage` is the one BuildUserTurnAsync built, not an injected
    // AIContextProvider message, so StorageInputRequestMessageFilter would have kept it verbatim; and
    // `reply` accumulates TEXT only — a reasoning model's thinking goes to its own event and never lands
    // there — so StorageInputResponseMessageFilter would have had nothing to strip. TurnAttachments needs
    // no attention either: BuildUserTurnAsync added this turn's entry before the run, so appending the
    // user message is what RESTORES the 1:1 join the positional image lookup depends on.
    //
    // A research turn (ADR 0082) is the exception to "the framework wrote nothing": each iteration that FINISHED
    // is already in the session, including the user message. So the user message is appended only when nothing of
    // the turn is stored, and the reply only when its text came from the iteration that was cut short. Appending
    // either unconditionally would duplicate it (measured: a stop in iteration 2 left iteration 1 stored whole).
    // Both checks read the history itself, not only the observer's count, so a stop landing between an iteration
    // being stored and the observer recording it still cannot double-append.
    private bool AppendStoppedTurnToSession(ChatMessage userMessage, StringBuilder reply, ResearchRun? research)
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            _logger.LogWarning(
                "Agent grain {GrainKey} stopped a turn but could not reach the session history; "
                    + "the turn was not persisted",
                this.GetPrimaryKeyString());
            return false;
        }

        if (research?.UserMessageStored(messages.Count) != true)
        {
            messages.Add(userMessage);
        }

        // No assistant message when nothing was streamed: an empty assistant turn would be sent back to
        // the provider on every later turn, and some reject one. The prompt still survives, and the client
        // says the reply never started (ADR 0078).
        if (reply.Length > 0 && research?.ReplyIsUnpersisted(messages.Count) != false)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, reply.ToString())
            {
                // Mirrors the Name the framework stamps on a normal turn (BuildAgent's ChatClientAgent
                // options), so a stopped turn is not distinguishable by its author.
                AuthorName = _state.State.AgentName ?? "Agent42",
            });
        }

        return true;
    }

    /// <summary>
    /// How the model run ended, out of band from the events it yielded. Only <c>Stopped</c> for now; it is
    /// a class rather than a bool return because the producer is an iterator.
    /// </summary>
    private sealed class TurnOutcome
    {
        public bool Stopped { get; set; }

        /// <summary>True once the turn produced anything the user can see: reply text, a tool call or an image.</summary>
        public bool ProducedOutput { get; private set; }

        /// <summary>True once the provider reported an error for this turn (an <c>Error</c> event was projected).</summary>
        public bool ReportedError { get; private set; }

        public void Observe(AgentStreamEvent evt)
        {
            ProducedOutput |= evt.Type is AgentStreamEventType.TextDelta or AgentStreamEventType.ToolCallStart
                or AgentStreamEventType.ToolCallEnd or AgentStreamEventType.Image;
            ReportedError |= evt.Type == AgentStreamEventType.Error;
        }
    }

    /// <summary>
    /// The <c>UsageDetails.AdditionalCounts</c> key under which the Anthropic SDK's <c>IChatClient</c> (12.58.0) reports
    /// prompt-cache creation tokens. The SDK's own string, so it lives in one place (ADR 0104).
    /// </summary>
    public const string CacheCreationInputTokensKey = "CacheCreationInputTokens";

    /// <summary>
    /// One turn's running token totals. A class rather than three locals so
    /// <see cref="ProjectStreamedContent"/> can accumulate into it without <c>ref</c> parameters —
    /// which an iterator method cannot hand out across its own state machine.
    /// </summary>
    private sealed class TurnTokenTotals
    {
        public long Input { get; set; }

        public long Output { get; set; }

        public long Cached { get; set; }

        /// <summary>Anthropic prompt-cache writes (ADR 0104); no other provider reports them.</summary>
        public long CacheWrite { get; set; }

        /// <summary>The provider kind and bare model the turn is priced against (ADR 0110); <c>("", "")</c> prices nothing.</summary>
        public (string ProviderKind, string Model) Pricing { get; init; } = ("", "");

        /// <summary>
        /// The terminating <c>Done</c> event. A total of zero is reported as <c>null</c>, not 0: the
        /// browser distinguishes "this provider reported no usage" from "this turn cost nothing".
        ///
        /// <para><paramref name="stopped"/> is reported the same way — <c>true</c> or absent, never
        /// <c>false</c> — and it is the flag that settles the race where a reply finishes in the same
        /// instant the stop button is pressed: that turn completed, and must not be labelled as
        /// interrupted just because the client asked.</para>
        /// </summary>
        public AgentStreamEvent ToDoneEvent(bool stopped = false)
        {
            // A turn with no reported usage carries no cost at all; otherwise priced, included or unpriced (ADR 0110).
            var cost = Input > 0 || Output > 0
                ? ModelPricing.Estimate(
                    Pricing.ProviderKind, Pricing.Model, DateTimeOffset.UtcNow, new TokenCounts(Input, Output, Cached, CacheWrite))
                : null;

            return new(
                AgentStreamEventType.Done,
                InputTokens: Input > 0 ? Input : null,
                OutputTokens: Output > 0 ? Output : null,
                CachedTokens: Cached > 0 ? Cached : null,
                Stopped: stopped ? true : null,
                CacheWriteTokens: CacheWrite > 0 ? CacheWrite : null,
                CostUsd: cost?.Usd,
                CostStatus: cost?.Status);
        }
    }

    // A ToolCallEnd of an MCP App tool carries the view and the tool's raw result (ADR 0133). The result was recorded on
    // THIS run's context by the tool wrapper, keyed by call id, and only when the run is interactive and the browser can
    // render it: so a background run, or a server with apps off, finds nothing here and the event is unchanged.
    private static AgentStreamEvent AttachMcpApp(AgentStreamEvent evt, TurnContext turn)
        => evt.Type == AgentStreamEventType.ToolCallEnd && turn.AppResultFor(evt.ToolCallId) is { } app
            ? evt with { McpApp = app }
            : evt;

    // Every event one streamed update produces, in order. A synchronous iterator, so the async iterator
    // above stays one loop deep instead of two.
    private static IEnumerable<AgentStreamEvent> ProjectStreamedUpdate(
        AgentResponseUpdate update, StringBuilder reply, TurnTokenTotals tokens)
    {
        foreach (AIContent content in update.Contents)
        {
            if (ProjectStreamedContent(content, reply, tokens) is { } evt)
            {
                yield return evt;
            }
        }
    }

    // Follow-up suggestions (ADR 0036), or null when they are not wanted for this turn. The gating lives
    // HERE rather than at the call site so the model call is still skipped — not merely discarded — on
    // the agent-run/reminder drain (not interactive), when the user has opted out, on
    // an empty reply, and on a turn the user STOPPED (ADR 0078 — the fourth gate, and the only one the
    // empty-reply check does not already cover: a turn stopped after a few words has a non-empty reply,
    // so without it the answer to a stop would be a second model call about the cancelled answer).
    // Best-effort beyond that: GenerateFollowUpsAsync owns its try/catch and returns null on any
    // failure, so a slow or failing suggestion call never breaks the turn.
    // A GitHub Copilot model (ADR 0108) has no chat client — its provider's client refuses every call — so a
    // copilot chat on one skips the call rather than log a warning each turn. Running it on another of the user's
    // models instead would send the exchange to a second provider the user never picked for this chat.
    private async Task<IReadOnlyList<string>?> SuggestFollowUpsAsync(
        string message, StringBuilder reply, bool stopped, bool interactive)
        => !stopped && interactive && _suggestFollowUpsEnabled && reply.Length > 0
            // The analyzer panel shows no suggestions (ADR 0123), so the call would only resend its reply to the model.
            && !AgentKinds.IsFleetAnalyzer(_state.State.AgentKind)
            && !GitHubCopilotProvider.IsGitHubModel(_modelId)
            ? await GenerateFollowUpsAsync(message, reply.ToString())
            : null;

    // No model configured (ADR 0019). The factory is resolved once per activation, so a user who added their FIRST provider after
    // this grain activated still holds the empty factory here: re-resolve to pick it up without a reactivation. If a model is now
    // available, rebind onto it (history preserved) and let the normal turn run; if still none the caller tells the user to
    // configure one and stops, never running the no-op client. Returns whether a model is bound, and the model the switch below
    // should still apply: null once a rebind happened (so it does not repeat the rebuild), else the request's own.
    private async Task<(bool Bound, string? Model)> EnsureModelBoundAsync(string? model)
    {
        if (!string.IsNullOrEmpty(_modelId))
        {
            return (true, model);
        }

        _chatClientFactory = await _chatClientFactoryProvider.GetForUserAsync(_userId);
        if (string.IsNullOrEmpty(_chatClientFactory.DefaultModelId))
        {
            return (false, model);
        }

        // Honour a model the request picked (the same save could have populated the picker), else the newly-resolved default.
        // `auto` is a wire value, never a model (ADR 0137).
        await RebindModelAsync(string.IsNullOrWhiteSpace(model) || AutoModelId.IsAuto(model) ? _chatClientFactory.DefaultModelId : model);

        _logger.LogInformation(
            "Agent grain {GrainKey} bound to model {Model} after a provider was configured",
            this.GetPrimaryKeyString(), _modelId);
        return (true, null);
    }

    // Rehosts the existing conversation on a newly picked model: snapshot the session, rebuild the
    // agent against the new client, then restore the session so history carries over. A request that picked no
    // model, or the one already in use, is a no-op (the guard lives here to keep ChatCoreAsync under S3776).
    private async Task SwitchModelAsync(string? model)
    {
        // `auto` is a wire value, never a deployment (ADR 0137): ignore it here as the last line of defence.
        if (string.IsNullOrWhiteSpace(model) || model == _modelId || AutoModelId.IsAuto(model))
        {
            return;
        }

        var snapshot = await _agent.SerializeSessionAsync(_session);
        _modelId = model;
        _state.State.DeploymentName = model;
        _agent = BuildAgent(model);
        _session = await _agent.DeserializeSessionAsync(snapshot);

        // A DOWNGRADE — tools-capable conversation onto a model that has none — would replay this
        // conversation's FunctionCallContent/FunctionResultContent in a request that declares no
        // tools. GitHub Models accepts that today (ADR 0013 spike), but that is a service behaviour
        // we don't control and OpenAI-compatible endpoints commonly reject it; the failure mode is
        // a conversation that can never be reopened on this model. Cheap to prevent, so we do.
        if (!_chatClientFactory.GetCapabilities(model).SupportsTools)
        {
            StripToolHistory();
        }

        _logger.LogInformation("Agent grain {GrainKey} switched to model {Model}",
            this.GetPrimaryKeyString(), model);
    }

    // Turns one streamed content part into the event the client sees, or null when it produces none.
    // A UsageContent yields nothing — it only accumulates into <paramref name="tokens"/>.
    private static AgentStreamEvent? ProjectStreamedContent(
        AIContent content, StringBuilder reply, TurnTokenTotals tokens)
    {
        switch (content)
        {
            case TextContent text when !string.IsNullOrEmpty(text.Text):
                reply.Append(text.Text);
                return new AgentStreamEvent(AgentStreamEventType.TextDelta, Text: text.Text);

            case FunctionCallContent call:
                return new AgentStreamEvent(AgentStreamEventType.ToolCallStart,
                    ToolName: call.Name, ToolCallId: call.CallId, ToolInput: SerializeOrNull(call.Arguments));

            case FunctionResultContent result:
                return new AgentStreamEvent(AgentStreamEventType.ToolCallEnd,
                    ToolCallId: result.CallId, ToolResult: SerializeOrNull(result.Result));

            // Reasoning ("thinking") from a reasoning model, ADR 0071. Deliberately NOT appended to
            // `reply`: that StringBuilder feeds the ADR 0036 follow-up-suggestion call, so reasoning in it
            // would be re-sent to the model as if the assistant had said it — a second, pointless
            // third-party egress (ADR 0013) of the model's private deliberation. It is kept out of the
            // serialized session by StripReasoningFromStorage, not here; this projection cannot reach the
            // session, which Agent Framework fills from the response message independently.
            case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                return new AgentStreamEvent(AgentStreamEventType.Reasoning, Text: reasoning.Text);

            // A provider failure that arrived as a stream event (a Responses `error` event is a 200, not an exception).
            // Dropped before this, which left the user an empty bubble. Live-only like reasoning: StripReasoningFromStorage
            // keeps it out of the session. Capped, because the text is the provider's own.
            case ErrorContent error when !string.IsNullOrWhiteSpace(error.Message):
                return new AgentStreamEvent(AgentStreamEventType.Error, Text: error.Message.Length > ErrorTextLimit ? error.Message[..ErrorTextLimit] : error.Message);

            case UsageContent usage:
                tokens.Input += usage.Details.InputTokenCount.GetValueOrDefault();
                tokens.Output += usage.Details.OutputTokenCount.GetValueOrDefault();
                tokens.Cached += usage.Details.CachedInputTokenCount.GetValueOrDefault();
                tokens.CacheWrite += usage.Details.AdditionalCounts?.GetValueOrDefault(CacheCreationInputTokensKey) ?? 0;
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Drops every <see cref="TextReasoningContent"/> part from the assistant messages Agent Framework is
    /// about to store, so reasoning never reaches <c>SerializedSession</c> (ADR 0071, decision: live-only).
    /// Wired as the history provider's <c>StorageInputResponseMessageFilter</c> in <c>BuildAgent</c> — the
    /// response-side twin of the <c>StorageInputRequestMessageFilter</c> that already excludes injected
    /// context messages there.
    ///
    /// <para>This is a strip, not a no-op guard: measured on Agent Framework 1.19, WITHOUT it the framework
    /// stores the part and the session round-trips it as
    /// <c>{"$type":"reasoning","text":"…"}</c> — on BOTH the streaming and the <c>RunAsync</c> (reminder)
    /// path. So reasoning was already being persisted for any OpenAI-compatible reasoning model a user had
    /// added (ADR 0018), invisibly, before this feature surfaced it.</para>
    ///
    /// <para>Nothing downstream wants it back: the Responses path is stateless by mandate (ADR 0014) and
    /// providers that emit <c>reasoning_content</c> document that it must not be echoed into the next
    /// request. Dropping it also keeps it out of everything fed FROM the session — the 2 MB item (ADR 0004),
    /// public share links (ADR 0035), and the search projection (ADR 0061).</para>
    ///
    /// <para>Messages with no reasoning part are passed through by reference; only a message that actually
    /// carries one is rebuilt, so the common turn allocates nothing.</para>
    ///
    /// <para><b>A message left with ZERO parts is dropped, not stored empty.</b> On the Responses path a
    /// reasoning summary arrives as its own output item, distinct from the message item, so it can coalesce
    /// into a <see cref="ChatMessage"/> of its own — and stripping that one's only part would leave an empty
    /// message in the session, replayed to the provider on every later turn. An OpenAI-compatible endpoint
    /// rejects an empty message outright, which bricks the conversation; a fake answers it happily, which is
    /// why this is the same defect AC11 (<c>NoVisionModel_ImageOnlyTurn_StillReplies_AndSendsNoImage</c>)
    /// exists to catch on the image-stripping path. That path keeps a placeholder because the user's turn
    /// must stay visible; here there is nothing to show, so dropping is right.</para>
    ///
    /// <para>It owns <see cref="ErrorContent"/> too (a provider error that streamed as an event is live-only, never
    /// replayed to the model), and it drops a message with NO parts at all. That is a change from "an already-empty
    /// message passes through": a model that answers 200 with nothing we map (gpt-6.2-sol/astra) made the framework
    /// store an empty assistant message, which the next turn replays to the provider and a chat-completions
    /// endpoint rejects outright, and which the browser's reply recovery adopts as an empty bubble. A message with
    /// no parts means nothing to a model, so storing it is never right.</para>
    /// </summary>
    private static IEnumerable<ChatMessage> StripReasoningFromStorage(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Contents.Count == 0 && message.Role == ChatRole.Assistant)
            {
                continue;
            }

            if (!message.Contents.Any(c => c is TextReasoningContent or ErrorContent))
            {
                yield return message;
                continue;
            }

            var kept = message.Contents.Where(c => c is not (TextReasoningContent or ErrorContent)).ToList();
            if (kept.Count == 0)
            {
                continue;
            }

            yield return new ChatMessage(message.Role, kept)
            {
                AuthorName = message.AuthorName,
                CreatedAt = message.CreatedAt,
                MessageId = message.MessageId,
                AdditionalProperties = message.AdditionalProperties,
            };
        }
    }

    // An ephemeral turn (ADR 0034) runs the model but never writes its session item, so the
    // conversation leaves no trace in Cosmos — the incognito guarantee. Skip the whole serialize.
    // Returns whether the turn was persisted: the outbound webhook's gate (ADR 0094) inherits this decision.
    private async Task<bool> PersistTurnUnlessEphemeralAsync(TurnContext turn)
    {
        if (turn.Ephemeral)
        {
            // The incognito turn is still in the IN-MEMORY session (ADR 0034 guarantees "not
            // persisted, not listed", never "not in memory"), so the next normal turn of this same
            // activation would otherwise re-project it straight into the search index. Consume its
            // ordinals without indexing them — see ChatSearchProjection.SkipEphemeralTurnAsync (ADR 0061).
            await _search.SkipEphemeralTurnAsync();

            _logger.LogInformation("Agent grain {GrainKey} completed an ephemeral turn (not persisted)",
                this.GetPrimaryKeyString());
            return false;
        }

        // Retention (ADR 0016): stamp every message, then delete whole turns that fell outside the window
        // — continuous chats only. Before the serialize, so the pruned session is what gets written.
        await StoreMcpAppsAsync(turn);
        CompactToolPayloads();
        var pruned = ApplyRetention();

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        // This write carries any shared-state edit made during the run (ADR 0089), so nothing is left to flush.
        _sharedStateDirty = false;
        await _state.WriteStateAsync();

        // AFTER the durable write, deliberately: once a chat is older than the window most turns prune
        // something, so this fires on most turns — a filtered-query delete plus N deletes and N upserts on
        // a non-reentrant grain, which must not sit in front of the write ADR 0046's stream recovery reads.
        await _search.ReconcileAfterPruneAsync(pruned);

        // Search projection (ADR 0061). No ephemeral guard needed: the early return above already
        // took the incognito path, so reaching here means the turn is persisted. Best-effort inside.
        // When the reconcile above reset the cursor, this is the re-projection from ordinal 0.
        // The fleet analyzer panel's chat is never indexed (ADR 0123): its text quotes fleet output and /api/search
        // would list it, opening a conversation no sidebar knows.
        if (!AgentKinds.IsFleetAnalyzer(_state.State.AgentKind))
        {
            await _search.IndexNewTurnsAsync();
        }

        _logger.LogInformation("Agent grain {GrainKey} completed a turn and persisted its session",
            this.GetPrimaryKeyString());
        return true;
    }

    // Builds the user turn. Images ride alongside the text as UriContent parts pointing at a
    // store-provided URL (a blob SAS in prod, a placeholder offline) — never raw bytes, so the
    // serialized session stays small (ADR 0004). Returns the message and how many images were withheld;
    // the caller streams the user-facing notice, because this method cannot yield.
    private async Task<(ChatMessage Message, int SkippedImages)> BuildUserTurnAsync(
        string message, IReadOnlyList<AttachmentRef>? attachments, bool canSeeImages)
    {
        // Build the user turn. Images ride alongside the text as UriContent parts pointing at
        // a store-provided URL (a blob SAS in prod, a placeholder offline) — never raw bytes,
        // so the serialized session stays small (ADR 0004). Record this turn's refs so history
        // can re-render them.
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(message))
        {
            contents.Add(new TextContent(message));

            // A URL the user wrote themselves is fetchable (ADR 0011) — "summarize this link" must
            // work without searching first. This is the ONLY place user-authored text feeds the
            // allowlist; text the model read off a web page never does.
            RememberFetchableUrlsInUserText(message);
        }

        // The attachment still lands in TurnAttachments even when withheld, so history renders it and
        // the image is not lost — switching back to a vision model restores it to the model's view too
        // (see RehydrateAttachmentUrlsAsync).
        var skippedImages = 0;

        var turnRefs = new List<AttachmentRef>();
        if (attachments is { Count: > 0 })
        {
            foreach (var attachment in attachments)
            {
                if (!canSeeImages)
                {
                    skippedImages++;
                    turnRefs.Add(attachment);
                    continue;
                }

                var url = await _attachments.GetReadUrlAsync(attachment);
                contents.Add(new UriContent(url, attachment.MediaType));
                turnRefs.Add(attachment);
            }
        }

        if (skippedImages > 0)
        {
            _logger.LogInformation(
                "Agent grain {GrainKey} withheld {Count} image(s) from model {Model}: no vision support",
                this.GetPrimaryKeyString(), skippedImages, _modelId);
        }

        // Never send a message with no content parts (an empty text-only turn keeps a TextContent). An
        // image-only turn on a no-vision model lands here too — every part it had was withheld — and it
        // gets the placeholder rather than an empty string, so the model can see that something was
        // omitted rather than receiving a blank turn.
        if (contents.Count == 0)
        {
            contents.Add(new TextContent(skippedImages > 0 ? WithheldImagePlaceholder : string.Empty));
        }

        // Keep TurnAttachments 1:1 with the user turns already in history before appending this
        // turn's entry, so the index join holds even for conversations created before this list
        // existed (old Cosmos state deserializes it empty).
        AlignTurnAttachmentsToHistory();
        _state.State.TurnAttachments.Add(turnRefs);

        return (new ChatMessage(ChatRole.User, contents), skippedImages);
    }

    private static string? SerializeOrNull(object? value) =>
        value is not null ? JsonSerializer.Serialize(value) : null;

    public Task<IReadOnlyList<ChatTurn>> GetHistoryAsync()
        => Task.FromResult<IReadOnlyList<ChatTurn>>(ProjectHistory().Select(p => WithMcpApps(p.Turn)).ToList());

    /// <summary>
    /// The one history projection (<see cref="GetHistoryAsync"/>, search ordinals, ADR 0046 recovery), with each
    /// turn tagged by the SESSION user turn it belongs to: the 0-based index of the user message that opened its
    /// exchange, or -1 for a turn before the first user message. Edit, regenerate and fork (ADR 0092) map a
    /// client-visible position to a session position through this walk only, never through a second count
    /// (gotcha "Chat-search ordinals must come from ONE projection").
    /// </summary>
    private List<ProjectedTurn> ProjectHistory()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? stored) || stored is null)
        {
            return [];
        }

        // A research turn still running is hidden whole (ADR 0082): the loop persists each finished iteration
        // mid-turn, and ADR 0046 recovery must not adopt a draft as the reply.
        var messages = VisibleHistory(stored);

        // Results FIRST, over the whole history, because a call's result never arrives on the message
        // that made the call — it comes back on a later tool-role message. Matching them in one forward
        // pass would drop the result of any call that shared a message with text: that message projects
        // to a real turn, the chain flushes onto it immediately, and the result then arrives with nothing
        // left to match against. A model emitting `content` and `tool_calls` together is an ordinary
        // provider shape, so that is a silently empty chip on a common path, not a corner case.
        var toolResults = CollectToolResults(messages);

        var turns = new List<ProjectedTurn>();
        var userTurnIndex = 0;
        // The tool-call chain currently open: filled as the walk passes the assistant message that made
        // the calls, flushed onto the assistant turn that finally speaks (ADR 0080). Nothing here adds,
        // removes or reorders a turn — search ordinals (ADR 0061) and stream recovery (ADR 0046) both
        // count this list.
        var pendingToolCalls = new List<ChatToolCall>();
        foreach (var message in messages)
        {
            // Harvest BEFORE projecting, so a model that emits text and a call in the SAME message
            // gets its own chips — which is where the live renderer puts them (above that bubble).
            HarvestToolCalls(message, pendingToolCalls, toolResults);

            // The two roles are projected separately because they resolve their images differently:
            // a user turn joins TurnAttachments POSITIONALLY (so it must advance the index even when
            // the turn is then skipped), while an assistant turn looks its image up by id.
            var turn = message.Role == ChatRole.User
                ? ProjectUserTurn(message, ref userTurnIndex)
                : message.Role == ChatRole.Assistant ? ProjectAssistantTurn(message) : null;

            turn = AttachPendingToolCalls(message, turn, pendingToolCalls);

            if (turn is not null)
            {
                // ProjectUserTurn has already advanced the index past this message, so the exchange it opened
                // (and every assistant turn after it) is one behind.
                turns.Add(new ProjectedTurn(turn, userTurnIndex - 1));
            }
        }

        return turns;
    }

    /// <summary>One projected turn and the session user turn it belongs to (see <see cref="ProjectHistory"/>).</summary>
    private sealed record ProjectedTurn(ChatTurn Turn, int SessionUserTurn);

    // Every tool result in the history, keyed by the call it answers. Whole-history and position-blind on
    // purpose — see the call site. Call ids are unique per call, so the last writer only ever wins over an
    // identical entry.
    private static Dictionary<string, string?> CollectToolResults(List<ChatMessage> messages)
    {
        var results = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent result)
                {
                    results[result.CallId] = SerializeOrNull(result.Result);
                }
            }
        }

        return results;
    }

    // One message's tool calls, harvested into the open chain with their results already joined on.
    // Serialized with the same SerializeOrNull the live stream uses, so a replayed payload is
    // byte-identical to the one that was streamed.
    //
    // A call with no entry in <paramref name="results"/> keeps a null ToolResult and still replays, as a
    // completed chip with no result body — see ChatToolCall.
    private static void HarvestToolCalls(
        ChatMessage message, List<ChatToolCall> pending, Dictionary<string, string?> results)
    {
        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent call)
            {
                pending.Add(new ChatToolCall(
                    call.CallId,
                    call.Name,
                    SerializeOrNull(call.Arguments),
                    results.GetValueOrDefault(call.CallId)));
            }
        }
    }

    // Flushes the open chain onto the assistant turn that answers it, emptying it either way.
    //
    // The one deliberate drop (ADR 0080): a chain still open at a USER turn is discarded, because its
    // reply never materialised and its chips have no bubble to live in. A call whose result is missing is
    // NOT dropped — it flushes with a null ToolResult and the frontend completes it anyway, rather than
    // leaving the chip pulsing in a finished transcript.
    private static ChatTurn? AttachPendingToolCalls(
        ChatMessage message, ChatTurn? turn, List<ChatToolCall> pending)
    {
        if (message.Role == ChatRole.User)
        {
            pending.Clear();
            return turn;
        }

        if (turn is null || pending.Count == 0)
        {
            return turn;
        }

        var calls = pending.ToArray();
        pending.Clear();
        return turn with { ToolCalls = calls, Artifacts = ArtifactsFromToolCalls(calls) };
    }

    // One user turn, or null when it carries neither text nor an image and should not be shown.
    //
    // ADVANCES userTurnIndex unconditionally, including for a skipped turn — the join to
    // TurnAttachments is positional (one entry per user turn, in order — see ChatAsync/
    // ReceiveReminder), so skipping the increment would misalign every image after it.
    private ChatTurn? ProjectUserTurn(ChatMessage message, ref int userTurnIndex)
    {
        var refs = userTurnIndex < _state.State.TurnAttachments.Count
            ? _state.State.TurnAttachments[userTurnIndex]
            : null;
        userTurnIndex++;

        var hasAttachments = refs is { Count: > 0 };
        // Retain an image-only turn (blank text but an image attached) — the old filter
        // dropped any blank-text message, which would hide an image-only prompt.
        return string.IsNullOrWhiteSpace(message.Text) && !hasAttachments
            ? null
            : new ChatTurn(message.Role.Value, message.Text ?? "", hasAttachments ? refs : null);
    }

    // One assistant turn, or null when it carries neither text nor a generated image.
    //
    // A generated-image turn (ADR 0020) is typically blank-text; it is retained by its ref, looked up by
    // the message-level assistantImageId — id-keyed, so it survives interleaved tool-call assistant
    // messages (the old filter dropped any blank-text assistant message, hiding it).
    private ChatTurn? ProjectAssistantTurn(ChatMessage message)
    {
        var generated = AssistantImageRef(message);
        var hasImage = generated is not null;

        return string.IsNullOrWhiteSpace(message.Text) && !hasImage
            ? null
            : new ChatTurn(message.Role.Value, message.Text ?? "", hasImage ? [generated!] : null);
    }

    public async Task PurgeAsync()
    {
        // Reminders are durable and keep firing on their own schedule, so they must be
        // unregistered explicitly — clearing state alone would leave them ticking.
        foreach (var reminder in await this.GetReminders())
        {
            await this.UnregisterReminder(reminder);
        }

        // Remove the conversation's indexed document chunks too, so deleting a conversation doesn't
        // leave its extracted document text behind (privacy — ADR 0007). The index is owner-scoped
        // by the same grain key.
        await _documentIndex.DeleteAsync(this.GetPrimaryKeyString());

        // And its indexed messages, so deleted conversation text does not survive in the search
        // projection (ADR 0061). Best-effort and bounded inside: this grain is non-reentrant and
        // DELETE /api/conversations/{id} awaits it synchronously.
        await _search.DeleteConversationAsync();

        // And Copilot's own transcript of a copilot chat (its SessionFs files), which the session item only points
        // at. Before ClearStateAsync, which takes the agent kind with it. Best-effort inside.
        await DeleteCopilotTranscriptAsync();

        _state.State.ScheduledPrompts.Clear();
        _state.State.DocumentNames.Clear();
        // The web-fetched lists must be cleared alongside DocumentNames, and for the reason that line
        // exists at all: DeactivateOnIdle() below is *on idle*, so a request landing in the window before
        // deactivation would otherwise see a grain whose chunks are already gone but whose
        // WebDocumentNames still gate the retrieval provider AND still name attacker-influenced filenames
        // in the system prompt (ADR 0066).
        _state.State.WebDocumentNames.Clear();
        _state.State.WebDocumentUrls.Clear();
        // The metadata goes; the blobs stay, the same gap attachments have (no blob delete on purge).
        _state.State.Artifacts.Clear();
        _state.State.McpApps.Clear();
        _state.State.SerializedSession = null;
        _state.State.DeploymentName = null;
        await _state.ClearStateAsync();

        _logger.LogInformation("Agent grain {GrainKey} purged", this.GetPrimaryKeyString());

        // Start the next activation from clean state rather than the stale in-memory session.
        this.DeactivateOnIdle();
    }

    private static DateTime GetDate() => DateTime.Now;

    /// <summary>
    /// Called by the Orleans runtime when a scheduled reminder ticks. The reminder's
    /// prompt is sent to the agent as a user message; the resulting turn is persisted
    /// in the session, so the user sees both the prompt and the agent's response the
    /// next time they open the conversation.
    /// </summary>
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // The idle retention sweep (ADR 0140) is not a scheduled prompt: it runs no model, sends no push, publishes no
        // usage and does not touch _runsInFlight, so it is dispatched before everything below.
        if (reminderName == RetentionLimits.SweepReminderName)
        {
            await SweepRetentionAsync();
            return;
        }

        PublishUsageApplication();

        // A fired reminder always persists and nobody watches it, so its run is background (ADR 0106). A fresh
        // grain call starts with no current context, so it never inherits a chat turn's.
        var turn = TurnContext.ForBackground();

        // The second run site of the shared-state run count (ADR 0089); ChatCoreAsync is the first.
        _runsInFlight++;
        try
        {
            await RunReminderAsync(reminderName, turn);
        }
        finally
        {
            await EndRunAsync();
        }
    }

    // The reminder's run. A separate method only so the run count above can wrap it.
    private async Task RunReminderAsync(string reminderName, TurnContext turn)
    {
        if (!_state.State.ScheduledPrompts.TryGetValue(reminderName, out var info))
        {
            _logger.LogWarning(
                "Reminder '{Reminder}' fired for conversation {GrainKey} but no metadata was found.",
                reminderName, this.GetPrimaryKeyString());
            return;
        }

        // No model configured (ADR 0019): re-resolve in case the user added a provider since activation,
        // exactly like ChatCoreAsync. A reminder that still finds no model is SKIPPED — never run the no-op
        // client — so it simply doesn't fire until a provider exists (the user sees nothing, which is right).
        if (string.IsNullOrEmpty(_modelId))
        {
            _chatClientFactory = await _chatClientFactoryProvider.GetForUserAsync(_userId);
            if (string.IsNullOrEmpty(_chatClientFactory.DefaultModelId))
            {
                _logger.LogInformation(
                    "Reminder '{Reminder}' fired for conversation {GrainKey} but no model provider is "
                    + "configured; skipping.", reminderName, this.GetPrimaryKeyString());
                return;
            }

            await RebindModelAsync(_chatClientFactory.DefaultModelId);
        }

        // A copilot chat with no agent skips the reminder like the no-model case: nothing to run it on. (A reminder
        // is never incognito, so the other half of CopilotTurnRefusal cannot apply.)
        if (_copilotUnavailable is not null)
        {
            _logger.LogInformation(
                "Reminder '{Reminder}' fired for conversation {GrainKey} but its copilot agent is unavailable; skipping.",
                reminderName, this.GetPrimaryKeyString());
            return;
        }

        // Refresh prior turns' image URLs before this model call too (a reminder can fire on a
        // long-warm grain whose SAS URLs have since expired) — same reason as ChatAsync.
        var canSeeImages = CanSeeImages();
        turn.SetToolImagesVisible(canSeeImages);
        await RehydrateAttachmentUrlsAsync(canSeeImages);

        // A reminder-fired turn is a user turn too, so it must push a (always empty) attachment
        // entry to keep TurnAttachments aligned 1:1 with user turns by index (constraint 11).
        AlignTurnAttachmentsToHistory();
        var prompt = new ChatMessage(ChatRole.User, info.Prompt);

        // The research loop's second run site (ADR 0082): this method does not drain ChatCoreAsync, so it resets
        // the todos, hides the turn and folds it itself, in the same order.
        var research = BeginResearchRun(prompt);
        var copilotRun = OpenCopilotRun(turn);
        AgentResponse response;
        try
        {
            // Inside RunInAsync so the run's tools and providers see this run's context (ADR 0106).
            response = await TurnContext.RunInAsync(turn, async () => await _agent.RunAsync([prompt], _session));
        }
        finally
        {
            CloseCopilotRun(copilotRun);
            // In the finally, so a run that fails in a later iteration still folds the iterations it stored.
            ReleaseResearchRun(research, foldFirst: true);
        }

        _state.State.TurnAttachments.Add([]);

        // Retention (ADR 0016), same as the chat path. This site does its OWN serialize — it does not
        // delegate to PersistTurnUnlessEphemeralAsync — so the call has to be repeated here.
        CompactToolPayloads();
        var pruned = ApplyRetention();

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);

        _state.State.ScheduledPrompts[reminderName] = info with { TimesFired = info.TimesFired + 1 };

        // This write carries any shared-state edit made during the run (ADR 0089), so nothing is left to flush.
        _sharedStateDirty = false;
        await _state.WriteStateAsync();

        // After the write, for the reason given at the chat site.
        await _search.ReconcileAfterPruneAsync(pruned);

        // Search projection (ADR 0061). Deliberately NO ephemeral guard here — a fired reminder is never
        // ephemeral and always persists. Best-effort inside.
        await _search.IndexNewTurnsAsync();

        // Consume site 2 of 2 (ADR 0066), and the one that is easy to miss: this method does NOT drain
        // ChatCoreAsync — it calls _agent.RunAsync directly and does its own serialize/write/index —
        // so ChatCoreAsync's consume site never runs for a reminder turn. Without this call, a reminder
        // that ingested a document would persist the name list and lose the rebuild, leaving retrieval
        // detached for the rest of the activation while the instructions claim the document exists.
        await RebuildForIngestedWebDocumentAsync(turn);

        // Outbound webhook (ADR 0094), fire site 3 of 3: after this method's own write, and BEFORE the push early
        // return below — a model that pushed during the turn (ADR 0075) must not cost the owner the webhook.
        await NotifyAgentWebhookAsync(true, new WebhookTurn(
            AgentWebhookEvent.Triggers.Reminder, info.Prompt, LastAssistantText(response), ReminderName: reminderName));

        // Away-reply push (ADR 0028): a scheduled prompt fired with no live client watching. After the
        // persist, so a push only ever follows a durably-recorded reply. No _modelId guard needed — the
        // no-model case already early-returned above (a reminder with no provider is skipped entirely).
        // The sender's contract is "never throws", but the reminder's TimesFired is already persisted, so
        // a defensive wrap keeps a future misbehaving sender from faulting an already-recorded reminder
        // (EnqueuePromptAsync's send is likewise inside its own try).
        // Suppressed when the model already pushed this turn (ADR 0075) — same reasoning as the
        // agent-run path: with no `tag` on the notification the two would stack in the shade for one
        // reply, and the model's own text is the better of the two. The position (after the persist) and
        // the defensive try below are both unchanged.
        //
        // Chat delivery (ADR 0111) sits BEFORE that early return: the origin chat is not a shade notification, so the
        // model's own push does not make it redundant (DeliverAwayReplyToChatAsync skips only the home fallback then).
        await DeliverAwayReplyToChatAsync(turn, LastAssistantText(response));

        if (turn.PushesSent > 0)
        {
            return;
        }

        try
        {
            await SendAwayReplyPushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Away-reply push failed after reminder {GrainKey}; the reply was already persisted.",
                this.GetPrimaryKeyString());
        }
    }

    // Rebinds the conversation onto <paramref name="modelId"/>: snapshot → rebuild the agent against the
    // new client → restore history → strip tool history on a downgrade. This is the no-model→model rebind
    // of ADR 0019 (a provider was configured while this grain was already activated); it mirrors the
    // inline snapshot/rebuild/restore of the picker-switch and bind paths, and applies the same
    // no-tools-downgrade strip (ADR 0013) so a rebind onto a no-tools model can't replay tool content.
    private async Task RebindModelAsync(string modelId)
    {
        ThrowIfAuto(modelId);
        _modelId = modelId;
        _state.State.DeploymentName = modelId;
        await RebuildAgentPreservingSessionAsync(modelId);

        if (!_chatClientFactory.GetCapabilities(modelId).SupportsTools)
        {
            StripToolHistory();
        }
    }

    // Snapshot → rebuild the agent from the grain's current fields → restore the history. Shared by the no-model
    // rebind above, the MCP re-resolve below (ADR 0081) and the web-document rebuild (ADR 0066), which change
    // different inputs to BuildAgent.
    private async Task RebuildAgentPreservingSessionAsync(string modelId)
    {
        var snapshot = await _agent.SerializeSessionAsync(_session);
        _agent = BuildAgent(modelId);
        _session = await _agent.DeserializeSessionAsync(snapshot);
    }

    // Mid-turn MCP recovery (ADR 0081), following the _chatClientFactory re-resolve precedent at the top of
    // ChatCoreAsync. Runs only when there is something to recover (a group flagged ReauthRequired, whose tools are
    // therefore missing from the agent), only on an interactive run, and only when no other interactive run is live
    // — a rebuild swaps _agent/_session, which must never happen under a run still iterating them (the reason
    // RebuildForIngestedWebDocumentAsync gives). The provider's cache makes an unchanged version a cheap hit.
    private async Task ReResolveMcpAfterReconnectAsync(bool interactive)
    {
        if (!interactive || _turnCts is not null || !_mcpServers.Any(s => s.ReauthRequired))
        {
            return;
        }

        var set = await _mcpToolProvider.GetForUserAsync(_userId);
        if (set.Version == _mcpVersion)
        {
            return;
        }

        _mcpServers = set.Servers;
        _mcpVersion = set.Version;
        _mcpReauthNotified.Clear();
        await RebuildAgentPreservingSessionAsync(_modelId);
        _logger.LogInformation(
            "Agent grain {GrainKey} re-resolved its MCP tools after a reconnect (version {Version})",
            this.GetPrimaryKeyString(), set.Version);
    }

    // The flagged servers this conversation actually uses: ANY of its tools enabled (IsMcpToolEnabled), not
    // IsMcpServerEnabled — a partial per-tool selection (mcp:{id}:{tool}, ADR 0058) must still get the notice.
    // A model that takes no tools is offered none, so it gets no notice either.
    private IEnumerable<string> McpServersNeedingReauth()
    {
        if (!_mcpServers.Any(s => s.ReauthRequired) || !_chatClientFactory.GetCapabilities(_modelId).SupportsTools)
        {
            return [];
        }

        var enabled = EffectiveEnabledTools().ToHashSet(StringComparer.Ordinal);
        return _mcpServers
            .Where(s => s.ReauthRequired
                && (s.KnownToolNames ?? []).Any(n => ToolCatalog.IsMcpToolEnabled(s.ServerId, n, enabled)))
            .Select(s => s.ServerId);
    }

    // One live McpReauthRequired event per server not yet notified on this activation — and nothing at all, not
    // even the bookkeeping, for a background run (see _mcpReauthNotified).
    private List<AgentStreamEvent> TakeMcpReauthNotices(IEnumerable<string> serverIds, bool interactive)
    {
        var notices = new List<AgentStreamEvent>();
        if (!interactive)
        {
            return notices;
        }

        foreach (var id in serverIds)
        {
            if (_mcpReauthNotified.Add(id))
            {
                var name = _mcpServers.FirstOrDefault(s => s.ServerId == id)?.ServerName ?? id;
                notices.Add(new AgentStreamEvent(AgentStreamEventType.McpReauthRequired, Text: name, McpServerId: id));
            }
        }

        return notices;
    }

    // Drops function-call and function-result content from the session (ADR 0013). Used when the
    // conversation moves onto a model that declares no tools: replaying tool content in a request that
    // carries no tool schema is rejected by many OpenAI-compatible endpoints, and the conversation
    // would be stuck there. Assistant messages left with nothing but a tool call are dropped whole.
    private void StripToolHistory()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        foreach (var message in messages)
        {
            for (var i = message.Contents.Count - 1; i >= 0; i--)
            {
                if (message.Contents[i] is FunctionCallContent or FunctionResultContent)
                {
                    message.Contents.RemoveAt(i);
                }
            }
        }

        messages.RemoveAll(m => m.Contents.Count == 0);
    }
}
