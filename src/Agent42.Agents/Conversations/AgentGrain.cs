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
/// instance field — every dependency and every turn-scoped flag — plus activation, the chat entry
/// points and <see cref="ReceiveReminder"/>. The feature slices live beside their feature
/// (<c>Web/AgentGrain.WebTools.cs</c>, <c>Documents/AgentGrain.Documents.cs</c>, and so on) but all
/// declare this one namespace: a partial class cannot span namespaces, which is why
/// <c>AgentGrain.*.cs</c> is exempted from the IDE0130 folder/namespace rule in
/// <c>.editorconfig</c>.</para>
///
/// <para><b><see cref="ReceiveReminder"/> stays here on purpose.</b> The safety of
/// <c>_turnLocation</c> and <c>_ephemeral</c> rests on TWO clears that must be read together — the
/// <c>finally</c> in <see cref="ChatAsync"/> and the nulling on entry to
/// <see cref="ReceiveReminder"/> (see the entries at <c>.agentic/gotchas.md</c> for both flags).
/// Filing the reminder callback under <c>Scheduling/</c> would split that pair across files, which
/// is exactly the reading failure those gotchas exist to prevent. Only the schedule/cancel tool
/// methods live in <c>Scheduling/AgentGrain.Scheduling.cs</c>.</para>
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

    private readonly IUserMcpToolProvider _mcpToolProvider;

    private readonly IUserSkillProvider _userSkillProvider;

    private readonly IAttachmentStore _attachments;

    private readonly IDocumentIndex _documentIndex;

    private readonly IChatSearchIndex _chatSearchIndex;

    private readonly IWebSearch _webSearch;

    private readonly IWebPageFetcher _webPageFetcher;

    // read_web_document (ADR 0066). Deliberately NOT the same seam as _webPageFetcher: this one dials the
    // model-chosen URL from OUR container (so it carries the SSRF guard), and it returns bytes rather
    // than scraped text.
    private readonly IWebDocumentFetcher _webDocumentFetcher;

    private readonly IDocumentIngestor _documentIngestor;

    private readonly IGeocoder _geocoder;

    private readonly HindsightIntegration _memory;

    private readonly IPushNotificationSender _pushSender;

    private readonly ILogger<AgentGrain> _logger;

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

    // This user's UPLOADED skills (ADR 0049), resolved ONCE on activation from this grain's userId — BEFORE
    // the first BuildAgent, the same ordering hazard as _mcpServers, _chatClientFactory and
    // _userDefaultEnabledTools. BuildAgent merges these with SkillCatalog into one catalog (upload wins on a
    // name clash) for BOTH the provider gate and the `skills` wildcard expansion in GetEnabledToolsAsync, and
    // ReceiveReminder reuses that same _agent — so resolving later would have the first chat turn AND every
    // reminder turn on this activation silently ignore the user's uploads.
    //
    // NOTE this is precisely why ExpandSkillsWildcard is no longer static. ADR 0048 could say the skills
    // expansion "needs no grain field ... there is no OnActivateAsync ordering hazard here" because
    // SkillCatalog is static and process-wide; making skills per-user re-opens the hazard the MCP expansion
    // already had.
    //
    // Best-effort: a missing or corrupt package costs that one skill, never the activation.
    private IReadOnlyList<SkillInfo> _userSkills = [];

    /// <summary>
    /// The user's position for the turn currently being processed, or null (ADR 0012). Set at the top
    /// of <see cref="ChatAsync"/> from the position the browser sent with the request.
    ///
    /// <para><b>This field lives for the grain ACTIVATION, not the call — so it must be cleared
    /// explicitly, and it is, in two places.</b> A <c>finally</c> at the end of <c>ChatAsync</c> drops
    /// it when the turn ends, and <see cref="ReceiveReminder"/> nulls it on entry. Doing nothing on the
    /// reminder path would <i>not</i> leave it null: a reminder is a separate call on the same
    /// activation, so after any located chat turn the field is still populated and a scheduled prompt
    /// would confidently report where the user was hours ago. Non-reentrancy is not the argument that
    /// makes this safe — <c>ChatAsync</c> is an <c>IAsyncEnumerable</c> method that suspends between
    /// <c>yield return</c>s — the explicit clear is.</para>
    ///
    /// <para>It is deliberately NOT in <see cref="AgentGrainState"/>: a persisted fix would outlive the
    /// turn it was captured for and have the model reporting last week's position as today's.</para>
    /// </summary>
    private UserLocation? _turnLocation;

    /// <summary>
    /// True while an ephemeral (incognito) turn is being processed (ADR 0034). Set at the top of
    /// <see cref="ChatAsync"/> from the flag the browser sent, it makes <see cref="ChatCoreAsync"/> and
    /// <see cref="GenerateImageTurnAsync"/> skip their session-state writes, and makes
    /// <see cref="SchedulePromptAsync"/> refuse (a durable reminder would fire the persisting/pushing
    /// reminder path). <b>Like <see cref="_turnLocation"/>, this field lives for the grain ACTIVATION,
    /// not the call, so it is cleared explicitly</b> — in the <c>finally</c> of <see cref="ChatAsync"/> —
    /// or a warm activation's next (non-ephemeral) turn would silently inherit it and skip its own write.
    /// </summary>
    private bool _ephemeral;

    /// <summary>
    /// True while an INTERACTIVE (browser-streamed) turn is being processed. Gates the extra follow-up
    /// suggestion model call to the live path only: <see cref="EnqueuePromptAsync"/> (agent-run) and
    /// <see cref="ReceiveReminder"/> drain the same <see cref="ChatCoreAsync"/>, but their streamed events
    /// have no listener, so generating suggestions there would be a wasted call for a discarded event.
    /// Defaults OFF and is set ON only in <see cref="ChatAsync"/>; like <see cref="_ephemeral"/> it lives
    /// for the ACTIVATION, so it is cleared in that method's <c>finally</c>.
    /// </summary>
    private bool _interactiveTurn;

    /// <summary>
    /// True when <c>read_web_document</c> ingested a document during the turn currently running, so the
    /// agent must be rebuilt once that run is over (ADR 0066).
    ///
    /// <para>The rebuild cannot happen inside the tool: it swaps <c>_session</c> out from under the
    /// framework run that is iterating it. So the tool sets this and
    /// <see cref="RebuildForIngestedWebDocumentAsync"/> consumes it at the <b>two</b> places a run
    /// completes — after <c>PersistTurnUnlessEphemeralAsync</c> in <see cref="ChatCoreAsync"/>, and after
    /// <see cref="ReceiveReminder"/>'s own write. Miss the second and a reminder turn's ingest never
    /// attaches retrieval.</para>
    ///
    /// <para>Like <see cref="_ephemeral"/> and <see cref="_turnLocation"/> it lives for the grain
    /// ACTIVATION, not the call, so it is cleared in the same three places: the <c>finally</c> of
    /// <see cref="ChatAsync"/>, and on entry to <see cref="EnqueuePromptAsync"/> and
    /// <see cref="ReceiveReminder"/>. Consuming it clears it too, so a normal turn leaves it false.</para>
    /// </summary>
    private bool _webDocumentIngested;

    public AgentGrain(
        [PersistentState("agent", StorageProviderName)] IPersistentState<AgentGrainState> state,
        IUserChatClientFactoryProvider chatClientFactoryProvider,
        IUserMcpToolProvider mcpToolProvider,
        IUserSkillProvider userSkillProvider,
        IAttachmentStore attachments,
        IDocumentIndex documentIndex,
        IChatSearchIndex chatSearchIndex,
        IWebSearch webSearch,
        IWebPageFetcher webPageFetcher,
        IWebDocumentFetcher webDocumentFetcher,
        IDocumentIngestor documentIngestor,
        IGeocoder geocoder,
        HindsightIntegration memory,
        IPushNotificationSender pushSender,
        ILogger<AgentGrain> logger)
    {
        _state = state;
        _chatClientFactoryProvider = chatClientFactoryProvider;
        _mcpToolProvider = mcpToolProvider;
        _userSkillProvider = userSkillProvider;
        _attachments = attachments;
        _documentIndex = documentIndex;
        _chatSearchIndex = chatSearchIndex;
        _webSearch = webSearch;
        _webPageFetcher = webPageFetcher;
        _webDocumentFetcher = webDocumentFetcher;
        _documentIngestor = documentIngestor;
        _geocoder = geocoder;
        _memory = memory;
        _pushSender = pushSender;
        _logger = logger;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Resolve THIS user's effective provider set (baseline ⊕ overlay, ADR 0018) FIRST: every use of
        // _chatClientFactory below — the DefaultModelId read on the next line, BuildAgent, and the ~8
        // sync call sites — dereferences it, so resolving it later would NRE (m4). The userId is
        // everything before the first '/' of the grain key ({userId}/{conversationId}, see AgentApi.AgentKey).
        _userId = UserIdFromGrainKey(this.GetPrimaryKeyString());
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
        _mcpServers = (await _mcpToolProvider.GetForUserAsync(_userId, cancellationToken)).Servers;

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
    private static string UserIdFromGrainKey(string grainKey)
    {
        var slash = grainKey.IndexOf('/');
        return slash < 0 ? grainKey : grainKey[..slash];
    }

    // The conversationId is everything after the FIRST '/' of the grain key ({userId}/{conversationId}).
    // A key with no '/' (defensive) has no conversation part, so returns the whole key — the push then
    // opens "/" rather than a specific conversation, which is a harmless degradation.
    private static string ConversationIdFromGrainKey(string grainKey)
    {
        var slash = grainKey.IndexOf('/');
        return slash < 0 ? grainKey : grainKey[(slash + 1)..];
    }

    public Task<string> GetSelectedModelAsync() => Task.FromResult(_modelId);

    public async Task BindAgentAsync(AgentBinding binding)
    {
        // Immutable once bound: a conversation keeps the first persona it was given, so a
        // repeat bind (e.g. a retried first turn) can never flip it mid-conversation.
        if (!string.IsNullOrEmpty(_state.State.AgentId))
        {
            return;
        }

        _state.State.AgentId = binding.AgentId;
        _state.State.AgentName = binding.Name;
        _state.State.SystemPrompt = binding.SystemPrompt;
        _state.State.DeploymentName = binding.Model;

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

        await _state.WriteStateAsync();

        _logger.LogInformation("Agent grain {GrainKey} bound to agent {AgentId} ({AgentName}) on model {Model}",
            this.GetPrimaryKeyString(), binding.AgentId, binding.Name, binding.Model);
    }

    public Task<AgentBinding?> GetAgentBindingAsync()
        => Task.FromResult(_state.State.AgentId is { Length: > 0 } id
            ? new AgentBinding(
                id, _state.State.AgentName ?? "", _state.State.SystemPrompt ?? "", _modelId,
                _state.State.EnabledTools)
            : null);

    public async IAsyncEnumerable<AgentStreamEvent> ChatAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments = null,
        UserLocation? location = null, bool ephemeral = false)
    {
        // The position and the ephemeral flag are both turn-scoped, and the fields outlive the call
        // (see _turnLocation / _ephemeral), so the clears are explicit and in a finally: they must
        // happen even if the caller abandons the stream half-way (the finally then runs on the
        // enumerator's disposal), or the next turn — or a reminder firing on this same activation —
        // would inherit this turn's fix, or skip its own session write.
        _turnLocation = location is { IsValid: true } ? location : null;
        _ephemeral = ephemeral;
        // This is the live browser-streamed path, so follow-up suggestions may be generated (gated further
        // by the user preference). Turn-scoped like the two above; cleared in the finally.
        _interactiveTurn = true;

        try
        {
            await foreach (var evt in ChatCoreAsync(message, model, attachments))
            {
                yield return evt;
            }
        }
        finally
        {
            _turnLocation = null;
            _ephemeral = false;
            _interactiveTurn = false;
            // Consumed inside ChatCoreAsync on the normal path; cleared here for the same
            // activation-scoped reason as the two above — an abandoned stream must not leave a warm
            // activation carrying a pending rebuild into someone else's turn (ADR 0066).
            _webDocumentIngested = false;
        }
    }

    public async Task EnqueuePromptAsync(string message, IReadOnlyList<AttachmentRef>? attachments)
    {
        // A background (agent-run) turn has no browser and no location — clear _turnLocation before
        // running, exactly like the reminder path: the field lives for the ACTIVATION, so a prior chat
        // turn's fix would otherwise leak into this one and have get_current_location report a stale place.
        // Same reasoning for _ephemeral: an agent-run conversation is always listed and persisted (it is
        // opened later by its owner), so a stale incognito flag must never make this turn skip its write.
        _turnLocation = null;
        _ephemeral = false;
        // A background (agent-run) turn has no live client watching the stream, so follow-up suggestions
        // would be a wasted model call for a discarded event — keep the interactive gate OFF.
        _interactiveTurn = false;
        // Same activation-scoped reason as the two clears above (ADR 0066). This turn drains
        // ChatCoreAsync, so its own ingest is consumed there; what must not happen is inheriting a
        // pending rebuild from a chat turn whose stream was abandoned.
        _webDocumentIngested = false;

        try
        {
            // Reuse the full chat turn core by DRAINING it. ChatCoreAsync already does the no-model guard,
            // the image-generation branch (ADR 0020 — so an image model edits the attachment), the vision
            // gate + attachment→UriContent materialization (ADR 0013/0004), document retrieval, the model
            // run and the session persist — everything a browser turn does. The streamed events have no
            // listener here (this is OneWay/202), so they are simply discarded; the side effects — the
            // persisted turn — are the point. model: null keeps the bound/current model.
            await foreach (var _ in ChatCoreAsync(message, model: null, attachments))
            {
            }

            // Away-reply push (ADR 0028): an agent-run turn completed with a real reply and no live client
            // was watching. INSIDE the try, after the drain — so a faulted turn (caught below) never
            // notifies. Gated on _modelId so the no-model path (which drains normally after emitting the
            // "configure a provider" notice, ChatCoreAsync) does NOT fire a false "your agent replied";
            // _modelId is set by ChatCoreAsync's re-resolve/rebind if a provider appeared mid-flight.
            if (!string.IsNullOrEmpty(_modelId))
            {
                await SendAwayReplyPushAsync();
            }
        }
        catch (Exception ex)
        {
            // Best-effort: no caller to surface this to (the 202 was already returned). Log and move on,
            // rather than faulting a OneWay grain call into the void.
            _logger.LogError(
                ex, "Agent grain {GrainKey} failed a background (agent-run) turn", this.GetPrimaryKeyString());
        }
    }

    // Publish this turn's usage-dashboard "application" dimension (ADR 0031). An agent chat groups under
    // the agent's name; a regular chat leaves it unset so the ingestion span processor falls back to the
    // service constant. Rides Orleans RequestContext like the user.* identity (ADR 0010) — the processor
    // stamps it onto the in-grain chat/tool spans at OnStart. RequestContext is per-call, so this is
    // naturally turn-scoped (no clear-on-exit hazard like _turnLocation).
    private void PublishUsageApplication()
    {
        if (_state.State.AgentName is { Length: > 0 } name)
        {
            RequestContext.Set(UsageSpanProjector.ApplicationContextKey, name);
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> ChatCoreAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments)
    {
        PublishUsageApplication();

        _logger.LogInformation(
            "Agent grain {GrainKey} received a message ({Length} chars, {Attachments} attachment(s), location: {HasLocation})",
            this.GetPrimaryKeyString(), message.Length, attachments?.Count ?? 0, _turnLocation is not null);

        // No model configured (ADR 0019). The factory is resolved once per activation, so a user who added
        // their FIRST provider after this grain activated still holds the empty factory here — re-resolve to
        // pick it up without a reactivation. If a model is now available, rebind onto it (history preserved)
        // and fall through to the normal turn; if still none, tell the user to configure one and stop —
        // never running the no-op client.
        if (string.IsNullOrEmpty(_modelId))
        {
            _chatClientFactory = await _chatClientFactoryProvider.GetForUserAsync(_userId);
            if (string.IsNullOrEmpty(_chatClientFactory.DefaultModelId))
            {
                yield return new AgentStreamEvent(
                    AgentStreamEventType.TextDelta,
                    Text: "_No model provider is configured. Add one in Preferences → model providers to "
                        + "start chatting._\n\n");
                yield return new AgentStreamEvent(AgentStreamEventType.Done);
                yield break;
            }

            // Honour a model the request picked (the same save could have populated the picker), else the
            // newly-resolved default. Null out `model` so the switch branch below doesn't repeat the rebuild.
            await RebindModelAsync(string.IsNullOrWhiteSpace(model) ? _chatClientFactory.DefaultModelId : model);
            model = null;

            _logger.LogInformation(
                "Agent grain {GrainKey} bound to model {Model} after a provider was configured",
                this.GetPrimaryKeyString(), _modelId);
        }

        if (!string.IsNullOrWhiteSpace(model) && model != _modelId)
        {
            await SwitchModelAsync(model);
        }

        // Image-generation model (ADR 0020): the turn produces an IMAGE, not a chat reply. Branch BEFORE the
        // chat vision-gate below — an image model CONSUMES images (it edits them), so the "withhold from a
        // no-vision model" gate must not fire here, and it runs a different API entirely
        // (IImageGenerationService, not the model's IChatClient). The no-op NoModelChatClient path never
        // reaches here: _modelId is non-empty by now (guarded above).
        if (_chatClientFactory.GetCapabilities(_modelId).SupportsImageGeneration)
        {
            await foreach (var evt in GenerateImageTurnAsync(message, attachments))
            {
                yield return evt;
            }

            yield break;
        }

        // Images are gated on the model's VISION capability, and this gate is not merely about a turn
        // that would fail anyway (ADR 0013). An image reaches the model as a SAS URL into our PRIVATE
        // attachments container (ADR 0004), and the model's host really does fetch that URL server-side
        // (the ADR 0013 spike confirmed it) — so handing it to a third-party endpoint that cannot even
        // read images leaks a live credential-bearing URL for nothing. Gate it HERE, in the grain, not
        // only in the UI: the UI is not a security boundary.
        var canSeeImages = _chatClientFactory.GetCapabilities(_modelId).SupportsVision;

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
        // run has several); accumulate them and report the totals on the Done event.
        var tokens = new TurnTokenTotals();

        // Accumulate the reply text (only) so we can feed the last exchange to the follow-up
        // suggestion call after the turn — cheaper than re-reading the serialized session.
        var reply = new StringBuilder();

        await foreach (AgentResponseUpdate update in _agent.RunStreamingAsync([userMessage], _session))
        {
            // The projection is a plain iterator because it must not be an ASYNC one; usage updates
            // produce no event at all, they only accumulate into `tokens`.
            foreach (var evt in ProjectStreamedUpdate(update, reply, tokens))
            {
                yield return evt;
            }
        }

        await PersistTurnUnlessEphemeralAsync();

        // Consume site 1 of 2 (ADR 0066): if read_web_document ingested during this run, rebuild now that
        // the run is over — the tool could not, because a rebuild swaps _session out from under the
        // framework run iterating it. Covers the live chat path AND /api/agent-run, which drains this
        // method. Site 2 is in ReceiveReminder, which does NOT drain this method.
        await RebuildForIngestedWebDocumentAsync();

        // Follow-up suggestions (interactive path only, opt-out via Preferences). Best-effort: the helper
        // owns its try/catch and returns null on any failure, so a slow/failing suggestion call never breaks
        // the turn. Emitted BEFORE Done, after the reply is fully streamed. Skipped on the agent-run/reminder
        // drain (_interactiveTurn is false there) and on an empty reply. The generator uses a throwaway
        // message list and never touches _session, so nothing is persisted.
        if (await SuggestFollowUpsAsync(message, reply) is { Count: > 0 } suggestions)
        {
            yield return new AgentStreamEvent(AgentStreamEventType.Suggestions, Suggestions: suggestions);
        }

        yield return tokens.ToDoneEvent();
    }

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

        /// <summary>
        /// The terminating <c>Done</c> event. A total of zero is reported as <c>null</c>, not 0: the
        /// browser distinguishes "this provider reported no usage" from "this turn cost nothing".
        /// </summary>
        public AgentStreamEvent ToDoneEvent() => new(
            AgentStreamEventType.Done,
            InputTokens: Input > 0 ? Input : null,
            OutputTokens: Output > 0 ? Output : null,
            CachedTokens: Cached > 0 ? Cached : null);
    }

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
    // the agent-run/reminder drain (_interactiveTurn is false there), when the user has opted out, and
    // on an empty reply. Best-effort beyond that: GenerateFollowUpsAsync owns its try/catch and returns
    // null on any failure, so a slow or failing suggestion call never breaks the turn.
    private async Task<IReadOnlyList<string>?> SuggestFollowUpsAsync(string message, StringBuilder reply)
        => _interactiveTurn && _suggestFollowUpsEnabled && reply.Length > 0
            ? await GenerateFollowUpsAsync(message, reply.ToString())
            : null;

    // Rehosts the existing conversation on a newly picked model: snapshot the session, rebuild the
    // agent against the new client, then restore the session so history carries over.
    private async Task SwitchModelAsync(string model)
    {
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

            case UsageContent usage:
                tokens.Input += usage.Details.InputTokenCount.GetValueOrDefault();
                tokens.Output += usage.Details.OutputTokenCount.GetValueOrDefault();
                tokens.Cached += usage.Details.CachedInputTokenCount.GetValueOrDefault();
                return null;

            default:
                return null;
        }
    }

    // An ephemeral turn (ADR 0034) runs the model but never writes its session item, so the
    // conversation leaves no trace in Cosmos — the incognito guarantee. Skip the whole serialize.
    private async Task PersistTurnUnlessEphemeralAsync()
    {
        if (_ephemeral)
        {
            // The incognito turn is still in the IN-MEMORY session (ADR 0034 guarantees "not
            // persisted, not listed", never "not in memory"), so the next normal turn of this same
            // activation would otherwise re-project it straight into the search index. Consume its
            // ordinals without indexing them — see IndexNewTurnsForSearchAsync (ADR 0061).
            await SkipIndexingForEphemeralTurnAsync();

            _logger.LogInformation("Agent grain {GrainKey} completed an ephemeral turn (not persisted)",
                this.GetPrimaryKeyString());
            return;
        }

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        await _state.WriteStateAsync();

        // Search projection (ADR 0061). No _ephemeral guard needed: the early return above already
        // took the incognito path, so reaching here means the turn is persisted. Best-effort inside.
        await IndexNewTurnsForSearchAsync();

        _logger.LogInformation("Agent grain {GrainKey} completed a turn and persisted its session",
            this.GetPrimaryKeyString());
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
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return Task.FromResult<IReadOnlyList<ChatTurn>>([]);
        }

        var turns = new List<ChatTurn>();
        var userTurnIndex = 0;
        foreach (var message in messages)
        {
            // The two roles are projected separately because they resolve their images differently:
            // a user turn joins TurnAttachments POSITIONALLY (so it must advance the index even when
            // the turn is then skipped), while an assistant turn looks its image up by id.
            var turn = message.Role == ChatRole.User
                ? ProjectUserTurn(message, ref userTurnIndex)
                : message.Role == ChatRole.Assistant ? ProjectAssistantTurn(message) : null;

            if (turn is not null)
            {
                turns.Add(turn);
            }
        }

        return Task.FromResult<IReadOnlyList<ChatTurn>>(turns);
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
        await DeleteSearchIndexForConversationAsync();

        _state.State.ScheduledPrompts.Clear();
        _state.State.DocumentNames.Clear();
        // The web-fetched lists must be cleared alongside DocumentNames, and for the reason that line
        // exists at all: DeactivateOnIdle() below is *on idle*, so a request landing in the window before
        // deactivation would otherwise see a grain whose chunks are already gone but whose
        // WebDocumentNames still gate the retrieval provider AND still name attacker-influenced filenames
        // in the system prompt (ADR 0066).
        _state.State.WebDocumentNames.Clear();
        _state.State.WebDocumentUrls.Clear();
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
        PublishUsageApplication();

        // A scheduled prompt never knows where the user is. This is NOT a no-op: _turnLocation lives
        // for the activation, so without it a reminder firing after a located chat turn would still see
        // that turn's fix and have the model announce the user's position hours later, somewhere else.
        // _ephemeral is cleared for the same activation-scoped reason: a fired reminder always persists.
        // _webDocumentIngested likewise (ADR 0066) — this method has its OWN consume site below, and it
        // must consume this turn's ingest, not one inherited from an abandoned chat stream.
        _turnLocation = null;
        _ephemeral = false;
        _webDocumentIngested = false;

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

        // Refresh prior turns' image URLs before this model call too (a reminder can fire on a
        // long-warm grain whose SAS URLs have since expired) — same reason as ChatAsync.
        await RehydrateAttachmentUrlsAsync(
            _chatClientFactory.GetCapabilities(_modelId).SupportsVision);

        // A reminder-fired turn is a user turn too, so it must push a (always empty) attachment
        // entry to keep TurnAttachments aligned 1:1 with user turns by index (constraint 11).
        AlignTurnAttachmentsToHistory();
        await _agent.RunAsync([new ChatMessage(ChatRole.User, info.Prompt)], _session);
        _state.State.TurnAttachments.Add([]);

        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);

        _state.State.ScheduledPrompts[reminderName] = info with { TimesFired = info.TimesFired + 1 };

        await _state.WriteStateAsync();

        // Search projection (ADR 0061). Deliberately NO _ephemeral guard here — this method clears
        // _ephemeral on entry because a fired reminder always persists, so adding one would be the
        // fresh-guard mistake ADR 0034 warns about. Best-effort inside.
        await IndexNewTurnsForSearchAsync();

        // Consume site 2 of 2 (ADR 0066), and the one that is easy to miss: this method does NOT drain
        // ChatCoreAsync — it calls _agent.RunAsync directly and does its own serialize/write/index —
        // so ChatCoreAsync's consume site never runs for a reminder turn. Without this call, a reminder
        // that ingested a document would persist the name list and lose the rebuild, leaving retrieval
        // detached for the rest of the activation while the instructions claim the document exists.
        await RebuildForIngestedWebDocumentAsync();

        // Away-reply push (ADR 0028): a scheduled prompt fired with no live client watching. After the
        // persist, so a push only ever follows a durably-recorded reply. No _modelId guard needed — the
        // no-model case already early-returned above (a reminder with no provider is skipped entirely).
        // The sender's contract is "never throws", but the reminder's TimesFired is already persisted, so
        // a defensive wrap keeps a future misbehaving sender from faulting an already-recorded reminder
        // (EnqueuePromptAsync's send is likewise inside its own try).
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
        var snapshot = await _agent.SerializeSessionAsync(_session);
        _modelId = modelId;
        _state.State.DeploymentName = modelId;
        _agent = BuildAgent(modelId);
        _session = await _agent.DeserializeSessionAsync(snapshot);

        if (!_chatClientFactory.GetCapabilities(modelId).SupportsTools)
        {
            StripToolHistory();
        }
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
