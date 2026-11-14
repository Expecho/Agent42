// Agent 42 · agent fleet (ADR 0097). Ported from the Agent Teams dashboard: every view is rebuilt from the task's
// events, which /api/fleet/tasks/{id}/events replays on (re)connect and then streams live. The task list comes from
// /api/fleet/tasks. Model text is rendered ONLY as DOMPurify.sanitize(marked.parse(...)); everything else is set as
// text, never innerHTML. One classic script, like /logs (see index.html).
"use strict";

(function () {
  const $ = s => document.querySelector(s);
  const nf = new Intl.NumberFormat();
  const compact = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });

  // ---------- state ----------
  const S = {
    tasks: [],              // the user's tasks from the API, newest first
    teams: new Map(), calls: new Map(), rootTeamId: null, live: null,
    budget: null, prompt: null,
    selectedTask: null, selectedTeam: null, selectedAttempt: null,
    follow: true,
    open: new Set(), expanded: new Set(),
    view: "tasks",          // or "prompts"
    prompts: null, promptKey: "planner", drafts: {},
    panel: null,
    freshTask: true,        // the composer starts a new task, instead of a follow-up on the selected task
    config: null, models: null, files: null,
    canvas: null,           // the selected task's canvas pages (ADR 0102), as { taskId, list }
    tools: null,            // the fleet's rows of GET /api/tools, or null when it failed (then no override is ever sent)
    defaultTools: null,     // the user's default chat selection (profile.enabledTools), null meaning all
    attachments: [],        // File objects the composer will send with the next task (ADR 0101)
    ingested: null,         // the selected task's documents.ingested files, from its event log
    autoAnswers: [],        // questions a chat-started task answered by itself (ADR 0115), from its event log
    clarify: null,          // the clarify phase (ADR 0118), from its event log: { phase: "asking" | "done", brief, questions, cleared }
    detailView: loadDetailView(), // "details" | "graph" (ADR 0116), a per-viewer preference kept in localStorage
    graphOpen: new Set(),   // done sub-teams the graph shows in full although the fleet is large
  };

  function loadClarify() {
    try { return localStorage.getItem("agent42.fleet.clarify") !== "off"; } catch { return true; }
  }
  function saveClarify(on) {
    try { localStorage.setItem("agent42.fleet.clarify", on ? "on" : "off"); } catch { /* not remembered */ }
  }

  // ADR 0119: off by default (each fix pass costs tokens), remembered per viewer.
  function loadSelfReview() {
    try { return localStorage.getItem("agent42.fleet.selfReview") === "on"; } catch { return false; }
  }
  function saveSelfReview(on) {
    try { localStorage.setItem("agent42.fleet.selfReview", on ? "on" : "off"); } catch { /* not remembered */ }
  }

  // A per-viewer convenience only: without storage (private window, blocked site data) the page starts on Details.
  function loadDetailView() {
    try { return localStorage.getItem("agent42.fleet.detailView") === "graph" ? "graph" : "details"; } catch { return "details"; }
  }

  function resetTaskState() {
    S.teams.clear(); S.calls.clear(); S.rootTeamId = null; S.live = null;
    S.budget = null; S.prompt = null; S.panel = null; S.ingested = null; S.autoAnswers = []; S.clarify = null; S.graphOpen.clear(); graphScrolledTo = null; dirtyText.clear();
    renderPrompt();
    invalidate();
  }

  let uidN = 0;
  const uid = () => "u" + (++uidN);

  // ---------- dom helpers ----------
  function h(tag, props, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(props || {})) {
      if (v === undefined || v === null || v === false) continue;
      if (k === "class") el.className = v;
      else if (k === "text") el.textContent = v;
      else if (k === "style") el.style.cssText = v;
      else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
      else el.setAttribute(k, v === true ? "" : v);
    }
    for (const kid of kids.flat(Infinity)) {
      if (kid === null || kid === undefined || kid === false) continue;
      el.append(kid instanceof Node ? kid : String(kid));
    }
    return el;
  }

  function det(id, cls, summary, ...body) {
    const el = h("details", { class: cls }, h("summary", null, summary), ...body);
    if (S.open.has(id)) el.open = true;
    el.addEventListener("toggle", () => el.open ? S.open.add(id) : S.open.delete(id));
    return el;
  }

  // Markdown from agents, sanitized: the page can start tasks and answer permission questions, so it must never run
  // their html. Without both libraries the text is shown as plain text.
  const markdown = typeof marked !== "undefined" && typeof DOMPurify !== "undefined";
  if (markdown) {
    marked.setOptions({ gfm: true, breaks: true });
    DOMPurify.addHook("afterSanitizeAttributes", node => {
      if (node.tagName === "A") { node.setAttribute("target", "_blank"); node.setAttribute("rel", "noopener noreferrer"); }
    });
  }
  function setMarkdown(el, text) {
    el.classList.add("md");
    if (markdown) el.innerHTML = DOMPurify.sanitize(marked.parse(text || ""));
    else { el.textContent = text || ""; el.style.whiteSpace = "pre-wrap"; }
  }
  const mdBlock = (cls, text) => { const el = h("div", { class: cls }); setMarkdown(el, text); return el; };

  // The task's canvas pages (ADR 0102) open in the shared sandboxed artifact pane (/shared/lib/artifactpane.js), the
  // chat's own. Its routes point at the selected task. The pane only says whether it is open, so the open page's name
  // is tracked here, for canvas.updated to reopen it at the new version.
  const Pane = window.Agent42ArtifactPane;
  let canvasPane = null;
  let canvasOpenName = null;
  const escapeHtml = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  function ensureCanvasPane() {
    if (!Pane) return null;
    if (!canvasPane) {
      const page = (name, version, route) =>
        `/api/fleet/tasks/${encodeURIComponent(S.selectedTask)}/canvas/${encodeURIComponent(name)}/versions/${version}/${route}`;
      canvasPane = Pane.create({
        host: document.body,
        renderMarkdown: text => markdown ? DOMPurify.sanitize(marked.parse(text || "")) : `<pre>${escapeHtml(text || "")}</pre>`,
        urls: {
          list: () => `/api/fleet/tasks/${encodeURIComponent(S.selectedTask)}/canvas`,
          content: (name, version) => page(name, version, "content"),
          frame: (name, version) => page(name, version, "frame"),
        },
      });
    }
    return canvasPane;
  }
  function openCanvasPage(name, version) {
    const pane = ensureCanvasPane();
    if (!pane) return;
    canvasOpenName = name;
    pane.open(name, version);
  }
  function closeCanvasPane() {
    canvasOpenName = null;
    canvasPane?.close();
  }

  function clampText(id, cls, text, limit = 900, md = false) {
    const box = md ? mdBlock(cls, text) : h("div", { class: cls, text });
    if (text.length <= limit || S.expanded.has(id)) return box;
    box.classList.add("clamp");
    return h("div", null, box, h("button", { class: "more", text: "Show all", onclick: () => { S.expanded.add(id); invalidate(); } }));
  }

  const PALETTE = [205, 150, 30, 280, 340, 95, 180, 255, 15, 60];
  function agentColor(name) {
    let x = 0; for (const ch of name || "") x = (x * 31 + ch.charCodeAt(0)) >>> 0;
    return `hsl(${PALETTE[x % PALETTE.length]} var(--agent-s) var(--agent-l))`;
  }
  const firstLine = (s, n = 90) => { const l = (s || "").trim().split(/\r?\n/)[0]; return l.length > n ? l.slice(0, n - 1) + "…" : l; };
  const dur = (a, b) => h("span", { "data-since": a, "data-until": b || null, text: duration(a, b) });
  function duration(a, b) {
    if (!a) return "";
    const s = Math.max(0, Math.round(((b ? new Date(b) : new Date()) - new Date(a)) / 1000));
    return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${String(s % 60).padStart(2, "0")}s`;
  }
  const fmtBytes = n => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;

  // ---------- tasks ----------
  const taskById = id => S.tasks.find(t => t.id === id);
  // Numbered oldest first, like the engine's task-N.
  const taskNumber = id => { const sorted = [...S.tasks].reverse(); return sorted.findIndex(t => t.id === id) + 1; };
  const running = () => S.tasks.some(t => t.status === "running");

  async function loadTasks() {
    const r = await api("GET", "/api/fleet/tasks");
    if (r.ok) { S.tasks = r.data || []; invalidate(); }
    return r.ok;
  }

  // ---------- model ----------
  const teamOf = d => S.teams.get(d.teamId);
  const attemptOf = d => teamOf(d)?.attempts.find(a => a.n === d.attempt);
  const currentPlan = a => a && ([...a.plans].reverse().find(p => p.status === "accepted") || [...a.plans].reverse().find(p => p.status === "proposed"));
  const agentNames = a => new Set((currentPlan(a)?.agents || []).map(x => (x.name || "").toLowerCase()));

  function openTurn(d, create) {
    const a = attemptOf(d);
    if (!a) return null;
    let turn = a.turns.get(d.executorId);
    if ((!turn || turn.done) && create) {
      turn = { kind: "turn", id: uid(), executorId: d.executorId, agent: d.agent, items: [], done: false, failed: null };
      a.turns.set(d.executorId, turn);
      a.timeline.push(turn);
    }
    return turn;
  }

  function appendText(d, kind) {
    const turn = openTurn(d, true);
    if (!turn) return;
    let item = turn.items[turn.items.length - 1];
    if (!item || item.kind !== kind) { item = { kind, id: uid(), text: "", turn }; turn.items.push(item); invalidate(); }
    item.text += d.text;
    dirtyText.add(item);
    schedule();
  }

  function markStopped(team, status) {
    if (team.status === "running") team.status = status;
    for (const a of team.attempts) {
      if (!["passed", "retry", "done", "failed"].includes(a.status)) a.status = status;
      for (const t of a.turns.values()) { if (!t.done) t.cut = true; t.done = true; } // cut = a stop ended it, not the agent
    }
  }

  // One handler per event type the backend publishes. Nothing enforces that: a new Publish("…") needs an entry here by
  // discipline (FleetPageAssetsTests pins only the todos.updated one, and that titles are never set as markup).
  const handlers = {
    "task.started"(d, time) { S.live = { status: "running", started: time, model: d.model }; },
    "documents.ingested"(d) { S.ingested = d.files || []; },
    "task.finished"(d, time) {
      S.live = { status: d.status, finished: time };
      const t = taskById(d.taskId);
      if (t) { t.status = d.status; t.reason = d.reason; t.finishedAt = time; t.result = d.result; t.canFollowUp = d.canFollowUp; }
      for (const team of S.teams.values()) { markStopped(team, d.status === "done" ? "done" : d.status); if (!team.finished) team.finished = time; }
      S.prompt = null; renderPrompt();
      loadFiles();
    },
    "team.started"(d, time) {
      const team = { id: d.teamId, parentTeamId: d.parentTeamId, parentCallId: d.parentCallId, depth: d.depth, task: d.task,
        status: "running", attempts: [], children: [], started: time };
      S.teams.set(team.id, team);
      const parent = S.teams.get(d.parentTeamId);
      if (parent) parent.children.push(team.id);
      else if (!S.rootTeamId) S.rootTeamId = team.id;
      const call = S.calls.get(d.parentCallId);
      if (call) { call.subTeamId = team.id; team.delegatedBy = call.agent; }
      if (S.follow) { S.selectedTeam = team.id; S.selectedAttempt = null; }
    },
    "team.finished"(d, time) {
      const team = teamOf(d); if (!team) return;
      team.status = d.failed ? "failed" : "done"; team.result = d.result; team.finished = time;
      const last = team.attempts[team.attempts.length - 1];
      if (last && ["running", "planning"].includes(last.status)) last.status = d.failed ? "failed" : "done";
      const parent = S.teams.get(team.parentTeamId);
      if (S.follow && S.selectedTeam === team.id && parent && parent.status === "running") { S.selectedTeam = parent.id; S.selectedAttempt = null; }
    },
    "attempt.started"(d, time) {
      const team = teamOf(d); if (!team) return;
      const fixes = d.fixOf && team.attempts.find(x => x.n === d.fixOf);
      team.attempts.push({ n: d.attempt, status: fixes ? "running" : "planning", plans: fixes ? fixes.plans.filter(p => p.status === "accepted") : [],
        notes: [], timeline: [], turns: new Map(), todos: [], started: time, review: d.review, fix: !!fixes });
      if (S.follow && S.selectedTeam === team.id) S.selectedAttempt = null;
    },
    "attempt.failed"(d) { const a = attemptOf(d); if (a) { a.status = "failed"; a.notes.push(d.reason); } },
    "plan.submitted"(d) {
      const a = attemptOf(d); if (!a) return;
      a.plans.push({ ...d, status: "proposed" });
      if (d.needsApproval) a.status = "approval";
    },
    "plan.rejected"(d) {
      const a = attemptOf(d); if (!a) return;
      const p = a.plans[a.plans.length - 1];
      if (p && p.status === "proposed") p.status = "rejected";
      a.notes.push(d.reason);
      if (a.status === "approval") a.status = "planning";
    },
    "plan.accepted"(d) {
      const a = attemptOf(d); if (!a) return;
      const p = a.plans[a.plans.length - 1]; if (p) p.status = "accepted";
      a.status = "running";
    },
    "planner.failed"(d) { const a = attemptOf(d); if (a) a.notes.push("The planner failed: " + d.error); },
    "stage.started"(d) { const a = attemptOf(d); if (a) { a.timeline.push({ kind: "stage", id: uid(), stage: d.stage, stages: d.stages, agents: d.agents }); a.stage = d.stage; } },
    "agent.started"(d) { if (agentNames(attemptOf(d)).has((d.agent || "").toLowerCase())) openTurn(d, true); },
    "agent.done"(d) {
      const a = attemptOf(d); const turn = a?.turns.get(d.executorId);
      if (!turn || turn.done) return;
      turn.done = true;
      if (turn.items.length === 0) a.timeline.splice(a.timeline.indexOf(turn), 1);
    },
    "agent.failed"(d) { const turn = openTurn(d, true); if (turn) { turn.failed = d.error; turn.done = true; } },
    "agent.text"(d) { appendText(d, "text"); return false; },
    "agent.thinking"(d) { appendText(d, "thinking"); return false; },
    "tool.call"(d) {
      const turn = openTurn(d, true); if (!turn) return;
      const call = { callId: d.callId, name: d.name, arguments: d.arguments, agent: d.agent, teamId: d.teamId, attempt: d.attempt, result: null, failed: false, pending: true };
      S.calls.set(d.callId, call);
      turn.items.push({ kind: "tool", id: uid(), call });
      const sub = [...S.teams.values()].find(t => t.parentCallId === d.callId);
      if (sub) { call.subTeamId = sub.id; sub.delegatedBy = d.agent; }
    },
    "tool.result"(d) {
      const call = S.calls.get(d.callId); if (!call) return;
      call.result = d.result; call.failed = d.failed; call.pending = false;
    },
    "delegation.refused"(d) { const a = attemptOf(d); if (a) a.timeline.push({ kind: "event", id: uid(), label: "No sub-team", text: d.reason }); },
    "magentic.plan"(d) { const a = attemptOf(d); if (a) a.timeline.push({ kind: "event", id: uid(), label: d.replanned ? "Manager replanned" : "Manager's plan", text: d.text }); },
    "workflow.error"(d) { const a = attemptOf(d); if (a) a.timeline.push({ kind: "event", error: true, id: uid(), label: "Workflow error", text: d.error }); },
    "attempt.result"(d) { const a = attemptOf(d); if (a) { a.result = d.result; a.status = "done"; for (const t of a.turns.values()) t.done = true; } },
    // The whole list rides on every event, so a replay or a reload replaces it (ADR 0117).
    "todos.updated"(d) { const a = attemptOf(d); if (a) a.todos = d.todos || []; },
    "judge.started"(d) { const a = attemptOf(d); if (a) a.status = "judging"; },
    "verdict"(d) { const a = attemptOf(d); if (a) { a.verdict = d; a.status = d.passed ? "passed" : d.willRetry ? "retry" : "failed"; } },
    "budget"(d) { S.budget = d; renderBudget(); return false; },
    // A committed canvas write: reload the list and, when the pane shows that page, reopen it at the new version.
    "canvas.updated"(d) {
      scheduleCanvasLoad();
      if (canvasOpenName === d.name && canvasPane?.isOpen()) openCanvasPage(d.name, d.version);
      return false;
    },
    // The clarify phase (ADR 0118): the interviewer's questions arrive as ordinary prompts, in between these two.
    "clarify.started"() { S.clarify = { phase: "asking" }; renderPrompt(); },
    "clarify.done"(d) { S.clarify = { phase: "done", brief: d.brief || "", questions: d.questions || 0, cleared: !!d.cleared }; renderPrompt(); },
    "prompt"(d) { S.prompt = d; renderPrompt(); return false; },
    "prompt.answered"() { S.prompt = null; renderPrompt(); return false; },
    // A task started from a chat (ADR 0115) asks nobody: each question it answered by itself is logged here.
    "question.auto"(d) { S.autoAnswers.push({ question: d.question || "", answer: d.answer || "" }); },
  };

  // A FormData body goes as it is: the browser sets its multipart content-type with the boundary.
  async function api(method, path, body) {
    const form = body instanceof FormData;
    try {
      const res = await fetch(path, {
        method,
        headers: body === undefined || form ? {} : { "content-type": "application/json" },
        body: body === undefined ? undefined : form ? body : JSON.stringify(body),
        credentials: "same-origin",
      });
      const text = await res.text();
      let data = null;
      try { data = text ? JSON.parse(text) : null; } catch { data = null; }
      return { ok: res.ok, status: res.status, data };
    } catch (err) {
      return { ok: false, status: 0, data: { error: "The server is not reachable." } };
    }
  }

  // ---------- event stream ----------
  // One stream for the selected task. The server replays the whole log on every (re)connect, so the state starts over
  // on open. A finished task's stream simply ends; the task record then says it is done and no reconnect is made.
  // Repeated failures with the task still unknown mean the Easy Auth session expired: the page offers a sign-in.
  let source = null, retryTimer = 0, failures = 0;

  function connect(taskId) {
    disconnect();
    if (!taskId) return;
    const conn = $("#conn");
    const es = new EventSource(`/api/fleet/tasks/${encodeURIComponent(taskId)}/events`);
    source = es;
    es.onopen = () => { failures = 0; $("#signInBanner").hidden = true; resetTaskState(); conn.className = "conn on"; conn.title = "Connected"; };
    es.onmessage = msg => {
      let e;
      try { e = JSON.parse(msg.data); } catch { return; }
      const fn = handlers[e.type];
      if (!fn) return;
      try { if (fn(e.data || {}, e.time) !== false) invalidate(); }
      catch (err) { console.error(e.type, err); }
    };
    es.onerror = () => onStreamError(es, taskId);
  }

  async function onStreamError(es, taskId) {
    if (source !== es) return;
    es.close();
    const conn = $("#conn");
    conn.className = "conn off"; conn.title = "Disconnected";
    const r = await api("GET", `/api/fleet/tasks/${encodeURIComponent(taskId)}`);
    if (source !== es) return;
    if (r.ok && r.data) {
      failures = 0;
      replaceTask(r.data);
      if (r.data.status !== "running") { conn.className = "conn"; conn.title = "Finished"; invalidate(); return; }
    } else if (++failures >= 3) {
      $("#signInBanner").hidden = false;
    }
    const delay = Math.min(30000, 1000 * 2 ** Math.min(failures, 5));
    retryTimer = setTimeout(() => { if (source === es) connect(taskId); }, delay);
  }

  function disconnect() {
    clearTimeout(retryTimer);
    if (source) { source.close(); source = null; }
  }

  function replaceTask(record) {
    const i = S.tasks.findIndex(t => t.id === record.id);
    if (i >= 0) S.tasks[i] = record; else S.tasks.unshift(record);
  }

  // ---------- rendering ----------
  let frame = 0, structural = true;
  const dirtyText = new Set();
  function invalidate() { structural = true; schedule(); }
  function schedule() { if (!frame) frame = requestAnimationFrame(render); }
  const atBottom = el => el.scrollHeight - el.scrollTop - el.clientHeight < 140;

  function render() {
    frame = 0;
    const pane = $("#detail"), body = $("#panelBody");
    const paneAtBottom = atBottom(pane), bodyAtBottom = atBottom(body);
    if (structural) {
      structural = false;
      renderTaskSelect(); renderBudget(); renderHeader();
      if (S.view === "tasks") { renderTree(); renderDetail(); }
      renderPanel();
    }
    const now = performance.now();
    for (const item of [...dirtyText]) {
      if (item.kind === "text" && now - (item.renderedAt || 0) < 120) continue;
      if (item.el) renderItemText(item);
      renderPreview(item.turn);
      item.renderedAt = now;
      dirtyText.delete(item);
    }
    if (dirtyText.size) schedule();
    if (S.follow && paneAtBottom) pane.scrollTop = pane.scrollHeight;

    const p = S.panel;
    if (p?.focus) {
      const turn = body.querySelector(`[data-turn="${p.focus}"]`);
      p.focus = null;
      if (turn) { turn.scrollIntoView({ block: "start" }); turn.classList.remove("flash"); void turn.offsetWidth; turn.classList.add("flash"); }
    } else if (p?.toBottom || (p && bodyAtBottom)) {
      p.toBottom = false;
      body.scrollTop = body.scrollHeight;
    }
  }

  function renderBudget() {
    const b = S.budget;
    $("#mAgents").textContent = nf.format(b?.agents || 0);
    $("#mDepth").textContent = b ? `(deepest team level ${b.deepestLevel})` : "";
    $("#mIn").textContent = nf.format(b?.inputTokens || 0);
    $("#mCached").textContent = nf.format(b?.cachedInputTokens || 0); // a subset of input, not added (ADR 0104)
    $("#mOut").textContent = nf.format(b?.outputTokens || 0);
    $("#tokText").textContent = b ? `${compact.format(b.tokens)} / ${compact.format(b.maxTokens)}` : "–";
    $("#agText").textContent = b ? `${b.agents} / ${b.maxAgents}` : "–";
    const tf = b && b.maxTokens ? b.tokens / b.maxTokens : 0, af = b ? b.agents / b.maxAgents : 0;
    $("#tokFill").style.width = `${Math.min(100, tf * 100)}%`; $("#tokFill").classList.toggle("high", tf >= .8);
    $("#agFill").style.width = `${Math.min(100, af * 100)}%`; $("#agFill").classList.toggle("high", af >= .8);
  }

  function renderHeader() {
    const t = taskById(S.selectedTask);
    $("#stopBtn").hidden = !(t && t.status === "running");
    // The server refuses to delete a running task (409): stop it first.
    $("#deleteBtn").hidden = !(t && t.status !== "running");
    // A link can be made for any task, a running one included: the view is live (ADR 0120).
    $("#shareBtn").hidden = !t;
    $("#analyzeBtn").hidden = !t;
    analyzerTaskChanged();
    $("#promptsBtn").classList.toggle("active", S.view === "prompts");
    $("#lessonsBtn").classList.toggle("active", S.view === "lessons");
  }

  let shownPrompt = null;
  function renderPrompt() {
    $("#promptBanner").hidden = !S.prompt;
    $("#promptLabel").textContent = S.clarify?.phase === "asking" ? "Before it starts, the fleet asks" : "The fleet asks";
    if (S.prompt === shownPrompt) return;
    shownPrompt = S.prompt;
    if (!S.prompt) return;
    $("#promptText").textContent = S.prompt.question;
    $("#promptChoices").replaceChildren(...(S.prompt.choices || []).map(c =>
      h("button", { class: "btn", type: "button", text: c.label, onclick: () => answer(c.answer) })));
    const input = $("#promptInput");
    input.value = "";
    input.placeholder = S.prompt.freeTextHint || "Your answer";
  }

  async function answer(text) {
    if (!S.selectedTask) return;
    const r = await api("POST", `/api/fleet/tasks/${encodeURIComponent(S.selectedTask)}/answer`, { answer: text, questionId: S.prompt?.id ?? null });
    if (r.ok) { S.prompt = null; renderPrompt(); }
  }

  // ---------- composer ----------
  // A model the fleet can run: a chat model that takes tools (FleetTaskService.IsToolCapable).
  const toolCapable = m => m.supportsTools && !m.supportsEmbeddings && !m.supportsSpeech && !m.supportsTranscription && !m.supportsImageGeneration;

  const composer = (() => {
    const ta = h("textarea", { "aria-label": "Task", onkeydown: e => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) submitTask(); } });
    const err = h("div", { class: "error-text" });
    const btn = h("button", { class: "btn primary", onclick: () => submitTask() });
    const title = h("div", { class: "composer-title" });
    const hint = h("span", { class: "muted hint" });
    const model = h("select", { "aria-label": "Model" });
    const tokens = h("input", { type: "number", min: "1", step: "100000", "aria-label": "Token cap" });
    const toolsSummary = h("summary");
    const toolsList = h("div", { class: "tool-list" });
    const tools = h("details", { class: "tool-picker", hidden: true }, toolsSummary, toolsList);
    // ADR 0118: on by default; a per-viewer convenience, so storage that throws just leaves the default.
    const clarify = h("input", { type: "checkbox", checked: loadClarify(), onchange: e => saveClarify(e.target.checked) });
    const selfReview = h("input", { type: "checkbox", checked: loadSelfReview(), onchange: e => saveSelfReview(e.target.checked) });
    const options = h("div", { class: "composer-options" }, h("label", null, "Model", model), h("label", null, "Token cap", tokens),
      h("label", { title: "The fleet asks you questions about the task until the requirements are clear, then plans it" }, clarify, "Ask me questions first"),
      h("label", { title: "The agent that writes the result checks it against the task and fixes gaps before it is judged. Costs extra tokens" }, selfReview, "Self-review the result"), tools);
    const fileInput = h("input", { type: "file", multiple: true, hidden: true, onchange: e => { addAttachments(e.target.files); e.target.value = ""; } });
    const chips = h("div", { class: "attachments" });
    const files = h("div", { class: "composer-files" },
      h("button", { class: "btn", type: "button", text: "Attach files", onclick: () => fileInput.click() }), fileInput, chips,
      h("span", { class: "muted hint", text: "Agents read attached files in the workspace. Documents (PDF, Word, PowerPoint, Excel, text) become searchable when you've chosen an embedding model in Settings." }));
    const el = h("div", { class: "card composer" }, title, ta, files, options, h("div", { class: "composer-row" }, hint, err, btn));
    el.addEventListener("dragover", e => { if (e.dataTransfer?.types?.includes("Files")) { e.preventDefault(); el.classList.add("dropping"); } });
    el.addEventListener("dragleave", e => { if (!el.contains(e.relatedTarget)) el.classList.remove("dropping"); });
    el.addEventListener("drop", e => { if (e.dataTransfer?.files?.length) { e.preventDefault(); addAttachments(e.dataTransfer.files); } el.classList.remove("dropping"); });
    return { el, ta, err, btn, title, hint, model, tokens, clarify, selfReview, tools, toolsSummary, toolsList, chips };
  })();

  // ---------- attached files (ADR 0101) ----------
  // The server refuses two files of one name, so a file replaces an attached one of the same name (case-insensitive).
  // File names are the user's: text only.
  function addAttachments(list) {
    for (const file of list) {
      S.attachments = S.attachments.filter(f => f.name.toLowerCase() !== file.name.toLowerCase()).concat(file);
    }
    renderAttachments();
  }

  function renderAttachments() {
    composer.chips.replaceChildren(...S.attachments.map(f => h("span", { class: "attachment" },
      h("span", { class: "name", text: f.name }), h("span", { class: "size", text: fmtBytes(f.size) }),
      h("button", { type: "button", class: "remove", title: "Remove", "aria-label": "Remove " + f.name, text: "×",
        onclick: () => { S.attachments = S.attachments.filter(x => x !== f); renderAttachments(); } }))));
  }

  // ---------- tools picker (ADR 0099) ----------
  // A task follows the user's default chat selection unless this picker was changed: only then is an override sent,
  // so a new MCP server still reaches a task that didn't override (ADR 0021, null == all). It shows only what the
  // fleet uses, web_search and the MCP servers with their tools, so a sent override
  // drops every other key on purpose: it is this task's, never written back to the profile. A local picker, because
  // this classic script can't import js/tools.js. MCP names are server-supplied: text only.
  const fleetToolKey = key => key === "web_search" || key.startsWith("mcp:");
  const toolPicker = { picked: new Set(), dirty: false, target: undefined };
  const toolChildren = row => (S.tools || []).filter(r => r.parentKey === row.key);

  // Whether a selection grants a row, as ToolCatalog.IsMcpToolEnabled does: the bare mcp, its server's key, or its own.
  const grants = (selection, row) => selection.has(row.key)
    || (row.key.startsWith("mcp:") && selection.has("mcp")) || (!!row.parentKey && selection.has(row.parentKey));

  // Starts the picker from an override, or from the user's default for null. A given override stays dirty, so a
  // follow-up sends it again.
  function resetToolPicker(override) {
    const selection = override ?? S.defaultTools;
    const set = selection && new Set(selection);
    toolPicker.picked = new Set((S.tools || []).filter(r => !set || grants(set, r)).map(r => r.key));
    toolPicker.dirty = override != null;
    renderToolPicker();
  }

  // The override: a server's own key when all of its tools are ticked (or it has none listed yet, the cold-start
  // window), so it also grants tools the server adds later; otherwise only its ticked tools.
  function readToolPicker() {
    const keys = [];
    for (const row of (S.tools || []).filter(r => !r.parentKey)) {
      const on = toolChildren(row).filter(c => toolPicker.picked.has(c.key)).map(c => c.key);
      if (toolPicker.picked.has(row.key) && on.length === toolChildren(row).length) keys.push(row.key);
      else keys.push(...on);
    }
    return keys;
  }

  // Sent only when the user changed the picker and the catalog loaded: an empty catalog must not become "no tools".
  const toolOverride = () => toolPicker.dirty && S.tools ? readToolPicker() : undefined;

  // A server row takes its tools with it; a tool row of an unticked server grants that tool alone. A server whose
  // last ticked tool goes is unticked too, so a ticked box never sends nothing.
  function toggleTool(row, on) {
    for (const key of [row.key, ...(row.parentKey ? [] : toolChildren(row).map(c => c.key))]) {
      if (on) toolPicker.picked.add(key); else toolPicker.picked.delete(key);
    }
    const parent = row.parentKey && (S.tools || []).find(r => r.key === row.parentKey);
    if (parent && !toolChildren(parent).some(c => toolPicker.picked.has(c.key))) toolPicker.picked.delete(parent.key);
    toolPicker.dirty = true;
    renderToolPicker();
  }

  function renderToolPicker() {
    const rows = S.tools || [];
    composer.tools.hidden = rows.length === 0;
    composer.toolsSummary.textContent = "Tools: " + (toolPicker.dirty ? `${readToolPicker().length} selected` : "your defaults");
    const check = (row, cls) => h("label", { class: cls },
      h("input", { type: "checkbox", checked: toolPicker.picked.has(row.key), onchange: e => toggleTool(row, e.target.checked) }),
      h("span", { text: row.label }));
    composer.toolsList.replaceChildren(
      ...rows.filter(r => !r.parentKey).map(row => h("div", { class: "tool-group" },
        check(row, "tool-row"), toolChildren(row).map(c => check(c, "tool-row child")))),
      ...(toolPicker.dirty ? [h("a", { class: "switch", text: "Back to your defaults", onclick: () => resetToolPicker(null) })] : []));
  }

  function fillComposerOptions() {
    const models = (S.models?.models || []).filter(toolCapable);
    composer.model.replaceChildren(...models.map(m => h("option", { value: m.id, text: m.displayName ? `${m.displayName} (${m.id})` : m.id })));
    const preferred = models.find(m => m.id === S.models?.default) || models[0];
    if (preferred) composer.model.value = preferred.id;
    if (S.config) {
      composer.tokens.value = S.config.defaultMaxTokens;
      composer.tokens.max = S.config.maxTokensLimit;
    }
  }

  function hasModels() { return (S.models?.models || []).some(toolCapable); }

  function followUpTarget() {
    const t = taskById(S.selectedTask);
    return t && t.canFollowUp && !S.freshTask && !running() ? t : null;
  }

  function updateComposer() {
    const t = followUpTarget();
    // A follow-up starts from the override of the task it follows up, a new task from the user's default.
    if (toolPicker.target !== (t?.id ?? null)) { toolPicker.target = t?.id ?? null; resetToolPicker(t?.enabledTools ?? null); }
    const done = S.tasks.find(x => x.canFollowUp && x.id === S.selectedTask);
    composer.el.classList.toggle("follow-up", !!t);
    composer.title.replaceChildren(...(t
      ? [`Follow up on task #${taskNumber(t.id)}`, h("a", { class: "switch", text: "Start a new task instead", onclick: () => { S.freshTask = true; invalidate(); } })]
      : ["New task", done && h("a", { class: "switch", text: `Follow up on task #${taskNumber(done.id)} instead`, onclick: () => { S.freshTask = false; invalidate(); } })].filter(Boolean)));
    composer.ta.placeholder = t
      ? "What's next? For example: add a section on pricing, or turn the report into a slide outline."
      : "Describe a task, for example: compare the three most used .NET web frameworks and write a short report.";
    composer.hint.textContent = t
      ? "Ctrl+Enter to run. A new team gets this with the earlier requests, their results and the files in the workspace."
      : "Ctrl+Enter to run. A planner designs a team of agents for it, on your own models.";
    composer.btn.textContent = t ? "Run follow-up" : "Run task";
  }

  // Without a tool-capable model there is nothing to run on: the same gate as the chat composer (ADR 0019).
  function providerGate() {
    return h("div", { class: "card gate" },
      h("b", { text: "Add a model provider first" }),
      h("div", { class: "muted", text: "The fleet runs on your own models, and none of them supports tools yet. Add a provider with a tool-capable model in Settings, then come back." }),
      h("div", { style: "margin-top:8px" }, h("a", { href: "/", text: "Open Agent 42 →" })));
  }

  // Run again passes the record's enabledTools (null too), which then wins over the picker, and the task whose files
  // it copies (copyInputsOf). Only the composer's own submit sends the attached files: as multipart, to the files
  // route, and as plain JSON when there are none.
  async function submitTask(text, followUpOf, enabledTools, copyInputsOf, clarify, selfReview) {
    const task = (text ?? composer.ta.value).trim();
    if (!task) { composer.ta.focus(); return; }
    composer.btn.disabled = true;
    const request = {
      task,
      followUpOf: followUpOf === undefined ? followUpTarget()?.id : followUpOf,
      model: composer.model.value || null,
      maxTokens: Number(composer.tokens.value) || null,
      enabledTools: enabledTools !== undefined ? enabledTools : toolOverride(),
      copyInputsOf: copyInputsOf || null,
      clarify: clarify !== undefined ? clarify : composer.clarify.checked,
      selfReview: selfReview !== undefined ? selfReview : composer.selfReview.checked,
    };
    const files = text === undefined ? S.attachments : [];
    let r;
    if (files.length > 0) {
      const form = new FormData();
      form.append("request", JSON.stringify(request));
      for (const file of files) form.append("files", file, file.name);
      r = await api("POST", "/api/fleet/tasks/with-files", form);
    } else {
      r = await api("POST", "/api/fleet/tasks", request);
    }
    composer.btn.disabled = false;
    if (r.ok) {
      if (files.length > 0) { S.attachments = []; renderAttachments(); }
      composer.ta.value = ""; composer.err.textContent = ""; S.freshTask = true; setFollow(true); toolPicker.target = undefined;
      await loadTasks();
      selectTask(r.data.taskId);
    } else {
      // No error field means something in front of the app answered (Easy Auth, the ingress): show the status.
      composer.err.textContent = r.data?.error || `The task could not be started (HTTP ${r.status}).`;
    }
  }

  function renderTaskSelect() {
    const sel = $("#taskSelect");
    const placeholder = !S.selectedTask && h("option", { value: "", text: S.tasks.length ? "Your tasks…" : "No tasks yet", disabled: true, hidden: true });
    sel.replaceChildren(...[placeholder, ...S.tasks.map(t => {
      const mark = t.status === "running" ? "● " : t.status === "done" ? "✓ " : "✕ ";
      const parent = t.followUpOf && taskById(t.followUpOf);
      return h("option", { value: t.id, text: `${mark}#${taskNumber(t.id)} ${parent ? `↳ #${taskNumber(parent.id)} ` : ""}${firstLine(t.request, 60)}` });
    })].filter(Boolean));
    sel.value = S.selectedTask || "";
  }

  function teamLabel(team) {
    if (team.depth === 0) return "Main team";
    return team.delegatedBy ? `${team.delegatedBy}'s sub-team` : `Sub-team (level ${team.depth})`;
  }

  function teamStatus(team) {
    const a = team.attempts[team.attempts.length - 1];
    if (team.status === "running" && a) return a.status;
    return team.status;
  }

  function renderTree() {
    const tree = $("#tree");
    const task = taskById(S.selectedTask);
    if (!task) {
      tree.replaceChildren(h("div", { class: "tree-task", text: S.tasks.length ? "Pick a task at the top to see it or follow it up." : "No tasks yet." }));
      return;
    }
    const node = id => {
      const team = S.teams.get(id);
      const a = team.attempts[team.attempts.length - 1];
      const plan = currentPlan(a);
      const meta = [plan && plan.pattern, plan && `${plan.agents.length} agent${plan.agents.length === 1 ? "" : "s"}`, team.attempts.length > 1 && `attempt ${team.attempts.length}`].filter(Boolean).join(" · ");
      return h("div", null,
        h("div", { class: "node" + (S.selectedTeam === id ? " sel" : ""), onclick: () => select(team.id) },
          h("span", { class: "dot " + teamStatus(team) }),
          h("div", { class: "node-body" },
            h("div", { class: "label", text: teamLabel(team) }),
            team.depth > 0 && h("div", { class: "sub", text: firstLine(team.task, 140) }),
            meta && h("div", { class: "meta", text: meta }))),
        team.children.length > 0 && h("div", { class: "children" }, team.children.map(node)));
    };
    const parent = task.followUpOf && taskById(task.followUpOf);
    tree.replaceChildren(
      h("div", { class: "tree-task" },
        h("div", null, `Task #${taskNumber(task.id)} · ${task.status} · `, dur(task.startedAt, task.finishedAt)),
        parent && h("div", null, "Follow-up on ", h("a", { class: "task-link", text: `task #${taskNumber(parent.id)}`, onclick: () => selectTask(parent.id) })),
        h("div", { class: "t", text: task.request })),
      S.rootTeamId ? node(S.rootTeamId)
        : task.status === "running" ? h("div", { class: "node" }, h("span", { class: "dot planning" }), h("span", { class: "muted", text: "Starting…" }))
        : h("div", { class: "node" }, h("span", { class: "dot " + task.status }), h("span", { class: "muted", text: "No team ran." })));
  }

  function select(teamId) {
    if (S.selectedTeam !== teamId) S.panel = null;
    S.selectedTeam = teamId; S.selectedAttempt = null;
    setFollow(false);
    invalidate();
  }

  function setFollow(on) { S.follow = on; $("#follow").checked = on; }

  function renderDetail() {
    const pane = $("#detail");
    const task = taskById(S.selectedTask);
    if (!running()) updateComposer();
    const top = S.models && !hasModels() ? providerGate() : !running() && composer.el;
    const team = S.teams.get(S.selectedTeam);
    if (!team) {
      pane.replaceChildren(h("div", { class: "detail-inner" }, top,
        task ? taskSummary(task) : h("div", { class: "empty", style: "height:auto;padding:40px 0" }, h("div", null,
          h("h3", { text: "Nothing running yet" }),
          h("div", { text: "Describe a task above. The teams and agents that work on it show up here, live." })))));
      return;
    }
    if (S.detailView === "graph" && S.rootTeamId) {
      showGraph(pane, h("div", { class: "detail-inner wide" }, top, viewSwitch(), renderGraph()));
      return;
    }
    const a = (S.selectedAttempt && team.attempts.find(x => x.n === S.selectedAttempt)) || team.attempts[team.attempts.length - 1];

    const chain = [];
    for (let t = team; t; t = S.teams.get(t.parentTeamId)) chain.unshift(t);
    const crumbs = h("div", { class: "crumbs" }, chain.map((t, i) => i === chain.length - 1
      ? h("span", { text: teamLabel(t) })
      : [h("a", { text: teamLabel(t), onclick: () => select(t.id) }), " › "]));

    const status = teamStatus(team);
    const head = h("div", { class: "card" },
      crumbs,
      h("h1", null, teamLabel(team), h("span", { class: "pill " + status, text: statusText(status) })),
      h("div", { class: "sub-head" }, `Level ${team.depth} · `, dur(team.started, team.finished), team.depth === 0 && task && toolsText(task)),
      clampText("task-" + team.id, "task-text", team.task, 600));

    const tabs = team.attempts.length > 1 && h("div", { class: "tabs" }, team.attempts.map(x => h("button", {
      class: "tab" + (x === a ? " sel" : ""),
      onclick: () => { S.selectedAttempt = x.n; setFollow(false); invalidate(); },
    }, h("span", { class: "dot " + x.status }), `Attempt ${x.n}`, x.fix && h("span", { class: "muted", text: "fix" }), x.verdict && h("span", { class: "muted", text: `${x.verdict.score}/10` }))));

    pane.replaceChildren(h("div", { class: "detail-inner" }, top, viewSwitch(), head, team.depth === 0 && task && attachedCard(task), tabs, a ? renderAttempt(team, a) : null,
      team.depth === 0 && task && taskFooter(task)));
  }

  // Whether the task ran on the user's default tools or on its own override (ADR 0099).
  const toolsText = task => ` · Tools: ${task.enabledTools ? `${task.enabledTools.length} selected` : "your defaults"}`;

  // A task's own outcome, when no team is shown: its request, result or reason, files and what can be done next.
  function taskSummary(task) {
    return [
      h("div", { class: "card" },
        h("h1", null, `Task #${taskNumber(task.id)}`, h("span", { class: "pill " + task.status, text: statusText(task.status) })),
        h("div", { class: "sub-head" }, `${task.plannerModel} · `, dur(task.startedAt, task.finishedAt), toolsText(task)),
        clampText("task-" + task.id, "task-text", task.request, 600)),
      attachedCard(task),
      lessonsAppliedCard(task),
      task.status === "running" && h("div", { class: "card waiting", style: "margin-top:12px" }, h("span", { class: "dot planning" }), "The planner is putting a team together…"),
      taskFooter(task),
    ];
  }

  // The task's attached files with how each was ingested (the documents.ingested event), and which documents the
  // agents can search. Names are the user's and the reasons the server's: text only.
  function attachedCard(task) {
    const files = S.ingested || (task.inputs || []).map(name => ({ name }));
    if (files.length === 0 && !(task.documents || []).length) return null;
    const outcome = f => !f.status ? ""
      : f.status === "indexed" ? `searchable (${f.chunks} ${f.chunks === 1 ? "chunk" : "chunks"})`
      : f.reason ? `${f.status}: ${f.reason}` : f.status;
    const copy = f => f.textCopy ? ` · text copy ${f.textCopy}` : f.textCopySkipped ? ` · text copy skipped: ${f.textCopySkipped}` : "";
    return h("div", { class: "card attached", style: "margin-top:12px" },
      h("b", { text: "Attached files" }),
      files.length > 0 && h("ul", { class: "files" }, files.map(f => h("li", null,
        h("span", { class: "name", text: "inputs/" + f.name }), h("span", { class: "muted", text: [outcome(f), copy(f)].filter(Boolean).join("") })))),
      (task.documents || []).length > 0 && h("div", { class: "muted", text: "Agents can search: " + task.documents.join(", ") }));
  }

  // The questions a task started from a chat answered by itself (ADR 0115). The question text quotes model-written
  // commands, file names and MCP server messages: text only, never innerHTML.
  function autoAnswersCard() {
    if (S.autoAnswers.length === 0) return null;
    return [h("h2", { text: "Answered automatically" }), h("div", { class: "card" },
      h("div", { class: "muted", text: "Started from a chat, so nobody was asked: the plan and agents' actions were approved, MCP questions declined." }),
      h("ul", { class: "files" }, S.autoAnswers.map(a => h("li", null,
        h("span", { class: "name", text: a.question }), h("span", { class: "muted", text: a.answer })))))];
  }

  // What the clarify phase wrote from the request (ADR 0118): the brief is model text, shown as text only.
  function requirementsCard() {
    const c = S.clarify;
    if (c?.phase !== "done") return null;
    const asked = `${c.questions} ${c.questions === 1 ? "question" : "questions"} asked`;
    return [h("h2", { text: "Requirements" }), h("div", { class: "card" },
      h("div", { class: "muted", text: c.cleared ? `${asked}. The team was planned from this brief, not from your request as typed.` : `${asked}, but no brief came out of it: the team was planned from your request as typed.` }),
      c.cleared && h("div", { class: "brief", text: c.brief }))];
  }

  function taskFooter(task) {
    return [
      requirementsCard(),
      autoAnswersCard(),
      task.result && [h("h2", { text: "Result" }), h("div", { class: "card" }, clampText("result-" + task.id, "result", task.result, 2500, true))],
      task.reason && task.status !== "done" && [h("h2", { text: "Why it ended" }), h("div", { class: "card", text: task.reason })],
      canvasCard(task),
      filesCard(task),
      followUpCard(task),
      runAgainCard(task),
    ];
  }

  function filesCard(task) {
    const files = S.files?.taskId === task.id ? S.files.list : null;
    if (!files || files.length === 0) return null;
    const href = path => `/api/fleet/tasks/${encodeURIComponent(task.id)}/files/${path.split("/").map(encodeURIComponent).join("/")}`;
    return [h("h2", { text: "Workspace files" }), h("div", { class: "card" },
      h("ul", { class: "files" }, files.map(f => h("li", null, h("a", { href: href(f.path), download: f.path.split("/").pop(), text: f.path }), h("span", { class: "size", text: fmtBytes(f.size) })))))];
  }

  // The canvas pages, while the task runs too, so the user can watch. Names and titles are model-written: text only.
  function canvasCard(task) {
    const pages = S.canvas?.taskId === task.id ? S.canvas.list : null;
    if (!pages || pages.length === 0) return null;
    const latest = p => p.versions.length ? p.versions[p.versions.length - 1].version : 0;
    return [h("h2", { text: "Canvas" }), h("div", { class: "card" },
      h("ul", { class: "files canvas-pages" }, pages.map(p => h("li", null,
        h("button", { class: "linkish", type: "button", text: p.title, title: `Open ${p.id}`, onclick: () => openCanvasPage(p.id, latest(p)) }),
        h("span", { class: "size", text: `${p.kind} · v${latest(p)}` })))))];
  }

  async function loadCanvas() {
    const id = S.selectedTask;
    if (!id) return;
    const r = await api("GET", `/api/fleet/tasks/${encodeURIComponent(id)}/canvas`);
    if (r.ok && S.selectedTask === id) { S.canvas = { taskId: id, list: r.data || [] }; invalidate(); }
  }

  // A replayed log holds a canvas.updated per write, so the reloads they ask for are coalesced into one.
  let canvasLoadTimer = null;
  function scheduleCanvasLoad() {
    clearTimeout(canvasLoadTimer);
    canvasLoadTimer = setTimeout(loadCanvas, 150);
  }

  async function loadFiles() {
    const id = S.selectedTask;
    if (!id) return;
    const r = await api("GET", `/api/fleet/tasks/${encodeURIComponent(id)}/files`);
    if (r.ok && S.selectedTask === id) { S.files = { taskId: id, list: r.data || [] }; invalidate(); }
  }

  function followUpCard(task) {
    return task?.canFollowUp && !running() && h("div", { class: "card follow-up-cta" },
      h("div", null, h("b", { text: "Want more?" }), h("div", { class: "muted", text: "Ask a follow-up, a new team builds on this result and the files in the workspace." })),
      h("button", { class: "btn primary", text: "Follow up", onclick: () => { S.freshTask = false; updateComposer(); $("#detail").scrollTop = 0; composer.ta.focus(); } }));
  }

  // An interrupted, stopped or failed task can't be followed up, but its request can run again as a new task. A follow-up
  // runs again in the shared workspace; a new task gets a new one, so it copies the old task's attached files (ADR 0101).
  function runAgainCard(task) {
    return ["interrupted", "stopped", "failed"].includes(task?.status) && !running() && h("div", { class: "card follow-up-cta" },
      h("div", null, h("b", { text: task.status === "interrupted" ? "Interrupted by a restart" : "Didn't finish" }),
        h("div", { class: "muted", text: "Run the same request again as a new task." })),
      h("button", { class: "btn", text: "Run again", onclick: () => submitTask(task.request, task.followUpOf || null, task.enabledTools ?? null, task.followUpOf ? null : task.id, !!task.clarify, !!task.selfReview) }));
  }

  function statusText(s) {
    return { planning: "planning", approval: "awaiting approval", running: "working", judging: "judging", passed: "passed", retry: "retrying", done: "done", failed: "failed", stopped: "stopped", interrupted: "interrupted" }[s] || s;
  }

  function renderAttempt(team, a) {
    const plan = currentPlan(a);
    const out = [];
    if (a.review) out.push(h("h2", { text: `Fixing the review of attempt ${a.review.fromAttempt}` }),
      h("div", { class: "card verdict" },
        h("div", { class: "score fail" }, String(a.review.score), h("small", { text: "/10" })),
        h("div", null,
          h("div", { class: "muted", style: "margin-bottom:4px", text: a.fix
            ? "The same team works on its result again, with this feedback."
            : "A new team was designed. It gets the task with this feedback and the previous result." }),
          clampText("review-" + team.id + "-" + a.n, "result", a.review.feedback || "(no feedback given)", 1200, true))));
    out.push(h("h2", { text: "Team" }));
    out.push(plan ? renderPlan(team, a, plan) : h("div", { class: "card" },
      a.status === "planning" ? h("div", { class: "waiting" }, h("span", { class: "dot planning" }), "The planner is designing a team for this task…")
        : h("div", { class: "muted", text: "No team was created." }),
      notesList(a)));

    out.push(...renderTodos(a));
    const hasTurns = a.timeline.some(e => e.kind === "turn");
    out.push(h("h2", null, "Activity", hasTurns && h("span", { class: "muted", style: "font-weight:400;text-transform:none;letter-spacing:0;margin-left:10px", text: "Click an agent to see its output" })));
    out.push(a.timeline.length
      ? h("div", { class: "timeline" }, a.timeline.map(e => renderEntry(e, team, a)))
      : h("div", { class: "muted", text: plan && a.status === "running" ? "Waiting for the first agent…" : "Nothing yet." }));

    if (a.status === "judging") out.push(h("h2", { text: "Review" }), h("div", { class: "card waiting" }, h("span", { class: "dot judging" }), "The judge is reviewing the result…"));
    if (a.verdict) out.push(h("h2", { text: "Review" }), renderVerdict(a.verdict));
    if (a.result != null) out.push(h("h2", { text: "Result" }), h("div", { class: "card" }, clampText("result-" + team.id + "-" + a.n, "result", a.result || "(empty)", 2500, true)));
    return out;
  }

  // The team's shared checklist (ADR 0117), read-only. Titles are agent text: textContent only.
  const todoCount = todos => `${todos.filter(t => t.done).length}/${todos.length}`;
  function renderTodos(a) {
    const todos = a.todos || [];
    if (todos.length === 0) return [];
    return [h("h2", null, "Todos", h("span", { class: "muted todo-count", text: todoCount(todos) })),
      h("ul", { class: "card todos" }, todos.map(t => h("li", { class: t.done ? "done" : "" },
        h("span", { class: "todo-box", text: t.done ? "✓" : "" }), h("span", { text: t.title }))))];
  }

  function notesList(a) {
    return a.notes.length > 0 && det("notes-" + a.n + a.started, "revisions", `Planner revisions (${a.notes.length})`, h("ul", { class: "notes" }, a.notes.map(n => h("li", { text: n }))));
  }

  function agentState(a, name) {
    let state = "idle";
    for (const t of a.turns.values()) if ((t.agent || "").toLowerCase() === (name || "").toLowerCase()) state = t.done ? (state === "working" ? state : "done") : "working";
    return state;
  }

  function chip(a, name, onclick) {
    const state = agentState(a, name);
    return h("span", { class: "chip" + (onclick ? " link" : ""), style: `--c:${agentColor(name)}`, title: onclick ? `${state}, click to see the output` : state, onclick },
      h("span", { class: "dot " + state }), name);
  }

  function renderFlow(team, a, plan) {
    const names = plan.agents.map(x => x.name);
    const linkChip = name => chip(a, name, () => openPanel(team.id, a.n, name));
    const arrow = () => h("span", { class: "arrow", text: "→" });
    const group = (label, list) => h("div", { class: "group" }, label && h("div", { class: "group-label", text: label }), list.map(linkChip));
    if (names.length === 1) return h("div", { class: "flow" }, linkChip(names[0]));
    switch (plan.pattern) {
      case "Concurrent": return h("div", { class: "flow" }, group("in parallel", names));
      case "Staged": return h("div", { class: "flow" }, (plan.stages || []).map((st, i) => [i > 0 && arrow(), group(`stage ${i + 1}`, st)]));
      case "GroupChat": return h("div", { class: "flow" }, group("group chat", names),
        h("div", { class: "flow-note" }, plan.terminationCriteria ? `Ends when: ${plan.terminationCriteria}` : "", h("br"), `Max ${plan.maxGroupChatRounds} turns, a moderator picks who speaks next`));
      case "Handoff": return h("div", { class: "flow" }, linkChip(names[0]), h("span", { class: "arrow", text: "⇄" }), group("specialists", names.slice(1)));
      case "Magentic": return h("div", { class: "flow" }, h("span", { class: "chip", text: "Manager" }), arrow(), group("participants", names),
        h("div", { class: "flow-note", text: `Max ${plan.maxGroupChatRounds} rounds` }));
      default: return h("div", { class: "flow" }, names.map((n, i) => [i > 0 && arrow(), linkChip(n)]));
    }
  }

  function yamlTools(yaml) {
    const i = (yaml || "").search(/^tools:/m);
    if (i < 0) return [];
    return [...yaml.slice(i).matchAll(/^\s+(?:-\s+)?name:\s*(\S+)/gm)].map(m => m[1]);
  }

  function renderPlan(team, a, plan) {
    const started = a.status !== "approval" && a.status !== "planning";
    return h("div", { class: "card" },
      h("div", { class: "plan-top" }, h("span", { class: "pattern", text: plan.pattern }), h("span", { class: "rationale", text: plan.rationale })),
      a.status === "approval" && h("div", { class: "waiting", style: "margin-top:8px" }, h("span", { class: "dot approval" }), "Waiting for your approval, answer the question at the top."),
      renderFlow(team, a, plan),
      h("div", { class: "agents" }, plan.agents.map(ag => {
        const tools = yamlTools(ag.yaml);
        const live = agentTurns(a, ag.name).findLast(t => !t.done);
        return h("div", {
          class: "agent" + (started ? " clickable" : ""), style: `--c:${agentColor(ag.name)}`,
          title: started ? "Click to see the output" : null,
          onclick: started ? e => { if (!e.target.closest("details")) openPanel(team.id, a.n, ag.name); } : null,
        },
          h("div", { class: "agent-name" }, h("span", { text: ag.name }), h("span", { class: "model", text: ag.model })),
          ag.description && h("div", { class: "agent-desc", text: ag.description }),
          tools.length > 0 && h("div", { class: "tools" }, tools.map(t => h("span", { class: "tool-chip", text: t }))),
          h("div", { class: "agent-live", "data-turn-preview": live?.id, text: live ? turnPreview(live) : "" }),
          det("yaml-" + a.started + ag.name, "yaml", "Definition", h("pre", { text: ag.yaml })));
      })),
      notesList(a));
  }

  function renderEntry(e, team, a) {
    switch (e.kind) {
      case "stage": return h("div", { class: "marker", text: `Stage ${e.stage} of ${e.stages} · ${e.agents.join(", ")}` });
      case "event": return h("div", { class: "event" + (e.error ? " error" : "") }, h("span", { class: "lbl", text: e.label }), clampText("ev-" + e.id, "", e.text || "", 1200, true));
      case "turn": return activityRow(e, team, a);
    }
  }

  const sameAgent = (x, y) => (x || "").toLowerCase() === (y || "").toLowerCase();
  const agentTurns = (a, name) => a.timeline.filter(e => e.kind === "turn" && sameAgent(e.agent, name));
  const turnState = turn => turn.failed ? "failed" : turn.done ? "done" : "working";

  function turnPreview(turn) {
    if (turn.failed) return "Failed: " + turn.failed;
    const last = turn.items[turn.items.length - 1];
    if (!last) return turn.done ? "" : "Starting…";
    if (last.kind === "tool") return `${last.call.pending ? "Calling" : "Called"} ${last.call.name}`;
    if (last.kind === "thinking") return turn.done ? "Thought it over" : "Thinking…";
    const lines = last.text.split(/\r?\n/).map(l => l.replace(/^[\s#>*\-|`]+/, "").replace(/[*_`]/g, "").trim()).filter(Boolean);
    return ((turn.done ? lines[0] : lines[lines.length - 1]) || "").slice(0, 300);
  }

  function renderPreview(turn) {
    if (!turn) return;
    const text = turnPreview(turn);
    for (const el of document.querySelectorAll(`[data-turn-preview="${turn.id}"]`)) el.textContent = text;
  }

  function activityRow(turn, team, a) {
    const calls = turn.items.filter(i => i.kind === "tool").map(i => i.call);
    const subs = calls.map(c => c.subTeamId && S.teams.get(c.subTeamId)).filter(Boolean);
    const state = turnState(turn);
    const p = S.panel;
    const sel = p && p.teamId === team.id && p.attempt === a.n && sameAgent(p.agent, turn.agent);
    return h("div", null,
      h("div", { class: `act-row ${state}${sel ? " sel" : ""}`, style: `--c:${agentColor(turn.agent)}`, onclick: () => openPanel(team.id, a.n, turn.agent, turn.id) },
        h("span", { class: "dot " + state }),
        h("b", { text: turn.agent }),
        h("span", { class: "act-preview", "data-turn-preview": turn.id, text: turnPreview(turn) }),
        calls.length > 0 && h("span", { class: "act-meta", text: `${calls.length} tool call${calls.length === 1 ? "" : "s"}` }),
        h("span", { class: "go", text: "Output →" })),
      subs.length > 0 && h("div", { class: "act-subteams" }, subs.map(subTeamLink)));
  }

  // ---------- graph (ADR 0116) ----------
  // A picture of the task: a box per team, a node per agent the planner designed, edges from the plan's pattern, and
  // a sub-team box under the agent that delegated to it. Built only from the state the handlers already fold, so it
  // is the same on replay, on a reload mid-run and live. Node labels are set as text, never as markup.
  const G = { nodeW: 150, nodeH: 46, gapX: 44, gapY: 18, pad: 12, head: 24, cap: 24 };

  // The agent names of a plan, grouped by rank (a column), plus the pattern's edges between names. Handoff and
  // GroupChat edges are structural (what the pattern means), not observed hops.
  // An agent listed in two stages keeps its first one, so a node id is unique within a team.
  function stagedRanks(plan, names) {
    if (!plan.stages?.length) return [names];
    const seen = new Set();
    return plan.stages.map(st => st.filter(n => !seen.has(n) && seen.add(n))).filter(r => r.length);
  }

  function patternShape(plan) {
    const names = plan.agents.map(x => x.name);
    switch (names.length > 1 ? plan.pattern : "") {
      case "Concurrent": case "GroupChat": return { ranks: [names], links: [] };
      case "Staged": return { ranks: stagedRanks(plan, names), links: "all" };
      case "Handoff": return { ranks: [[names[0]], names.slice(1)], links: "all", both: true };
      case "Magentic": return { ranks: [[null], names], links: "all" };
      default: return { ranks: names.map(n => [n]), links: "all" };
    }
  }

  // The state of one node, from its turns: pending (idle), working, done or failed. A node that never ran in a team
  // that was stopped, interrupted or failed is drawn in the team's state (an agent has no state of its own for that).
  function graphAgentState(team, a, name) {
    const turns = agentTurns(a, name);
    if (turns.length === 0) return ["stopped", "interrupted", "failed"].includes(team.status) ? team.status : "idle";
    if (turns.some(t => !t.done)) return "working";
    const last = turns[turns.length - 1];
    if (last.cut && ["stopped", "interrupted", "failed"].includes(team.status)) return team.status;
    const state = turnState(last);
    return state === "done" && a.status === "retry" ? "retry" : state;
  }

  function graphCells(team, a, shape, subs) {
    const key = name => `${team.id}|${a.n}|${(name || "").toLowerCase()}`;
    return shape.ranks.map(rank => rank.map(name => ({
      id: `${team.id}:${a.n}:${name === null ? "manager" : name}`, name: name === null ? "Manager" : name, manager: name === null,
      state: name === null ? "" : graphAgentState(team, a, name),
      subs: name === null ? [] : (subs.get(key(name)) || []).map(id => graphTeam(id, subs)).filter(Boolean),
    })));
  }

  // A delegation whose agent is not a node of the plan (a name that drifted, or a call logged without an attempt) must
  // not make its sub-team vanish: it hangs under the first agent node, as the details tree still lists it.
  function adoptOrphans(m, team, a, subs) {
    const names = new Set(m.ranks.flat().filter(c => !c.manager).map(c => c.name.toLowerCase()));
    const host = m.ranks.flat().find(c => !c.manager);
    if (!host) return;
    for (const [key, ids] of subs) {
      const [teamId, attempt, agent] = key.split("|");
      if (teamId !== team.id || !(attempt === String(a.n) ? !names.has(agent) : attempt === "undefined")) continue;
      host.subs.push(...ids.map(id => graphTeam(id, subs)).filter(Boolean));
    }
  }

  // Edges run from every agent of a rank to every agent of the next one.
  function graphLinks(ranks, shape) {
    const links = [];
    if (shape.links !== "all") return links;
    for (let i = 0; i + 1 < ranks.length; i++)
      for (const from of ranks[i]) for (const to of ranks[i + 1]) links.push({ from: from.id, to: to.id, both: !!shape.both });
    return links;
  }

  function graphTeam(id, subs) {
    const team = S.teams.get(id);
    if (!team) return null;
    const a = team.attempts[team.attempts.length - 1]; // always the latest attempt; the attempt tabs belong to Details
    const plan = currentPlan(a);
    const m = { id, label: teamLabel(team), status: teamStatus(team), attempt: team.attempts.length > 1 && a ? a.n : 0,
      started: !!a && a.status !== "approval" && a.status !== "planning", attemptN: a?.n, todos: a?.todos || [], ranks: [], links: [], collapsed: false };
    if (!plan) return m;
    const shape = patternShape(plan);
    m.ranks = graphCells(team, a, shape, subs);
    adoptOrphans(m, team, a, subs);
    m.links = graphLinks(m.ranks, shape);
    return m;
  }

  const graphAgents = m => m.ranks.flat().reduce((n, c) => n + (c.manager ? 0 : 1) + c.subs.reduce((k, s) => k + graphAgents(s), 0), 0);

  // A large fleet stays legible: a finished sub-team shrinks to one box (click it to open it).
  function collapseDone(m, root) {
    for (const cell of m.ranks.flat()) for (const sub of cell.subs) {
      collapseDone(sub, false);
      if (sub.status === "done" && !S.graphOpen.has(sub.id)) sub.collapsed = true;
    }
    return root;
  }

  // Pure: reads S.teams and S.calls, changes nothing. Delegations are keyed by the attempt their call belongs to, so
  // a retry never shows an earlier attempt's sub-team under the same agent.
  function buildGraph(rootId) {
    const subs = new Map();
    for (const c of S.calls.values()) {
      if (!c.subTeamId) continue;
      const key = `${c.teamId}|${c.attempt}|${(c.agent || "").toLowerCase()}`;
      subs.set(key, [...(subs.get(key) || []), c.subTeamId]);
    }
    const root = graphTeam(rootId, subs);
    if (root && graphAgents(root) > G.cap) collapseDone(root, true);
    return root;
  }

  // ---- layout: each cell is a node plus the sub-teams under it; a rank stacks its cells; a team lays its ranks out
  // left to right. measure sizes everything bottom-up, place then gives absolute positions.
  function measureCell(cell, dim) {
    let w = G.nodeW, h = G.nodeH;
    for (const sub of cell.subs) { const d = measureTeam(sub, dim); w = Math.max(w, d.w); h += G.gapY + d.h; }
    const d = { w, h };
    dim.set(cell, d);
    return d;
  }

  function measureTeam(m, dim) {
    let d;
    if (m.collapsed || m.ranks.length === 0) d = { w: G.nodeW + G.pad * 2, h: G.head + G.pad + 18 };
    else {
      let w = G.pad, tall = 0;
      m.ranks.forEach((rank, i) => {
        const sizes = rank.map(cell => measureCell(cell, dim));
        const r = { w: Math.max(G.nodeW, ...sizes.map(s => s.w)), h: sizes.reduce((n, s) => n + s.h, 0) + G.gapY * (rank.length - 1) };
        dim.set(rank, r);
        w += r.w + (i < m.ranks.length - 1 ? G.gapX : G.pad);
        tall = Math.max(tall, r.h);
      });
      d = { w, h: G.head + tall + G.pad * 2 };
    }
    dim.set(m, d);
    return d;
  }

  function placeCell(cell, m, rankBox, cx, cy, dim, out) {
    const nx = cx + (rankBox.w - G.nodeW) / 2;
    const node = { cell, team: m, x: nx, y: cy };
    out.nodes.push(node);
    let sy = cy + G.nodeH + G.gapY;
    for (const sub of cell.subs) {
      const sd = dim.get(sub), sx = cx + (rankBox.w - sd.w) / 2;
      out.edges.push({ kind: "delegate", x1: nx + G.nodeW / 2, y1: cy + G.nodeH, x2: sx + sd.w / 2, y2: sy });
      placeTeam(sub, sx, sy, dim, out);
      sy += sd.h + G.gapY;
    }
    return node;
  }

  function placeTeam(m, x, y, dim, out) {
    const d = dim.get(m);
    out.boxes.push({ m, x, y, w: d.w, h: d.h });
    out.w = Math.max(out.w, x + d.w); out.h = Math.max(out.h, y + d.h);
    if (m.collapsed || m.ranks.length === 0) return;
    const at = new Map();
    let cx = x + G.pad;
    for (const rank of m.ranks) {
      const rankBox = dim.get(rank);
      let cy = y + G.head + G.pad;
      for (const cell of rank) { at.set(cell.id, placeCell(cell, m, rankBox, cx, cy, dim, out)); cy += dim.get(cell).h + G.gapY; }
      cx += rankBox.w + G.gapX;
    }
    for (const l of m.links) {
      const from = at.get(l.from), to = at.get(l.to);
      if (from && to) out.edges.push({ kind: l.both ? "both" : "flow", x1: from.x + G.nodeW, y1: from.y + G.nodeH / 2, x2: to.x, y2: to.y + G.nodeH / 2 });
    }
  }

  const SVG_NS = "http://www.w3.org/2000/svg";
  function svgEl(tag, attrs, ...kids) {
    const el = document.createElementNS(SVG_NS, tag);
    for (const [k, v] of Object.entries(attrs || {})) el.setAttribute(k, v);
    el.append(...kids);
    return el;
  }

  function graphEdge(e) {
    const bend = e.kind === "delegate" ? Math.max(14, (e.y2 - e.y1) / 2) : Math.max(18, (e.x2 - e.x1) / 2);
    const d = e.kind === "delegate"
      ? `M${e.x1} ${e.y1} C${e.x1} ${e.y1 + bend} ${e.x2} ${e.y2 - bend} ${e.x2} ${e.y2}`
      : `M${e.x1} ${e.y1} C${e.x1 + bend} ${e.y1} ${e.x2 - bend} ${e.y2} ${e.x2} ${e.y2}`;
    return svgEl("path", { d, class: "gedge " + e.kind, "marker-end": "url(#garrow)", ...(e.kind === "both" ? { "marker-start": "url(#garrow)" } : {}) });
  }

  function graphCluster(b) {
    const m = b.m;
    const label = (m.attempt ? `${m.label} · attempt ${m.attempt}` : m.label) + (m.todos.length ? ` · ${todoCount(m.todos)}` : "");
    const head = h("div", { class: "gcluster-head", text: label, title: label,
      onclick: () => { if (m.collapsed) { S.graphOpen.add(m.id); invalidate(); } else select(m.id); } });
    return h("div", { class: `gcluster ${m.status}${S.selectedTeam === m.id ? " sel" : ""}${m.collapsed ? " collapsed" : ""}`, "data-team": m.id,
      style: `left:${b.x}px;top:${b.y}px;width:${b.w}px;height:${b.h}px`, title: m.collapsed ? "Done. Click to open this team" : null },
      head,
      m.collapsed && h("div", { class: "gcluster-note", text: `${graphAgents(m)} agents` }),
      !m.collapsed && m.ranks.length === 0 && h("div", { class: "gcluster-note", text: "Planning…" }));
  }

  function graphNode(b) {
    const c = b.cell, m = b.team;
    const label = c.manager ? "Manager" : c.name;
    const clickable = !c.manager && m.started;
    return h("div", { class: `gnode ${c.manager ? "manager" : c.state}${clickable ? " clickable" : ""}`, style: `left:${b.x}px;top:${b.y}px;--c:${agentColor(c.name)}`,
      title: clickable ? `${label}, click to see the output` : label,
      onclick: clickable ? () => openPanel(m.id, m.attemptN, c.name) : null },
      h("span", { class: "gnode-name", text: label }),
      !c.manager && h("span", { class: "gnode-state" }, h("span", { class: "dot " + c.state }), c.state === "idle" ? "pending" : c.state));
  }

  let graphScrolledTo = null;
  function renderGraph() {
    const root = buildGraph(S.rootTeamId);
    if (!root) return h("div", { class: "muted", text: "No team yet." });
    const out = { boxes: [], nodes: [], edges: [], w: 0, h: 0 };
    const dim = new Map();
    measureTeam(root, dim);
    placeTeam(root, 0, 0, dim, out);
    const arrow = svgEl("marker", { id: "garrow", viewBox: "0 0 10 10", refX: "9", refY: "5", markerWidth: "7", markerHeight: "7", orient: "auto-start-reverse" },
      svgEl("path", { d: "M0 1 L10 5 L0 9 z", class: "garrowhead" }));
    const svg = svgEl("svg", { class: "gedges", width: out.w, height: out.h, viewBox: `0 0 ${out.w} ${out.h}`, "aria-hidden": "true" },
      svgEl("defs", null, arrow), ...out.edges.map(graphEdge));
    const graph = h("div", { class: "graph", style: `width:${out.w}px;height:${out.h}px` }, out.boxes.map(graphCluster), svg, out.nodes.map(graphNode));
    return h("div", { class: "graph-scroll" }, graph);
  }

  // The graph is rebuilt on every structural render, which resets its scroll: keep it, and bring the followed team
  // into view only when it changed.
  function showGraph(pane, content) {
    const was = pane.querySelector(".graph-scroll");
    const left = was?.scrollLeft || 0, top = was?.scrollTop || 0;
    pane.replaceChildren(content);
    const scroller = pane.querySelector(".graph-scroll");
    if (!scroller) return;
    scroller.scrollLeft = left; scroller.scrollTop = top;
    if (S.follow && S.selectedTeam && graphScrolledTo !== S.selectedTeam) {
      const sel = scroller.querySelector(".gcluster.sel");
      if (sel) { graphScrolledTo = S.selectedTeam; sel.scrollIntoView({ block: "nearest", inline: "nearest" }); }
    }
  }

  function setDetailView(view) {
    S.detailView = view;
    try { localStorage.setItem("agent42.fleet.detailView", view); } catch { /* a per-viewer convenience only */ }
    invalidate();
  }

  const viewSwitch = () => h("div", { class: "view-switch", role: "group", "aria-label": "View" },
    [["details", "Details"], ["graph", "Graph"]].map(([view, label]) =>
      h("button", { class: "btn" + (S.detailView === view ? " sel" : ""), type: "button", "aria-pressed": String(S.detailView === view), text: label, onclick: () => setDetailView(view) })));

  // ---------- agent panel ----------
  function openPanel(teamId, attempt, agent, focus) {
    S.panel = { teamId, attempt, agent, focus, toBottom: !focus };
    invalidate();
  }

  function closePanel() { S.panel = null; invalidate(); }

  function renderPanel() {
    const el = $("#agentPanel");
    const p = S.panel;
    const team = p && S.teams.get(p.teamId);
    const a = team?.attempts.find(x => x.n === p.attempt);
    const shown = !!a && S.view === "tasks";
    el.hidden = !shown;
    $("main").classList.toggle("with-panel", shown);
    if (!shown) return;

    const plan = currentPlan(a);
    const names = (plan?.agents || []).map(x => x.name);
    for (const t of a.timeline) if (t.kind === "turn" && !names.some(n => sameAgent(n, t.agent))) names.push(t.agent);
    const def = plan?.agents.find(x => sameAgent(x.name, p.agent));
    const turns = agentTurns(a, p.agent);
    const state = turns.length ? (turns.some(t => !t.done) ? "working" : turnState(turns[turns.length - 1])) : "idle";

    el.style.setProperty("--c", agentColor(p.agent));
    $("#panelHead").replaceChildren(
      h("div", { class: "panel-title" },
        h("b", { text: def?.name || p.agent }),
        def?.model && h("span", { class: "model", text: def.model }),
        h("span", { class: "pill " + (state === "working" ? "running" : state), text: state }),
        h("button", { class: "close", title: "Close (Esc)", "aria-label": "Close", text: "×", onclick: closePanel })),
      h("div", { class: "panel-sub" }, teamLabel(team), team.attempts.length > 1 ? ` · attempt ${a.n}` : "", def?.description ? ` · ${def.description}` : ""),
      names.length > 1 ? h("div", { class: "panel-tabs" }, names.map(n => {
        const c = chip(a, n, () => openPanel(team.id, a.n, n));
        if (sameAgent(n, p.agent)) c.classList.add("sel");
        return c;
      })) : "");

    $("#panelBody").replaceChildren(...(turns.length
      ? turns.flatMap((t, i) => [turns.length > 1 && h("div", { class: "marker", text: `Turn ${i + 1}` }), renderTurn(t)].filter(Boolean))
      : [h("div", { class: "muted", text: a.status === "running" ? "This agent hasn't started yet." : "This agent didn't run." })]));
  }

  function renderItemText(item) {
    if (item.shown === item.text) return;
    item.shown = item.text;
    if (item.kind === "text") setMarkdown(item.el, item.text);
    else item.el.textContent = item.text;
  }

  function renderTurn(turn) {
    const items = turn.items.map((item, i) => {
      const last = i === turn.items.length - 1;
      if (item.kind === "text") {
        item.el = item.el || h("div", { class: "text" });
        renderItemText(item);
        item.el.classList.toggle("cursor", last && !turn.done);
        return item.el;
      }
      if (item.kind === "thinking") {
        item.el = item.el || h("pre");
        renderItemText(item);
        return det("th-" + item.id, "thinking", turn.done || !last ? "Thinking" : "Thinking…", item.el);
      }
      return renderCall(item.call);
    });
    const state = turn.failed ? `failed: ${turn.failed}` : turn.done ? "" : turn.items.length ? "working…" : "starting…";
    return h("div", { class: "turn", "data-turn": turn.id, style: `--c:${agentColor(turn.agent)}` },
      h("div", { class: "turn-head" }, h("b", { text: turn.agent }), h("span", { class: "state", text: state })),
      items);
  }

  function argPreview(args) {
    try {
      const v = JSON.parse(args);
      if (Array.isArray(v)) return v.join(", ");
      if (v && typeof v === "object") return Object.entries(v).map(([k, x]) => `${k}: ${typeof x === "string" ? x : JSON.stringify(x)}`).join("  ");
    } catch { /* not JSON: shown as is */ }
    return args || "";
  }
  function pretty(text) {
    try { const v = JSON.parse(text); if (typeof v === "object" && v) return JSON.stringify(v, null, 2); } catch { /* not JSON */ }
    return text;
  }

  function renderCall(call) {
    const sub = call.subTeamId && S.teams.get(call.subTeamId);
    const stateText = call.pending ? (sub ? "" : "running…") : call.failed ? "failed" : "done";
    const preview = call.name === "delegate_to_team" ? "" : firstLine(argPreview(call.arguments), 160);
    const el = det("call-" + call.callId, "tool",
      [h("span", { class: "tool-name", text: call.name }), h("span", { class: "tool-preview", text: preview }), h("span", { class: "tool-state" + (call.failed ? " failed" : ""), text: stateText })],
      h("div", { class: "tool-body" },
        h("div", { class: "lbl", text: "Arguments" }), h("pre", { text: pretty(call.arguments) || "(none)" }),
        !call.pending && [h("div", { class: "lbl", text: "Result" }), h("pre", { text: pretty(call.result) || "(empty)" })]));
    return sub ? h("div", null, el, subTeamLink(sub)) : el;
  }

  function subTeamLink(sub) {
    const st = teamStatus(sub);
    return h("div", { class: "subteam", onclick: () => select(sub.id) },
      h("span", { class: "dot " + st }),
      h("span", null, h("b", { text: `Sub-team (level ${sub.depth})` }), " · ", statusText(st), " · ", h("span", { class: "muted", text: firstLine(sub.task, 90) })),
      h("span", { class: "go", text: "Open →" }));
  }

  function renderVerdict(v) {
    return h("div", { class: "card verdict" },
      h("div", { class: "score " + (v.passed ? "pass" : "fail") }, String(v.score), h("small", { text: "/10" })),
      h("div", null,
        h("div", null, h("b", { text: v.passed ? "Passed" : `Below the passing score of ${v.passingScore}` }),
          h("span", { class: "muted", text: v.passed ? "" : v.next === "fix" ? " · the team will fix its result" : v.willRetry ? " · a new team will try again" : " · keeping the best result" })),
        v.feedback && mdBlock("result verdict-feedback", v.feedback)));
  }

  // ---------- system prompt editor ----------
  async function openPrompts() {
    S.view = "prompts";
    renderHeader(); renderPanel();
    const r = await api("GET", "/api/fleet/prompts");
    if (!r.ok) {
      $("#detail").replaceChildren(h("div", { class: "detail-inner" }, h("div", { class: "error-text", text: r.data?.error || "The prompts could not be loaded." })));
      return;
    }
    S.prompts = r.data.map(p => ({ ...p, text: lf(p.text), default: lf(p.default) }));
    renderPromptsView();
  }

  const lf = text => (text || "").replace(/\r\n/g, "\n");
  function closePrompts() { S.view = "tasks"; invalidate(); }
  const isDirty = p => S.drafts[p.key] !== undefined && S.drafts[p.key] !== p.text;

  function renderPromptList() {
    $("#tree").replaceChildren(
      h("div", { class: "tree-task" },
        h("div", { class: "t", text: "System prompts" }),
        h("div", { text: "The instructions of the fleet's own agents, for your tasks only. A saved edit is used from the next task." })),
      ...S.prompts.map(p => h("div", { class: "prompt-item" + (p.key === S.promptKey ? " sel" : ""), onclick: () => { S.promptKey = p.key; renderPromptsView(); } },
        h("div", { class: "label" }, p.title,
          isDirty(p) ? h("span", { class: "badge unsaved", text: "unsaved" }) : p.edited && h("span", { class: "badge", text: "edited" })),
        h("div", { class: "meta", text: p.description }))));
  }

  function renderPromptsView() {
    if (S.view !== "prompts" || !S.prompts) return;
    const p = S.prompts.find(x => x.key === S.promptKey) || S.prompts[0];
    S.promptKey = p.key;
    renderPromptList();

    const ta = h("textarea", { spellcheck: "false", "aria-label": p.title });
    ta.value = S.drafts[p.key] ?? p.text;
    const warn = h("ul", { class: "warn-list" });
    const status = h("span", { class: "muted" });
    const save = h("button", { class: "btn primary", text: "Save" });
    const discard = h("button", { class: "btn", text: "Discard changes" });
    const reset = h("button", { class: "btn danger", text: "Reset to default" });

    const update = () => {
      if (ta.value === p.text) delete S.drafts[p.key]; else S.drafts[p.key] = ta.value;
      save.disabled = discard.disabled = !isDirty(p);
      reset.hidden = !p.edited;
      warn.replaceChildren(...Object.entries(p.placeholders).filter(([name]) => !ta.value.includes(`{{${name}}}`)).map(([name, description]) =>
        h("li", null, h("code", { text: `{{${name}}}` }), ` is missing, so this won't be in the prompt: ${description}`)));
      renderPromptList();
    };
    const doSave = async () => {
      if (!isDirty(p)) return;
      save.disabled = true;
      const r = await api("PUT", "/api/fleet/prompts/" + encodeURIComponent(p.key), { text: ta.value });
      if (r.ok) { p.text = lf(r.data.text); p.edited = r.data.edited; status.textContent = "Saved, used from your next task."; }
      else status.textContent = "Saving failed.";
      update();
    };

    ta.addEventListener("input", () => { status.textContent = ""; update(); });
    ta.addEventListener("keydown", e => { if (e.key === "s" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); doSave(); } });
    save.addEventListener("click", doSave);
    discard.addEventListener("click", () => { ta.value = p.text; status.textContent = ""; update(); });
    reset.addEventListener("click", async () => {
      // A second click confirms: a dialog would block the page.
      if (!reset.dataset.confirm) {
        reset.dataset.confirm = "1"; reset.textContent = "Click again to reset";
        setTimeout(() => { delete reset.dataset.confirm; reset.textContent = "Reset to default"; }, 3000);
        return;
      }
      const r = await api("DELETE", "/api/fleet/prompts/" + encodeURIComponent(p.key));
      if (r.ok) { p.text = lf(r.data.text); p.edited = r.data.edited; delete S.drafts[p.key]; renderPromptsView(); }
    });

    const placeholders = Object.entries(p.placeholders);
    $("#detail").replaceChildren(h("div", { class: "detail-inner editor" },
      h("div", { class: "crumbs" }, h("a", { text: "← Back to tasks", onclick: closePrompts })),
      h("h1", null, p.title, p.edited && h("span", { class: "pill retry", text: "edited" })),
      h("div", { class: "sub-head", text: p.description }),
      placeholders.length > 0 && h("div", null,
        h("div", { class: "muted", style: "font-size:12px;margin-bottom:6px", text: "Placeholders, which the fleet fills in. Click one to insert it at the cursor." }),
        h("div", { class: "placeholders" }, placeholders.map(([name, description]) => h("button", {
          class: "btn placeholder", type: "button", title: description, text: `{{${name}}}`,
          onclick: () => { ta.setRangeText(`{{${name}}}`, ta.selectionStart, ta.selectionEnd, "end"); ta.focus(); update(); },
        })))),
      warn, ta,
      h("div", { class: "editor-actions" }, save, discard, h("span", { class: "spacer" }), status, reset)));
    update();
  }

  // ---------- lessons (ADR 0124) ----------
  // What the nightly dream learned from the user's finished tasks, where it was applied, and the controls. Every string
  // below is model-written or derived from model output, so it is written as plain text only, never as markup.
  const lessonTime = iso => iso ? new Date(iso).toLocaleString() : "never";
  const lessonExpiry = l => new Date(new Date(l.lastSupportedAt).getTime() + (S.lessons?.ttlDays || 90) * 86400000).toLocaleDateString();

  function updateLessonsBadge() {
    const n = S.lessons?.newCount || 0;
    const badge = $("#lessonsBadge");
    badge.hidden = n === 0;
    badge.textContent = String(n);
  }

  async function loadLessons() {
    const r = await api("GET", "/api/fleet/lessons");
    if (r.ok) S.lessons = r.data;
    updateLessonsBadge();
    return r;
  }

  async function openLessons() {
    S.view = "lessons";
    renderHeader(); renderPanel();
    const r = await loadLessons();
    if (!r.ok) {
      $("#detail").replaceChildren(h("div", { class: "detail-inner" }, h("div", { class: "error-text", text: r.data?.error || "The lessons could not be loaded." })));
      return;
    }
    // Lessons created since the last visit stay marked as new in this view; opening it clears the badge.
    S.lessonsSeenBefore = r.data.settings?.seenAt || null;
    renderLessonsView();
    if ((S.lessons.newCount || 0) > 0) {
      api("POST", "/api/fleet/lessons/seen").then(() => { if (S.lessons) { S.lessons.newCount = 0; updateLessonsBadge(); } });
    }
  }

  function closeLessons() { S.view = "tasks"; invalidate(); }

  // A task id as a link that opens that task, or plain text when the task is gone.
  function lessonTaskLink(id) {
    const n = taskNumber(id);
    return taskById(id) ? h("a", { class: "lesson-task", text: `#${n}`, title: "Open this task", onclick: () => selectTask(id) }) : h("span", { class: "muted", text: "(deleted task)" });
  }

  function lessonCard(l) {
    const isNew = l.createdAt && (!S.lessonsSeenBefore || l.createdAt > S.lessonsSeenBefore);
    const off = l.status === "disabled";
    const del = h("button", { class: "btn danger", text: "Delete" });
    del.addEventListener("click", async () => {
      // A second click confirms: a dialog would block the page.
      if (!del.dataset.confirm) {
        del.dataset.confirm = "1"; del.textContent = "Click again to delete";
        setTimeout(() => { delete del.dataset.confirm; del.textContent = "Delete"; }, 3000);
        return;
      }
      if ((await api("DELETE", "/api/fleet/lessons/" + encodeURIComponent(l.id))).ok) await refreshLessons();
    });
    const toggle = h("button", { class: "btn", text: off ? "Enable" : "Disable" });
    toggle.addEventListener("click", async () => {
      if ((await api("POST", `/api/fleet/lessons/${encodeURIComponent(l.id)}/${off ? "enable" : "disable"}`)).ok) await refreshLessons();
    });
    const applied = l.applications || [];
    return h("div", { class: "card lesson" + (off ? " off" : "") },
      h("div", { class: "lesson-text", text: l.text }),
      h("div", { class: "muted lesson-meta" },
        `${l.topic} · ${l.confidence} confidence · learned ${lessonTime(l.createdAt)} · last supported ${lessonTime(l.lastSupportedAt)}`,
        off ? "" : ` · expires ${lessonExpiry(l)}`,
        isNew && h("span", { class: "badge", text: "new" })),
      h("div", { class: "lesson-links" }, h("b", { text: "Learned from " }), (l.evidence || []).map((id, i) => [i > 0 && " ", lessonTaskLink(id)])),
      h("div", { class: "lesson-links" }, h("b", { text: "Applied " }),
        l.appliedCount > 0
          ? [`${l.appliedCount}× (last ${lessonTime(applied[applied.length - 1]?.at)}) in `, applied.map((a, i) => [i > 0 && " ", lessonTaskLink(a.taskId)])]
          : h("span", { class: "muted", text: "not yet" })),
      h("div", { class: "editor-actions" }, toggle, del));
  }

  function dreamRunRow(run) {
    const bits = [run.trigger, run.status];
    if (run.status === "ran") bits.push(`${run.tasksRead} tasks read`, `${run.learned} learned`, `${run.reinforced} reinforced`, `${run.refused} refused`);
    if (run.reason) bits.push(run.reason);
    if (run.model) bits.push(run.model);
    return h("div", { class: "dream-run" }, h("span", { class: "muted", text: lessonTime(run.startedAt) }), " ", bits.join(" · "));
  }

  async function refreshLessons() {
    const r = await loadLessons();
    if (r.ok && S.view === "lessons") renderLessonsView();
  }

  function renderLessonsView() {
    if (S.view !== "lessons" || !S.lessons) return;
    const d = S.lessons;
    const active = d.lessons.filter(l => l.status === "active");
    const disabled = d.lessons.filter(l => l.status === "disabled");

    $("#tree").replaceChildren(h("div", { class: "tree-task" },
      h("div", { class: "t", text: "Lessons" }),
      h("div", { text: `${active.length} active, ${disabled.length} turned off. Learned from your finished tasks and given to every planner, sub-teams included.` })));

    const enabled = h("input", { type: "checkbox", id: "lessonsEnabled" });
    enabled.checked = !!d.settings.enabled;
    const model = h("select", { id: "lessonsModel", "aria-label": "Dream model" }, h("option", { value: "", text: "Your default model" }),
      (S.models?.models || []).filter(toolCapable).map(m => h("option", { value: m.id, text: m.displayName ? `${m.displayName} (${m.id})` : m.id })));
    model.value = d.settings.model || "";
    const status = h("span", { class: "muted" });
    const saveSettings = async () => {
      const r = await api("PUT", "/api/fleet/lessons/settings", { enabled: enabled.checked, model: model.value || null });
      status.textContent = r.ok ? (enabled.checked ? "Saved. The dream runs tonight." : "Saved. The nightly dream is off.") : (r.data?.error || "Saving failed.");
      if (r.ok) d.settings = r.data; else enabled.checked = !!d.settings.enabled;
    };
    enabled.addEventListener("change", saveSettings);
    model.addEventListener("change", saveSettings);

    const when = d.schedule.amsterdam ? `about ${d.schedule.amsterdam} Amsterdam time` : `at ${d.schedule.atUtc} UTC`;
    const dreamNow = h("button", { class: "btn", id: "dreamNowBtn", text: d.running ? "Dreaming…" : "Dream now" });
    dreamNow.disabled = !!d.running || !d.dreamEnabled;
    dreamNow.addEventListener("click", async () => {
      dreamNow.disabled = true; dreamNow.textContent = "Dreaming…"; status.textContent = "";
      const r = await api("POST", "/api/fleet/lessons/dream", { model: model.value || null });
      if (!r.ok) status.textContent = r.data?.error || "The dream could not run.";
      await refreshLessons();
    });

    $("#detail").replaceChildren(h("div", { class: "detail-inner editor" },
      h("div", { class: "crumbs" }, h("a", { text: "← Back to tasks", onclick: closeLessons })),
      h("h1", { text: "Lessons" }),
      h("div", { class: "sub-head", text: "Once a night a model reads your recent finished tasks and keeps a few lessons about team design that several tasks agree on. They are used automatically from your next task on, and you can turn any of them off." }),
      h("div", { class: "card" },
        h("label", { class: "follow" }, enabled, " Learn nightly"),
        h("div", { class: "muted", text: d.dreamEnabled
          ? `Runs ${when}, within a ${d.schedule.windowHours} hour window, when at least ${d.minNewTasks} tasks finished since the last run. Off by default: it spends your own tokens.`
          : "The nightly dream is switched off on this server." }),
        h("div", { class: "editor-actions" }, h("span", { text: "Model " }), model, h("span", { class: "spacer" }), status, dreamNow),
        h("div", { class: "muted", text: "Dream now ignores the time window and uses up tonight's run." })),
      h("h2", { text: "Learned" }),
      active.length === 0 ? h("div", { class: "muted", text: "Nothing learned yet." }) : active.map(lessonCard),
      disabled.length > 0 && h("details", { class: "lessons-off" }, h("summary", { text: `Turned off (${disabled.length})` }), disabled.map(lessonCard)),
      h("h2", { text: "Dream log" }),
      d.runs.length === 0 ? h("div", { class: "muted", text: "No dream has run yet." }) : d.runs.map(dreamRunRow)));
  }

  // The lessons the task's planners received, as a snapshot (the lesson may have been deleted since).
  function lessonsAppliedCard(task) {
    const applied = task.appliedLessons || [];
    if (applied.length === 0) return null;
    return h("div", { class: "card attached", style: "margin-top:12px" },
      h("b", { text: `Lessons applied (${applied.length})` }),
      h("ul", { class: "files" }, applied.map(l => h("li", null, h("span", { class: "name", text: l.text })))));
  }

  // ---------- wiring ----------
  function selectTask(taskId) {
    S.view = "tasks"; S.panel = null; S.freshTask = !taskById(taskId)?.canFollowUp;
    S.selectedTask = taskId || null;
    S.selectedTeam = null; S.selectedAttempt = null; S.files = null; S.canvas = null;
    closeCanvasPane();
    resetTaskState();
    connect(S.selectedTask);
    loadFiles();
    loadCanvas();
    invalidate();
  }

  $("#promptsBtn").addEventListener("click", () => S.view === "prompts" ? closePrompts() : openPrompts());
  $("#lessonsBtn").addEventListener("click", () => S.view === "lessons" ? closeLessons() : openLessons());
  $("#stopBtn").addEventListener("click", () => S.selectedTask && api("POST", `/api/fleet/tasks/${encodeURIComponent(S.selectedTask)}/stop`));
  function newTask() { disconnect(); closeCanvasPane(); S.selectedTask = null; S.freshTask = true; resetTaskState(); S.view = "tasks"; invalidate(); composer.ta.focus(); }
  $("#newTaskBtn").addEventListener("click", newTask);
  // Deletes the record, its event log and, once no follow-up shares it, the workspace with its files.
  $("#deleteBtn").addEventListener("click", async () => {
    const id = S.selectedTask;
    if (!id || !confirm(`Delete task #${taskNumber(id)}, its log and its files? This cannot be undone.`)) return;
    const r = await api("DELETE", `/api/fleet/tasks/${encodeURIComponent(id)}`);
    if (!r.ok) { alert(r.data?.error || `The task could not be deleted (HTTP ${r.status}).`); return; }
    S.tasks = S.tasks.filter(t => t.id !== id);
    if (S.selectedTask === id) newTask(); else invalidate();
  });
  // ---------- share link (ADR 0120) ----------
  // Does this browser have the OS share sheet? A pure feature detect, never a user-agent or pointer test (ADR 0047).
  const canNativeShare = typeof navigator.share === "function";
  let shareTaskId = null;
  let shareTitle = "";
  function renderShareState(url) {
    const shared = typeof url === "string" && url.length > 0;
    $("#shareResult").hidden = !shared;
    $("#shareCreateBtn").hidden = shared;
    if (shared) $("#shareUrl").value = url;
  }
  // Nothing is minted until Create is pressed: that press is the user's consent to what the dialog says the link exposes.
  async function openShare() {
    const id = S.selectedTask;
    if (!id) return;
    shareTaskId = id;
    shareTitle = `Task #${taskNumber(id)}`;
    $("#shareTaskTitle").textContent = shareTitle;
    renderShareState(null);
    $("#shareCreateBtn").disabled = false;
    $("#shareDialog").showModal();
    const r = await api("GET", `/api/fleet/tasks/${encodeURIComponent(id)}/share`);
    if (r.ok && shareTaskId === id) renderShareState(r.data?.url);
  }
  // Both rejections the sheet can raise are non-events (dismissed, or no transient activation left); the link is on screen.
  async function nativeShare() {
    const url = $("#shareUrl").value;
    if (!url) return;
    try { await navigator.share({ title: `Agent 42 fleet: ${shareTitle}`, url }); } catch { /* dismissed or blocked */ }
  }
  async function createShare() {
    if (!shareTaskId) return;
    $("#shareCreateBtn").disabled = true;
    const r = await api("POST", `/api/fleet/tasks/${encodeURIComponent(shareTaskId)}/share`);
    if (r.ok && r.data?.url) {
      // Render BEFORE sharing: the sheet can be dismissed or blocked, and the user must be left holding the link.
      renderShareState(r.data.url);
      if (canNativeShare) await nativeShare();
    }
    $("#shareCreateBtn").disabled = false;
  }
  async function revokeShare() {
    if (!shareTaskId || !confirm("Revoke this link? Anyone holding it will lose access.")) return;
    const r = await api("DELETE", `/api/fleet/tasks/${encodeURIComponent(shareTaskId)}/share`);
    if (r.ok || r.status === 404) renderShareState(null);
  }
  async function copyShare() {
    const url = $("#shareUrl");
    try { await navigator.clipboard.writeText(url.value); $("#shareCopyBtn").textContent = "Copied"; setTimeout(() => { $("#shareCopyBtn").textContent = "Copy"; }, 1500); }
    catch { url.focus(); url.select(); }
  }
  $("#shareBtn").addEventListener("click", openShare);
  $("#shareCreateBtn").addEventListener("click", createShare);
  $("#shareNativeBtn").addEventListener("click", nativeShare);
  $("#shareCopyBtn").addEventListener("click", copyShare);
  $("#shareRevokeBtn").addEventListener("click", revokeShare);
  $("#shareCloseBtn").addEventListener("click", () => $("#shareDialog").close());
  // Share REPLACES Copy where the sheet exists: a swap, not an add.
  $("#shareNativeBtn").hidden = !canNativeShare;
  $("#shareCopyBtn").hidden = canNativeShare;

  // An empty answer would pick the default, which differs per question: the buttons are for that.
  $("#promptForm").addEventListener("submit", e => { e.preventDefault(); const text = $("#promptInput").value.trim(); if (text) answer(text); else $("#promptInput").focus(); });
  document.addEventListener("keydown", e => { if (e.key === "Escape" && S.panel && S.view === "tasks") closePanel(); });
  $("#taskSelect").addEventListener("change", e => selectTask(e.target.value));
  $("#follow").addEventListener("change", e => {
    S.follow = e.target.checked;
    if (S.follow) {
      const teams = [...S.teams.values()];
      const busy = teams.filter(t => t.status === "running");
      S.selectedTeam = (busy[busy.length - 1] || teams[0])?.id || null; S.selectedAttempt = null;
      invalidate();
      $("#detail").scrollTop = 1e9;
    }
  });
  setInterval(() => document.querySelectorAll("[data-since]:not([data-until])").forEach(el => el.textContent = duration(el.dataset.since)), 1000);

  // ---------- analyzer panel (ADR 0123) ----------
  // A chat about the selected task whose agent has only the fleet tools. Model text goes through setMarkdown (DOMPurify +
  // marked); tool calls, the user's text and errors are set as text. The history is the server's, per task.
  const An = { task: null, open: false, busy: false, token: 0 };
  const anUrl = () => `/api/fleet/tasks/${encodeURIComponent(An.task)}/analyzer`;

  function anModels() { return (S.models?.models || []).filter(m => toolCapable(m) && !m.copilotOnly); }

  function anFillModels() {
    const models = anModels();
    $("#anModel").replaceChildren(...models.map(m => h("option", { value: m.id, text: m.displayName ? `${m.displayName} (${m.id})` : m.id })));
    const preferred = models.find(m => m.id === S.models?.default) || models[0];
    if (preferred) $("#anModel").value = preferred.id;
  }

  function anError(text) { $("#anError").hidden = !text; $("#anError").textContent = text || ""; }

  function anToolChip(name, input, result) {
    return h("details", { class: "an-tool" }, h("summary", { text: name }),
      input ? h("pre", { text: String(input).slice(0, 2000) }) : null,
      result ? h("pre", { text: String(result).slice(0, 2000) }) : null);
  }

  function anScroll() { const log = $("#anLog"); log.scrollTop = log.scrollHeight; }

  async function anLoad() {
    const token = ++An.token;
    An.busy = false;
    $("#anSend").disabled = false;
    $("#anLog").replaceChildren();
    anError("");
    const res = await api("GET", anUrl());
    if (token !== An.token) return;
    if (!res.ok) { anError(res.data?.error || "The analyzer history could not be loaded."); return; }
    for (const turn of res.data || []) {
      if (turn.role === "user") { $("#anLog").append(h("div", { class: "an-msg an-user", text: turn.text })); continue; }
      const bubble = h("div", { class: "an-msg an-assistant" });
      for (const call of turn.toolCalls || []) bubble.append(anToolChip(call.toolName, call.toolInput, call.toolResult));
      const body = h("div", { class: "an-text" });
      setMarkdown(body, turn.text);
      bubble.append(body);
      $("#anLog").append(bubble);
    }
    anScroll();
  }

  // Called on every header render: a different selected task means a different conversation.
  function analyzerTaskChanged() {
    if (An.task === S.selectedTask) return;
    An.task = S.selectedTask;
    An.token++;
    An.busy = false;
    $("#anSend").disabled = false;
    $("#anLog").replaceChildren();
    anError("");
    if (!An.task) $("#analyzer").hidden = true;
    else if (An.open) anLoad();
  }

  function anToggle(open) {
    An.open = open;
    $("#analyzer").hidden = !open || !An.task;
    $("#analyzeBtn").classList.toggle("active", open);
    if (open && An.task) anLoad();
  }

  async function anSend(message) {
    const model = $("#anModel").value;
    if (!model) { anError("Add a model provider that supports tools, in the chat's Settings."); return; }
    const token = ++An.token;
    An.busy = true;
    $("#anSend").disabled = true;
    anError("");
    $("#anLog").append(h("div", { class: "an-msg an-user", text: message }));
    const bubble = h("div", { class: "an-msg an-assistant" });
    const body = h("div", { class: "an-text" });
    bubble.append(body);
    $("#anLog").append(bubble);
    anScroll();
    let answer = "";
    try {
      const res = await fetch(anUrl(), {
        method: "POST", headers: { "content-type": "application/json" }, credentials: "same-origin",
        body: JSON.stringify({ message, model }),
      });
      if (!res.ok) {
        let text = "";
        try { text = (await res.json()).error; } catch { text = ""; }
        throw new Error(text || `The analyzer failed (${res.status}).`);
      }
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
          if (token !== An.token) continue; // another task was opened meanwhile
          if (chunk.type === "textDelta") { answer += chunk.text; setMarkdown(body, answer); }
          else if (chunk.type === "toolCallStart") bubble.insertBefore(anToolChip(chunk.toolName, chunk.toolInput, null), body);
          anScroll();
        }
      }
      if (token === An.token && !answer && !bubble.querySelector(".an-tool")) anError("The analyzer returned no answer. Try again.");
    } catch (err) {
      if (token === An.token) anError(err.message || "The analyzer failed.");
    } finally {
      if (token === An.token) { An.busy = false; $("#anSend").disabled = false; }
    }
  }

  $("#analyzeBtn").addEventListener("click", () => anToggle(!An.open));
  $("#anClose").addEventListener("click", () => anToggle(false));
  $("#anClear").addEventListener("click", async () => {
    if (!An.task) return;
    An.token++;
    const res = await api("DELETE", anUrl());
    An.busy = false;
    $("#anSend").disabled = false;
    $("#anLog").replaceChildren();
    anError(res.ok ? "" : res.data?.error || "The conversation could not be cleared.");
  });
  $("#anForm").addEventListener("submit", ev => {
    ev.preventDefault();
    const message = $("#anInput").value.trim();
    if (!message || An.busy) return;
    $("#anInput").value = "";
    anSend(message);
  });
  $("#anInput").addEventListener("keydown", ev => {
    if (ev.key === "Enter" && !ev.shiftKey && !ev.isComposing) { ev.preventDefault(); $("#anForm").requestSubmit(); }
  });

  (async function start() {
    const [config, models, tools, profile] = await Promise.all([
      api("GET", "/api/fleet/config"), api("GET", "/api/models"), api("GET", "/api/tools"), api("GET", "/api/profile")]);
    if (!config.ok && config.status !== 503 && !models.ok) $("#signInBanner").hidden = false;
    S.config = config.ok ? config.data : null;
    S.models = models.ok ? models.data : { models: [] };
    // Without both, the picker stays hidden and tasks follow the default, which the server resolves when they run.
    S.tools = tools.ok && profile.ok ? (tools.data || []).filter(t => fleetToolKey(t.key)) : null;
    S.defaultTools = profile.ok ? profile.data?.enabledTools ?? null : null;
    fillComposerOptions();
    anFillModels();
    toolPicker.target = undefined;
    await loadTasks();
    loadLessons();
    // A deep link from a chat (ADR 0115) opens that task; an id that isn't the user's does nothing. Otherwise open the
    // running task, if any: its stream picks up where it is.
    const linked = taskById(new URLSearchParams(location.search).get("task"));
    const live = linked || S.tasks.find(t => t.status === "running");
    if (live) selectTask(live.id);
    invalidate();
  })();
})();
