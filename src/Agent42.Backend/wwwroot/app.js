/* Agent 42 console — conversation sidebar, streaming NDJSON chat, markdown rendering. */
(() => {
  "use strict";

  const conversationList = document.getElementById("conversationList");
  const newChatBtn = document.getElementById("newChatBtn");
  const thread = document.getElementById("thread");
  const chatScroll = document.getElementById("chatScroll");
  const emptyState = document.getElementById("emptyState");
  const form = document.getElementById("composerForm");
  const input = document.getElementById("promptInput");
  const sendBtn = document.getElementById("sendBtn");
  const attachBtn = document.getElementById("attachBtn");
  const micBtn = document.getElementById("micBtn");
  const voiceBtn = document.getElementById("voiceBtn");
  const fileInput = document.getElementById("fileInput");
  const attachmentPreview = document.getElementById("attachmentPreview");
  const modelSelect = document.getElementById("modelSelect");
  const modelField = document.getElementById("modelField");
  const noModelHint = document.getElementById("noModelHint");
  const agentLabel = document.getElementById("agentLabel");
  const menuToggle = document.getElementById("menuToggle");
  const backdrop = document.getElementById("backdrop");
  const app = document.querySelector(".app");

  // Token-usage dashboard (ADR 0031) — a dedicated full-page overlay, opened from the sidebar footer.
  const openUsageBtn = document.getElementById("openUsageBtn");
  const usageView = document.getElementById("usageView");
  const usageCloseBtn = document.getElementById("usageCloseBtn");
  const usageApplication = document.getElementById("usageApplication");
  const usageUser = document.getElementById("usageUser");
  const usageRange = document.getElementById("usageRange");
  const usageRefresh = document.getElementById("usageRefresh");
  const usageTiles = {
    toolCalls: document.getElementById("usageTileToolCalls"),
    input: document.getElementById("usageTileInput"),
    output: document.getElementById("usageTileOutput"),
    cached: document.getElementById("usageTileCached"),
    calls: document.getElementById("usageTileCalls"),
    errors: document.getElementById("usageTileErrors"),
  };
  const usageModelsBody = document.getElementById("usageModelsBody");
  const usageModelsEmpty = document.getElementById("usageModelsEmpty");
  const usageToolsBody = document.getElementById("usageToolsBody");
  const usageToolsEmpty = document.getElementById("usageToolsEmpty");
  const usageUsersBody = document.getElementById("usageUsersBody");
  const usageUsersEmpty = document.getElementById("usageUsersEmpty");
  // Handle for the auto-refresh timer, so a new interval selection (or closeUsage) can cancel the
  // previous one — a leaked timer would keep polling /api/usage from the console view.
  let usageRefreshTimer = null;

  const agentList = document.getElementById("agentList");
  const addAgentBtn = document.getElementById("addAgentBtn");
  const agentDialog = document.getElementById("agentDialog");
  const agentForm = document.getElementById("agentForm");
  const agentFormTitle = document.getElementById("agentFormTitle");
  const agentNameInput = document.getElementById("agentName");
  const agentDescriptionInput = document.getElementById("agentDescription");
  const agentModelSelect = document.getElementById("agentModelSelect");
  const agentSystemPromptInput = document.getElementById("agentSystemPrompt");
  const agentContinuousInput = document.getElementById("agentContinuous");
  const agentKeySection = document.getElementById("agentKeySection");
  const agentRegenerateKeyBtn = document.getElementById("agentRegenerateKeyBtn");
  const agentKeyReveal = document.getElementById("agentKeyReveal");
  const agentKeyValue = document.getElementById("agentKeyValue");
  const agentKeyCopyBtn = document.getElementById("agentKeyCopyBtn");
  const agentRunUrl = document.getElementById("agentRunUrl");
  const agentRunUrlCopyBtn = document.getElementById("agentRunUrlCopyBtn");
  const agentCancelBtn = document.getElementById("agentCancelBtn");

  const openProfileBtn = document.getElementById("openProfileBtn");
  const profileDialog = document.getElementById("profileDialog");
  const profileForm = document.getElementById("profileForm");
  const profileLanguage = document.getElementById("profileLanguage");
  const profileTheme = document.getElementById("profileTheme");
  const profileSuggest = document.getElementById("profileSuggest");
  const profileCancelBtn = document.getElementById("profileCancelBtn");
  const locationEnableBtn = document.getElementById("locationEnableBtn");
  const locationStatus = document.getElementById("locationStatus");
  const pushSection = document.getElementById("pushSection");
  const pushStatus = document.getElementById("pushStatus");
  const pushToggleBtn = document.getElementById("pushToggleBtn");
  // Preferences + agent dialog tabs. wireTabs (defined near openProfileDialog) takes [tab, panel]
  // pairs in tablist order, attaches the click + keyboard nav, and returns a select(index, focus).
  // The Configuration tab is APPENDED LAST on purpose: initMcpOAuthReturn deep-links to select(3) (the
  // MCP tab), so MCP must stay index 3 — a new tab inserted before it would shift MCP to 4 and break the
  // OAuth return. Append → Configuration is index 4, MCP stays 3.
  const profileTabs = wireTabs([
    [document.getElementById("tabGeneral"), document.getElementById("panelGeneral")],
    [document.getElementById("tabTools"), document.getElementById("panelTools")],
    [document.getElementById("tabProviders"), document.getElementById("panelProviders")],
    [document.getElementById("tabMcp"), document.getElementById("panelMcp")],
    [document.getElementById("tabConfig"), document.getElementById("panelConfig")],
  ]);
  const agentTabs = wireTabs([
    [document.getElementById("tabAgentGeneral"), document.getElementById("panelAgentGeneral")],
    [document.getElementById("tabAgentTools"), document.getElementById("panelAgentTools")],
    [document.getElementById("tabAgentApi"), document.getElementById("panelAgentApi")],
  ]);

  const defaultModelSelect = document.getElementById("defaultModelSelect");
  const embeddingModelSelect = document.getElementById("embeddingModelSelect");
  const providerList = document.getElementById("providerList");
  const addProviderBtn = document.getElementById("addProviderBtn");
  const providerDialog = document.getElementById("providerDialog");
  const providerForm = document.getElementById("providerForm");
  const providerFormTitle = document.getElementById("providerFormTitle");
  const providerKeyInput = document.getElementById("providerKey");
  const providerKindSelect = document.getElementById("providerKind");
  const providerDisplayNameInput = document.getElementById("providerDisplayName");
  const providerEndpointInput = document.getElementById("providerEndpoint");
  const providerApiKeyInput = document.getElementById("providerApiKey");
  const providerResponsesField = document.getElementById("providerResponsesField");
  const providerResponsesApi = document.getElementById("providerResponsesApi");
  const providerModelList = document.getElementById("providerModelList");
  const addModelRowBtn = document.getElementById("addModelRowBtn");
  const providerError = document.getElementById("providerError");
  const providerCancelBtn = document.getElementById("providerCancelBtn");

  // Remote MCP servers (config-driven remote MCP servers) — mirrors the provider dialog.
  const mcpServerList = document.getElementById("mcpServerList");
  const addMcpServerBtn = document.getElementById("addMcpServerBtn");
  const configHealthList = document.getElementById("configHealthList");
  const runConfigProbesBtn = document.getElementById("runConfigProbesBtn");
  const mcpDialog = document.getElementById("mcpDialog");
  const mcpForm = document.getElementById("mcpForm");
  const mcpFormTitle = document.getElementById("mcpFormTitle");
  const mcpNameInput = document.getElementById("mcpName");
  const mcpEndpointInput = document.getElementById("mcpEndpoint");
  const mcpEnabledInput = document.getElementById("mcpEnabled");
  const mcpAuthSelect = document.getElementById("mcpAuth");
  const mcpHeaderNameField = document.getElementById("mcpHeaderNameField");
  const mcpHeaderNameInput = document.getElementById("mcpHeaderName");
  const mcpHeaderValueField = document.getElementById("mcpHeaderValueField");
  const mcpHeaderValueInput = document.getElementById("mcpHeaderValue");
  const mcpAuthorityField = document.getElementById("mcpAuthorityField");
  const mcpAuthorityInput = document.getElementById("mcpAuthority");
  const mcpClientIdField = document.getElementById("mcpClientIdField");
  const mcpClientIdInput = document.getElementById("mcpClientId");
  const mcpClientSecretField = document.getElementById("mcpClientSecretField");
  const mcpClientSecretInput = document.getElementById("mcpClientSecret");
  const mcpScopesField = document.getElementById("mcpScopesField");
  const mcpScopesInput = document.getElementById("mcpScopes");
  const mcpOAuthNote = document.getElementById("mcpOAuthNote");
  const mcpError = document.getElementById("mcpError");
  const mcpCancelBtn = document.getElementById("mcpCancelBtn");

  // Selectable tools (ADR 0021): checkbox groups in the agent + preferences dialogs, and a per-chat popover.
  const agentToolChecks = document.getElementById("agentToolChecks");
  const profileToolChecks = document.getElementById("profileToolChecks");
  const toolsBtn = document.getElementById("toolsBtn");
  const incognitoBtn = document.getElementById("incognitoBtn");
  const toolsDialog = document.getElementById("toolsDialog");
  const chatToolChecks = document.getElementById("chatToolChecks");
  const toolsDoneBtn = document.getElementById("toolsDoneBtn");

  // Web Share Target picker (Android PWA, ADR 0023): a shared payload lands on a /?share=1 load.
  const shareDialog = document.getElementById("shareDialog");
  const shareAgentList = document.getElementById("shareAgentList");
  const shareNewChatBtn = document.getElementById("shareNewChatBtn");
  const shareCancelBtn = document.getElementById("shareCancelBtn");

  // Read-only share link (ADR 0035): mint/copy/revoke a public, no-login link to one conversation.
  // Distinct from the ADR 0023 Web Share Target above (which shares INTO the app).
  const shareLinkDialog = document.getElementById("shareLinkDialog");
  const shareLinkConvTitle = document.getElementById("shareLinkConvTitle");
  const shareLinkCreateBtn = document.getElementById("shareLinkCreateBtn");
  const shareLinkResult = document.getElementById("shareLinkResult");
  const shareLinkUrl = document.getElementById("shareLinkUrl");
  const shareLinkCopyBtn = document.getElementById("shareLinkCopyBtn");
  const shareLinkRevokeBtn = document.getElementById("shareLinkRevokeBtn");
  const shareLinkCloseBtn = document.getElementById("shareLinkCloseBtn");
  let shareLinkConvId = null;

  let conversations = [];
  let activeId = crypto.randomUUID();
  // Incognito flag for the active chat (ADR 0034). Fixed at chat creation: settable only on a fresh,
  // empty regular chat, then locked once the first message is sent (like the conversation id itself).
  let ephemeral = false;
  let streaming = false;
  let defaultModel = null;
  let modelsData = { models: [], default: null };

  // Images uploaded for the turn being composed: {id, mediaType, fileName} from the upload
  // endpoint. Sent as attachmentIds on the next send, then cleared. Reset on conversation switch.
  let pendingAttachments = [];

  // Documents uploaded to this conversation: {kind, fileName, chunkCount}. Display-only — a
  // document is ingest-only (indexed server-side for automatic retrieval, ADR 0007/0008), so it
  // is NEVER sent back as an attachmentId. The chip is a "just indexed" indicator; a chunkCount of
  // 0 means no text could be extracted (e.g. a scanned PDF), so it renders as a warning instead —
  // the server did NOT register that document with the grain, so the agent stays unaware of it.
  let pendingDocuments = [];

  // Non-OK uploads for the turn being composed: {fileName, message}. Rendered as dismissible error
  // chips so a rejected/failed upload is surfaced truthfully instead of silently swallowed. Reset
  // on conversation switch, like the other pending arrays.
  let pendingNotices = [];

  let agents = [];
  // The user's own configured providers (their overlay — ADR 0018), fetched masked (hasKey, never the
  // key). Rendered in Preferences; edited through the provider dialog. The key of the one being edited,
  // or null when adding a new provider.
  let userProviders = [];
  let editingProviderKey = null;
  // The user's STORED default-model preference (GET /api/providers `default`), as opposed to `defaultModel`
  // above, which is the default the server actually RESOLVED (/api/models). They differ when the stored id
  // names a provider that no longer resolves — the resolver falls through, but the preference is kept.
  let userDefaultModel = null;
  // The user's default embedding model id, or null when none is configured. When null, attaching documents
  // is blocked in the composer (RAG needs an embedding model). Independent of userDefaultModel (chat).
  let userDefaultEmbeddingModel = null;
  // The user's own remote MCP servers (config-driven remote MCP servers), fetched masked (hasHeaderValue,
  // never the secret). The id of the one being edited, or null when adding a new server.
  let mcpServers = [];
  let editingMcpId = null;
  // The agent id to *bind*, sent on the wire only on the first turn of a freshly started
  // agent chat, then cleared. Kept distinct from activeAgentId so a resumed agent chat never
  // re-resolves (and rebinds against) a possibly-deleted agent.
  let pendingAgentId = null;
  // The agent id of the current chat (fresh or resumed). Drives the picker lock + label only.
  let activeAgentId = null;
  // The agent being edited in the dialog, or null when creating a new one.
  let editingAgentId = null;

  // Sidebar agent-group state: the set of agent ids whose transmissions group is *expanded*.
  // Persisting the expanded set (not the collapsed one) means a newly-seen agent is absent →
  // starts collapsed by default, while an explicit expand survives reloads. Lives outside
  // renderSidebar (which re-runs on every refresh); persistence is best-effort (private mode throws).
  const EXPANDED_AGENTS_KEY = "agent42.sidebar.expandedAgents";
  const expandedAgents = loadExpandedAgents();

  // The last profile fetched from the server. The server is the source of truth; this is
  // only a render cache, always reconciled against a fresh GET/PUT response.
  let profile = { preferredLanguage: "", theme: "system", enabledTools: null, suggestFollowUps: true };

  // Theme engine (ADR 0005 field #2). The server profile is the source of truth; THEME_KEY is
  // only a pre-paint cache the inline head script reads to avoid a dark flash. applyTheme resolves
  // "system" via matchMedia and, only for "system", keeps a live OS-follow listener registered.
  const THEME_KEY = "agent42.theme";
  let systemThemeMedia = null;      // the (prefers-color-scheme: dark) MediaQueryList, when following
  let onSystemThemeChange = null;   // its change handler, so it can be removed on an explicit choice
  let profileSaved = false;         // set by saveProfile so the dialog's close handler skips the revert

  // The selectable-tools catalog (ADR 0021), fetched once from /api/tools: [{key,label,requiresHostedTools}].
  let toolCatalog = [];
  // The current conversation's effective tool selection (list of keys). Set on new/select; the composer
  // popover renders from it and PUTs changes. "All" is the full explicit list — never null (server: null==all).
  let chatTools = [];

  // Stops live speech dictation. Reassigned by the speech-input wiring below when the Web
  // Speech API is available; stays a no-op when it isn't (e.g. Firefox). Called at the top
  // of send() so a late recognition result can't repopulate the just-cleared textarea.
  let stopSpeech = () => {};

  // Aborts the in-flight chat fetch. Set for the life of a send() (its AbortController),
  // null otherwise. Barge-in (ADR 0037) and teardown call sendAbort?.abort() to stop
  // generation mid-stream; an AbortError is treated as a clean interruption, not a failure.
  let sendAbort = null;

  // Hands-free voice conversation controller (ADR 0037). Reassigned by the voice wiring below
  // when SpeechRecognition + speechSynthesis are both available; stays inert otherwise. send()
  // feeds it stream deltas so the reply can be spoken sentence-by-sentence as it arrives.
  let voice = { active: false, onDelta: () => {}, onStreamEnd: () => {}, stop: () => {} };

  // Curated BCP-47 tag → display name for the preferred-language picker. This selects the
  // *preferred language value* (seeds future speech input); it does not translate the UI.
  // An empty tag means "use the device language". Extend this list as needed.
  const LANGUAGES = [
    { tag: "", name: "Use device language" },
    { tag: "en-US", name: "English (United States)" },
    { tag: "en-GB", name: "English (United Kingdom)" },
    { tag: "nl-NL", name: "Nederlands (Nederland)" },
    { tag: "nl-BE", name: "Nederlands (België)" },
    { tag: "de-DE", name: "Deutsch (Deutschland)" },
    { tag: "fr-FR", name: "Français (France)" },
    { tag: "es-ES", name: "Español (España)" },
    { tag: "it-IT", name: "Italiano (Italia)" },
    { tag: "pt-PT", name: "Português (Portugal)" },
    { tag: "pt-BR", name: "Português (Brasil)" },
    { tag: "sv-SE", name: "Svenska (Sverige)" },
    { tag: "pl-PL", name: "Polski (Polska)" },
    { tag: "ja-JP", name: "日本語 (日本)" },
    { tag: "zh-CN", name: "中文 (简体)" },
  ];

  marked.setOptions({ gfm: true, breaks: true });

  const renderMarkdown = (text) =>
    DOMPurify.sanitize(marked.parse(text), { USE_PROFILES: { html: true } });

  const escapeHtml = (text) =>
    text.replace(/[&<>"']/g, (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // Turns bare http(s) URLs in an otherwise-PLAIN user message into clickable links. The user's own
  // text is not markdown (that would render their `# foo`/`*bar*` unexpectedly and open an XSS surface),
  // so we escape the whole string first and then wrap only matched http(s) URLs in an anchor — the only
  // HTML introduced is our own <a> over a validated http(s) href. Trailing sentence punctuation is left
  // outside the link so "see https://example.com." doesn't swallow the full stop. rel guards tab-nabbing.
  const linkifyPlainText = (text) => {
    const urlPattern = /\bhttps?:\/\/[^\s<]+/g;
    let html = "";
    let lastIndex = 0;
    for (let match; (match = urlPattern.exec(text)) !== null; ) {
      html += escapeHtml(text.slice(lastIndex, match.index));
      let url = match[0];
      const trailing = url.match(/[.,;:!?)\]}'"]+$/);
      const tail = trailing ? trailing[0] : "";
      if (tail) url = url.slice(0, -tail.length);
      const safeUrl = escapeHtml(url);
      html += `<a href="${safeUrl}" target="_blank" rel="noopener noreferrer nofollow">${safeUrl}</a>`;
      html += escapeHtml(tail);
      lastIndex = match.index + match[0].length;
    }
    html += escapeHtml(text.slice(lastIndex));
    return html;
  };

  const timeLabel = (iso) => {
    const d = new Date(iso);
    const today = new Date();
    const sameDay = d.toDateString() === today.toDateString();
    return sameDay
      ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
      : d.toLocaleDateString([], { month: "short", day: "numeric" }) +
        " · " + d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  };

  /* ————— model picker ————— */

  // Live-queried from Azure AI Foundry: the deployments the agent can run on.
  //
  // pin:false skips re-pointing the composer at the default. The default-model preference handler needs
  // the refreshed catalog but must NOT retarget the conversation the user currently has open — send()
  // transmits modelSelect.value, so re-pinning here would silently move an open chat onto another model.
  async function loadModels({ pin = true } = {}) {
    try {
      const res = await fetch("/api/models");
      if (!res.ok) return;
      modelsData = await res.json();
      defaultModel = modelsData.default ?? null;

      fillModelOptions(modelSelect);
      if (pin && defaultModel) setModel(defaultModel);
      modelSelect.disabled = modelSelect.options.length === 0;
      applyModelAvailability();
      syncDefaultModelSelect();
      syncEmbeddingModelSelect();
    } catch { /* leave the picker disabled; chat still uses the server's default model */ }
  }

  // Gates the composer on whether ANY chat provider is configured (ADR 0019). With none, /api/models is
  // empty: disable send + the input and show the hint, so the user can't transmit into a grain that has no
  // model (it would only answer with a "configure a provider" notice). Re-run after every loadModels() —
  // including the one saveProvider() fires — so adding a provider re-enables the composer live, no restart.
  // The streaming lock wins while a turn is in flight; send()'s finally re-applies this when it ends.
  function applyModelAvailability() {
    const hasModel = (modelsData.models ?? []).length > 0;
    noModelHint.hidden = hasModel;
    if (!streaming) {
      sendBtn.disabled = !hasModel;
      input.disabled = !hasModel;
    }
  }

  // A model is named by a qualified id — "provider:model", split on the FIRST colon (a provider's own
  // model name can contain a "/", e.g. github:openai/gpt-4o-mini). A legacy id has no colon at all and
  // belongs to the default provider; conversations created before multi-provider hold those.
  function modelLabel(id) {
    if (!id) return "";
    const known = (modelsData.models ?? []).find((m) => m.id === id);
    if (known?.displayName) return known.displayName;
    const colon = id.indexOf(":");
    return colon < 0 ? id : id.slice(colon + 1);
  }

  // The label for a provider's optgroup. Models are GROUPED by provider key (ids are qualified by it),
  // but the group is LABELLED with the provider's display name when it has one — falling back to the raw
  // key, then to "Models" for the unkeyed synthesized default. Shared by every provider-grouped select.
  function providerGroupLabel(m) {
    return (m.providerDisplayName ?? m.provider) || "Models";
  }

  // Fills a <select> with the available models, grouped by provider. Shared by the composer picker AND
  // the agent dialog — both get the grouping, and neither hand-rolls its own option list.
  function fillModelOptions(select) {
    select.innerHTML = "";
    const groups = new Map();
    for (const m of modelsData.models ?? []) {
      const provider = m.provider ?? "";
      if (!groups.has(provider)) {
        const group = document.createElement("optgroup");
        group.label = providerGroupLabel(m);
        groups.set(provider, group);
        select.appendChild(group);
      }

      const opt = document.createElement("option");
      opt.value = m.id;
      opt.textContent = m.displayName ?? m.id;
      if (m.version) opt.title = `${m.model ?? m.id} · ${m.version}`;
      groups.get(provider).appendChild(opt);
    }
  }

  // Selects a model in a <select>, adding it first if the list doesn't have it — a conversation pinned
  // to a model that has since been removed from config, or whose provider was dropped. It goes into its
  // own optgroup rather than as a bare <option>: appended straight to a select that already has
  // optgroups, it would render as their sibling and read as if it belonged to the last group.
  function selectModel(select, id) {
    if (!id) return;
    if (![...select.options].some((o) => o.value === id)) {
      let unknown = select.querySelector("optgroup[data-unknown]");
      if (!unknown) {
        unknown = document.createElement("optgroup");
        unknown.label = "Unavailable";
        unknown.setAttribute("data-unknown", "");
        select.appendChild(unknown);
      }

      const opt = document.createElement("option");
      opt.value = id;
      opt.textContent = modelLabel(id);
      unknown.appendChild(opt);
    }
    select.value = id;
  }

  function setModel(id) {
    selectModel(modelSelect, id);
  }

  /* ————— sidebar ————— */

  async function refreshConversations() {
    try {
      const res = await fetch("/api/conversations");
      if (!res.ok) return;
      conversations = await res.json();
      renderSidebar();
      cachePut("conversations", conversations); // mirror for offline (ADR 0032)
    } catch {
      // Offline/silo unreachable: fall back to the last cached list instead of an empty sidebar.
      // Only when we have nothing yet, so a live list already on screen is never overwritten.
      const cached = await cacheGet("conversations");
      if (cached && conversations.length === 0) { conversations = cached; renderSidebar(); }
    }
  }

  function loadExpandedAgents() {
    try {
      return new Set(JSON.parse(localStorage.getItem(EXPANDED_AGENTS_KEY) || "[]"));
    } catch { return new Set(); /* private mode or bad cache — all groups start collapsed */ }
  }

  function saveExpandedAgents() {
    try { localStorage.setItem(EXPANDED_AGENTS_KEY, JSON.stringify([...expandedAgents])); }
    catch { /* private mode — persistence is best-effort */ }
  }

  // Builds the flat conversation row (button + delete) shared by ungrouped chats and the
  // items inside an agent group.
  function buildConvRow(conv) {
    const li = document.createElement("li");
    li.className = "conv-row";
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "conv" + (conv.id === activeId ? " is-active" : "");
    btn.addEventListener("click", () => selectConversation(conv.id));

    const title = document.createElement("span");
    title.className = "conv__title";
    title.textContent = conv.title;

    const time = document.createElement("span");
    time.className = "conv__time";
    time.textContent = timeLabel(conv.updatedAt);

    btn.append(title, time);

    // Read-only share link (ADR 0035). Present on every row, so both regular and agent chats can be shared.
    const share = document.createElement("button");
    share.type = "button";
    share.className = "conv__share";
    share.setAttribute("aria-label", `Share conversation: ${conv.title}`);
    share.textContent = "↗";
    share.addEventListener("click", (e) => {
      e.stopPropagation();
      openShareLink(conv.id, conv.title);
    });

    const del = document.createElement("button");
    del.type = "button";
    del.className = "conv__delete";
    del.setAttribute("aria-label", `Delete conversation: ${conv.title}`);
    del.textContent = "×";
    del.addEventListener("click", (e) => {
      e.stopPropagation();
      deleteConversation(conv.id, conv.title);
    });

    li.append(btn, share, del);
    return li;
  }

  // Renders one collapsible agent group: a header (chevron + name + count) toggling a body
  // list of that agent's conversations. Groups start collapsed; an explicit expand is keyed
  // by agent id and persisted.
  function buildAgentGroup(agentId, agentName, convs) {
    const li = document.createElement("li");
    const collapsed = !expandedAgents.has(agentId);
    li.className = "conv-group" + (collapsed ? " is-collapsed" : "");

    const header = document.createElement("button");
    header.type = "button";
    header.className = "conv-group__header";
    header.setAttribute("aria-expanded", String(!collapsed));

    const chevron = document.createElement("span");
    chevron.className = "conv-group__chevron";
    chevron.setAttribute("aria-hidden", "true");
    chevron.textContent = "▸";

    const name = document.createElement("span");
    name.className = "conv-group__name";
    name.textContent = agentName || "Agent";

    const count = document.createElement("span");
    count.className = "conv-group__count";
    count.textContent = String(convs.length);

    header.append(chevron, name, count);
    header.addEventListener("click", () => {
      if (expandedAgents.has(agentId)) expandedAgents.delete(agentId);
      else expandedAgents.add(agentId);
      saveExpandedAgents();
      renderSidebar();
    });

    const body = document.createElement("ul");
    body.className = "conv-group__body";
    for (const conv of convs) body.appendChild(buildConvRow(conv));

    li.append(header, body);
    return li;
  }

  function renderSidebar() {
    conversationList.innerHTML = "";
    if (conversations.length === 0) {
      const li = document.createElement("li");
      li.className = "conversations__empty";
      li.textContent = "no transmissions yet";
      conversationList.appendChild(li);
      return;
    }

    // Walk the (recency-sorted) list once. Non-agent chats render as flat rows; agent chats
    // collect into a per-agent group placed at its first-encountered (= most-recent) member,
    // so groups interleave with regular chats by recency.
    const groups = new Map();
    for (const conv of conversations) {
      if (!conv.agentId) {
        conversationList.appendChild(buildConvRow(conv));
        continue;
      }
      let group = groups.get(conv.agentId);
      if (!group) {
        // Placeholder <li> reserves this group's slot in recency order; filled in below.
        const placeholder = document.createElement("li");
        conversationList.appendChild(placeholder);
        group = { name: conv.agentName, convs: [], placeholder };
        groups.set(conv.agentId, group);
      }
      group.convs.push(conv);
    }

    for (const [agentId, group] of groups) {
      group.placeholder.replaceWith(buildAgentGroup(agentId, group.name, group.convs));
    }
  }

  // Permanently deletes a conversation (session + reminders) after confirmation, then
  // reconciles the sidebar. If the open conversation is deleted, reset to a new chat.
  async function deleteConversation(id, title) {
    if (streaming) return;
    if (!confirm(`Delete "${title}"? This can't be undone.`)) return;

    try {
      const res = await fetch(`/api/conversations/${encodeURIComponent(id)}`, { method: "DELETE" });
      if (!res.ok) return;
    } catch { return; /* silo may be unreachable; leave the row in place */ }

    conversations = conversations.filter((c) => c.id !== id);
    cacheDelete("messages:" + id); // drop the offline copy; refreshConversations re-caches the list
    if (id === activeId) startNewConversation();
    else renderSidebar();
    refreshConversations();
  }

  /* ————— read-only share link (ADR 0035) ————— */

  // Toggles the dialog between its unshared (Create) and shared (URL + Copy/Revoke) states from a url
  // (a non-empty string when shared, null/absent when not).
  function renderShareLinkState(url) {
    const shared = typeof url === "string" && url.length > 0;
    shareLinkResult.hidden = !shared;
    shareLinkCreateBtn.hidden = shared;
    if (shared) shareLinkUrl.value = url;
  }

  // Opens the share dialog for one conversation and loads its current link state. A public link lets
  // ANYONE holding it view this conversation read-only WITHOUT signing in (ADR 0035), so nothing is minted
  // until the user presses Create — that press is their explicit consent to the exposure (ADR 0013).
  async function openShareLink(convId, title) {
    if (streaming) return;
    shareLinkConvId = convId;
    shareLinkConvTitle.textContent = `“${title}”`;
    renderShareLinkState(null); // optimistic unshared until the fetch resolves
    shareLinkCreateBtn.disabled = false;
    shareLinkDialog.showModal();
    try {
      const res = await fetch(`/api/conversations/${encodeURIComponent(convId)}/share`);
      if (res.ok) renderShareLinkState((await res.json()).url);
    } catch { /* silo unreachable — leave the unshared state; Create will retry */ }
  }

  async function createShareLink() {
    if (!shareLinkConvId) return;
    shareLinkCreateBtn.disabled = true;
    try {
      const res = await fetch(`/api/conversations/${encodeURIComponent(shareLinkConvId)}/share`, { method: "POST" });
      if (res.ok) renderShareLinkState((await res.json()).url);
    } catch { /* silo unreachable — stay unshared */ }
    finally { shareLinkCreateBtn.disabled = false; }
  }

  async function revokeShareLink() {
    if (!shareLinkConvId) return;
    if (!confirm("Revoke this link? Anyone holding it will lose access.")) return;
    try {
      const res = await fetch(`/api/conversations/${encodeURIComponent(shareLinkConvId)}/share`, { method: "DELETE" });
      if (res.ok || res.status === 404) renderShareLinkState(null);
    } catch { /* silo unreachable — leave it shared */ }
  }

  async function copyShareLink() {
    try {
      await navigator.clipboard.writeText(shareLinkUrl.value);
      const label = shareLinkCopyBtn.textContent;
      shareLinkCopyBtn.textContent = "Copied";
      setTimeout(() => { shareLinkCopyBtn.textContent = label; }, 1500);
    } catch {
      // Clipboard API unavailable (insecure context / denied) — select the field so the user can copy manually.
      shareLinkUrl.focus();
      shareLinkUrl.select();
    }
  }

  shareLinkCreateBtn.addEventListener("click", createShareLink);
  shareLinkCopyBtn.addEventListener("click", copyShareLink);
  shareLinkRevokeBtn.addEventListener("click", revokeShareLink);
  shareLinkCloseBtn.addEventListener("click", () => shareLinkDialog.close());

  /* ————— stored agents ————— */

  async function loadAgents() {
    try {
      const res = await fetch("/api/agents");
      if (!res.ok) return;
      agents = await res.json();
      renderAgents();
      cachePut("agents", agents); // mirror for offline (ADR 0032)
    } catch {
      // Offline/silo unreachable: fall back to the last cached agents instead of an empty list.
      const cached = await cacheGet("agents");
      if (cached && agents.length === 0) { agents = cached; renderAgents(); }
    }
  }

  function renderAgents() {
    agentList.innerHTML = "";
    if (agents.length === 0) {
      const li = document.createElement("li");
      li.className = "agents__empty";
      li.textContent = "no agents yet";
      agentList.appendChild(li);
      return;
    }
    for (const agent of agents) {
      const li = document.createElement("li");
      li.className = "agent-row";

      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "agent";
      btn.title = agent.description || agent.name;
      btn.addEventListener("click", () => startAgentChat(agent));

      const name = document.createElement("span");
      name.className = "agent__name";
      name.textContent = agent.name;

      const model = document.createElement("span");
      model.className = "agent__model";
      // The stored value is a qualified id; show the friendly name, not "github:openai/gpt-4o-mini".
      model.textContent = modelLabel(agent.model);
      model.title = agent.model;

      btn.append(name, model);

      const edit = document.createElement("button");
      edit.type = "button";
      edit.className = "agent__action";
      edit.setAttribute("aria-label", `Edit agent: ${agent.name}`);
      edit.textContent = "✎";
      edit.addEventListener("click", (e) => {
        e.stopPropagation();
        openAgentDialog(agent);
      });

      const del = document.createElement("button");
      del.type = "button";
      del.className = "agent__action";
      del.setAttribute("aria-label", `Delete agent: ${agent.name}`);
      del.textContent = "🗑";
      del.addEventListener("click", (e) => {
        e.stopPropagation();
        deleteAgent(agent.id, agent.name);
      });

      li.append(btn, edit, del);
      agentList.appendChild(li);
    }
  }

  // Clicking an agent always starts a NEW chat seeded with that agent's persona. The model
  // is locked to the agent's; only pendingAgentId goes on the wire (bound on the first send).
  function startAgentChat(agent) {
    closeDrawer();
    if (streaming) return;
    // Continuous agents route every start onto one deterministic conversation id
    // (`agent-<id>`), so all their chats share one ongoing history (ADR 0016); per-chat
    // agents get a fresh random id. crypto.randomUUID() never emits the `agent-` prefix,
    // so the two id spaces never collide.
    const convId = agent.continuous ? `agent-${agent.id}` : crypto.randomUUID();
    // Resume an existing continuous conversation instead of showing a blank thread over a
    // grain that already has history — delegate to selectConversation, which loads the
    // messages/model/binding and locks the picker.
    if (agent.continuous && conversations.some((c) => c.id === convId)) {
      selectConversation(convId);
      return;
    }
    activeId = convId;
    activeAgentId = agent.id;
    pendingAgentId = agent.id;
    clearPendingAttachments();
    clearThread();
    setEmptyVisible(true);
    setModel(agent.model);
    lockPicker(agent.name, agent.model);
    renderSidebar();
    input.focus();
  }

  function openAgentDialog(agent) {
    editingAgentId = agent?.id ?? null;
    agentFormTitle.textContent = agent ? "Edit agent" : "New agent";
    agentNameInput.value = agent?.name ?? "";
    agentDescriptionInput.value = agent?.description ?? "";
    agentSystemPromptInput.value = agent?.systemPrompt ?? "";
    agentContinuousInput.checked = agent?.continuous ?? false;
    // The key section (with Regenerate) is meaningful only for a SAVED agent; a new agent has no key
    // yet — one is minted on create and revealed then. Always start with the raw key hidden (ADR 0022).
    agentKeySection.hidden = !agent;
    agentKeyReveal.hidden = true;
    agentKeyValue.textContent = "";
    // The run endpoint is agent-independent and not a secret (ADR 0022: the key alone identifies the
    // agent), so show its absolute URL ALWAYS — even for a not-yet-saved agent — for off-browser copy.
    agentRunUrl.textContent = `${location.origin}/api/agent-run`;
    agentRunUrlCopyBtn.textContent = "Copy"; // reset a stale "Copied" from a prior open (button is always visible)
    pendingRegenerate = false;
    // Null enabledTools (a new agent, or one saved before ADR 0021) means "all selectable tools".
    renderToolChecks(agentToolChecks, agent?.enabledTools ?? allToolKeys());
    fillModelOptions(agentModelSelect);
    // Preselect the agent's model when editing, otherwise the server default. Uses the same helper as
    // the composer picker rather than a second copy of the append-if-missing logic.
    selectModel(agentModelSelect, agent?.model ?? defaultModel);
    agentTabs.select(0); // always open on the General tab, never a stale Tools tab
    agentDialog.showModal();
    agentNameInput.focus();
  }

  // Set by the Regenerate-key button so the next save rotates the key (ADR 0022). Reset on every
  // dialog open and after each save, so a plain Save never regenerates.
  let pendingRegenerate = false;

  async function saveAgent() {
    const payload = {
      name: agentNameInput.value.trim(),
      description: agentDescriptionInput.value.trim(),
      model: agentModelSelect.value,
      systemPrompt: agentSystemPromptInput.value.trim(),
      continuous: agentContinuousInput.checked,
    };
    // The full explicit list (never null) — "all selected" is the whole list, per ADR 0021. But ONLY
    // when the catalog actually loaded: if /api/tools failed, toolCatalog is [] and readToolChecks would
    // return [] (= none), silently disabling every tool. Omit the field so the server keeps the default
    // (a create then stores null == all) rather than storing an accidental empty set.
    if (toolCatalog.length > 0) payload.enabledTools = readToolChecks(agentToolChecks);
    // Regenerate only on an existing agent (PUT) — a create always mints a fresh key anyway.
    if (editingAgentId && pendingRegenerate) payload.regenerateKey = true;
    if (!payload.name || !payload.systemPrompt || !payload.model) return;

    const url = editingAgentId ? `/api/agents/${encodeURIComponent(editingAgentId)}` : "/api/agents";
    const method = editingAgentId ? "PUT" : "POST";
    let body;
    try {
      const res = await fetch(url, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!res.ok) return;
      body = await res.json().catch(() => null);
    } catch { return; /* silo may be unreachable; leave the dialog open */ }

    pendingRegenerate = false;
    loadAgents();

    // The response is { agent, apiKey }. A non-null apiKey means one was just minted (create, or a
    // regenerate) — reveal it ONCE and keep the dialog open (now in edit mode) so the user can copy it;
    // it is never returned again. Otherwise this was a plain save, so just close.
    if (body && body.apiKey) {
      editingAgentId = body.agent.id;
      agentFormTitle.textContent = "Edit agent";
      agentTabs.select(2); // the key lives in the API-key panel — show it even if Save came from another tab
      agentKeySection.hidden = false;
      agentKeyValue.textContent = body.apiKey;
      agentKeyReveal.hidden = false;
      agentKeyCopyBtn.textContent = "Copy";
    } else {
      agentDialog.close();
    }
  }

  async function deleteAgent(id, name) {
    if (!confirm(`Delete agent "${name}"? Existing chats keep their persona.`)) return;
    try {
      const res = await fetch(`/api/agents/${encodeURIComponent(id)}`, { method: "DELETE" });
      if (!res.ok) return;
    } catch { return; /* silo may be unreachable; leave the row in place */ }

    agents = agents.filter((a) => a.id !== id);
    renderAgents();
    loadAgents();
  }

  // In an agent chat the model is fixed to the agent's, so the picker is replaced by a
  // static "agent · model" label; a normal chat shows the live picker.
  function lockPicker(name, model) {
    modelField.hidden = true;
    agentLabel.hidden = false;
    agentLabel.textContent = `⛓ ${name} · ${modelLabel(model)}`;
    // An agent chat's tools come from its bind snapshot (ADR 0021), so the per-chat popover is hidden —
    // a PUT before the first agent turn would be clobbered by the bind anyway. Incognito is likewise a
    // regular-chat-only affordance (ADR 0034), so it hides under the same lock.
    toolsBtn.hidden = true;
    incognitoBtn.hidden = true;
  }

  function unlockPicker() {
    modelField.hidden = false;
    agentLabel.hidden = true;
    toolsBtn.hidden = false;
    incognitoBtn.hidden = false;
  }

  // Reflect the incognito flag onto the toggle and the empty-state hint (ADR 0034). `interactive` is
  // false once a chat has a message or history — ephemerality is fixed at creation, so the button locks.
  function setIncognito(on, interactive) {
    ephemeral = on;
    incognitoBtn.setAttribute("aria-pressed", on ? "true" : "false");
    incognitoBtn.classList.toggle("is-active", on);
    if (interactive !== undefined) incognitoBtn.disabled = !interactive;
    document.body.classList.toggle("is-incognito", on);
  }

  /* ————— thread rendering ————— */

  function clearThread() {
    thread.querySelectorAll(".msg").forEach((el) => el.remove());
  }

  function setEmptyVisible(visible) {
    emptyState.style.display = visible ? "" : "none";
  }

  function addMessage(role, options = {}) {
    setEmptyVisible(false);
    const msg = document.createElement("article");
    msg.className = role === "user" ? "msg msg--user" : "msg msg--agent";
    if (options.error) msg.classList.add("is-error");

    const meta = document.createElement("div");
    meta.className = "msg__meta";
    meta.textContent = role === "user" ? "YOU" : "AGENT 42";

    const bubble = document.createElement("div");
    bubble.className = "msg__bubble";
    if (role !== "user") {
      const prose = document.createElement("div");
      prose.className = "prose";
      bubble.appendChild(prose);
    }

    msg.append(meta, bubble);
    thread.appendChild(msg);
    return msg;
  }

  // Fills a user bubble with its (optional) images followed by its text. Images are always
  // fetched through the first-party display proxy by id, so the same path renders a live send
  // and a reloaded turn — the page never holds a blob URL.
  function fillUserBubble(msg, text, attachments, convId) {
    const bubble = msg.querySelector(".msg__bubble");
    bubble.textContent = "";

    if (attachments && attachments.length) {
      const gallery = document.createElement("div");
      gallery.className = "msg__images";
      for (const att of attachments) {
        const img = document.createElement("img");
        img.className = "msg__image";
        img.loading = "lazy";
        img.alt = att.fileName || "attachment";
        img.src = attachmentUrl(convId, att.id);
        gallery.appendChild(img);
      }
      bubble.appendChild(gallery);
    }

    if (text) {
      const body = document.createElement("div");
      body.className = "msg__text";
      // Plain text with bare http(s) URLs made clickable — not markdown (see linkifyPlainText).
      body.innerHTML = linkifyPlainText(text);
      bubble.appendChild(body);
    }
  }

  const attachmentUrl = (convId, attachmentId) =>
    `/api/conversations/${encodeURIComponent(convId)}/attachments/${encodeURIComponent(attachmentId)}`;

  // Adds a generated image (ADR 0020) to an assistant bubble, ABOVE its prose. Fetched through the same
  // first-party display proxy by id as user images, so a live image stream event and a reloaded assistant
  // turn render through one path — the page never holds a blob URL.
  function addAgentImage(msg, convId, attachmentId, fileName) {
    const bubble = msg.querySelector(".msg__bubble");
    let gallery = bubble.querySelector(".msg__images");
    if (!gallery) {
      gallery = document.createElement("div");
      gallery.className = "msg__images";
      bubble.insertBefore(gallery, bubble.firstChild); // above the prose
    }
    const img = document.createElement("img");
    img.className = "msg__image";
    img.loading = "lazy";
    img.alt = fileName || "generated image";
    img.src = attachmentUrl(convId, attachmentId);
    gallery.appendChild(img);
  }

  /* ————— attachments (compose) ————— */

  // Uploads each picked file to the current conversation. The server branches on media type: an
  // image comes back as {id,...} and is sent as an attachmentId on the next turn; a document comes
  // back as {kind:"document",...}, is indexed for retrieval server-side, and is shown as a chip but
  // never sent back (ADR 0007). A non-OK response (unsupported type, oversize, or a server error)
  // is surfaced as a dismissible error chip rather than silently dropped.
  async function handleFiles(files) {
    if (streaming) return;
    // Pin the results to the conversation the upload was started in: the fetch stores bytes under
    // uploadId, so a mid-upload conversation switch must NOT apply the chip/attachment to the new
    // one. After every await we bail via break (not return) so the fileInput reset below still runs
    // and the same file can be re-picked.
    const uploadId = activeId;
    for (const file of files) {
      // Incognito document gate (ADR 0034): RAG ingestion writes durable chunks to the Cosmos vector
      // store, which would outlive an ephemeral chat — so block documents while incognito. Images are
      // fine (session-only ephemerality). The server does not enforce this, so the gate lives here.
      if (ephemeral && !file.type.startsWith("image/")) {
        pendingNotices.push({
          fileName: file.name,
          message: "Documents can't be attached in an incognito chat — they'd be indexed and stored. Turn off incognito to attach documents.",
        });
        renderAttachmentPreview();
        continue;
      }
      // Document-attach gate (ADR 0007/0018): a document needs an embedding model to be indexed for RAG, so
      // block the attempt when none is configured — images never need embeddings and are always allowed. The
      // server also rejects it (defense-in-depth); this just avoids a pointless upload + a cryptic error.
      // Note: loadProviders() populates userDefaultEmbeddingModel fire-and-forget at init, so in the brief
      // window before it resolves a configured user could see a transient false-negative here — the server
      // would still accept the upload; the gate self-corrects once /api/providers returns.
      if (!userDefaultEmbeddingModel && !file.type.startsWith("image/")) {
        pendingNotices.push({
          fileName: file.name,
          message: "Add a model marked as an embedding model to a provider and pick it as the default embedding model in Preferences to attach documents.",
        });
        renderAttachmentPreview();
        continue;
      }
      try {
        const body = new FormData();
        body.append("file", file, file.name);
        const res = await fetch(attachmentsUrl(uploadId), { method: "POST", body });
        if (activeId !== uploadId) break; // user moved on — discard this result
        if (!res.ok) {
          const message = (await res.text()).trim() || "Upload failed.";
          if (activeId !== uploadId) break;
          pendingNotices.push({ fileName: file.name, message });
          renderAttachmentPreview();
          continue;
        }
        const meta = await res.json();
        if (activeId !== uploadId) break;
        if (meta.kind === "document") {
          pendingDocuments.push(meta);
        } else {
          pendingAttachments.push(meta);
        }
        renderAttachmentPreview();
      } catch { /* skip this file; the others still upload */ }
    }
    fileInput.value = ""; // let the same file be picked again after a remove
  }

  const attachmentsUrl = (convId) =>
    `/api/conversations/${encodeURIComponent(convId)}/attachments`;

  function renderAttachmentPreview() {
    attachmentPreview.innerHTML = "";
    attachmentPreview.hidden =
      pendingAttachments.length === 0 && pendingDocuments.length === 0 && pendingNotices.length === 0;
    pendingNotices.forEach((notice) => {
      // A rejected/failed upload — surface it as a dismissible error chip (the server never stored
      // anything, so dismissing only clears the notice). Reuses the doc-chip shape; the --error
      // variant paints it red (styles.css).
      const chip = document.createElement("div");
      chip.className = "attach-chip attach-chip--doc attach-chip--error";
      chip.title = notice.message;

      const label = document.createElement("span");
      label.className = "attach-chip__doc";
      label.textContent = `⚠️ ${notice.fileName} — upload failed`;

      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "attach-chip__remove";
      remove.setAttribute("aria-label", `Dismiss ${notice.fileName}`);
      remove.textContent = "×";
      remove.addEventListener("click", () => {
        pendingNotices = pendingNotices.filter((n) => n !== notice);
        renderAttachmentPreview();
      });

      chip.append(label, remove);
      attachmentPreview.appendChild(chip);
    });
    pendingDocuments.forEach((doc) => {
      // chunkCount 0 = no extractable text, so the server never registered it with the grain and
      // the agent won't see it. Warn clearly rather than showing a "just indexed" chip that invites
      // a question the agent can't answer.
      const indexed = doc.chunkCount > 0;
      const chip = document.createElement("div");
      chip.className = indexed
        ? "attach-chip attach-chip--doc"
        : "attach-chip attach-chip--doc attach-chip--warn";
      chip.title = indexed
        ? `${doc.fileName} — ${doc.chunkCount} passage(s) indexed. Ask a question about it.`
        : `${doc.fileName} — no readable text found (a scanned image?). It was NOT indexed, so the assistant can't answer questions about it.`;

      const label = document.createElement("span");
      label.className = "attach-chip__doc";
      label.textContent = indexed ? `📄 ${doc.fileName}` : `⚠️ ${doc.fileName} — not indexed`;

      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "attach-chip__remove";
      remove.setAttribute("aria-label", `Dismiss ${doc.fileName}`);
      remove.textContent = "×";
      // Display-only: dismissing the chip doesn't un-index the document (that happens when the
      // whole conversation is deleted).
      remove.addEventListener("click", () => {
        pendingDocuments = pendingDocuments.filter((d) => d !== doc);
        renderAttachmentPreview();
      });

      chip.append(label, remove);
      attachmentPreview.appendChild(chip);
    });
    pendingAttachments.forEach((att) => {
      const chip = document.createElement("div");
      chip.className = "attach-chip";

      const img = document.createElement("img");
      img.className = "attach-chip__thumb";
      img.alt = att.fileName || "attachment";
      img.src = attachmentUrl(activeId, att.id);

      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "attach-chip__remove";
      remove.setAttribute("aria-label", `Remove ${att.fileName || "attachment"}`);
      remove.textContent = "×";
      remove.addEventListener("click", () => {
        pendingAttachments = pendingAttachments.filter((a) => a.id !== att.id);
        renderAttachmentPreview();
      });

      chip.append(img, remove);
      attachmentPreview.appendChild(chip);
    });
  }

  function clearPendingAttachments() {
    pendingAttachments = [];
    pendingDocuments = [];
    pendingNotices = [];
    renderAttachmentPreview();
  }

  function setAgentMarkdown(msg, text, { caret = false } = {}) {
    const prose = msg.querySelector(".prose");
    prose.innerHTML = renderMarkdown(text);
    if (caret) {
      const caretEl = document.createElement("span");
      caretEl.className = "caret";
      (prose.lastElementChild ?? prose).appendChild(caretEl);
    }
  }

  // Adds a hover-revealed copy button to the bottom of a finalized agent bubble. The raw markdown
  // is captured by closure here — it is NOT recoverable from the DOM (the .prose div holds only
  // rendered HTML). Mirrors the agent-key/run-URL clipboard buttons and the JS-created glyph
  // actions (.conv__delete "×", .agent__action "✎"): swap the glyph to a check on success, revert
  // after ~1.5s, swallow the catch. Skips empty/image-only replies (nothing to copy).
  function addCopyButton(msg, rawText) {
    if (!rawText || !rawText.trim()) return;
    const bubble = msg.querySelector(".msg__bubble");
    if (!bubble) return;
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "msg__copy";
    btn.setAttribute("aria-label", "Copy response");
    btn.textContent = "⧉";
    btn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(rawText);
        btn.textContent = "✓";
        setTimeout(() => { btn.textContent = "⧉"; }, 1500);
      } catch {
        /* clipboard blocked (insecure context / permission) — the reply is visible to select manually */
      }
    });
    bubble.appendChild(btn);
  }

  // Follow-up suggestion chips (up to 3) under a reply. Clicking one sends it immediately as the next
  // message — a click is explicit intent — guarded so it never fires mid-stream. Live-only: not replayed
  // by renderTurns, so they exist only under the freshly streamed turn and are cleared when the next turn
  // starts (clearSuggestions in send()).
  function renderSuggestions(msg, items) {
    if (!Array.isArray(items) || items.length === 0) return;
    const bubble = msg.querySelector(".msg__bubble");
    if (!bubble) return;
    const row = document.createElement("div");
    row.className = "msg__suggestions";
    for (const text of items.slice(0, 3)) {
      const chip = document.createElement("button");
      chip.type = "button";
      chip.className = "suggestion-chip";
      chip.textContent = text;
      chip.addEventListener("click", () => { if (!streaming) send(text); });
      row.appendChild(chip);
    }
    bubble.appendChild(row);
  }

  // Removes any suggestion chips currently in the thread (only the newest turn should carry them).
  function clearSuggestions() {
    thread.querySelectorAll(".msg__suggestions").forEach((el) => el.remove());
  }

  const scrollToBottom = () => { chatScroll.scrollTop = chatScroll.scrollHeight; };

  /* ————— tool calls & token usage ————— */

  const prettyJson = (text) => {
    try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
  };

  // Tool activity is shown as collapsible chips above the reply text, one per call.
  function addToolCall(msg, { toolCallId, toolName, toolInput }) {
    const bubble = msg.querySelector(".msg__bubble");
    let tools = bubble.querySelector(".msg__tools");
    if (!tools) {
      tools = document.createElement("div");
      tools.className = "msg__tools";
      bubble.insertBefore(tools, bubble.querySelector(".prose"));
    }

    const item = document.createElement("details");
    item.className = "tool-call is-running";
    if (toolCallId) item.dataset.callId = toolCallId;

    const summary = document.createElement("summary");
    const status = document.createElement("span");
    status.className = "tool-call__status";
    const name = document.createElement("span");
    name.className = "tool-call__name";
    name.textContent = toolName ?? "tool";
    summary.append(status, name);
    item.appendChild(summary);

    if (toolInput) appendToolIo(item, "input", toolInput);
    tools.appendChild(item);
  }

  function completeToolCall(msg, { toolCallId, toolResult }) {
    const item =
      (toolCallId && msg.querySelector(`.tool-call[data-call-id="${CSS.escape(toolCallId)}"]`)) ??
      msg.querySelector(".tool-call.is-running");
    if (!item) return;
    item.classList.remove("is-running");
    item.classList.add("is-done");
    if (toolResult) appendToolIo(item, "result", toolResult);
  }

  function appendToolIo(item, label, json) {
    const io = document.createElement("div");
    io.className = "tool-call__io";
    const lab = document.createElement("div");
    lab.className = "tool-call__io-label";
    lab.textContent = label;
    const pre = document.createElement("pre");
    pre.textContent = prettyJson(json);
    io.append(lab, pre);
    item.appendChild(io);
  }

  function showUsage(msg, { inputTokens, outputTokens, cachedTokens }) {
    if (inputTokens == null && outputTokens == null) return;
    const parts = [];
    if (inputTokens != null) parts.push(`${inputTokens.toLocaleString()} in`);
    if (outputTokens != null) parts.push(`${outputTokens.toLocaleString()} out`);
    if (cachedTokens != null) parts.push(`${cachedTokens.toLocaleString()} cached`);
    const usage = document.createElement("div");
    usage.className = "msg__usage";
    usage.textContent = `tokens · ${parts.join(" · ")}`;
    msg.appendChild(usage);
  }

  /* ————— conversation switching ————— */

  async function selectConversation(id) {
    closeDrawer();
    if (streaming || id === activeId) return;
    voice.stop(); // exit hands-free mode when switching chats (ADR 0037)
    activeId = id;
    // An opened conversation is a persisted one (an ephemeral chat never gets a sidebar row), so
    // incognito is off and locked here — it can only be chosen on a fresh chat (ADR 0034).
    setIncognito(false, false);
    clearPendingAttachments();
    renderSidebar();
    clearThread();
    setEmptyVisible(true);

    try {
      const [msgRes, modelRes, agentRes, toolsRes] = await Promise.all([
        fetch(`/api/conversations/${encodeURIComponent(id)}/messages`),
        fetch(`/api/conversations/${encodeURIComponent(id)}/model`),
        fetch(`/api/conversations/${encodeURIComponent(id)}/agent`),
        fetch(`/api/conversations/${encodeURIComponent(id)}/tools`),
      ]);
      if (!msgRes.ok) return;
      const turns = await msgRes.json();
      renderTurns(id, turns);
      cachePut("messages:" + id, turns); // mirror this history for offline viewing (ADR 0032)
      // Restore the model picker only after the history is on screen, so a failure
      // here can never prevent the conversation itself from rendering.
      if (modelRes.ok) setModel(await modelRes.json());
      // Restore this conversation's effective tool selection for the popover (ADR 0021). Hidden on an
      // agent chat, but harmless to hold the snapshot's set.
      if (toolsRes.ok) chatTools = await toolsRes.json();
      // Then apply the agent binding — after the render + setModel, for the same reason.
      // The binding is already snapshotted server-side, so leave pendingAgentId null: a
      // resumed agent chat must never re-resolve (and rebind against) a deleted agent.
      const binding = agentRes.ok ? await agentRes.json() : null;
      if (binding) {
        activeAgentId = binding.agentId;
        pendingAgentId = null;
        setModel(binding.model);
        lockPicker(binding.name, binding.model);
      } else {
        activeAgentId = null;
        pendingAgentId = null;
        unlockPicker();
      }
    } catch {
      // Offline: render this chat's history from the cache if it was opened while online before.
      // The model/agent/tools siblings simply degrade — the composer can't send offline anyway.
      const cached = await cacheGet("messages:" + id);
      if (cached) renderTurns(id, cached);
    }
  }

  // Render a history array (from the network or the offline cache) into the open thread. Extracted
  // so the live path and the offline-cache fallback in selectConversation share one renderer.
  function renderTurns(id, turns) {
    for (const turn of turns) {
      const msg = addMessage(turn.role === "user" ? "user" : "agent");
      if (turn.role === "user") {
        fillUserBubble(msg, turn.text, turn.attachments, id);
      } else {
        setAgentMarkdown(msg, turn.text);
        // History-restored reply: capture turn.text by closure so copy works after a reload too.
        addCopyButton(msg, turn.text);
        // An assistant turn may carry a generated image (ADR 0020); re-render it above the prose.
        for (const att of turn.attachments ?? []) addAgentImage(msg, id, att.id, att.fileName);
      }
    }
    scrollToBottom();
  }

  function startNewConversation() {
    closeDrawer();
    if (streaming) return;
    voice.stop(); // exit hands-free mode when switching chats (ADR 0037)
    activeId = crypto.randomUUID();
    activeAgentId = null;
    pendingAgentId = null;
    // A new regular chat starts from the user's default tool set (ADR 0021); null profile default = all.
    chatTools = profile.enabledTools ?? allToolKeys();
    // ...and from their default model, so the picker doesn't inherit whatever the last-opened
    // conversation was pinned to. `defaultModel` is the server-RESOLVED default (/api/models).
    if (defaultModel) setModel(defaultModel);
    clearPendingAttachments();
    unlockPicker();
    // A fresh chat is where incognito can be chosen — off by default, and interactive until first send.
    setIncognito(false, true);
    clearThread();
    setEmptyVisible(true);
    renderSidebar();
    input.focus();
  }

  /* ————— mobile drawer ————— */

  // Off-canvas sidebar toggle for ≤760px. The backdrop's visibility and the slide
  // animation are owned entirely by the CSS `.app.is-drawer-open` rules; JS only flips
  // that class and keeps aria-expanded in sync. No-ops harmlessly on desktop.
  function openDrawer() {
    app.classList.add("is-drawer-open");
    menuToggle.setAttribute("aria-expanded", "true");
  }

  function closeDrawer() {
    app.classList.remove("is-drawer-open");
    menuToggle.setAttribute("aria-expanded", "false");
  }

  function toggleDrawer() {
    if (app.classList.contains("is-drawer-open")) closeDrawer();
    else openDrawer();
  }

  /* ————— user location (ADR 0012) ————— */

  // A fix older than this is dropped rather than sent: the model must never be told the user is
  // somewhere they left half an hour ago.
  const LOCATION_MAX_AGE_MS = 30 * 60 * 1000;
  // Background refresh cadence while the tab is visible. Nobody is ever waiting on these.
  const LOCATION_REFRESH_MS = 5 * 60 * 1000;
  // Set ONLY after a getCurrentPosition() actually succeeded — see the WebKit leg below.
  const LOCATION_GRANT_KEY = "agent42.location.granted";

  const geolocationSupported = "geolocation" in navigator;

  // The last known fix, refreshed out of band. The send path reads this synchronously and never
  // awaits a new one — see currentLocationForSend().
  let lastFix = null;
  let locationRefreshTimer = null;

  // ——— Authorization: two legs, because one of them does not exist on iOS ———
  //
  // navigator.permissions.query({name: "geolocation"}) is NOT supported in WebKit — i.e. every
  // browser on iOS/iPadOS — where it rejects. Gating capture purely on state === "granted" would
  // therefore be dead on every iPhone: the user grants, no fix is ever attached, and the grant button
  // never hides. So:
  //
  //   Leg 1 — Permissions API present (Chrome/Edge/Firefox, desktop + Android): trust state === "granted".
  //   Leg 2 — Permissions API absent/rejecting (WebKit): trust a localStorage flag that we set only
  //           after a getCurrentPosition() has actually SUCCEEDED. A permission the browser has already
  //           granted resolves without re-prompting, so capturing under that flag never ambushes the
  //           user; a PERMISSION_DENIED on any later capture clears it (they revoked it in site
  //           settings) and brings the grant button back.
  //
  // getCurrentPosition() is never called on a send path under either leg.
  async function geolocationPermissionStatus() {
    if (!navigator.permissions?.query) return null;
    try {
      return await navigator.permissions.query({ name: "geolocation" });
    } catch {
      return null; // WebKit: the query itself rejects.
    }
  }

  function storedGrant() {
    try {
      return localStorage.getItem(LOCATION_GRANT_KEY) === "1";
    } catch {
      return false; // Private mode / storage disabled: no grant, no capture.
    }
  }

  function setStoredGrant(granted) {
    try {
      if (granted) localStorage.setItem(LOCATION_GRANT_KEY, "1");
      else localStorage.removeItem(LOCATION_GRANT_KEY);
    } catch {
      /* storage unavailable — leg 2 simply stays unauthorized */
    }
  }

  async function isCaptureAuthorized() {
    if (!geolocationSupported) return false;
    const status = await geolocationPermissionStatus();
    return status ? status.state === "granted" : storedGrant();
  }

  function readPosition() {
    return new Promise((resolve, reject) => {
      // enableHighAccuracy is affordable precisely BECAUSE nobody is waiting on this: it runs in the
      // background, never in the send path, so a cold GPS spin-up costs the user nothing.
      navigator.geolocation.getCurrentPosition(resolve, reject, {
        enableHighAccuracy: true,
        timeout: 15000,
        maximumAge: 60000,
      });
    });
  }

  function toFix(position) {
    return {
      latitude: position.coords.latitude,
      longitude: position.coords.longitude,
      accuracyMetres: position.coords.accuracy ?? null,
      capturedAt: new Date(position.timestamp).toISOString(),
    };
  }

  // PositionError.PERMISSION_DENIED === 1. The user revoked the permission in site settings, so the
  // WebKit leg's flag is now a lie — clear it and offer the grant button again.
  function handlePositionError(error) {
    if (error?.code === 1) {
      setStoredGrant(false);
      lastFix = null;
      stopBackgroundLocationRefresh();
      updateLocationControl();
    }
  }

  // Refreshes the cached fix out of band. Nothing awaits this — a failure is silent by design.
  async function refreshFix() {
    if (!(await isCaptureAuthorized())) return;
    try {
      lastFix = toFix(await readPosition());
    } catch (error) {
      handlePositionError(error);
    }
  }

  function startBackgroundLocationRefresh() {
    stopBackgroundLocationRefresh();
    if (document.visibilityState !== "visible") return;
    locationRefreshTimer = setInterval(refreshFix, LOCATION_REFRESH_MS);
  }

  function stopBackgroundLocationRefresh() {
    if (locationRefreshTimer !== null) {
      clearInterval(locationRefreshTimer);
      locationRefreshTimer = null;
    }
  }

  // THE SEND PATH. Synchronous, and deliberately so: it reads whatever is already cached and returns
  // null if there is nothing fresh. It never calls getCurrentPosition() and never awaits a fix — an
  // awaited GPS acquisition would add seconds to every message once the cached fix went stale, and
  // typing "hi" must not pay for a GPS spin-up. A stale fix is dropped, never sent.
  function currentLocationForSend() {
    if (!lastFix) return null;
    const age = Date.now() - Date.parse(lastFix.capturedAt);
    if (!Number.isFinite(age) || age > LOCATION_MAX_AGE_MS) return null;
    return lastFix;
  }

  // Reflects the location state in Preferences → General: status text + the Enable button, which
  // shows exactly when we could capture but are not yet allowed to. Safe to call while the dialog
  // is closed (updates hidden nodes) — driven on open and from the Permissions 'change' handler.
  async function updateLocationControl() {
    if (!locationStatus || !locationEnableBtn) return;
    if (!geolocationSupported) {
      locationStatus.textContent = "Not available in this browser";
      locationEnableBtn.hidden = true;
      return;
    }
    const authorized = await isCaptureAuthorized();
    locationStatus.textContent = authorized ? "Sharing on" : "Off";
    locationEnableBtn.hidden = authorized;
  }

  // The one place a browser permission prompt is ever triggered — on an explicit tap.
  async function requestLocationGrant() {
    if (!geolocationSupported) return;
    locationEnableBtn.disabled = true;
    try {
      lastFix = toFix(await readPosition());
      setStoredGrant(true); // Leg 2: only ever set after a capture actually succeeded.
      startBackgroundLocationRefresh();
    } catch (error) {
      handlePositionError(error);
    } finally {
      locationEnableBtn.disabled = false;
      await updateLocationControl();
    }
  }

  async function initLocation() {
    if (!geolocationSupported) {
      await updateLocationControl(); // paint "Not available" — do NOT gate this behind the early return
      return;
    }

    locationEnableBtn?.addEventListener("click", requestLocationGrant);

    // Leg 1 browsers tell us when the permission changes (granted in site settings, or revoked).
    const status = await geolocationPermissionStatus();
    status?.addEventListener?.("change", async () => {
      if (status.state !== "granted") {
        lastFix = null;
        stopBackgroundLocationRefresh();
      } else {
        await refreshFix();
        startBackgroundLocationRefresh();
      }
      await updateLocationControl();
    });

    document.addEventListener("visibilitychange", async () => {
      if (document.visibilityState === "visible") {
        await refreshFix();
        startBackgroundLocationRefresh();
      } else {
        stopBackgroundLocationRefresh();
      }
    });

    await updateLocationControl();

    // Already authorized from an earlier visit: warm the cache now so the first message of the
    // session can carry a position, without prompting (an already-granted permission resolves silently).
    if (await isCaptureAuthorized()) {
      await refreshFix();
      startBackgroundLocationRefresh();
    }
  }

  /* ————— Web Push notifications (Preferences → General, ADR 0028) ————— */

  // Filled from GET /api/push/config on init: whether the server enabled push and the VAPID public key
  // the browser needs to subscribe. pushSupported also requires the browser APIs (feature detection).
  let pushConfig = null;
  const pushSupported =
    "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;

  // VAPID public keys are base64url; pushManager.subscribe wants a Uint8Array applicationServerKey.
  function urlBase64ToUint8Array(base64) {
    const padding = "=".repeat((4 - (base64.length % 4)) % 4);
    const normalized = (base64 + padding).replace(/-/g, "+").replace(/_/g, "/");
    const raw = atob(normalized);
    return Uint8Array.from(raw, (c) => c.charCodeAt(0));
  }

  // Reflects the push state in Preferences → General. Safe to call while the dialog is closed. The whole
  // section stays hidden unless the server enabled push AND this browser supports it (so unsupported
  // browsers, including iOS-in-browser, simply never see the toggle).
  async function updatePushControl() {
    if (!pushSection) return;
    if (!pushConfig || !pushConfig.enabled || !pushSupported) {
      pushSection.hidden = true;
      return;
    }
    pushSection.hidden = false;
    try {
      const reg = await navigator.serviceWorker.ready;
      const sub = await reg.pushManager.getSubscription();
      pushStatus.textContent = sub ? "On" : "Off";
      pushToggleBtn.textContent = sub ? "Disable" : "Enable";
    } catch {
      pushStatus.textContent = "Off";
      pushToggleBtn.textContent = "Enable";
    }
  }

  // Subscribe (prompting for permission) or unsubscribe this device, then sync with the server. The only
  // place a notification permission prompt is triggered — on an explicit tap.
  async function togglePush() {
    if (!pushConfig || !pushConfig.enabled || !pushSupported) return;
    pushToggleBtn.disabled = true;
    try {
      const reg = await navigator.serviceWorker.ready;
      const existing = await reg.pushManager.getSubscription();
      if (existing) {
        await fetch("/api/push/subscriptions?endpoint=" + encodeURIComponent(existing.endpoint),
          { method: "DELETE" });
        await existing.unsubscribe();
      } else {
        if (await Notification.requestPermission() !== "granted") return;
        const sub = await reg.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: urlBase64ToUint8Array(pushConfig.publicKey),
        });
        const keys = sub.toJSON().keys;
        await fetch("/api/push/subscriptions", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ endpoint: sub.endpoint, p256dh: keys.p256dh, auth: keys.auth }),
        });
      }
    } catch (err) {
      console.warn("Push toggle failed", err);
    } finally {
      pushToggleBtn.disabled = false;
      await updatePushControl();
    }
  }

  async function initPush() {
    if (!pushSupported) return; // feature-detect: unsupported browsers keep the section hidden
    try {
      const res = await fetch("/api/push/config");
      if (res.ok) pushConfig = await res.json();
    } catch { /* config unreachable — leave push disabled */ }

    pushToggleBtn?.addEventListener("click", togglePush);

    // A notificationclick in the SW focuses this client and posts which conversation to open.
    navigator.serviceWorker?.addEventListener("message", (event) => {
      if (event.data && event.data.type === "open-conversation" && event.data.conversationId) {
        selectConversation(event.data.conversationId);
      }
    });

    await updatePushControl();
  }

  /* ————— sending & streaming ————— */

  async function send(prompt) {
    stopSpeech(); // capture the dictated prompt and reset the mic before clearing the box
    streaming = true;
    sendBtn.disabled = true;
    modelSelect.disabled = true;
    // Ephemerality is fixed at creation (ADR 0034): once the first prompt goes out, lock the toggle
    // for the life of this chat. "New chat" re-enables it.
    incognitoBtn.disabled = true;
    input.value = "";
    autosize();

    // Snapshot the composed attachments for this turn and clear the strip so the next turn
    // starts clean. They were uploaded to activeId, so they belong to this conversation.
    const turnAttachments = pendingAttachments;
    const convId = activeId;
    clearPendingAttachments();

    // Follow-up suggestion chips are live-only and belong to the newest turn: drop any left over from
    // the previous reply before this turn appends its own.
    clearSuggestions();

    const userMsg = addMessage("user");
    fillUserBubble(userMsg, prompt, turnAttachments, convId);
    const agentMsg = addMessage("agent");
    setAgentMarkdown(agentMsg, "", { caret: true });
    scrollToBottom();

    let answer = "";
    try {
      // In an agent chat the model is pinned server-side, so never send a model (it would
      // switch the conversation off the agent's model). agentId is sent only on the first
      // turn of a freshly started agent chat (pendingAgentId), never on resumes.
      const isAgentChat = activeAgentId != null;
      // Barge-in (ADR 0037): an AbortController lets voice mode (or teardown) stop generation
      // mid-stream the instant the user talks over the reply.
      sendAbort = new AbortController();
      const res = await fetch(`/api/chat/${encodeURIComponent(activeId)}`, {
        method: "POST",
        signal: sendAbort.signal,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          message: prompt,
          model: isAgentChat ? null : (modelSelect.value || null),
          agentId: pendingAgentId,
          attachmentIds: turnAttachments.map((a) => a.id),
          // Whatever fix is already cached (ADR 0012) — read synchronously, never awaited. No
          // geolocation outcome (denied, timed out, unsupported, stale, absent) may delay a chat.
          location: currentLocationForSend(),
          // Incognito (ADR 0034): the server skips this turn's session write and never lists the
          // conversation, so it leaves no trace. Also keeps it out of the offline cache — an ephemeral
          // chat is never TouchAsync'd, so refreshConversations()/cachePut never see it.
          ephemeral,
        }),
      });
      if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";

      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        const lines = buffer.split("\n");
        buffer = lines.pop() ?? "";
        for (const line of lines) {
          if (!line.trim()) continue;
          const chunk = JSON.parse(line);
          if (chunk.type === "textDelta") {
            answer += chunk.text;
            setAgentMarkdown(agentMsg, answer, { caret: true });
            voice.onDelta(chunk.text); // speak the reply as it streams (ADR 0037); no-op when voice mode is off
          } else if (chunk.type === "toolCallStart") {
            addToolCall(agentMsg, chunk);
          } else if (chunk.type === "toolCallEnd") {
            completeToolCall(agentMsg, chunk);
          } else if (chunk.type === "image") {
            // An image-generation model produced an image (ADR 0020): render it live in the assistant
            // bubble via the display proxy, exactly like it will re-render on reload.
            addAgentImage(agentMsg, activeId, chunk.attachmentId);
          } else if (chunk.type === "suggestions") {
            // Up to 3 clickable follow-up prompts under the reply; clicking one sends it immediately.
            renderSuggestions(agentMsg, chunk.suggestions);
          } else if (chunk.type === "done") {
            showUsage(agentMsg, chunk);
          }
          scrollToBottom();
        }
      }
      setAgentMarkdown(agentMsg, answer);
      // Live-streamed reply is final: capture the raw markdown accumulator by closure for copy.
      addCopyButton(agentMsg, answer);
      voice.onStreamEnd(); // speak any buffered tail (ADR 0037); no-op when voice mode is off
      // The persona is now bound server-side; don't resend agentId on later turns. Kept on
      // failure so a retry rebinds.
      pendingAgentId = null;
    } catch (err) {
      // Barge-in (ADR 0037): an aborted turn is a deliberate interruption, not a failure —
      // leave the partial reply as-is, don't paint the error state. voice mode drives what
      // happens next (it speaks the tail / captures the interrupting utterance).
      if (err.name === "AbortError") {
        setAgentMarkdown(agentMsg, answer);
        voice.onStreamEnd();
      } else {
        agentMsg.classList.add("is-error");
        setAgentMarkdown(agentMsg, `**Transmission failed.** ${err.message ?? err}`);
      }
    } finally {
      sendAbort = null;
      streaming = false;
      sendBtn.disabled = false;
      modelSelect.disabled = modelSelect.options.length === 0;
      applyModelAvailability(); // keep the composer gated if there's still no model (ADR 0019)
      scrollToBottom();
      input.focus();
      refreshConversations();
    }
  }

  /* ————— composer ————— */

  function autosize() {
    input.style.height = "auto";
    input.style.height = Math.min(input.scrollHeight, 180) + "px";
  }

  /* ————— profile / preferences ————— */

  // Paint an already-resolved theme ("light"|"dark"): set data-theme on <html> and sync the
  // mobile browser-chrome color. Kept separate so the OS-follow listener can repaint cheaply.
  function paintTheme(resolved) {
    document.documentElement.dataset.theme = resolved;
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = resolved === "dark" ? "#100f0d" : "#f4efe4";
  }

  // Resolve a stored choice to the explicit theme actually painted.
  function resolveTheme(choice) {
    if (choice === "light" || choice === "dark") return choice;
    return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  // Apply a theme choice ("system"|"light"|"dark"): paint it, write the pre-paint cache, and —
  // only for "system" — (re)register a live OS-follow listener (removed for an explicit choice).
  function applyTheme(choice) {
    const c = choice === "light" || choice === "dark" || choice === "system" ? choice : "system";
    paintTheme(resolveTheme(c));
    try { localStorage.setItem(THEME_KEY, c); } catch { /* private mode — cache is best-effort */ }

    if (systemThemeMedia && onSystemThemeChange) {
      systemThemeMedia.removeEventListener("change", onSystemThemeChange);
      systemThemeMedia = null;
      onSystemThemeChange = null;
    }
    if (c === "system") {
      systemThemeMedia = matchMedia("(prefers-color-scheme: dark)");
      onSystemThemeChange = () => paintTheme(resolveTheme("system"));
      systemThemeMedia.addEventListener("change", onSystemThemeChange);
    }
  }

  // Boot the theme: re-apply the cached choice (the head script painted it but registered no
  // OS-follow listener), then reconcile against the server profile, which wins (ADR 0005).
  async function initTheme() {
    let cached = "system";
    try { cached = localStorage.getItem(THEME_KEY) ?? "system"; } catch { /* private mode */ }
    applyTheme(cached);
    await loadProfile();
    if (profile && profile.theme) applyTheme(profile.theme);
  }

  // GET the server-stored profile (the source of truth). Silent on failure — the panel still
  // opens with the device-language default.
  async function loadProfile() {
    try {
      const res = await fetch("/api/profile");
      if (!res.ok) return;
      profile = await res.json();
    } catch { /* silo may be unreachable; keep the current cached profile */ }
  }

  // Fills the language <select>, preselecting the stored tag. A stored tag that isn't in the
  // curated list is appended so it's never silently dropped.
  function fillLanguageOptions(select, selectedTag) {
    select.innerHTML = "";
    for (const lang of LANGUAGES) {
      const opt = document.createElement("option");
      opt.value = lang.tag;
      opt.textContent = lang.name;
      select.appendChild(opt);
    }
    if (selectedTag && ![...select.options].some((o) => o.value === selectedTag)) {
      const opt = document.createElement("option");
      opt.value = selectedTag;
      opt.textContent = selectedTag;
      select.appendChild(opt);
    }
    select.value = selectedTag ?? "";
  }

  // Shared ARIA tablist controller. Given [tab, panel] pairs in tablist order, attaches each tab's
  // click + arrow/Home/End keyboard nav and returns { select(index, focus) } — select toggles
  // aria-selected + roving tabindex on the tabs and `hidden` on the panels; `focus` moves keyboard
  // focus to the newly selected tab (arrow-key nav). Used by both the Preferences and agent dialogs.
  function wireTabs(tabPairs) {
    function select(index, focus = false) {
      tabPairs.forEach(([tab, panel], i) => {
        const active = i === index;
        tab.setAttribute("aria-selected", active ? "true" : "false");
        tab.tabIndex = active ? 0 : -1;
        panel.hidden = !active;
        if (active && focus) tab.focus();
      });
    }
    tabPairs.forEach(([tab], i) => {
      tab.addEventListener("click", () => select(i));
      tab.addEventListener("keydown", (e) => {
        const last = tabPairs.length - 1;
        let next = null;
        if (e.key === "ArrowRight" || e.key === "ArrowDown") next = i === last ? 0 : i + 1;
        else if (e.key === "ArrowLeft" || e.key === "ArrowUp") next = i === 0 ? last : i - 1;
        else if (e.key === "Home") next = 0;
        else if (e.key === "End") next = last;
        if (next !== null) {
          e.preventDefault();
          select(next, true);
        }
      });
    });
    return { select };
  }

  async function openProfileDialog() {
    await loadProfile(); // reconcile against the server before showing the current value
    fillLanguageOptions(profileLanguage, profile.preferredLanguage ?? "");
    // Null enabledTools (never saved, or saved before ADR 0021) means "all selectable tools".
    renderToolChecks(profileToolChecks, profile.enabledTools ?? allToolKeys());
    profileTheme.value = profile.theme ?? "system";
    // Null-sentinel: null (never saved) and true both mean ON; only an explicit false is unchecked.
    profileSuggest.checked = profile.suggestFollowUps !== false;
    profileSaved = false; // fresh edit session; the close handler reverts a preview unless we save
    profileTabs.select(0); // always open on General
    updateLocationControl(); // refresh the location status/button for the current grant state
    updatePushControl(); // and the push notifications status/button (ADR 0028)
    loadProviders(); // refresh the user's providers list alongside the profile
    loadMcpServers(); // and their MCP servers (config-driven remote MCP servers)
    loadConfigHealth(); // and the STATIC configuration health (live probes are opt-in, on the button)
    profileDialog.showModal();
    profileLanguage.focus();
  }

  async function saveProfile() {
    // The endpoint does read-modify-write (absent-means-keep), so sending these fields never wipes the
    // others. enabledTools is the full explicit list — "all" is every key (ADR 0021) — but ONLY when the
    // catalog loaded: if /api/tools failed, toolCatalog is [] and sending [] would wipe the default to
    // "none". Omitting it then leaves the stored default untouched (absent-means-keep).
    const payload = {
      preferredLanguage: profileLanguage.value,
      theme: profileTheme.value,
      suggestFollowUps: profileSuggest.checked,
    };
    if (toolCatalog.length > 0) payload.enabledTools = readToolChecks(profileToolChecks);
    try {
      const res = await fetch("/api/profile", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!res.ok) return;
      profile = await res.json(); // reconcile: the server trims/caps the tag and normalizes theme
    } catch { return; /* silo may be unreachable; leave the dialog open */ }

    applyTheme(profile.theme ?? "system"); // commit the server-normalized value (paint + cache)
    profileSaved = true;
    profileDialog.close();
  }

  openProfileBtn.addEventListener("click", () => openProfileDialog());
  profileCancelBtn.addEventListener("click", () => profileDialog.close());
  // Live-preview the theme as the user changes the select (staged: not persisted until Save).
  profileTheme.addEventListener("change", () => applyTheme(profileTheme.value));
  // Revert an unsaved preview when the dialog closes via Cancel, Esc, or backdrop.
  profileDialog.addEventListener("close", () => {
    if (!profileSaved) applyTheme(profile.theme ?? "system");
    profileSaved = false;
  });
  profileForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveProfile();
  });

  /* ————— selectable tools (ADR 0021) ————— */

  // The catalog is the single source of truth in the backend (ToolCatalog); fetch it once and drive
  // every checkbox group off it, so a new tool group appears in all three surfaces with no UI change.
  async function loadTools() {
    try {
      const res = await fetch("/api/tools");
      if (!res.ok) return;
      toolCatalog = await res.json();
    } catch { /* silo may be unreachable; the catalog stays empty and no checkboxes render */ }
  }

  const allToolKeys = () => toolCatalog.map((t) => t.key);

  // Re-fetch /api/tools after MCP CRUD (ADR 0026): the catalog is per-user for the MCP portion, so a newly
  // added/removed/toggled server must appear/disappear in the pickers without a page reload. Re-render the
  // Preferences Tools tab in place (preserving current checks) so it reflects the change even if it was
  // already open; the composer popover and agent editor re-render on their next open.
  async function refreshToolCatalog() {
    await loadTools();
    if (profileToolChecks.children.length > 0) {
      renderToolChecks(profileToolChecks, readToolChecks(profileToolChecks));
    }
  }

  // Renders one checkbox per catalog tool into `container`, checked when its key is in `selectedKeys`.
  // Per-server MCP keys (`mcp:{id}`, ADR 0026) render under an "MCP servers" subheading beneath the built-in
  // tools. A stored/effective selection may carry the bare `mcp` wildcard (a null==all default that reached
  // the client, or legacy state) — since it has no checkbox, treat it as "every MCP server checked" so a
  // save re-emits explicit per-server keys instead of silently dropping the wildcard (the ADR 0026 hazard).
  function renderToolChecks(container, selectedKeys) {
    const selected = new Set(selectedKeys);
    const mcpWildcard = selected.has("mcp");
    container.innerHTML = "";
    const builtins = toolCatalog.filter((t) => !t.key.startsWith("mcp:"));
    const mcpServers = toolCatalog.filter((t) => t.key.startsWith("mcp:"));

    const addCheck = (tool) => {
      const label = document.createElement("label");
      label.className = "tool-check";
      const cb = document.createElement("input");
      cb.type = "checkbox";
      cb.value = tool.key;
      cb.checked = selected.has(tool.key) || (mcpWildcard && tool.key.startsWith("mcp:"));
      const span = document.createElement("span");
      span.textContent = tool.requiresHostedTools ? `${tool.label} (Responses)` : tool.label;
      label.append(cb, span);
      container.appendChild(label);
    };

    for (const tool of builtins) addCheck(tool);
    if (mcpServers.length > 0) {
      const heading = document.createElement("div");
      heading.className = "tool-check__heading";
      heading.textContent = "MCP servers";
      container.appendChild(heading);
      for (const tool of mcpServers) addCheck(tool);
    }
  }

  const readToolChecks = (container) =>
    [...container.querySelectorAll("input[type=checkbox]")].filter((c) => c.checked).map((c) => c.value);

  // The per-conversation popover. Renders from chatTools; each toggle PUTs the new set for the active
  // conversation. The id is minted upfront (startNewConversation), so this persists an override even
  // before the first message — the grain activates and stores it, and the first send reuses that grain.
  function openToolsDialog() {
    renderToolChecks(chatToolChecks, chatTools);
    toolsDialog.showModal();
  }

  async function persistChatTools() {
    chatTools = readToolChecks(chatToolChecks);
    if (!activeId) return;
    try {
      const res = await fetch(`/api/conversations/${encodeURIComponent(activeId)}/tools`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tools: chatTools }),
      });
      if (res.ok) chatTools = await res.json(); // reconcile with the server-sanitized effective set
    } catch { /* silo may be unreachable; keep the local selection, retried on the next toggle */ }
  }

  toolsBtn.addEventListener("click", openToolsDialog);
  toolsDoneBtn.addEventListener("click", () => toolsDialog.close());

  // Flip incognito for the fresh chat (ADR 0034). The button is disabled once a chat has started, so
  // this only ever fires while the flag is still settable.
  incognitoBtn.addEventListener("click", () => setIncognito(!ephemeral));
  chatToolChecks.addEventListener("change", persistChatTools);

  /* ————— model providers (per-user overlay, ADR 0018) ————— */

  async function loadProviders() {
    try {
      const res = await fetch("/api/providers");
      if (!res.ok) return;
      const data = await res.json();
      userProviders = data.providers ?? [];
      userDefaultModel = data.default ?? null;
      userDefaultEmbeddingModel = data.embeddingDefault ?? null;
      renderProviders();
      syncDefaultModelSelect();
      syncEmbeddingModelSelect();
    } catch { /* silo may be unreachable; the list refreshes on next open */ }
  }

  // Repaints the default-model select from the CURRENT modelsData + stored preference. It is an idempotent
  // repaint hooked into BOTH of the two loaders, because each owns only half the inputs and they are called
  // in different combinations: preferences-open runs loadProviders() alone (which owns the stored value),
  // deleteProvider() runs loadModels() alone (which owns the catalog), and saveProvider() runs both. Hooking
  // either one alone would leave the select stale on the other's path. Reuses fillModelOptions/selectModel —
  // a stored default whose provider is gone shows under "Unavailable" rather than silently resetting.
  function syncDefaultModelSelect() {
    fillModelOptions(defaultModelSelect);
    const blank = document.createElement("option");
    blank.value = "";
    blank.textContent = "First available";
    defaultModelSelect.insertBefore(blank, defaultModelSelect.firstChild);
    defaultModelSelect.value = "";
    selectModel(defaultModelSelect, userDefaultModel);
  }

  // Persists the pick immediately (there is no Save button on this tab — the value lives in the provider
  // overlay grain, not the profile). A blank selection clears the preference back to "first available".
  // A failed save snaps the select BACK to the stored value: with no Save button there is no other signal,
  // and leaving the rejected pick on screen would read as saved.
  async function persistDefaultModel() {
    const value = defaultModelSelect.value;
    try {
      const res = await fetch("/api/default-model", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ default: value || null }),
      });
      if (!res.ok) return syncDefaultModelSelect();
    } catch { return syncDefaultModelSelect(); /* silo unreachable — don't show an unsaved value */ }
    userDefaultModel = value || null;
    loadModels({ pin: false }); // refresh the resolved default WITHOUT retargeting the open conversation
  }

  // Repaints the embedding-model select from the models a provider marked as embedding models, plus a leading
  // "None" option. Hooked into loadProviders() (owns the stored value) and loadModels() (owns the catalog),
  // same idempotent-repaint reasoning as syncDefaultModelSelect(). A stored id whose model is gone shows under
  // "Unavailable" via selectModel rather than silently resetting.
  function syncEmbeddingModelSelect() {
    if (!embeddingModelSelect) return;
    embeddingModelSelect.innerHTML = "";
    const none = document.createElement("option");
    none.value = "";
    none.textContent = "None (document Q&A off)";
    embeddingModelSelect.appendChild(none);

    const groups = new Map();
    for (const m of modelsData.models ?? []) {
      if (!m.supportsEmbeddings) continue; // only embedding-capable models are pickable here
      const provider = m.provider ?? "";
      if (!groups.has(provider)) {
        const group = document.createElement("optgroup");
        group.label = providerGroupLabel(m);
        groups.set(provider, group);
        embeddingModelSelect.appendChild(group);
      }
      const opt = document.createElement("option");
      opt.value = m.id;
      opt.textContent = m.displayName ?? m.id;
      groups.get(provider).appendChild(opt);
    }

    embeddingModelSelect.value = "";
    selectModel(embeddingModelSelect, userDefaultEmbeddingModel);
  }

  // Persists the embedding-model pick immediately (no Save button — the value lives in the provider overlay
  // grain). Blank clears it (document Q&A off). A failed save snaps the select back to the stored value.
  async function persistEmbeddingModel() {
    const value = embeddingModelSelect.value;
    try {
      const res = await fetch("/api/default-embedding-model", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ embeddingDefault: value || null }),
      });
      if (!res.ok) return syncEmbeddingModelSelect();
    } catch { return syncEmbeddingModelSelect(); /* silo unreachable — don't show an unsaved value */ }
    userDefaultEmbeddingModel = value || null;
  }

  function renderProviders() {
    providerList.innerHTML = "";
    if (userProviders.length === 0) {
      const li = document.createElement("li");
      li.className = "providers__empty";
      li.textContent = "no providers added";
      providerList.appendChild(li);
      return;
    }
    for (const provider of userProviders) {
      const li = document.createElement("li");
      li.className = "provider-row";

      const meta = document.createElement("div");
      meta.className = "provider-row__meta";
      const name = document.createElement("span");
      name.className = "provider-row__name";
      name.textContent = provider.displayName || provider.key;
      const sub = document.createElement("span");
      sub.className = "provider-row__sub";
      // Endpoint plus a lock glyph when a key is stored — never the key itself (it is never returned).
      sub.textContent = `${provider.key} · ${provider.endpoint ?? ""}${provider.hasKey ? " · 🔑" : ""}`;
      sub.title = provider.endpoint ?? "";
      meta.append(name, sub);

      const edit = document.createElement("button");
      edit.type = "button";
      edit.className = "provider-row__action";
      edit.setAttribute("aria-label", `Edit provider: ${provider.key}`);
      edit.textContent = "✎";
      edit.addEventListener("click", () => openProviderDialog(provider));

      const del = document.createElement("button");
      del.type = "button";
      del.className = "provider-row__action";
      del.setAttribute("aria-label", `Delete provider: ${provider.key}`);
      del.textContent = "🗑";
      del.addEventListener("click", () => deleteProvider(provider.key));

      li.append(meta, edit, del);
      providerList.appendChild(li);
    }
  }

  // ————— configuration health (Preferences → Configuration) —————

  // Fetches the health report and renders it. probe=false (the default, on tab open) is the STATIC report
  // and runs no metered calls; probe=true runs the opt-in live probes (button). Mirrors loadProviders.
  async function loadConfigHealth(probe = false) {
    try {
      const res = await fetch(`/api/config-health${probe ? "?probe=true" : ""}`);
      if (!res.ok) return;
      renderConfigHealth(await res.json());
    } catch { /* silo may be unreachable; the list refreshes on next open */ }
  }

  // Groups the checks by their Group label and renders each as a status pill + key + curated message. The
  // report never carries a secret, so every field here is safe to paint verbatim.
  function renderConfigHealth(report) {
    configHealthList.innerHTML = "";
    const checks = report.checks ?? [];
    if (checks.length === 0) {
      const empty = document.createElement("p");
      empty.className = "providers__empty";
      empty.textContent = "no checks";
      configHealthList.appendChild(empty);
      return;
    }

    const env = document.createElement("p");
    env.className = "config-health__env";
    env.textContent = `environment: ${report.environment ?? "?"}`;
    configHealthList.appendChild(env);

    const groups = [];
    for (const check of checks) {
      let group = groups.find((g) => g.name === check.group);
      if (!group) { group = { name: check.group, items: [] }; groups.push(group); }
      group.items.push(check);
    }

    for (const group of groups) {
      const heading = document.createElement("h3");
      heading.className = "config-health__group";
      heading.textContent = group.name;
      configHealthList.appendChild(heading);

      const list = document.createElement("ul");
      list.className = "config-health__checks";
      for (const check of group.items) {
        const li = document.createElement("li");
        li.className = "config-check";

        const pill = document.createElement("span");
        pill.className = `config-check__pill config-check__pill--${check.status}`;
        pill.textContent = check.status;

        const body = document.createElement("div");
        body.className = "config-check__body";
        const key = document.createElement("span");
        key.className = "config-check__key";
        key.textContent = check.key + (check.billable ? " · billable" : "");
        const msg = document.createElement("span");
        msg.className = "config-check__msg";
        msg.textContent = check.message ?? "";
        body.append(key, msg);

        li.append(pill, body);
        list.appendChild(li);
      }
      configHealthList.appendChild(list);
    }
  }

  // The Responses-API toggle is Foundry-only (ADR 0014) — an OpenAI-compatible provider ignores it.
  function syncProviderKindUi() {
    providerResponsesField.hidden = providerKindSelect.value !== "AzureOpenAI";
  }

  // Appends one editable model row. tools/vision gate what is sent to the model (ADR 0013), so they
  // default OFF for a new row — the unsafe direction is a wrong "true".
  function addModelRow(model) {
    const li = document.createElement("li");
    li.className = "provider-model-row";

    const id = document.createElement("input");
    id.type = "text";
    id.className = "agent-form__input provider-model-row__id";
    id.placeholder = "model id (e.g. llama3.1)";
    id.value = model?.id ?? "";

    const display = document.createElement("input");
    display.type = "text";
    display.className = "agent-form__input provider-model-row__display";
    display.placeholder = "display name (optional)";
    display.value = model?.displayName ?? "";

    // Capabilities as a compact dropdown (a <details> disclosure) rather than four inline checkboxes, so a
    // model row stays short. Order is load-bearing: collectModels reads the .agent-form__checkbox inputs
    // positionally (tools, vision, image gen, embeddings), so keep this list in that order.
    // image gen (ADR 0020) turns a turn into an image; embeddings marks a document-RAG model (ADR 0030).
    const caps = document.createElement("details");
    caps.className = "provider-model-row__caps";
    const capsSummary = document.createElement("summary");
    capsSummary.className = "provider-model-row__caps-summary";
    caps.appendChild(capsSummary);

    const capsBody = document.createElement("div");
    capsBody.className = "provider-model-row__caps-body";
    const capDefs = [
      { label: "tools", checked: model?.supportsTools ?? false },
      { label: "vision", checked: model?.supportsVision ?? false },
      { label: "image gen", checked: model?.supportsImageGeneration ?? false },
      { label: "embeddings", checked: model?.supportsEmbeddings ?? false },
    ];
    const capBoxes = [];
    const updateCapsSummary = () => {
      const on = capBoxes.filter((c) => c.box.checked).map((c) => c.label);
      capsSummary.textContent = on.length ? on.join(", ") : "capabilities";
    };
    for (const def of capDefs) {
      const flag = document.createElement("label");
      flag.className = "provider-model-row__flag";
      const box = document.createElement("input");
      box.type = "checkbox";
      box.className = "agent-form__checkbox";
      box.checked = def.checked;
      box.addEventListener("change", updateCapsSummary);
      flag.append(box, document.createTextNode(def.label));
      capsBody.appendChild(flag);
      capBoxes.push({ box, label: def.label });
    }
    caps.appendChild(capsBody);
    updateCapsSummary();
    // Close the dropdown on an outside click, like a normal menu.
    caps.addEventListener("toggle", () => {
      if (!caps.open) return;
      const onDocClick = (e) => {
        if (!caps.contains(e.target)) { caps.open = false; document.removeEventListener("click", onDocClick); }
      };
      document.addEventListener("click", onDocClick);
    });

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "provider-model-row__remove";
    remove.setAttribute("aria-label", "Remove model");
    remove.textContent = "×";
    remove.addEventListener("click", () => li.remove());

    li.append(id, display, caps, remove);
    providerModelList.appendChild(li);
  }

  function collectModels() {
    const models = [];
    for (const row of providerModelList.querySelectorAll(".provider-model-row")) {
      const id = row.querySelector(".provider-model-row__id").value.trim();
      if (!id) continue; // an empty row is dropped, not sent
      const [tools, vision, image, embed] = row.querySelectorAll(".agent-form__checkbox");
      const display = row.querySelector(".provider-model-row__display").value.trim();
      models.push({
        id,
        displayName: display || null,
        supportsTools: tools.checked,
        supportsVision: vision.checked,
        supportsImageGeneration: image.checked,
        supportsEmbeddings: embed.checked,
      });
    }
    return models;
  }

  function openProviderDialog(provider) {
    editingProviderKey = provider?.key ?? null;
    providerFormTitle.textContent = provider ? "Edit provider" : "New provider";
    providerError.hidden = true;
    providerKeyInput.value = provider?.key ?? "";
    // The key is the identity — changing it would be a new provider, so it is read-only when editing.
    providerKeyInput.readOnly = Boolean(provider);
    providerKindSelect.value = provider?.kind ?? "OpenAICompatible";
    providerDisplayNameInput.value = provider?.displayName ?? "";
    providerEndpointInput.value = provider?.endpoint ?? "";
    providerApiKeyInput.value = ""; // never prefilled — the key is write-only (blank = keep on edit)
    providerResponsesApi.checked = provider?.useResponsesApi ?? false;
    syncProviderKindUi();

    providerModelList.innerHTML = "";
    for (const model of provider?.models ?? []) addModelRow(model);

    providerDialog.showModal();
    providerKeyInput.focus();
  }

  async function saveProvider() {
    const payload = {
      key: providerKeyInput.value.trim(),
      kind: providerKindSelect.value,
      displayName: providerDisplayNameInput.value.trim() || null,
      endpoint: providerEndpointInput.value.trim(),
      // Blank means "keep the stored key" on edit, or "no key" on create — the server maps both.
      apiKey: providerApiKeyInput.value,
      models: collectModels(),
      useResponsesApi: providerResponsesApi.checked,
    };
    if (!payload.key || !payload.endpoint) return;

    const url = editingProviderKey
      ? `/api/providers/${encodeURIComponent(editingProviderKey)}`
      : "/api/providers";
    const method = editingProviderKey ? "PUT" : "POST";
    try {
      const res = await fetch(url, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!res.ok) {
        // The endpoint returns a plain-text reason for a 400 (bad endpoint/kind) — surface it.
        providerError.textContent = (await res.text()) || "Could not save the provider.";
        providerError.hidden = false;
        return;
      }
    } catch { return; /* silo may be unreachable; leave the dialog open */ }

    providerDialog.close();
    await loadProviders();
    loadModels(); // the picker reflects the change immediately — no restart (ADR 0018)
  }

  async function deleteProvider(key) {
    try {
      const res = await fetch(`/api/providers/${encodeURIComponent(key)}`, { method: "DELETE" });
      if (!res.ok) return;
    } catch { return; }
    userProviders = userProviders.filter((p) => p.key !== key);
    renderProviders();
    loadModels();
  }

  defaultModelSelect.addEventListener("change", persistDefaultModel);
  if (embeddingModelSelect) embeddingModelSelect.addEventListener("change", persistEmbeddingModel);
  addProviderBtn.addEventListener("click", () => openProviderDialog(null));
  addModelRowBtn.addEventListener("click", () => addModelRow(null));
  providerKindSelect.addEventListener("change", syncProviderKindUi);
  providerCancelBtn.addEventListener("click", () => providerDialog.close());
  providerForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveProvider();
  });

  /* ————— remote MCP servers (config-driven remote MCP servers) ————— */

  async function loadMcpServers() {
    try {
      const res = await fetch("/api/mcp-servers");
      if (!res.ok) return;
      const data = await res.json();
      mcpServers = data.servers ?? [];
      renderMcpServers();
    } catch { /* silo may be unreachable; the list refreshes on next open */ }
  }

  function renderMcpServers() {
    mcpServerList.innerHTML = "";
    if (mcpServers.length === 0) {
      const li = document.createElement("li");
      li.className = "providers__empty";
      li.textContent = "no MCP servers added";
      mcpServerList.appendChild(li);
      return;
    }
    for (const server of mcpServers) {
      const li = document.createElement("li");
      li.className = "provider-row";

      const meta = document.createElement("div");
      meta.className = "provider-row__meta";
      const name = document.createElement("span");
      name.className = "provider-row__name";
      name.textContent = server.name;
      const sub = document.createElement("span");
      sub.className = "provider-row__sub";
      // Endpoint + auth + a key glyph when a secret is stored (never the secret) + a pause glyph when off.
      // For OAuth, a connection state (🔗 connected / not connected) instead of the key glyph.
      const isOAuth = server.auth === "InteractiveOAuth";
      const bits = [server.endpoint, server.auth];
      if (server.hasHeaderValue || server.hasClientSecret) bits.push("🔑");
      if (isOAuth) bits.push(server.hasToken ? "🔗 connected" : "not connected");
      if (!server.enabled) bits.push("⏸ off");
      sub.textContent = bits.join(" · ");
      sub.title = server.endpoint;
      meta.append(name, sub);

      // Interactive-OAuth servers get a Connect (or Reconnect) action that starts the sign-in flow. Full
      // navigation to /api/mcp/oauth/start — the server 302s to the identity provider and returns to /callback.
      if (isOAuth) {
        const connect = document.createElement("button");
        connect.type = "button";
        connect.className = "provider-row__action";
        connect.setAttribute("aria-label", `${server.hasToken ? "Reconnect" : "Connect"} MCP server: ${server.name}`);
        connect.textContent = "🔗";
        connect.title = server.hasToken ? "Reconnect (sign in again)" : "Connect (sign in)";
        connect.addEventListener("click", () => {
          window.location.href = `/api/mcp/oauth/start?serverId=${encodeURIComponent(server.id)}`;
        });
        li.append(meta, connect);
      } else {
        li.appendChild(meta);
      }

      const edit = document.createElement("button");
      edit.type = "button";
      edit.className = "provider-row__action";
      edit.setAttribute("aria-label", `Edit MCP server: ${server.name}`);
      edit.textContent = "✎";
      edit.addEventListener("click", () => openMcpDialog(server));

      const del = document.createElement("button");
      del.type = "button";
      del.className = "provider-row__action";
      del.setAttribute("aria-label", `Delete MCP server: ${server.name}`);
      del.textContent = "🗑";
      del.addEventListener("click", () => deleteMcpServer(server.id));

      li.append(edit, del);
      mcpServerList.appendChild(li);
    }
  }

  // Each auth mode reveals only the fields it needs: Header → header name/value; ClientCredentials →
  // authority/clientId/clientSecret/scopes; InteractiveOAuth → clientId (optional) + scopes, plus a note;
  // None → nothing.
  function syncMcpAuthUi() {
    const isHeader = mcpAuthSelect.value === "Header";
    const isCc = mcpAuthSelect.value === "ClientCredentials";
    const isOAuth = mcpAuthSelect.value === "InteractiveOAuth";
    mcpHeaderNameField.hidden = !isHeader;
    mcpHeaderValueField.hidden = !isHeader;
    mcpAuthorityField.hidden = !isCc;
    mcpClientSecretField.hidden = !isCc;
    mcpClientIdField.hidden = !(isCc || isOAuth);
    mcpScopesField.hidden = !(isCc || isOAuth);
    mcpOAuthNote.hidden = !isOAuth;
  }

  function openMcpDialog(server) {
    editingMcpId = server?.id ?? null;
    mcpFormTitle.textContent = server ? "Edit MCP server" : "New MCP server";
    mcpError.hidden = true;
    mcpNameInput.value = server?.name ?? "";
    mcpEndpointInput.value = server?.endpoint ?? "";
    mcpEnabledInput.checked = server?.enabled ?? true;
    mcpAuthSelect.value = server?.auth ?? "None";
    mcpHeaderNameInput.value = server?.headerName ?? "Authorization";
    mcpHeaderValueInput.value = ""; // never prefilled — write-only (blank = keep on edit)
    mcpAuthorityInput.value = server?.authority ?? "";
    mcpClientIdInput.value = server?.clientId ?? "";
    mcpClientSecretInput.value = ""; // never prefilled — write-only (blank = keep on edit)
    mcpScopesInput.value = (server?.scopes ?? []).join(" ");
    syncMcpAuthUi();

    mcpDialog.showModal();
    mcpNameInput.focus();
  }

  async function saveMcpServer() {
    const isHeader = mcpAuthSelect.value === "Header";
    const isCc = mcpAuthSelect.value === "ClientCredentials";
    const isOAuth = mcpAuthSelect.value === "InteractiveOAuth";
    const payload = {
      name: mcpNameInput.value.trim(),
      endpoint: mcpEndpointInput.value.trim(),
      auth: mcpAuthSelect.value,
      enabled: mcpEnabledInput.checked,
      headerName: isHeader ? mcpHeaderNameInput.value.trim() : null,
      // Blank means "keep the stored value" on edit, or "no value" on create — the server maps both.
      headerValue: isHeader ? mcpHeaderValueInput.value : null,
      authority: isCc ? mcpAuthorityInput.value.trim() : null,
      // clientId is shared by ClientCredentials and InteractiveOAuth (optional for OAuth — dynamic otherwise).
      clientId: (isCc || isOAuth) ? mcpClientIdInput.value.trim() : null,
      clientSecret: isCc ? mcpClientSecretInput.value : null, // write-only, blank = keep
      // Scopes are space- or comma-separated in the UI; the API stores a list.
      scopes: (isCc || isOAuth) ? mcpScopesInput.value.split(/[\s,]+/).filter(Boolean) : null,
    };
    if (!payload.name || !payload.endpoint) return;

    const url = editingMcpId
      ? `/api/mcp-servers/${encodeURIComponent(editingMcpId)}`
      : "/api/mcp-servers";
    const method = editingMcpId ? "PUT" : "POST";
    try {
      const res = await fetch(url, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!res.ok) {
        // The endpoint returns a plain-text reason for a 400 (bad endpoint/auth) — surface it.
        mcpError.textContent = (await res.text()) || "Could not save the MCP server.";
        mcpError.hidden = false;
        return;
      }
    } catch { return; /* silo may be unreachable; leave the dialog open */ }

    mcpDialog.close();
    await loadMcpServers();
    await refreshToolCatalog(); // the added/edited server must (dis)appear in the tool pickers (ADR 0026)
  }

  async function deleteMcpServer(id) {
    try {
      const res = await fetch(`/api/mcp-servers/${encodeURIComponent(id)}`, { method: "DELETE" });
      if (!res.ok) return;
    } catch { return; }
    mcpServers = mcpServers.filter((s) => s.id !== id);
    renderMcpServers();
    await refreshToolCatalog(); // the deleted server must drop out of the tool pickers (ADR 0026)
  }

  addMcpServerBtn.addEventListener("click", () => openMcpDialog(null));

  // Opt-in live checks (billable). Disable + relabel while probing so a double-click can't fire two runs,
  // then repaint with the probed report.
  runConfigProbesBtn.addEventListener("click", async () => {
    runConfigProbesBtn.disabled = true;
    const label = runConfigProbesBtn.textContent;
    runConfigProbesBtn.textContent = "Running…";
    try {
      await loadConfigHealth(true);
    } finally {
      runConfigProbesBtn.textContent = label;
      runConfigProbesBtn.disabled = false;
    }
  });
  mcpAuthSelect.addEventListener("change", syncMcpAuthUi);
  mcpCancelBtn.addEventListener("click", () => mcpDialog.close());
  mcpForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveMcpServer();
  });

  input.addEventListener("input", autosize);

  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      form.requestSubmit();
    }
  });

  form.addEventListener("submit", (e) => {
    e.preventDefault();
    const prompt = input.value.trim();
    // Allow an image-only turn (no text) as long as something is attached.
    if ((!prompt && pendingAttachments.length === 0) || streaming) return;
    send(prompt);
  });

  attachBtn.addEventListener("click", () => fileInput.click());
  fileInput.addEventListener("change", () => handleFiles([...fileInput.files]));

  /* ————— usage dashboard (ADR 0031) ————— */
  //
  // A dedicated full-page overlay (#usageView), not a Preferences tab — fed solely from OTEL GenAI
  // spans. No chart library: the only "chart" is a <div> given a percentage width (the .bar__fill
  // below), same idiom as the sibling AI.Telemetry.Dashboard this view mirrors. Deliberately no
  // total-tokens/cost field anywhere.

  const USAGE_UNREPORTED = "unreported";
  const usageNumberFormat = new Intl.NumberFormat();

  function setUsageTile(element, value, isErrorTile = false) {
    element.textContent = usageNumberFormat.format(value ?? 0);
    if (isErrorTile) element.dataset.nonzero = String((value ?? 0) > 0);
  }

  // model/provider/user come back as "" when the span never reported one — render the absence
  // instead of a blank cell, which reads as a rendering bug.
  function usageKeyCell(value) {
    const cell = document.createElement("td");
    if (!value) {
      cell.textContent = USAGE_UNREPORTED;
      cell.className = "cell--unreported";
    } else {
      cell.textContent = value;
      cell.className = "cell--key";
    }
    return cell;
  }

  function usageNumberCell(value, isErrorCount = false) {
    const cell = document.createElement("td");
    cell.className = isErrorCount ? "grid__num cell--error" : "grid__num";
    cell.textContent = usageNumberFormat.format(value ?? 0);
    if (isErrorCount) cell.dataset.nonzero = String((value ?? 0) > 0);
    return cell;
  }

  // A proportional bar — the row's value against the max in its table (input+output for
  // models/users, count for tools). A zero max means every row is zero; render an empty track
  // rather than dividing by it.
  function usageBarCell(value, max) {
    const cell = document.createElement("td");
    cell.className = "grid__bar";
    const bar = document.createElement("div");
    bar.className = "bar";
    const fill = document.createElement("span");
    fill.className = "bar__fill";
    fill.style.width = max > 0 ? `${(value / max) * 100}%` : "0%";
    bar.appendChild(fill);
    cell.appendChild(bar);
    return cell;
  }

  function renderUsageModels(models) {
    usageModelsBody.replaceChildren();
    usageModelsEmpty.hidden = models.length > 0;
    // Share is input+output. Cached input is a SUBSET of input, so folding it in would make the
    // bars describe a total that does not exist.
    const weight = (m) => m.inputTokens + m.outputTokens;
    const max = Math.max(0, ...models.map(weight));
    for (const model of models) {
      const row = document.createElement("tr");
      row.append(
        usageKeyCell(model.application),
        usageKeyCell(model.model),
        usageKeyCell(model.provider),
        usageNumberCell(model.inputTokens),
        usageNumberCell(model.outputTokens),
        usageNumberCell(model.cachedInputTokens),
        usageNumberCell(model.reasoningOutputTokens),
        usageNumberCell(model.callCount),
        usageNumberCell(model.errorCount, true),
        usageBarCell(weight(model), max),
      );
      usageModelsBody.appendChild(row);
    }
  }

  function renderUsageTools(tools) {
    usageToolsBody.replaceChildren();
    usageToolsEmpty.hidden = tools.length > 0;
    const max = Math.max(0, ...tools.map((tool) => tool.count));
    for (const tool of tools) {
      const row = document.createElement("tr");
      row.append(
        usageKeyCell(tool.application),
        usageKeyCell(tool.toolName),
        usageNumberCell(tool.count),
        usageNumberCell(tool.errorCount, true),
        usageBarCell(tool.count, max),
      );
      usageToolsBody.appendChild(row);
    }
  }

  function renderUsageUsers(users) {
    usageUsersBody.replaceChildren();
    usageUsersEmpty.hidden = users.length > 0;
    const weight = (u) => u.inputTokens + u.outputTokens;
    const max = Math.max(0, ...users.map(weight));
    for (const user of users) {
      const row = document.createElement("tr");
      row.append(
        usageKeyCell(user.user),
        usageNumberCell(user.inputTokens),
        usageNumberCell(user.outputTokens),
        usageNumberCell(user.cachedInputTokens),
        usageNumberCell(user.reasoningOutputTokens),
        usageNumberCell(user.callCount),
        usageNumberCell(user.errorCount, true),
        usageNumberCell(user.toolCallCount),
        usageBarCell(weight(user), max),
      );
      usageUsersBody.appendChild(row);
    }
  }

  function renderUsage(report) {
    setUsageTile(usageTiles.toolCalls, report.totals?.toolCallCount);
    setUsageTile(usageTiles.input, report.totals?.inputTokens);
    setUsageTile(usageTiles.output, report.totals?.outputTokens);
    setUsageTile(usageTiles.cached, report.totals?.cachedInputTokens);
    setUsageTile(usageTiles.calls, report.totals?.callCount);
    setUsageTile(usageTiles.errors, report.totals?.errorCount, true);
    renderUsageModels(report.models ?? []);
    renderUsageTools(report.tools ?? []);
    renderUsageUsers(report.users ?? []);
  }

  // Fills a <select> from a list endpoint, preserving the "all" option the markup already carries.
  // Silent-fail like the other loaders (loadModels/loadConfigHealth): the "all" option still works.
  async function loadUsageOptions(url, select) {
    try {
      const res = await fetch(url);
      if (!res.ok) return;
      for (const name of await res.json()) {
        const option = document.createElement("option");
        option.value = name;
        option.textContent = name;
        select.appendChild(option);
      }
    } catch { /* silo may be unreachable; the "all" option still works */ }
  }

  async function loadUsage() {
    const query = new URLSearchParams({
      application: usageApplication.value,
      user: usageUser.value,
      range: usageRange.value,
    });
    try {
      const res = await fetch(`/api/usage?${query}`);
      if (!res.ok) return;
      renderUsage(await res.json());
    } catch { /* silo may be unreachable; the view keeps its last-rendered numbers */ }
  }

  // Any prior timer is always cleared first so intervals never stack, and closeUsage() calls this
  // too (the leak guard) — a timer left running would keep polling /api/usage from the console view.
  function stopUsageAutoRefresh() {
    if (usageRefreshTimer !== null) {
      clearInterval(usageRefreshTimer);
      usageRefreshTimer = null;
    }
  }

  // Restarts the auto-refresh timer from the current selection. Value is a whole number of
  // seconds; 0 (Off) leaves it stopped.
  function applyUsageAutoRefresh() {
    stopUsageAutoRefresh();
    const seconds = Number(usageRefresh.value);
    if (seconds > 0) {
      usageRefreshTimer = setInterval(loadUsage, seconds * 1000);
    }
  }

  async function openUsage() {
    // Reset to just the "all" option before repopulating — reopening must not duplicate entries.
    usageApplication.innerHTML = '<option value="all">All applications</option>';
    usageUser.innerHTML = '<option value="all">All users</option>';
    await Promise.all([
      loadUsageOptions("/api/usage/applications", usageApplication),
      loadUsageOptions("/api/usage/users", usageUser),
    ]);
    await loadUsage();
    usageView.hidden = false;
    applyUsageAutoRefresh();
  }

  function closeUsage() {
    usageView.hidden = true;
    stopUsageAutoRefresh();
  }

  openUsageBtn.addEventListener("click", () => openUsage());
  usageCloseBtn.addEventListener("click", () => closeUsage());
  usageApplication.addEventListener("change", () => loadUsage());
  usageUser.addEventListener("change", () => loadUsage());
  usageRange.addEventListener("change", () => loadUsage());
  usageRefresh.addEventListener("change", applyUsageAutoRefresh);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !usageView.hidden) closeUsage();
  });

  /* ————— speech input (browser-native Web Speech API dictation) ————— */

  // Vendor-prefixed in Chrome/Edge; absent in Firefox. Transcription runs on the browser
  // vendor's cloud (ADR 0006), not in the app's Azure tenant. Requires a secure context —
  // prod is https and localhost counts, so DevHost exercises it with a real mic.
  const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SpeechRecognition) {
    micBtn.hidden = true; // graceful degradation: no dictation, no button, no wiring
  } else {
    // Chrome on Android flags every interim growth stage isFinal: true while reporting
    // confidence: 0; trusting the flag re-appends each stage to the permanent accumulator
    // ("Hoe" + "Hoe laat" + …), the double-output bug on Android PWAs. Distrust unscored
    // finals THERE ONLY — desktop engines can report confidence 0 on a genuine final, and
    // demoting it there would silently drop the segment. UA check, mirroring react-speech-recognition.
    const distrustZeroConfidenceFinals = /android/i.test(navigator.userAgent);

    const recognition = new SpeechRecognition();
    recognition.continuous = true;
    recognition.interimResults = true;

    const defaultPlaceholder = input.placeholder; // restored when a new dictation starts

    // Text already in the box when dictation began; the transcript is appended to it so a
    // dictated phrase never clobbers what the user typed first.
    let baseText = "";
    // Finalized transcript accumulated across result events. In continuous mode each phrase
    // lands at its own result index, so finals must be folded in incrementally (once each) —
    // rebuilding from the whole results list every event stacks every interim snapshot.
    let finalTranscript = "";
    // True between start and end. Guards a late result fired after a stop from repopulating
    // an already-cleared textarea (see stopSpeech / send).
    let recognizing = false;

    function setRecordingUi(on) {
      micBtn.setAttribute("aria-pressed", on ? "true" : "false");
    }

    // Only walk the results changed in THIS event (from resultIndex): fold newly-finalized
    // segments into finalTranscript once, and rebuild just the trailing interim live. A final
    // is trusted only when the engine actually scored it (confidence > 0) on Android — an
    // unscored "final" is really an interim growth stage, so it must fall through to the
    // self-correcting interim branch, not the permanent accumulator. Flat by design
    // (cognitive-complexity gate).
    recognition.addEventListener("result", (event) => {
      if (!recognizing) return; // ignore trailing results after a stop
      let interim = "";
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const res = event.results[i];
        const trustworthyFinal =
          res.isFinal && (!distrustZeroConfidenceFinals || (res[0].confidence ?? 0) > 0);
        if (trustworthyFinal) finalTranscript += res[0].transcript;
        else interim += res[0].transcript;
      }
      input.value = baseText + finalTranscript + interim;
      autosize(); // programmatic value writes don't fire the "input" event
      input.selectionStart = input.selectionEnd = input.value.length; // caret at end
    });

    recognition.addEventListener("start", () => {
      recognizing = true;
      setRecordingUi(true);
    });

    recognition.addEventListener("end", () => {
      // Fires on manual stop and on Chrome's silence/timeout auto-stop; reset either way.
      recognizing = false;
      setRecordingUi(false);
      input.focus();
    });

    recognition.addEventListener("error", (event) => {
      recognizing = false;
      setRecordingUi(false);
      if (event.error === "not-allowed" || event.error === "service-not-allowed") {
        // Mic permission denied/blocked — subtle one-shot hint, no intrusive alert.
        input.placeholder = "Microphone access blocked — check browser permissions.";
      }
    });

    micBtn.addEventListener("click", () => {
      if (recognizing) {
        recognition.stop();
        return;
      }
      baseText = input.value; // append to whatever is already in the box
      finalTranscript = ""; // fresh session — don't carry a prior dictation's finals
      input.placeholder = defaultPlaceholder; // clear any stale "mic blocked" hint
      // Read the cached profile at start (no re-fetch); fall back to the device language.
      recognition.lang = profile.preferredLanguage || navigator.language;
      try {
        recognition.start();
      } catch { /* start() throws if already started; ignore the double-tap race */ }
    });

    // Idempotent stop, wired into send(). Mutes late results synchronously (recognizing =
    // false) before send() clears the textarea, then stops the engine and resets the mic.
    stopSpeech = () => {
      if (!recognizing) return;
      recognizing = false;
      recognition.stop();
    };
  }

  /* ————— hands-free voice conversation (browser-native, with barge-in) (ADR 0037) ————— */

  // Extends dictation (ADR 0006): SpeechRecognition to listen + speechSynthesis to speak, in a
  // hands-free loop (listen → auto-send → speak → interrupt). A DEDICATED recognition instance —
  // never the dictation one above — so its delicate accumulation logic stays untouched; the two
  // mic uses are mutually exclusive. Needs both APIs; where either is absent the button is hidden
  // (graceful degradation, mirroring the mic). speechSynthesis output is a new egress-free path:
  // the reply text never leaves the browser (ADR 0013 unchanged); recognition still uses the
  // vendor's cloud like dictation (ADR 0006's accepted residual).
  if (!SpeechRecognition || !window.speechSynthesis) {
    voiceBtn.hidden = true;
  } else {
    const SILENCE_MS = 1200; // end-of-utterance debounce: auto-send this long after speech stops
    const synth = window.speechSynthesis;
    const vr = new SpeechRecognition();
    vr.continuous = true;
    vr.interimResults = true;

    let active = false;             // voice mode on/off
    let state = "idle";             // idle | listening | thinking | speaking
    let finalText = "";             // finalized transcript of the in-progress listening utterance
    let finalBuf = "";              // finalText + trailing interim, the candidate to auto-send
    let silenceTimer = null;
    let speakBuf = "";              // reply text not yet flushed to a complete-sentence utterance
    let spokenText = "";            // reply text already handed to synthesis this turn (echo ref)
    let lastSubmitted = "";         // last auto-sent utterance (so its trailing result ≠ barge-in)
    let interrupted = false;        // set by barge-in so the aborted turn's tail is not spoken

    // Voices load asynchronously; refresh the cache on change and pick the best lang match.
    let voices = synth.getVoices();
    synth.addEventListener("voiceschanged", () => { voices = synth.getVoices(); });
    function pickVoice(lang) {
      const l = (lang || "").toLowerCase();
      if (!voices.length || !l) return null;
      return voices.find((v) => (v.lang || "").toLowerCase() === l)
        || voices.find((v) => (v.lang || "").toLowerCase().startsWith(l.slice(0, 2)))
        || null;
    }

    // Strip the common markdown marks so TTS never reads "asterisk asterisk" / a URL aloud.
    function stripMarkdown(s) {
      return s
        .replace(/```[\s\S]*?```/g, " ")
        .replace(/`([^`]+)`/g, "$1")
        .replace(/!\[[^\]]*\]\([^)]*\)/g, " ")
        .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
        .replace(/[*_#>~]+/g, " ")
        .replace(/\s+/g, " ")
        .trim();
    }

    function setUi() {
      voiceBtn.setAttribute("aria-pressed", active ? "true" : "false");
      if (active) voiceBtn.dataset.state = state; else delete voiceBtn.dataset.state;
    }

    function resetTurn() {
      finalText = ""; finalBuf = "";
      speakBuf = ""; spokenText = "";
      clearTimeout(silenceTimer);
    }

    function armListening() {
      state = "listening";
      resetTurn();
      setUi();
    }

    // Re-arm the mic for the next turn once the reply has fully finished speaking.
    function maybeArmListening() {
      if (!active || state === "listening") return;
      if (synth.speaking || synth.pending || streaming) return;
      armListening();
    }

    function speak(text) {
      const clean = stripMarkdown(text);
      if (!clean) return;
      spokenText += " " + clean;
      const u = new SpeechSynthesisUtterance(clean);
      u.lang = vr.lang || navigator.language;
      const v = pickVoice(u.lang);
      if (v) u.voice = v;
      u.addEventListener("start", () => { if (active) { state = "speaking"; setUi(); } });
      u.addEventListener("end", maybeArmListening);
      u.addEventListener("error", maybeArmListening);
      synth.speak(u);
    }

    // Flush complete sentences from the delta buffer (or everything, at stream end) so speech
    // starts before the whole reply has arrived. Splits on the last sentence terminator.
    function flush(all) {
      if (all) {
        const rest = speakBuf; speakBuf = "";
        speak(rest);
        return;
      }
      let cut = -1;
      for (let i = 0; i < speakBuf.length; i++) {
        const c = speakBuf[i];
        if (c === "." || c === "!" || c === "?" || c === "\n") cut = i;
      }
      if (cut < 0) return;
      const chunk = speakBuf.slice(0, cut + 1);
      speakBuf = speakBuf.slice(cut + 1);
      speak(chunk);
    }

    // A recognition result while the assistant is (or was just) talking is usually the mic
    // hearing the TTS, or the trailing final of what we just sent — not a real interruption.
    // Treat it as self/echo when most of its words already appear in what we're speaking or
    // just submitted. Best-effort: headphones remain the clean guarantee (ADR 0037).
    function isSelfOrEcho(text) {
      const words = text.toLowerCase().match(/\p{L}+/gu) || [];
      if (!words.length) return true;
      const ref = (spokenText + " " + lastSubmitted).toLowerCase();
      const hits = words.filter((w) => ref.includes(w)).length;
      return hits / words.length >= 0.6;
    }

    function bargeIn(heard) {
      interrupted = true;
      try { synth.cancel(); } catch { /* nothing speaking */ }
      if (sendAbort) sendAbort.abort(); // stop generation mid-stream
      state = "listening";
      resetTurn();
      finalText = heard;
      finalBuf = heard.trim();
      setUi();
      restartSilence();
    }

    function restartSilence() {
      clearTimeout(silenceTimer);
      silenceTimer = setTimeout(trySubmit, SILENCE_MS);
    }

    // Auto-send the accumulated utterance. Defers while a turn is still unwinding (a barge-in
    // aborts the previous fetch; send()'s finally clears `streaming` a tick later).
    function trySubmit() {
      if (!active || state !== "listening") return;
      const t = finalBuf.trim();
      if (!t) return;
      if (streaming) { setTimeout(trySubmit, 150); return; }
      lastSubmitted = t;
      interrupted = false;
      state = "thinking";
      resetTurn();
      setUi();
      send(t); // fire-and-forget: onDelta speaks it, onStreamEnd flushes the tail
    }

    vr.addEventListener("result", (event) => {
      if (!active) return;
      let finals = "", interim = "";
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const r = event.results[i];
        if (r.isFinal) finals += r[0].transcript; else interim += r[0].transcript;
      }
      const heard = (finals + interim).trim();
      if (!heard) return;
      if (state === "speaking" || state === "thinking") {
        if (!isSelfOrEcho(heard)) bargeIn(heard); // genuine interruption
        return;
      }
      // listening: accumulate and restart the end-of-utterance countdown
      finalText += finals;
      finalBuf = (finalText + interim).trim();
      restartSilence();
    });

    // Chrome ends continuous recognition on silence/timeout; keep the mic alive while active.
    vr.addEventListener("end", () => { if (active) { try { vr.start(); } catch { /* race */ } } });

    vr.addEventListener("error", (event) => {
      if (event.error === "not-allowed" || event.error === "service-not-allowed") {
        stopVoice();
        input.placeholder = "Microphone access blocked — check browser permissions.";
      }
      // transient errors (no-speech, network): the 'end' handler restarts if still active
    });

    function startVoice() {
      if (active) return;
      stopSpeech(); // dictation and voice mode share the one mic — never both at once
      active = true;
      vr.lang = profile.preferredLanguage || navigator.language;
      armListening();
      try { vr.start(); } catch { /* start() throws if already started */ }
    }

    function stopVoice() {
      if (!active) return;
      active = false;
      state = "idle";
      clearTimeout(silenceTimer);
      try { synth.cancel(); } catch { /* nothing speaking */ }
      if (sendAbort) sendAbort.abort();
      try { vr.stop(); } catch { /* not started */ }
      resetTurn();
      setUi();
    }

    voiceBtn.addEventListener("click", () => { if (active) stopVoice(); else startVoice(); });

    // Expose the hooks send() calls each turn (no-ops until a turn is in flight).
    voice = {
      get active() { return active; },
      onDelta(text) { if (active) { speakBuf += text; flush(false); } },
      onStreamEnd() {
        if (!active) return;
        if (interrupted) { interrupted = false; return; } // aborted turn: don't speak its tail
        flush(true);
        setTimeout(maybeArmListening, 60); // empty/silent reply → re-arm without an utterance
      },
      stop: stopVoice,
    };
  }

  newChatBtn.addEventListener("click", startNewConversation);

  addAgentBtn.addEventListener("click", () => openAgentDialog(null));
  agentCancelBtn.addEventListener("click", () => agentDialog.close());
  agentForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveAgent();
  });

  // Regenerate the agent's API key (ADR 0022): confirm (the current key stops working), then save with
  // the regenerate flag so the new key comes back and gets revealed once.
  agentRegenerateKeyBtn.addEventListener("click", () => {
    if (!editingAgentId) return;
    if (!confirm("Regenerate this agent's API key? The current key stops working immediately.")) return;
    pendingRegenerate = true;
    saveAgent();
  });

  agentKeyCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(agentKeyValue.textContent);
      agentKeyCopyBtn.textContent = "Copied";
    } catch {
      /* clipboard blocked (insecure context / permission) — the key is visible to select manually */
    }
  });

  agentRunUrlCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(agentRunUrl.textContent);
      agentRunUrlCopyBtn.textContent = "Copied";
    } catch {
      /* clipboard blocked (insecure context / permission) — the URL is visible to select manually */
    }
  });

  menuToggle.addEventListener("click", toggleDrawer);
  backdrop.addEventListener("click", closeDrawer);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && app.classList.contains("is-drawer-open")) {
      closeDrawer();
      menuToggle.focus();
    }
  });

  /* ————— PWA: register the service worker ————— */
  if ("serviceWorker" in navigator) {
    window.addEventListener("load", () => {
      navigator.serviceWorker.register("/sw.js").catch((err) =>
        console.warn("Service worker registration failed", err));
    });
  }

  /* ————— Web Share Target hand-off (Android PWA, ADR 0023) ————— */

  // The service worker stashed the shared payload in IndexedDB before redirecting here (see sw.js).
  // These constants MIRROR the SW's db/store/key — the two scripts share the store, not the code.
  const SHARE_DB = "agent42-share";
  const SHARE_STORE = "pending";
  const SHARE_KEY = "payload";

  // Read the stashed payload and clear it (a reload must not re-open the picker). Best-effort:
  // any IndexedDB failure resolves null and the share is silently dropped — never blocks load.
  function readSharePayload() {
    return new Promise((resolve) => {
      const open = indexedDB.open(SHARE_DB, 1);
      open.onupgradeneeded = () => open.result.createObjectStore(SHARE_STORE);
      open.onerror = () => resolve(null);
      open.onsuccess = () => {
        const tx = open.result.transaction(SHARE_STORE, "readwrite");
        const store = tx.objectStore(SHARE_STORE);
        const get = store.get(SHARE_KEY);
        get.onsuccess = () => { store.delete(SHARE_KEY); resolve(get.result || null); };
        get.onerror = () => resolve(null);
      };
    });
  }

  // Rebuild File objects from the stored {name,type,blob} records so the shared files run through
  // the normal handleFiles upload path unchanged (images → attachments, documents → RAG).
  const filesFromShare = (record) =>
    (record.files || []).map((f) => new File([f.blob], f.name, { type: f.type }));

  // Prefill text for the composer: a shared note and/or url (both → note then url), else the title.
  function sharePrefillText(record) {
    const parts = [record.text, record.url].filter(Boolean);
    return parts.length ? parts.join("\n") : (record.title || "");
  }

  // Populate the picker's agent list — one row per stored agent; a click chooses that destination.
  function renderShareAgents(onChoose) {
    shareAgentList.innerHTML = "";
    for (const agent of agents) {
      const li = document.createElement("li");
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = "share-agent";
      btn.textContent = agent.name;
      btn.title = agent.description || agent.name;
      btn.addEventListener("click", () => onChoose(agent));
      li.appendChild(btn);
      shareAgentList.appendChild(li);
    }
  }

  // After a destination is chosen: prefill the composer and push shared files through the upload
  // path. NEVER auto-sends — the user reviews and presses Send, keeping egress an explicit act
  // (ADR 0013); with no model the composer stays gated (applyModelAvailability), so it can't send.
  function applyShare(record) {
    input.value = sharePrefillText(record);
    autosize();
    const files = filesFromShare(record);
    if (files.length) handleFiles(files);
    input.focus();
  }

  // Run at init: if this load is a share hand-off (/?share=1), read the stash and open the picker.
  // loadAgents() is awaited so the agent list is populated before the picker opens (init's own
  // loadAgents is fire-and-forget — an eager open would render an empty list).
  async function initShare() {
    if (!new URLSearchParams(location.search).has("share")) return;
    history.replaceState(null, "", location.pathname); // drop ?share=1 so a reload isn't a re-share
    const record = await readSharePayload();
    if (!record) return;

    await loadAgents();
    renderShareAgents((agent) => { shareDialog.close(); startAgentChat(agent); applyShare(record); });
    shareNewChatBtn.onclick = () => { shareDialog.close(); startNewConversation(); applyShare(record); };
    shareCancelBtn.onclick = () => shareDialog.close();
    shareDialog.showModal();
  }

  // Run at init: the interactive-OAuth callback redirects back to /?mcp_oauth=connected|error. Strip the
  // param (so a reload isn't a re-trigger) and open Preferences → MCP servers so the connection status shows.
  async function initMcpOAuthReturn() {
    const params = new URLSearchParams(location.search);
    const result = params.get("mcp_oauth");
    if (!result) return;
    history.replaceState(null, "", location.pathname);
    if (result === "error") {
      alert(`Could not connect the MCP server: ${params.get("reason") || "unknown error"}`);
    }
    await openProfileDialog();
    profileTabs.select(3); // the MCP servers tab
  }

  /* ————— offline read cache (ADR 0032) ————— */

  // Agents, conversations and previously-opened chat histories are mirrored into IndexedDB so the
  // installed PWA can render the user's data offline. This is a client-side read cache ONLY — the
  // service worker still never touches /api (ADR 0002); the server stays the source of truth and
  // every successful fetch overwrites the mirror. Best-effort throughout: any IndexedDB error
  // resolves to a no-op (private mode, quota) and never blocks a load. A single "kv" store keyed by
  // "agents" / "conversations" / "messages:<id>".
  const CACHE_DB = "agent42-cache";
  const CACHE_STORE = "kv";

  function cacheDb() {
    return new Promise((resolve, reject) => {
      const open = indexedDB.open(CACHE_DB, 1);
      open.onupgradeneeded = () => open.result.createObjectStore(CACHE_STORE);
      open.onsuccess = () => resolve(open.result);
      open.onerror = () => reject(open.error);
    });
  }

  function cacheGet(key) {
    return new Promise((resolve) => {
      cacheDb().then((db) => {
        const get = db.transaction(CACHE_STORE, "readonly").objectStore(CACHE_STORE).get(key);
        get.onsuccess = () => resolve(get.result ?? null);
        get.onerror = () => resolve(null);
      }).catch(() => resolve(null));
    });
  }

  function cachePut(key, value) {
    return new Promise((resolve) => {
      cacheDb().then((db) => {
        const tx = db.transaction(CACHE_STORE, "readwrite");
        tx.objectStore(CACHE_STORE).put(value, key);
        tx.oncomplete = () => resolve();
        tx.onerror = () => resolve();
      }).catch(() => resolve());
    });
  }

  function cacheDelete(key) {
    return new Promise((resolve) => {
      cacheDb().then((db) => {
        const tx = db.transaction(CACHE_STORE, "readwrite");
        tx.objectStore(CACHE_STORE).delete(key);
        tx.oncomplete = () => resolve();
        tx.onerror = () => resolve();
      }).catch(() => resolve());
    });
  }

  // Paint the last-known agents + conversations from the cache at startup, so an offline (or slow)
  // load shows the user's data before—or instead of—the network fetch resolving. The fire-and-forget
  // loaders below overwrite both the arrays and the cache when the network succeeds; the length
  // guards keep a cache paint from clobbering data the network already delivered (fast path wins).
  async function hydrateFromCache() {
    const [cachedConvs, cachedAgents] = await Promise.all([
      cacheGet("conversations"),
      cacheGet("agents"),
    ]);
    if (cachedConvs && conversations.length === 0) {
      conversations = cachedConvs;
      renderSidebar();
    }
    if (cachedAgents && agents.length === 0) {
      agents = cachedAgents;
      renderAgents();
    }
  }

  /* ————— init ————— */

  loadModels();
  loadProviders(); // also sets userDefaultEmbeddingModel at startup, so the composer's document-attach gate works
  hydrateFromCache(); // paint cached agents + conversations first, so offline (or slow) loads aren't empty (ADR 0032)
  refreshConversations();
  loadAgents();
  const profileReady = initTheme(); // loads the profile and reconciles the theme (server wins over the pre-paint cache)
  const toolsReady = loadTools();
  // The initial state is a fresh regular chat, so the per-chat tools button is available (ADR 0021),
  // and incognito can be chosen — off and interactive until the first send (ADR 0034).
  toolsBtn.hidden = false;
  incognitoBtn.hidden = false;
  setIncognito(false, true);
  // The boot chat isn't created through startNewConversation(), so seed its composer tool selection
  // from the user default once BOTH the profile (enabledTools) and the catalog (allToolKeys) have
  // loaded — otherwise the Tools popover shows an empty set on startup instead of the saved default
  // (ADR 0021). Guard on the chat still being the untouched boot chat with no agent, so a share
  // hand-off (initShare → startNewConversation) or an opened conversation (selectConversation, which
  // sets chatTools from the server) wins the race instead of being clobbered. null default = all.
  const bootChatId = activeId;
  Promise.all([profileReady, toolsReady]).then(() => {
    if (activeId === bootChatId && activeAgentId === null) {
      chatTools = profile.enabledTools ?? allToolKeys();
    }
  });
  initLocation();
  initPush(); // wires the push toggle + the SW open-conversation message handler (ADR 0028)
  initShare(); // opens the destination picker when this load is an Android share hand-off
  initMcpOAuthReturn(); // surfaces the result when this load is an OAuth callback return
  input.focus();
})();
