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
  const fileInput = document.getElementById("fileInput");
  const attachmentPreview = document.getElementById("attachmentPreview");
  const modelSelect = document.getElementById("modelSelect");
  const modelField = document.getElementById("modelField");
  const noModelHint = document.getElementById("noModelHint");
  const agentLabel = document.getElementById("agentLabel");
  const menuToggle = document.getElementById("menuToggle");
  const backdrop = document.getElementById("backdrop");
  const app = document.querySelector(".app");

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
  const profileCancelBtn = document.getElementById("profileCancelBtn");
  const locationEnableBtn = document.getElementById("locationEnableBtn");
  const locationStatus = document.getElementById("locationStatus");
  // Preferences + agent dialog tabs. wireTabs (defined near openProfileDialog) takes [tab, panel]
  // pairs in tablist order, attaches the click + keyboard nav, and returns a select(index, focus).
  const profileTabs = wireTabs([
    [document.getElementById("tabGeneral"), document.getElementById("panelGeneral")],
    [document.getElementById("tabTools"), document.getElementById("panelTools")],
    [document.getElementById("tabProviders"), document.getElementById("panelProviders")],
  ]);
  const agentTabs = wireTabs([
    [document.getElementById("tabAgentGeneral"), document.getElementById("panelAgentGeneral")],
    [document.getElementById("tabAgentTools"), document.getElementById("panelAgentTools")],
    [document.getElementById("tabAgentApi"), document.getElementById("panelAgentApi")],
  ]);

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

  // Selectable tools (ADR 0021): checkbox groups in the agent + preferences dialogs, and a per-chat popover.
  const agentToolChecks = document.getElementById("agentToolChecks");
  const profileToolChecks = document.getElementById("profileToolChecks");
  const toolsBtn = document.getElementById("toolsBtn");
  const toolsDialog = document.getElementById("toolsDialog");
  const chatToolChecks = document.getElementById("chatToolChecks");
  const toolsDoneBtn = document.getElementById("toolsDoneBtn");

  // Web Share Target picker (Android PWA, ADR 0023): a shared payload lands on a /?share=1 load.
  const shareDialog = document.getElementById("shareDialog");
  const shareAgentList = document.getElementById("shareAgentList");
  const shareNewChatBtn = document.getElementById("shareNewChatBtn");
  const shareCancelBtn = document.getElementById("shareCancelBtn");

  let conversations = [];
  let activeId = crypto.randomUUID();
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
  // The agent id to *bind*, sent on the wire only on the first turn of a freshly started
  // agent chat, then cleared. Kept distinct from activeAgentId so a resumed agent chat never
  // re-resolves (and rebinds against) a possibly-deleted agent.
  let pendingAgentId = null;
  // The agent id of the current chat (fresh or resumed). Drives the picker lock + label only.
  let activeAgentId = null;
  // The agent being edited in the dialog, or null when creating a new one.
  let editingAgentId = null;

  // The last profile fetched from the server. The server is the source of truth; this is
  // only a render cache, always reconciled against a fresh GET/PUT response.
  let profile = { preferredLanguage: "", theme: "system" };

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
  async function loadModels() {
    try {
      const res = await fetch("/api/models");
      if (!res.ok) return;
      modelsData = await res.json();
      defaultModel = modelsData.default ?? null;

      fillModelOptions(modelSelect);
      if (defaultModel) setModel(defaultModel);
      modelSelect.disabled = modelSelect.options.length === 0;
      applyModelAvailability();
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

  // Fills a <select> with the available models, grouped by provider. Shared by the composer picker AND
  // the agent dialog — both get the grouping, and neither hand-rolls its own option list.
  function fillModelOptions(select) {
    select.innerHTML = "";
    const groups = new Map();
    for (const m of modelsData.models ?? []) {
      const provider = m.provider ?? "";
      if (!groups.has(provider)) {
        const group = document.createElement("optgroup");
        group.label = provider || "Models";
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
    } catch { /* silo may still be starting; the list refreshes on next action */ }
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
    for (const conv of conversations) {
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

      const del = document.createElement("button");
      del.type = "button";
      del.className = "conv__delete";
      del.setAttribute("aria-label", `Delete conversation: ${conv.title}`);
      del.textContent = "×";
      del.addEventListener("click", (e) => {
        e.stopPropagation();
        deleteConversation(conv.id, conv.title);
      });

      li.append(btn, del);
      conversationList.appendChild(li);
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
    if (id === activeId) startNewConversation();
    else renderSidebar();
    refreshConversations();
  }

  /* ————— stored agents ————— */

  async function loadAgents() {
    try {
      const res = await fetch("/api/agents");
      if (!res.ok) return;
      agents = await res.json();
      renderAgents();
    } catch { /* silo may still be starting; the list refreshes on next action */ }
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
    // a PUT before the first agent turn would be clobbered by the bind anyway.
    toolsBtn.hidden = true;
  }

  function unlockPicker() {
    modelField.hidden = false;
    agentLabel.hidden = true;
    toolsBtn.hidden = false;
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
      body.textContent = text;
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
    activeId = id;
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
      for (const turn of turns) {
        const msg = addMessage(turn.role === "user" ? "user" : "agent");
        if (turn.role === "user") {
          fillUserBubble(msg, turn.text, turn.attachments, id);
        } else {
          setAgentMarkdown(msg, turn.text);
          // An assistant turn may carry a generated image (ADR 0020); re-render it above the prose.
          for (const att of turn.attachments ?? []) addAgentImage(msg, id, att.id, att.fileName);
        }
      }
      scrollToBottom();
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
    } catch { /* leave the empty state visible */ }
  }

  function startNewConversation() {
    closeDrawer();
    if (streaming) return;
    activeId = crypto.randomUUID();
    activeAgentId = null;
    pendingAgentId = null;
    // A new regular chat starts from the user's default tool set (ADR 0021); null profile default = all.
    chatTools = profile.enabledTools ?? allToolKeys();
    clearPendingAttachments();
    unlockPicker();
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

  /* ————— sending & streaming ————— */

  async function send(prompt) {
    stopSpeech(); // capture the dictated prompt and reset the mic before clearing the box
    streaming = true;
    sendBtn.disabled = true;
    modelSelect.disabled = true;
    input.value = "";
    autosize();

    // Snapshot the composed attachments for this turn and clear the strip so the next turn
    // starts clean. They were uploaded to activeId, so they belong to this conversation.
    const turnAttachments = pendingAttachments;
    const convId = activeId;
    clearPendingAttachments();

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
      const res = await fetch(`/api/chat/${encodeURIComponent(activeId)}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          message: prompt,
          model: isAgentChat ? null : (modelSelect.value || null),
          agentId: pendingAgentId,
          attachmentIds: turnAttachments.map((a) => a.id),
          // Whatever fix is already cached (ADR 0012) — read synchronously, never awaited. No
          // geolocation outcome (denied, timed out, unsupported, stale, absent) may delay a chat.
          location: currentLocationForSend(),
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
          } else if (chunk.type === "toolCallStart") {
            addToolCall(agentMsg, chunk);
          } else if (chunk.type === "toolCallEnd") {
            completeToolCall(agentMsg, chunk);
          } else if (chunk.type === "image") {
            // An image-generation model produced an image (ADR 0020): render it live in the assistant
            // bubble via the display proxy, exactly like it will re-render on reload.
            addAgentImage(agentMsg, activeId, chunk.attachmentId);
          } else if (chunk.type === "done") {
            showUsage(agentMsg, chunk);
          }
          scrollToBottom();
        }
      }
      setAgentMarkdown(agentMsg, answer);
      // The persona is now bound server-side; don't resend agentId on later turns. Kept on
      // failure so a retry rebinds.
      pendingAgentId = null;
    } catch (err) {
      agentMsg.classList.add("is-error");
      setAgentMarkdown(agentMsg, `**Transmission failed.** ${err.message ?? err}`);
    } finally {
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
    profileSaved = false; // fresh edit session; the close handler reverts a preview unless we save
    profileTabs.select(0); // always open on General
    updateLocationControl(); // refresh the location status/button for the current grant state
    loadProviders(); // refresh the user's providers list alongside the profile
    profileDialog.showModal();
    profileLanguage.focus();
  }

  async function saveProfile() {
    // The endpoint does read-modify-write (absent-means-keep), so sending these fields never wipes the
    // others. enabledTools is the full explicit list — "all" is every key (ADR 0021) — but ONLY when the
    // catalog loaded: if /api/tools failed, toolCatalog is [] and sending [] would wipe the default to
    // "none". Omitting it then leaves the stored default untouched (absent-means-keep).
    const payload = { preferredLanguage: profileLanguage.value, theme: profileTheme.value };
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

  // Renders one checkbox per catalog tool into `container`, checked when its key is in `selectedKeys`.
  function renderToolChecks(container, selectedKeys) {
    const selected = new Set(selectedKeys);
    container.innerHTML = "";
    for (const tool of toolCatalog) {
      const label = document.createElement("label");
      label.className = "tool-check";
      const cb = document.createElement("input");
      cb.type = "checkbox";
      cb.value = tool.key;
      cb.checked = selected.has(tool.key);
      const span = document.createElement("span");
      span.textContent = tool.requiresHostedTools ? `${tool.label} (Responses)` : tool.label;
      label.append(cb, span);
      container.appendChild(label);
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
  chatToolChecks.addEventListener("change", persistChatTools);

  /* ————— model providers (per-user overlay, ADR 0018) ————— */

  async function loadProviders() {
    try {
      const res = await fetch("/api/providers");
      if (!res.ok) return;
      const data = await res.json();
      userProviders = data.providers ?? [];
      renderProviders();
    } catch { /* silo may be unreachable; the list refreshes on next open */ }
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

    const tools = document.createElement("label");
    tools.className = "provider-model-row__flag";
    const toolsBox = document.createElement("input");
    toolsBox.type = "checkbox";
    toolsBox.className = "agent-form__checkbox";
    toolsBox.checked = model?.supportsTools ?? false;
    tools.append(toolsBox, document.createTextNode("tools"));

    const vision = document.createElement("label");
    vision.className = "provider-model-row__flag";
    const visionBox = document.createElement("input");
    visionBox.type = "checkbox";
    visionBox.className = "agent-form__checkbox";
    visionBox.checked = model?.supportsVision ?? false;
    vision.append(visionBox, document.createTextNode("vision"));

    // Image generation (ADR 0020): picking this model produces an image instead of a chat reply. Off by
    // default — a declared model is a chat model unless it says otherwise.
    const image = document.createElement("label");
    image.className = "provider-model-row__flag";
    const imageBox = document.createElement("input");
    imageBox.type = "checkbox";
    imageBox.className = "agent-form__checkbox";
    imageBox.checked = model?.supportsImageGeneration ?? false;
    image.append(imageBox, document.createTextNode("image gen"));

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "provider-model-row__remove";
    remove.setAttribute("aria-label", "Remove model");
    remove.textContent = "×";
    remove.addEventListener("click", () => li.remove());

    li.append(id, display, tools, vision, image, remove);
    providerModelList.appendChild(li);
  }

  function collectModels() {
    const models = [];
    for (const row of providerModelList.querySelectorAll(".provider-model-row")) {
      const id = row.querySelector(".provider-model-row__id").value.trim();
      if (!id) continue; // an empty row is dropped, not sent
      const [tools, vision, image] = row.querySelectorAll(".agent-form__checkbox");
      const display = row.querySelector(".provider-model-row__display").value.trim();
      models.push({
        id,
        displayName: display || null,
        supportsTools: tools.checked,
        supportsVision: vision.checked,
        supportsImageGeneration: image.checked,
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

  addProviderBtn.addEventListener("click", () => openProviderDialog(null));
  addModelRowBtn.addEventListener("click", () => addModelRow(null));
  providerKindSelect.addEventListener("change", syncProviderKindUi);
  providerCancelBtn.addEventListener("click", () => providerDialog.close());
  providerForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveProvider();
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

  /* ————— init ————— */

  loadModels();
  refreshConversations();
  loadAgents();
  initTheme(); // loads the profile and reconciles the theme (server wins over the pre-paint cache)
  loadTools();
  // The initial state is a fresh regular chat, so the per-chat tools button is available (ADR 0021).
  toolsBtn.hidden = false;
  initLocation();
  initShare(); // opens the destination picker when this load is an Android share hand-off
  input.focus();
})();
