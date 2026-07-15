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
  const locationBtn = document.getElementById("locationBtn");
  const fileInput = document.getElementById("fileInput");
  const attachmentPreview = document.getElementById("attachmentPreview");
  const modelSelect = document.getElementById("modelSelect");
  const modelField = document.getElementById("modelField");
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
  const agentCancelBtn = document.getElementById("agentCancelBtn");

  const openProfileBtn = document.getElementById("openProfileBtn");
  const profileDialog = document.getElementById("profileDialog");
  const profileForm = document.getElementById("profileForm");
  const profileLanguage = document.getElementById("profileLanguage");
  const profileCancelBtn = document.getElementById("profileCancelBtn");

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
  let profile = { preferredLanguage: "" };

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
    } catch { /* leave the picker disabled; chat still uses the server's default model */ }
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
    fillModelOptions(agentModelSelect);
    // Preselect the agent's model when editing, otherwise the server default. Uses the same helper as
    // the composer picker rather than a second copy of the append-if-missing logic.
    selectModel(agentModelSelect, agent?.model ?? defaultModel);
    agentDialog.showModal();
    agentNameInput.focus();
  }

  async function saveAgent() {
    const payload = {
      name: agentNameInput.value.trim(),
      description: agentDescriptionInput.value.trim(),
      model: agentModelSelect.value,
      systemPrompt: agentSystemPromptInput.value.trim(),
      continuous: agentContinuousInput.checked,
    };
    if (!payload.name || !payload.systemPrompt || !payload.model) return;

    const url = editingAgentId ? `/api/agents/${encodeURIComponent(editingAgentId)}` : "/api/agents";
    const method = editingAgentId ? "PUT" : "POST";
    try {
      const res = await fetch(url, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!res.ok) return;
    } catch { return; /* silo may be unreachable; leave the dialog open */ }

    agentDialog.close();
    loadAgents();
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
  }

  function unlockPicker() {
    modelField.hidden = false;
    agentLabel.hidden = true;
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
      const [msgRes, modelRes, agentRes] = await Promise.all([
        fetch(`/api/conversations/${encodeURIComponent(id)}/messages`),
        fetch(`/api/conversations/${encodeURIComponent(id)}/model`),
        fetch(`/api/conversations/${encodeURIComponent(id)}/agent`),
      ]);
      if (!msgRes.ok) return;
      const turns = await msgRes.json();
      for (const turn of turns) {
        const msg = addMessage(turn.role === "user" ? "user" : "agent");
        if (turn.role === "user") fillUserBubble(msg, turn.text, turn.attachments, id);
        else setAgentMarkdown(msg, turn.text);
      }
      scrollToBottom();
      // Restore the model picker only after the history is on screen, so a failure
      // here can never prevent the conversation itself from rendering.
      if (modelRes.ok) setModel(await modelRes.json());
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

  // The grant button is visible exactly when we could capture but are not yet allowed to.
  async function updateLocationControl() {
    if (!locationBtn) return;
    locationBtn.hidden = !geolocationSupported || (await isCaptureAuthorized());
  }

  // The one place a browser permission prompt is ever triggered — on an explicit tap.
  async function requestLocationGrant() {
    if (!geolocationSupported) return;
    locationBtn.disabled = true;
    try {
      lastFix = toFix(await readPosition());
      setStoredGrant(true); // Leg 2: only ever set after a capture actually succeeded.
      startBackgroundLocationRefresh();
    } catch (error) {
      handlePositionError(error);
    } finally {
      locationBtn.disabled = false;
      await updateLocationControl();
    }
  }

  async function initLocation() {
    if (!geolocationSupported) {
      if (locationBtn) locationBtn.hidden = true;
      return;
    }

    locationBtn?.addEventListener("click", requestLocationGrant);

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

  async function openProfileDialog() {
    await loadProfile(); // reconcile against the server before showing the current value
    fillLanguageOptions(profileLanguage, profile.preferredLanguage ?? "");
    profileDialog.showModal();
    profileLanguage.focus();
  }

  async function saveProfile() {
    const payload = { preferredLanguage: profileLanguage.value };
    try {
      const res = await fetch("/api/profile", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });
      if (!res.ok) return;
      profile = await res.json(); // reconcile: the server trims/caps the tag
    } catch { return; /* silo may be unreachable; leave the dialog open */ }

    profileDialog.close();
  }

  openProfileBtn.addEventListener("click", () => openProfileDialog());
  profileCancelBtn.addEventListener("click", () => profileDialog.close());
  profileForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveProfile();
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

  /* ————— init ————— */

  loadModels();
  refreshConversations();
  loadAgents();
  loadProfile();
  initLocation();
  input.focus();
})();
