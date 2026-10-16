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
  };

  function resetTaskState() {
    S.teams.clear(); S.calls.clear(); S.rootTeamId = null; S.live = null;
    S.budget = null; S.prompt = null; S.panel = null; dirtyText.clear();
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
      for (const t of a.turns.values()) t.done = true;
    }
  }

  // One handler per event type the backend publishes (FleetPageAssetsTests checks every Publish("…") has one).
  const handlers = {
    "task.started"(d, time) { S.live = { status: "running", started: time, model: d.model }; },
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
        notes: [], timeline: [], turns: new Map(), started: time, review: d.review, fix: !!fixes });
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
      const call = { callId: d.callId, name: d.name, arguments: d.arguments, agent: d.agent, teamId: d.teamId, result: null, failed: false, pending: true };
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
    "judge.started"(d) { const a = attemptOf(d); if (a) a.status = "judging"; },
    "verdict"(d) { const a = attemptOf(d); if (a) { a.verdict = d; a.status = d.passed ? "passed" : d.willRetry ? "retry" : "failed"; } },
    "budget"(d) { S.budget = d; renderBudget(); return false; },
    "prompt"(d) { S.prompt = d; renderPrompt(); return false; },
    "prompt.answered"() { S.prompt = null; renderPrompt(); return false; },
  };

  async function api(method, path, body) {
    try {
      const res = await fetch(path, {
        method,
        headers: body === undefined ? {} : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
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
    $("#promptsBtn").classList.toggle("active", S.view === "prompts");
  }

  let shownPrompt = null;
  function renderPrompt() {
    $("#promptBanner").hidden = !S.prompt;
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
  const toolCapable = m => m.supportsTools && !m.supportsEmbeddings && !m.supportsSpeech && !m.supportsImageGeneration;

  const composer = (() => {
    const ta = h("textarea", { "aria-label": "Task", onkeydown: e => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) submitTask(); } });
    const err = h("div", { class: "error-text" });
    const btn = h("button", { class: "btn primary", onclick: () => submitTask() });
    const title = h("div", { class: "composer-title" });
    const hint = h("span", { class: "muted hint" });
    const model = h("select", { "aria-label": "Model" });
    const tokens = h("input", { type: "number", min: "1", step: "100000", "aria-label": "Token cap" });
    const options = h("div", { class: "composer-options" }, h("label", null, "Model", model), h("label", null, "Token cap", tokens));
    const el = h("div", { class: "card composer" }, title, ta, options, h("div", { class: "composer-row" }, hint, err, btn));
    return { el, ta, err, btn, title, hint, model, tokens };
  })();

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

  async function submitTask(text, followUpOf) {
    const task = (text ?? composer.ta.value).trim();
    if (!task) { composer.ta.focus(); return; }
    composer.btn.disabled = true;
    const r = await api("POST", "/api/fleet/tasks", {
      task,
      followUpOf: followUpOf === undefined ? followUpTarget()?.id : followUpOf,
      model: composer.model.value || null,
      maxTokens: Number(composer.tokens.value) || null,
    });
    composer.btn.disabled = false;
    if (r.ok) {
      composer.ta.value = ""; composer.err.textContent = ""; S.freshTask = true; setFollow(true);
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
      h("div", { class: "sub-head" }, `Level ${team.depth} · `, dur(team.started, team.finished)),
      clampText("task-" + team.id, "task-text", team.task, 600));

    const tabs = team.attempts.length > 1 && h("div", { class: "tabs" }, team.attempts.map(x => h("button", {
      class: "tab" + (x === a ? " sel" : ""),
      onclick: () => { S.selectedAttempt = x.n; setFollow(false); invalidate(); },
    }, h("span", { class: "dot " + x.status }), `Attempt ${x.n}`, x.fix && h("span", { class: "muted", text: "fix" }), x.verdict && h("span", { class: "muted", text: `${x.verdict.score}/10` }))));

    pane.replaceChildren(h("div", { class: "detail-inner" }, top, head, tabs, a ? renderAttempt(team, a) : null,
      team.depth === 0 && task && taskFooter(task)));
  }

  // A task's own outcome, when no team is shown: its request, result or reason, files and what can be done next.
  function taskSummary(task) {
    return [
      h("div", { class: "card" },
        h("h1", null, `Task #${taskNumber(task.id)}`, h("span", { class: "pill " + task.status, text: statusText(task.status) })),
        h("div", { class: "sub-head" }, `${task.plannerModel} · `, dur(task.startedAt, task.finishedAt)),
        clampText("task-" + task.id, "task-text", task.request, 600)),
      task.status === "running" && h("div", { class: "card waiting", style: "margin-top:12px" }, h("span", { class: "dot planning" }), "The planner is putting a team together…"),
      taskFooter(task),
    ];
  }

  function taskFooter(task) {
    return [
      task.result && [h("h2", { text: "Result" }), h("div", { class: "card" }, clampText("result-" + task.id, "result", task.result, 2500, true))],
      task.reason && task.status !== "done" && [h("h2", { text: "Why it ended" }), h("div", { class: "card", text: task.reason })],
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

  // An interrupted, stopped or failed task can't be followed up, but its request can run again as a new task.
  function runAgainCard(task) {
    return ["interrupted", "stopped", "failed"].includes(task?.status) && !running() && h("div", { class: "card follow-up-cta" },
      h("div", null, h("b", { text: task.status === "interrupted" ? "Interrupted by a restart" : "Didn't finish" }),
        h("div", { class: "muted", text: "Run the same request again as a new task." })),
      h("button", { class: "btn", text: "Run again", onclick: () => submitTask(task.request, task.followUpOf || null) }));
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

  // ---------- wiring ----------
  function selectTask(taskId) {
    S.view = "tasks"; S.panel = null; S.freshTask = !taskById(taskId)?.canFollowUp;
    S.selectedTask = taskId || null;
    S.selectedTeam = null; S.selectedAttempt = null; S.files = null;
    resetTaskState();
    connect(S.selectedTask);
    loadFiles();
    invalidate();
  }

  $("#promptsBtn").addEventListener("click", () => S.view === "prompts" ? closePrompts() : openPrompts());
  $("#stopBtn").addEventListener("click", () => S.selectedTask && api("POST", `/api/fleet/tasks/${encodeURIComponent(S.selectedTask)}/stop`));
  $("#newTaskBtn").addEventListener("click", () => { disconnect(); S.selectedTask = null; S.freshTask = true; resetTaskState(); S.view = "tasks"; invalidate(); composer.ta.focus(); });
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

  (async function start() {
    const [config, models] = await Promise.all([api("GET", "/api/fleet/config"), api("GET", "/api/models")]);
    if (!config.ok && config.status !== 503 && !models.ok) $("#signInBanner").hidden = false;
    S.config = config.ok ? config.data : null;
    S.models = models.ok ? models.data : { models: [] };
    fillComposerOptions();
    await loadTasks();
    // Open the running task, if any: its stream picks up where it is.
    const live = S.tasks.find(t => t.status === "running");
    if (live) selectTask(live.id);
    invalidate();
  })();
})();
