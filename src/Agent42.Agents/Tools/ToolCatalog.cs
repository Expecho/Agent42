namespace Agent42.Agents;

/// <summary>
/// The curated catalog of user-selectable tool groups (ADR 0021). One logical <b>key</b> maps to one or
/// more registered tools; this is the single source of truth for the <b>static</b> keys, shared by the
/// grain's <c>BuildAgent</c> filter and the <c>GET /api/tools</c> catalog endpoint.
///
/// <para><b>It is no longer the COMPLETE catalog, and that is deliberate.</b> Two groups are appended to
/// <see cref="Descriptors"/> by <c>GET /api/tools</c> rather than living here, because neither is static:
/// MCP keys (<c>mcp:{id}</c> per server — ADR 0026 — and <c>mcp:{id}:{toolName}</c> per tool, both per-user)
/// and per-skill keys (<c>skill:{name}</c> — ADR 0048, extended by ADR 0049). <b>Both are now per-user:</b> the skill keys are the shipped
/// <c>SkillCatalog</c> (read from disk, per-deployment) OVERLAID with the skills that user has uploaded,
/// an upload of the same name winning. Each group is represented here only by a bare <b>wildcard</b> key
/// (<see cref="Mcp"/>, <see cref="Skills"/>) that is a selection sentinel, never a rendered checkbox. The
/// static keys below stay authoritative and centralised.</para>
///
/// <para><b>Always-on, never selectable:</b> <c>get_date</c> and the document RAG <c>TextSearchProvider</c>.
/// <c>get_date</c> is filtered in as always-enabled here; the RAG provider is gated only by document presence
/// (ADR 0008), so it never appears in this catalog.</para>
///
/// <para><b>"Not a tool" and "not selectable" are two different things, and since ADR 0057 they no longer
/// coincide.</b> RAG is both — not a tool, and absent from this catalog. <see cref="Memory"/> is the first key
/// here that is <i>not</i> a tool: it gates an <c>AIContextProvider</c> attached outside <c>BuildAgent</c>'s
/// tool list, owns no entry in <c>ToolNameToKey</c>, and is nevertheless selectable. Do not infer from the RAG
/// sentence above that a non-tool cannot appear in this catalog.</para>
///
/// <para><b>Capability is the outer gate — for TOOLS.</b> This catalog only expresses <i>user intent</i>.
/// Whether a selected tool is actually registered is still decided by the model's
/// <see cref="ModelCapabilities"/> in <c>BuildAgent</c> (a text-only model gets none;
/// <c>code_interpreter</c> needs a hosted-tools model). The intersection — selection ∩ capability — keeps the
/// ADR 0013/0015 gates authoritative.</para>
///
/// <para><b><see cref="Memory"/> is the documented exception to that intersection (ADR 0057).</b> It gates a
/// context provider, not a tool, so it never passes through the <c>SupportsTools</c> filter: a text-only model
/// with <c>memory</c> selected still gets memory. <c>NoToolModel_StillGetsMemory</c> and
/// <c>MemorySelection_IsNotIntersectedWithCapability</c> pin that from the null- and explicit-selection sides
/// respectively. Folding capability into the memory arm fails them and nothing else.</para>
/// </summary>
public static class ToolCatalog
{
    // Logical keys — the selectable set. get_date is deliberately NOT here (always-on, see AlwaysOnDateTool).
    public const string WebSearch = "web_search";
    public const string Location = "location";
    public const string Scheduling = "scheduling";
    public const string CodeInterpreter = "code_interpreter";

    /// <summary>
    /// Long-term memory across conversations (ADR 0057, amends 0021 + 0056) — recall of relevant memories into
    /// the agent's instructions before each model call, and retention of the turn's transcript after it.
    /// Deselecting it attaches no provider at all, so the turn makes <b>zero</b> Hindsight calls: no recall
    /// <i>and</i> no retain, exactly the incognito semantics of ADR 0056. "Recall only, keep retaining" was
    /// considered and rejected — it leaves the always-on egress and the durable-injection risk fully intact
    /// while the UI claims memory is off.
    ///
    /// <para><b>The first key here that is not a tool.</b> It gates an <c>AIContextProvider</c> added outside
    /// <c>BuildAgent</c>'s tool list, so it maps to NO <c>ToolNameToKey</c> entries and must not gain any. That
    /// is safe because the only guard over that map,
    /// <c>EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool</c>, runs name → key only; there is
    /// no reverse guard requiring a <see cref="Descriptors"/> key to own tool names. Contrast
    /// <see cref="Skills"/>, which keeps its three entries precisely because it <i>does</i> have static tool
    /// names.</para>
    ///
    /// <para><b>It is exempt from the selection ∩ capability intersection</b> — see the class remarks. A
    /// text-only model with <c>memory</c> selected still gets memory.</para>
    ///
    /// <para><b>The operator's <c>Hindsight:ApiKey</c> remains the outer gate</b> and the only one that stops
    /// the egress deployment-wide. This key narrows within it; with no key configured, selecting memory still
    /// makes zero calls (<c>UnconfiguredMemory_MakesNoCalls</c>). Deselecting also does <b>not</b> remove
    /// memories already retained — ADR 0056's no-delete-path residual is untouched by it.</para>
    ///
    /// <para><b>This key lands OFF for anyone who has ever pressed Save</b>, exactly as the <c>calendar</c>
    /// key did (ADR 0053, superseded by ADR 0063 — that key and its tools now live outside this repo), and
    /// there is deliberately no migration. <b>For agent chats it is worse than that precedent:</b> <c>BindAgentAsync</c> always materialises a concrete list and a re-bind returns early
    /// (the persona snapshot is immutable, ADR 0003), while the per-conversation popover is hidden on agent
    /// chats — so <i>every already-bound agent conversation</i> has memory off with no in-UI remedy,
    /// permanently for a continuous agent (ADR 0016) short of deleting it. Accepted and documented in ADR 0057:
    /// it fails <b>safe</b>, since the widest egress in the app stays off on pre-existing chats until
    /// deliberately re-enabled. The remedy for regular chats is one press of Save in Preferences → Tools; new
    /// agent chats re-materialise the current key list at bind time and are fine.
    /// <c>LegacyAgentSnapshot_DoesNotEnableMemory</c> pins the no-migration half at grain level.</para>
    /// </summary>
    public const string Memory = "memory";

    /// <summary>
    /// Reading a web-hosted document into the conversation's retrieval index (ADR 0066) — the
    /// <c>read_web_document</c> tool.
    ///
    /// <para><b>Deliberately its own key, not folded into <see cref="WebSearch"/>.</b> That key covers
    /// tools whose outbound request is made by a third party (SearXNG, and context.dev since ADR 0027)
    /// and whose results are transient text. This one dials a model-chosen URL <b>from our own
    /// container</b> and writes durable vector chunks. Those are different things to consent to, so they
    /// are different checkboxes — even though a user who wants one usually wants the other.</para>
    ///
    /// <para>Appended LAST for the same reason <see cref="Memory"/> was: it keeps catalog order stable
    /// for every existing key, so the order-sensitive <c>Sanitize_OrdersStaticFirst_*</c> guards need no
    /// expectation edit. Like every key added after the fact, <b>it lands OFF for anyone who has ever
    /// pressed Save</b> — a stored selection is an explicit list — and there is deliberately no
    /// migration. One press of Save in Preferences → Tools restores it for regular chats; an
    /// already-bound agent chat has no in-UI remedy (ADR 0003's immutable snapshot), exactly as with
    /// <see cref="Memory"/>. It fails safe: the tool that opens a socket from our container stays off
    /// until deliberately enabled.</para>
    /// </summary>
    public const string ReadWebDocument = "read_web_document";

    /// <summary>
    /// Model-authored push notifications (ADR 0075) — the <c>send_push_notification</c> tool.
    ///
    /// <para><b>The key and the tool name deliberately differ</b>, unlike <see cref="ReadWebDocument"/>
    /// where a one-tool key is spelled identically to its tool. The key is a <b>consent noun</b> rendered
    /// in the picker ("Push notifications" — the thing you are switching on); the tool name is an
    /// <b>imperative</b> the model reads. <c>send_push_notification</c> as a checkbox key would read as a
    /// verb in a list of nouns, and <c>push_notification</c> as a tool name invites the model to treat it
    /// as a topic rather than an action. <see cref="Location"/> and <see cref="Scheduling"/> already
    /// establish that a key need not equal a tool name.</para>
    ///
    /// <para>Distinct from ADR 0028's away-reply push, which is not a tool and is not selectable: that one
    /// fires by itself on the two async paths with fixed text. This key governs only the notifications the
    /// <i>model</i> writes and asks for. Turning it off leaves the away-reply push untouched.</para>
    ///
    /// <para>Appended LAST for the same reason <see cref="Memory"/> and <see cref="ReadWebDocument"/> were:
    /// it keeps catalog order stable for every existing key, so the order-sensitive
    /// <c>Sanitize_OrdersStaticFirst_*</c> guards need no expectation edit. Like every key added after the
    /// fact, <b>it lands OFF for anyone who has ever pressed Save</b> — a stored selection is an explicit
    /// list — and there is deliberately no migration. One press of Save in Preferences → Tools restores it
    /// for regular chats; an already-bound agent chat has no in-UI remedy (ADR 0003's immutable snapshot).
    /// It fails safe: a tool that can put model-authored text on the user's lock screen stays off until
    /// deliberately enabled.</para>
    /// </summary>
    public const string PushNotification = "push_notification";

    /// <summary>
    /// Model-created artifacts — the <c>create_artifact</c>, <c>update_artifact</c> and <c>read_artifact</c> tools: a
    /// standalone HTML page, an SVG, a long markdown document or a code file the user opens in a side pane, with
    /// versions. Appended LAST for the order-stability reason <see cref="PushNotification"/> gives, and like every key
    /// added after the fact it <b>lands OFF for anyone who has ever pressed Save</b> (ADR 0021). A null binding (a
    /// stored agent with no tool list, <c>/api/agent-run</c>, an MCP-server consumer) gets it ON, which is accepted:
    /// the tools return ids in text and the artifacts stay viewable in that conversation.
    /// </summary>
    public const string Artifacts = "artifacts";

    /// <summary>
    /// Sequential reading of this conversation's ingested documents (ADR 0098) — the <c>read_document</c> tool, a
    /// one-tool key spelled like its tool, as <see cref="ReadWebDocument"/> is. Inserted before
    /// <see cref="BrowserTools"/>, which a guard pins as the last entries, and like every key added after the fact
    /// it <b>lands OFF for anyone who has ever pressed Save</b> (ADR 0021). It reads only this conversation's own
    /// chunks and sends nothing anywhere. Copilot chats never get it: they hold no documents (see
    /// <c>CopilotFunctions</c>).
    /// </summary>
    public const string ReadDocument = "read_document";

    /// <summary>
    /// The client tools (ADR 0089) — <c>confirm_action</c>, <c>control_artifact_pane</c> and
    /// <c>get_browser_context</c>, which the BROWSER runs. One key for all three: they are one consent ("let the
    /// assistant use my open browser tab"), and each answers "unavailable" wherever no browser is watching.
    /// Appended LAST for the order-stability reason <see cref="PushNotification"/> gives, and like every key added
    /// after the fact it <b>lands OFF for anyone who has ever pressed Save</b> (ADR 0021). Copilot chats never get
    /// these tools, whatever the selection (ADR 0085's tool thread; see <c>CopilotFunctions</c>).
    /// </summary>
    public const string BrowserTools = "browser_tools";

    /// <summary>
    /// Shared agent↔UI state (ADR 0089) — the <c>update_shared_state</c> tool, and with it the injection of the
    /// state into the model's instructions. Appended LAST, OFF for anyone who has ever pressed Save (ADR 0021), and
    /// never offered to a Copilot chat.
    /// </summary>
    public const string SharedState = "shared_state";

    /// <summary>
    /// Recall across conversations (ADR 0112) — <c>search_past_chats</c> and <c>read_past_chat</c>, which read the
    /// user's own chat-search projection (ADR 0061). One key for both: searching without reading is rarely useful,
    /// and they are one consent ("let the assistant look through my other chats"). Appended LAST for the
    /// order-stability reason <see cref="PushNotification"/> gives, and like every key added after the fact it
    /// <b>lands OFF for anyone who has ever pressed Save</b> (ADR 0021). An <c>ask_agent</c> binding strips it
    /// (ADR 0070), as it strips <see cref="Memory"/>, for the same reason.
    /// </summary>
    public const string PastChats = "past_chats";

    /// <summary>
    /// The agent-skills <b>wildcard</b> ("all skills") meta-key (per-skill selectable tools, amends 0021).
    /// Each skill is individually selectable as a per-skill key <c>skill:{name}</c> (see
    /// <see cref="SkillKey"/>) — both the ones shipped in <see cref="SkillCatalog"/> and the ones the user
    /// has uploaded (ADR 0049), which the grain merges into one catalog before this gate ever sees them, so
    /// a key cannot tell the two layers apart and must not; this bare <c>skills</c> key is the wildcard the grain
    /// honours in <c>BuildAgent</c> (<c>enabled.Contains(Skills) || enabled.Contains(skill:{name})</c>). Exactly
    /// like <see cref="Mcp"/>, it is deliberately <b>kept in</b> <see cref="AllSelectableToolKeys"/> (so the
    /// <c>null == all</c> default and any legacy stored selection carrying literal <c>"skills"</c> keep enabling
    /// every skill, with no migration) but <b>removed from</b> <see cref="Descriptors"/> (it is no longer a
    /// single UI checkbox — the picker renders one box per skill). Unlike <see cref="Mcp"/> it DOES keep its
    /// entries in <see cref="ToolNameToKey"/>: the provider's three tool names are static and known, so the
    /// "every registered tool maps to a catalog key" guard covers them through this key.
    /// </summary>
    public const string Skills = "skills";

    /// <summary>The prefix of a per-skill selection key: <c>skill:{name}</c>.</summary>
    public const string SkillKeyPrefix = "skill:";

    /// <summary>
    /// The remote-MCP-server <b>wildcard</b> ("all servers") meta-key (ADR 0026, amends 0024). Each MCP server
    /// is individually selectable as a per-server key <c>mcp:{serverId}</c> (see <see cref="McpServerKey"/>),
    /// and each of that server's tools as a per-tool key <c>mcp:{serverId}:{toolName}</c> (see
    /// <see cref="McpToolKey"/>) — a three-level hierarchy this bare <c>mcp</c> key sits at the top of, and
    /// which the grain honours in <c>BuildAgent</c> via <see cref="IsMcpToolEnabled"/>. It is deliberately <b>kept in</b>
    /// <see cref="AllSelectableToolKeys"/> (so the <c>null == all</c> default and any legacy stored selection
    /// carrying literal <c>"mcp"</c> keep enabling every server, with no migration) but <b>removed from</b>
    /// <see cref="Descriptors"/> (it is no longer a single UI checkbox — the picker renders one box per server).
    /// Like the per-server keys, it maps to NO static tool names (MCP tool names are user-defined and dynamic),
    /// so it is absent from <see cref="ToolNameToKey"/>; MCP tools stay the documented group-gated exception to
    /// the "every registered tool maps to a catalog key" guard.
    /// </summary>
    public const string Mcp = "mcp";

    /// <summary>
    /// The prefix of a per-server MCP selection key: <c>mcp:{serverId}</c> (ADR 0026). Also the prefix of a
    /// per-tool key <c>mcp:{serverId}:{toolName}</c>, which is a per-server key with a second segment appended
    /// — see <see cref="McpToolKey"/>.
    /// </summary>
    public const string McpServerKeyPrefix = "mcp:";

    /// <summary>The always-on date tool that no selection can disable.</summary>
    public const string AlwaysOnDateTool = "get_date";

    /// <summary>
    /// Every selectable key, in display order. This is the <c>null == all</c> fallback set: a null
    /// <c>EnabledTools</c> anywhere (user / agent / conversation) resolves to exactly this, preserving
    /// today's "every tool on" behaviour with no migration.
    /// </summary>
    public static IReadOnlyList<string> AllSelectableToolKeys { get; } =
        [
            WebSearch, Location, Scheduling, Skills, CodeInterpreter, Mcp, Memory, ReadWebDocument, PushNotification,
            Artifacts, ReadDocument, BrowserTools, SharedState, PastChats,
        ];

    // Maps a REGISTERED tool's Name to its logical key. get_date is deliberately absent (always-on).
    // web_search is intentionally the key for BOTH the SearXNG shims and HostedWebSearchTool — the hosted
    // tool surfaces under the same protocol name `web_search` (ADR 0015), so one key controls both wire
    // realizations. The three AgentSkillsProvider tools (load_skill / read_skill_resource / run_skill_script)
    // are mapped so the key-coverage guard test stays green wherever the provider surfaces them; the provider
    // itself is gated in BuildAgent by whether the selection resolves to AT LEAST ONE skill path (per-skill
    // selectable tools — no longer a bare enabled.Contains(Skills)), not by this map: they never enter the
    // filtered list.
    private static readonly Dictionary<string, string> ToolNameToKey = new(StringComparer.Ordinal)
    {
        [WebSearch] = WebSearch,
        ["fetch_web_page"] = WebSearch,
        [ReadWebDocument] = ReadWebDocument,
        [ReadDocument] = ReadDocument,
        ["send_push_notification"] = PushNotification,
        ["create_artifact"] = Artifacts,
        ["update_artifact"] = Artifacts,
        ["read_artifact"] = Artifacts,
        [ClientToolLimits.ConfirmAction] = BrowserTools,
        [ClientToolLimits.ControlArtifactPane] = BrowserTools,
        [ClientToolLimits.GetBrowserContext] = BrowserTools,
        [SharedStateLimits.UpdateToolName] = SharedState,
        [PastChatLimits.SearchToolName] = PastChats,
        [PastChatLimits.ReadToolName] = PastChats,
        [CodeInterpreter] = CodeInterpreter,
        [ClientToolLimits.GetCurrentLocation] = Location,
        ["schedule_prompt"] = Scheduling,
        ["cancel_scheduled_prompt"] = Scheduling,
        ["cancel_all_scheduled_prompts"] = Scheduling,
        ["load_skill"] = Skills,
        ["read_skill_resource"] = Skills,
        ["run_skill_script"] = Skills,
    };

    /// <summary>
    /// The UI-facing catalog (ADR 0021): each selectable key with a label and whether it needs a
    /// hosted-tools model, so the picker can grey out <c>code_interpreter</c> on a non-hosted model.
    /// </summary>
    public static IReadOnlyList<ToolDescriptor> Descriptors { get; } =
    [
        new(WebSearch, "Web search", RequiresHostedTools: false,
            Description: "Searches the web and fetches pages through the SearXNG sidecar."),
        new(Location, "Location", RequiresHostedTools: false,
            Description: "Shares your device location and street address with the model."),
        new(Scheduling, "Scheduling", RequiresHostedTools: false,
            Description: "Lets a chat schedule a prompt to run later, on its own."),
        new(CodeInterpreter, "Code interpreter", RequiresHostedTools: true,
            Description: "Runs Python in a hosted sandbox. Needs a Responses-API model."),
        // Skills is NOT here — skills surface as per-skill descriptors appended by GET /api/tools (ADR 0048),
        // and since ADR 0049 that list is PER-USER: the shipped set overlaid with the user's uploads. The
        // bare `skills` wildcard is a selection sentinel only, never a rendered checkbox.
        // Mcp is NOT here — MCP servers surface as per-user, per-server descriptors appended by GET /api/tools
        // (ADR 0026). The bare `mcp` wildcard is a selection sentinel only, never a rendered checkbox.
        //
        // Memory is LAST deliberately (ADR 0057): appending keeps catalog order stable for every existing key,
        // so the order-sensitive Sanitize_OrdersStaticFirst_* guards need no expectation edit. The label is
        // "Long-term memory", not "Memory" — plain "Memory" reads as the conversation's own history (the ADR
        // 0004 session), which this is not, and that misreading makes the checkbox look like it wipes the chat.
        // RequiresHostedTools is false: memory is not a tool at all, so no capability can grey it out.
        new(Memory, "Long-term memory", RequiresHostedTools: false,
            Description: "Recalls and retains memories across your conversations, via a third party."),
        // ReadWebDocument is LAST for the same reason Memory was — appending keeps catalog order stable
        // for every existing key, so the order-sensitive Sanitize_OrdersStaticFirst_* guards need no
        // expectation edit. RequiresHostedTools is false: it registers on BOTH capability branches,
        // because it is document ingestion and no hosted tool provides that (it is not part of ADR 0015's
        // swap). The description names what makes it a separate consent from Web search: this one fetches
        // from OUR container and keeps what it finds.
        new(ReadWebDocument, "Read web documents", RequiresHostedTools: false,
            Description: "Fetches a PDF or Office document from a link into this chat's searchable context."),
        // PushNotification is LAST for the same order-stability reason as the two above.
        // RequiresHostedTools is false: it registers on BOTH capability branches, because notifying the
        // user is not something any hosted tool provides (it is not part of ADR 0015's swap). The
        // description names the thing being consented to — the model choosing to interrupt you — rather
        // than the transport, and says where it lands, because that is what makes it different from a
        // reply appearing in the chat.
        new(PushNotification, "Push notifications", RequiresHostedTools: false,
            Description: "Lets the assistant send a notification to your devices when something is worth interrupting you for."),
        // Artifacts is LAST for the same order-stability reason. RequiresHostedTools is false: no hosted tool
        // provides it, so it registers on both capability branches.
        new(Artifacts, "Artifacts", RequiresHostedTools: false,
            Description: "Lets the assistant build pages, apps, diagrams and documents you open in a side pane, with versions."),
        // ReadDocument (ADR 0098) sits before BrowserTools, which a guard pins right after it.
        // RequiresHostedTools is false: no hosted tool reads this conversation's own chunks.
        new(ReadDocument, "Read whole documents", RequiresHostedTools: false,
            Description: "Lets the assistant read a document in this chat from start to end, part by part, for summaries and complete lists."),
        // BrowserTools and SharedState were appended for the same order-stability reason (ADR 0089).
        new(BrowserTools, "Browser tools", RequiresHostedTools: false,
            Description: "Lets the assistant ask you to confirm actions, open artifacts in the pane and read your time zone and selected text."),
        new(SharedState, "Shared state", RequiresHostedTools: false,
            Description: "Gives the chat a checklist and notes the assistant and you can both edit."),
        // PastChats (ADR 0112) is LAST for the same order-stability reason. RequiresHostedTools is false: no
        // hosted tool reads the user's own chat history.
        new(PastChats, "Past chats", RequiresHostedTools: false,
            Description: "Lets the assistant search and read your other conversations to recall what was said."),
    ];

    /// <summary>The logical key for a registered tool name, or null (e.g. <c>get_date</c>, or an unmapped tool).</summary>
    public static string? KeyFor(string toolName) => ToolNameToKey.TryGetValue(toolName, out var key) ? key : null;

    /// <summary>The per-server selection key for an MCP server id: <c>mcp:{serverId}</c> (ADR 0026).</summary>
    public static string McpServerKey(string serverId) => McpServerKeyPrefix + serverId;

    /// <summary>
    /// True when a key is <b>any</b> prefixed MCP key — a per-server one (<c>mcp:{id}</c>) or a per-tool one
    /// (<c>mcp:{id}:{toolName}</c>) — as opposed to the bare <c>mcp</c> wildcard. Deliberately a prefix test and
    /// not a parser: per-tool keys pass through <see cref="Sanitize"/> on this predicate with no code change,
    /// which is the whole reason the per-tool key was shaped as a per-server key plus a segment.
    /// </summary>
    public static bool IsMcpServerKey(string key) => key.StartsWith(McpServerKeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The per-tool selection key for one tool of one MCP server: <c>mcp:{serverId}:{toolName}</c>. Server ids
    /// are 32 hex chars (<c>Guid.NewGuid().ToString("n")</c>) and carry no colon, but nothing in this codebase
    /// relies on that by splitting: gating and expansion <b>construct</b> a key from a known
    /// <c>(serverId, toolName)</c> pair and test set membership. Do not add a parser.
    /// </summary>
    public static string McpToolKey(string serverId, string toolName)
        => McpServerKeyPrefix + serverId + ":" + toolName;

    /// <summary>
    /// True when a server's tools should be kept given the effective selection (ADR 0026): the selection
    /// contains either the bare <see cref="Mcp"/> wildcard (the <c>null == all</c> default or a legacy
    /// all-servers selection) OR this server's own <c>mcp:{serverId}</c> key. Capability (<c>SupportsTools</c>)
    /// is the outer gate applied by the caller, exactly as for the static keys.
    ///
    /// <para><b>Subsumed by <see cref="IsMcpToolEnabled"/> and kept anyway.</b> The per-tool gate is this test
    /// OR-ed with one more term, so <c>BuildAgent</c> calls that one instead. This overload stays because it is
    /// the honest predicate wherever a whole server — not a named tool — is the subject, and callers that have
    /// no tool name in hand should not have to invent one.</para>
    /// </summary>
    public static bool IsMcpServerEnabled(string serverId, ISet<string> enabled)
        => enabled.Contains(Mcp) || enabled.Contains(McpServerKey(serverId));

    /// <summary>
    /// True when one named tool of one MCP server should be kept given the effective selection — the third and
    /// narrowest level of the hierarchy <c>mcp</c> ⊃ <c>mcp:{serverId}</c> ⊃ <c>mcp:{serverId}:{toolName}</c>.
    /// Any of the three grants it: the bare wildcard (<c>null == all</c> / legacy), the server's own key (every
    /// tool of that server — its ADR 0026 meaning, unchanged), or this tool's own key.
    ///
    /// <para>Capability (<c>SupportsTools</c>) is the outer gate applied by the caller, exactly as for the
    /// static keys — selection can only narrow, never grant.</para>
    /// </summary>
    public static bool IsMcpToolEnabled(string serverId, string toolName, ISet<string> enabled)
        => IsMcpServerEnabled(serverId, enabled) || enabled.Contains(McpToolKey(serverId, toolName));

    /// <summary>The per-skill selection key for a skill's frontmatter name: <c>skill:{name}</c>.</summary>
    public static string SkillKey(string name) => SkillKeyPrefix + name;

    /// <summary>True when a key is a per-skill key (<c>skill:{name}</c>), not the bare <c>skills</c> wildcard.</summary>
    public static bool IsSkillKey(string key) => key.StartsWith(SkillKeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when a skill should be handed to the provider given the effective selection: the selection
    /// contains either the bare <see cref="Skills"/> wildcard (the <c>null == all</c> default or a legacy
    /// all-skills selection) OR this skill's own <c>skill:{name}</c> key. Capability (<c>SupportsTools</c>) is
    /// the outer gate applied by the caller, exactly as for the static keys and MCP servers.
    ///
    /// <para>Deliberately blind to WHERE the skill came from: the caller passes names from the merged
    /// shipped ⊕ uploaded catalog (ADR 0049), and a <c>skill:{name}</c> key means the same thing either
    /// way — which is what lets a user's upload shadow a shipped skill under one key rather than two.</para>
    /// </summary>
    public static bool IsSkillEnabled(string name, ISet<string> enabled)
        => enabled.Contains(Skills) || enabled.Contains(SkillKey(name));

    /// <summary>
    /// True when a tool with this registered name should be kept given the effective selection.
    /// <c>get_date</c> is always kept; every other tool is kept only if its key is in <paramref name="enabled"/>.
    /// A tool whose name is neither <c>get_date</c> nor in the catalog is kept too — an unmapped tool is not a
    /// selectable one and must not be silently stripped (that would be an unnoticed regression).
    /// </summary>
    public static bool IsToolEnabled(string toolName, ISet<string> enabled)
        => KeyFor(toolName) is not { } key || enabled.Contains(key);

    /// <summary>
    /// Server-side authority over a stored selection: drops unknown keys and de-dups, preserving catalog order.
    /// Null passes through unchanged (the <c>null == all</c> sentinel). Called by the grain before persisting a
    /// selection so state can never hold a garbage or client-invented key.
    ///
    /// <para>Per-skill keys (<c>skill:{name}</c>) and prefixed MCP keys — per-server (<c>mcp:{id}</c>, ADR
    /// 0026) <b>and</b> per-tool (<c>mcp:{id}:{toolName}</c>) alike — are passed through after the static keys,
    /// in input order, deduped. Per-tool keys need no clause of their own: they start with <c>mcp:</c>, so
    /// <see cref="IsMcpServerKey"/> already admits them. Sanitize is deliberately <b>static</b> (no per-user
    /// server list, no disk read for the skill list, and no MCP connection), so it validates neither a server
    /// id, nor a tool name, nor a skill name here. A stale <c>mcp:{deletedId}</c>,
    /// <c>mcp:{id}:{removedTool}</c> or <c>skill:{removedName}</c> is inert — it gates nothing in
    /// <c>BuildAgent</c>.</para>
    ///
    /// <para><b>Self-healing is NOT universal, and the exception is deliberate.</b> A stale key normally
    /// clears when the user next saves from the picker, which only offers current entries. But
    /// <c>readToolChecks</c> deliberately <b>carries forward</b> a <c>mcp:{id}:{tool}</c> key whose SERVER
    /// row is still in the catalog while its tool row was not rendered — that is the backstop against
    /// silently destroying a per-tool selection the picker could not display. So a per-tool key for a live
    /// server persists until that server's tools are known again; only keys whose server row is gone
    /// (deleted or management-disabled) clear on the next save. Both halves are intentional; do not "fix"
    /// either into the other.</para>
    /// </summary>
    public static IReadOnlyList<string>? Sanitize(IReadOnlyList<string>? tools)
        => tools is null
            ? null
            : [
                .. AllSelectableToolKeys.Where(tools.Contains),
                .. tools.Where(IsSkillKey).Distinct(),
                .. tools.Where(IsMcpServerKey).Distinct(),
            ];
}

/// <summary>
/// One entry in the selectable-tools catalog surfaced by <c>GET /api/tools</c> (ADR 0021):
/// the logical key, a display label, and whether the tool requires a hosted-tools model.
///
/// <para>A <b>response DTO only</b> — never grain state, so <paramref name="ParentKey"/> needs no
/// <c>[Id]</c> and no migration.</para>
/// </summary>
/// <param name="ParentKey">
/// The key of the row this one nests under, or null for a top-level row. Set to <c>mcp:{serverId}</c> on the
/// per-tool MCP descriptors so the picker can <b>partition on this field</b> rather than string-splitting keys
/// — a <c>startsWith("mcp:")</c> test cannot tell a server row from one of its tool rows.
/// </param>
/// <param name="Description">
/// One short sentence under the label in the picker, or null to render label-only. The <b>skill</b> and
/// <b>MCP</b> descriptors appended per-user by <c>GET /api/tools</c> leave this null on purpose: their labels
/// are user-uploaded skill names and server-supplied tool names, so there is no description to give.
/// </param>
public sealed record ToolDescriptor(
    string Key,
    string Label,
    bool RequiresHostedTools,
    string? ParentKey = null,
    string? Description = null);
