/* Agent 42 — shared state (ADR 0089): the conversation's JSON object that the assistant changes with a tool and the
   user edits here. Rendered in a collapsible panel above the composer, hidden while the state is empty. A
   `checklist` key ([{id, title, done}]) is an editable checklist; every other key is shown read-only as JSON.

   Edits are debounced and PUT whole with the version they were based on. A 409 (the assistant or another tab
   changed it meanwhile) re-fetches and drops the local edit; the server never merges. Everything the state holds is
   written with textContent / value, never innerHTML: it is model output and user text. */

import { sharedStatePanel } from "./dom.js";

const SAVE_DELAY_MS = 600;

// The conversation the panel shows, its state (a plain object, or null) and the version it came with.
let convId = null;
let state = null;
let version = 0;
// Bumped on every switch, so a late GET for a chat the user has left is dropped.
let generation = 0;
let saveTimer = null;
let collapsed = false;

// A chat was opened: show its state, if it has any.
export async function loadSharedState(id) {
  clearSharedState();
  convId = id;
  const current = generation;
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(id)}/state`);
    if (!res.ok) return;
    const body = await res.json();
    if (current !== generation) return;
    show(body.state, body.version);
  } catch {
    // Offline: the panel stays hidden; the state is still on the server.
  }
}

// A new chat, or any chat switch: the panel belongs to the old conversation.
export function clearSharedState() {
  generation++;
  clearTimeout(saveTimer);
  saveTimer = null;
  convId = null;
  show(null, 0);
}

// A live `stateSnapshot` from the turn streaming in `id`. The state arrives as JSON text.
export function applyStateSnapshot(id, json, newVersion) {
  if (id !== convId && convId !== null) return;
  convId = id;
  let value = null;
  try {
    value = json ? JSON.parse(json) : null;
  } catch {
    return;
  }
  // A newer snapshot replaces a pending local edit; that edit would only 409 against it.
  clearTimeout(saveTimer);
  saveTimer = null;
  show(value, newVersion);
}

function show(value, newVersion) {
  state = value && typeof value === "object" && !Array.isArray(value) ? value : null;
  version = Number(newVersion) || 0;
  render();
}

function isEmpty() {
  return !state || Object.keys(state).length === 0;
}

function render() {
  if (!sharedStatePanel) return;
  sharedStatePanel.hidden = isEmpty();
  sharedStatePanel.replaceChildren();
  if (isEmpty()) return;

  const header = document.createElement("button");
  header.type = "button";
  header.className = "state-panel__header";
  header.setAttribute("aria-expanded", String(!collapsed));
  const items = Array.isArray(state.checklist) ? state.checklist : null;
  const done = items ? items.filter((i) => i?.done).length : 0;
  header.textContent = items ? `Shared state · checklist ${done}/${items.length}` : "Shared state";
  header.addEventListener("click", () => {
    collapsed = !collapsed;
    render();
  });
  sharedStatePanel.appendChild(header);
  if (collapsed) return;

  const body = document.createElement("div");
  body.className = "state-panel__body";
  if (items) body.appendChild(renderChecklist(items));

  const rest = Object.fromEntries(Object.entries(state).filter(([key]) => key !== "checklist" || !items));
  if (Object.keys(rest).length > 0) {
    const pre = document.createElement("pre");
    pre.className = "state-panel__json";
    pre.textContent = JSON.stringify(rest, null, 2);
    body.appendChild(pre);
  }
  sharedStatePanel.appendChild(body);
}

function renderChecklist(items) {
  const list = document.createElement("ul");
  list.className = "state-panel__checklist";
  items.forEach((item, index) => {
    const row = document.createElement("li");
    row.className = item?.done ? "state-panel__item is-done" : "state-panel__item";

    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = Boolean(item?.done);
    box.setAttribute("aria-label", "Done");
    box.addEventListener("change", () => edit((s) => { s.checklist[index].done = box.checked; }));

    const title = document.createElement("input");
    title.type = "text";
    title.className = "state-panel__title";
    title.value = String(item?.title ?? "");
    title.setAttribute("aria-label", "Item");
    title.addEventListener("change", () => edit((s) => { s.checklist[index].title = title.value; }, false));

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "state-panel__remove";
    remove.textContent = "×";
    remove.setAttribute("aria-label", "Remove item");
    remove.addEventListener("click", () => edit((s) => { s.checklist.splice(index, 1); }));

    row.append(box, title, remove);
    list.appendChild(row);
  });

  const addRow = document.createElement("li");
  addRow.className = "state-panel__add";
  const add = document.createElement("input");
  add.type = "text";
  add.placeholder = "Add an item";
  add.setAttribute("aria-label", "Add an item");
  add.addEventListener("keydown", (e) => {
    if (e.key !== "Enter" || !add.value.trim()) return;
    e.preventDefault();
    const text = add.value.trim();
    edit((s) => { s.checklist.push({ id: crypto.randomUUID().slice(0, 8), title: text, done: false }); });
  });
  addRow.appendChild(add);
  list.appendChild(addRow);
  return list;
}

// Applies one edit to a copy, re-renders (unless the edit came from a text field the user is still in), and
// schedules the save.
function edit(change, rerender = true) {
  const copy = structuredClone(state);
  change(copy);
  state = copy;
  if (rerender) render();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(save, SAVE_DELAY_MS);
}

async function save() {
  saveTimer = null;
  const id = convId;
  if (!id) return;
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(id)}/state`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ state: state ?? {}, baseVersion: version }),
    });
    if (res.ok) {
      if (id === convId) version = (await res.json()).version;
      return;
    }
  } catch {
    // Offline: fall through and show what the server has, once it can be reached.
  }
  // 409 (a newer version, or a chat with no saved state), 400 or a failure: the server's copy wins.
  if (id === convId) loadSharedState(id);
}

export function wireSharedState() {
  // Nothing to wire up front: every control is rendered with its listener. The entry point exists so main.js
  // owns the module like every other feature (ADR 0041), and the panel starts hidden.
  if (sharedStatePanel) sharedStatePanel.hidden = true;
}
