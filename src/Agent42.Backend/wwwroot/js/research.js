/* Agent 42 — the research agent's live checklist (ADR 0082).

   A research turn plans its work as todos and ticks them off while it searches. The server streams the whole
   list as a `todos` event whenever it changes, so this module REPLACES the rendered list rather than patching
   it. Live-only, like follow-up suggestions: nothing is persisted, and a reloaded chat shows the answer and its
   tool calls (ADR 0080) but not the checklist. Imported by send.js, which is how it stays reachable from
   main.js (ADR 0041). */

// Renders (or re-renders) the checklist above the reply in an agent bubble. `todos` is [{ id, title, done }].
export function renderResearchTodos(msg, todos) {
  const bubble = msg.querySelector(".msg__bubble");
  if (!bubble || !Array.isArray(todos)) return;

  let panel = bubble.querySelector(".msg__todos");
  if (!panel) {
    panel = document.createElement("div");
    panel.className = "msg__todos";
    const heading = document.createElement("div");
    heading.className = "msg__todos-heading";
    const list = document.createElement("ol");
    list.className = "msg__todos-list";
    panel.append(heading, list);
    // First in the bubble: the plan comes before the searches it drives and the answer it produces.
    bubble.insertBefore(panel, bubble.firstChild);
  }

  const done = todos.filter((t) => t.done).length;
  panel.querySelector(".msg__todos-heading").textContent = `Research plan · ${done}/${todos.length}`;

  const list = panel.querySelector(".msg__todos-list");
  list.replaceChildren(...todos.map((todo) => {
    const item = document.createElement("li");
    item.className = todo.done ? "msg__todo is-done" : "msg__todo";
    // textContent, never innerHTML: a todo title is model output and is not sanitized like the reply.
    item.textContent = todo.title;
    return item;
  }));
}
