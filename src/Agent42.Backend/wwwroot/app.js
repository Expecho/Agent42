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
      li.appendChild(btn);
      conversationList.appendChild(li);
    }
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

  /* ————— conversation switching ————— */

  async function selectConversation(id) {
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
      if (modelRes.ok) setModel(await modelRes.json());
      if (!msgRes.ok) return;
      const turns = await msgRes.json();
      for (const turn of turns) {
        const msg = addMessage(turn.role === "user" ? "user" : "agent");
        if (turn.role === "user") setUserText(msg, turn.text);
        else setAgentMarkdown(msg, turn.text);
      }
      scrollToBottom();
    } catch { /* leave the empty state visible */ }
  }

  function startNewConversation() {
    if (streaming) return;
    activeId = crypto.randomUUID();
    clearThread();
    setEmptyVisible(true);
    renderSidebar();
    input.focus();
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
          if (chunk.type === "text") {
            answer += chunk.content;
            setAgentMarkdown(agentMsg, answer, { caret: true });
            scrollToBottom();
          }
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

  /* ————— init ————— */

  loadModels();
  refreshConversations();
  input.focus();
})();
