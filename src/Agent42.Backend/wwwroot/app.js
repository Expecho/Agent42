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
  const agentCancelBtn = document.getElementById("agentCancelBtn");

  let conversations = [];
  let activeId = crypto.randomUUID();
  let streaming = false;
  let defaultModel = null;
  let modelsData = { models: [], default: null };

  // Images uploaded for the turn being composed: {id, mediaType, fileName} from the upload
  // endpoint. Sent as attachmentIds on the next send, then cleared. Reset on conversation switch.
  let pendingAttachments = [];

  let agents = [];
  // The agent id to *bind*, sent on the wire only on the first turn of a freshly started
  // agent chat, then cleared. Kept distinct from activeAgentId so a resumed agent chat never
  // re-resolves (and rebinds against) a possibly-deleted agent.
  let pendingAgentId = null;
  // The agent id of the current chat (fresh or resumed). Drives the picker lock + label only.
  let activeAgentId = null;
  // The agent being edited in the dialog, or null when creating a new one.
  let editingAgentId = null;

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

  // Fills a <select> with the available model deployments (composer picker + agent form).
  function fillModelOptions(select) {
    select.innerHTML = "";
    for (const m of modelsData.models ?? []) {
      const opt = document.createElement("option");
      opt.value = m.deployment;
      opt.textContent = m.deployment;
      if (m.version) opt.title = `${m.model ?? m.deployment} · ${m.version}`;
      select.appendChild(opt);
    }
  }

  // Select a deployment, adding it as an option first if the list doesn't have it
  // (e.g. a conversation pinned to a model that has since been removed).
  function setModel(deployment) {
    if (!deployment) return;
    if (![...modelSelect.options].some((o) => o.value === deployment)) {
      const opt = document.createElement("option");
      opt.value = deployment;
      opt.textContent = deployment;
      modelSelect.appendChild(opt);
    }
    modelSelect.value = deployment;
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
      model.textContent = agent.model;

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
    activeId = crypto.randomUUID();
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
    fillModelOptions(agentModelSelect);
    // Preselect the agent's model when editing, otherwise the server default.
    const preferred = agent?.model ?? defaultModel;
    if (preferred) {
      if (![...agentModelSelect.options].some((o) => o.value === preferred)) {
        const opt = document.createElement("option");
        opt.value = preferred;
        opt.textContent = preferred;
        agentModelSelect.appendChild(opt);
      }
      agentModelSelect.value = preferred;
    }
    agentDialog.showModal();
    agentNameInput.focus();
  }

  async function saveAgent() {
    const payload = {
      name: agentNameInput.value.trim(),
      description: agentDescriptionInput.value.trim(),
      model: agentModelSelect.value,
      systemPrompt: agentSystemPromptInput.value.trim(),
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
    agentLabel.textContent = `⛓ ${name} · ${model}`;
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

  // Uploads each picked image to the current conversation and shows a removable thumbnail. The
  // upload returns an id the next send references; the bytes already live server-side by then.
  async function handleFiles(files) {
    if (streaming) return;
    for (const file of files) {
      if (!file.type.startsWith("image/")) continue;
      try {
        const body = new FormData();
        body.append("file", file, file.name);
        const res = await fetch(attachmentsUrl(activeId), { method: "POST", body });
        if (!res.ok) continue;
        pendingAttachments.push(await res.json());
        renderAttachmentPreview();
      } catch { /* skip this file; the others still upload */ }
    }
    fileInput.value = ""; // let the same file be picked again after a remove
  }

  const attachmentsUrl = (convId) =>
    `/api/conversations/${encodeURIComponent(convId)}/attachments`;

  function renderAttachmentPreview() {
    attachmentPreview.innerHTML = "";
    attachmentPreview.hidden = pendingAttachments.length === 0;
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

  /* ————— sending & streaming ————— */

  async function send(prompt) {
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
  input.focus();
})();
