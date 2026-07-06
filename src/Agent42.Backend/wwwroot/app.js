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
  const modelSelect = document.getElementById("modelSelect");
  const menuToggle = document.getElementById("menuToggle");
  const backdrop = document.getElementById("backdrop");
  const app = document.querySelector(".app");

  let conversations = [];
  let activeId = crypto.randomUUID();
  let streaming = false;
  let defaultModel = null;

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
      const data = await res.json();
      defaultModel = data.default ?? null;

      modelSelect.innerHTML = "";
      for (const m of data.models ?? []) {
        const opt = document.createElement("option");
        opt.value = m.deployment;
        opt.textContent = m.deployment;
        if (m.version) opt.title = `${m.model ?? m.deployment} · ${m.version}`;
        modelSelect.appendChild(opt);
      }
      if (defaultModel) setModel(defaultModel);
      modelSelect.disabled = modelSelect.options.length === 0;
    } catch { /* leave the picker disabled; chat still uses the server's default model */ }
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

  function setUserText(msg, text) {
    msg.querySelector(".msg__bubble").textContent = text;
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
    renderSidebar();
    clearThread();
    setEmptyVisible(true);

    try {
      const [msgRes, modelRes] = await Promise.all([
        fetch(`/api/conversations/${encodeURIComponent(id)}/messages`),
        fetch(`/api/conversations/${encodeURIComponent(id)}/model`),
      ]);
      if (!msgRes.ok) return;
      const turns = await msgRes.json();
      for (const turn of turns) {
        const msg = addMessage(turn.role === "user" ? "user" : "agent");
        if (turn.role === "user") setUserText(msg, turn.text);
        else setAgentMarkdown(msg, turn.text);
      }
      scrollToBottom();
      // Restore the model picker only after the history is on screen, so a failure
      // here can never prevent the conversation itself from rendering.
      if (modelRes.ok) setModel(await modelRes.json());
    } catch { /* leave the empty state visible */ }
  }

  function startNewConversation() {
    closeDrawer();
    if (streaming) return;
    activeId = crypto.randomUUID();
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

    const userMsg = addMessage("user");
    setUserText(userMsg, prompt);
    const agentMsg = addMessage("agent");
    setAgentMarkdown(agentMsg, "", { caret: true });
    scrollToBottom();

    let answer = "";
    try {
      const res = await fetch(`/api/chat/${encodeURIComponent(activeId)}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ message: prompt, model: modelSelect.value || null }),
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
    if (!prompt || streaming) return;
    send(prompt);
  });

  newChatBtn.addEventListener("click", startNewConversation);

  menuToggle.addEventListener("click", toggleDrawer);
  backdrop.addEventListener("click", closeDrawer);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && app.classList.contains("is-drawer-open")) {
      closeDrawer();
      menuToggle.focus();
    }
  });

  /* ————— init ————— */

  loadModels();
  refreshConversations();
  input.focus();
})();
