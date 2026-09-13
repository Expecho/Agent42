/* Agent 42 — custom conversation groups (ADR 0073).

   The user's own named, manually ordered groupings of chats. Sits beside sidebar.js rather than inside
   it: this module owns the group list, the two dialogs, and the drag-and-drop, while sidebar.js still
   owns the conversation list DOM. The two import from each other — a deliberate cycle, which ADR 0041 §5
   permits and several modules here already ship; every binding is used at call time, never at
   module-evaluation time, which is what makes it safe. */

import { cacheGet, cachePut } from "./cache.js";
import {
  addGroupBtn, conversationList, groupNameCancelBtn, groupNameDialog, groupNameInput,
  groupNameSaveBtn, groupNameTitle, moveToGroupCancelBtn, moveToGroupConvTitle, moveToGroupDialog,
  moveToGroupList, moveToGroupSaveBtn,
} from "./dom.js";
import { buildConvRow, refreshConversations, renderSidebar, wireRowReveal } from "./sidebar.js";
import { streaming } from "./send.js";

export let groups = [];

// The set of group ids whose body is EXPANDED. Persisting the expanded set (not the collapsed one)
// means a newly created group is absent → starts collapsed, while an explicit expand survives reloads.
// Exactly the expandedAgents shape in sidebar.js; persistence is best-effort (private mode throws).
const EXPANDED_GROUPS_KEY = "agent42.sidebar.expandedGroups";
const expandedGroups = loadExpandedGroups();

// The group being renamed, or null when the name dialog is in "create" mode.
let renamingGroupId = null;
// The conversation the move dialog is filing, held while it is open.
let movingConvId = null;

// What is currently being dragged: {kind: "conv"|"group", id}. Null between drags. A module field
// rather than dataTransfer alone, because dragover needs it and dataTransfer.getData is unreadable
// there in every browser (protected mode) — the id in dataTransfer exists to satisfy Firefox, which
// refuses to start a drag without any data set.
let dragging = null;
let dropTarget = null;

export async function refreshGroups() {
  try {
    const res = await fetch("/api/conversation-groups");
    if (!res.ok) return;
    groups = await res.json();
    renderSidebar();
    cachePut("groups", groups); // mirror for offline (ADR 0032)
  } catch {
    // Offline/silo unreachable: fall back to the last cached list rather than losing every group
    // heading. Only when we have nothing yet, so a live list already on screen is never overwritten.
    const cached = await cacheGet("groups");
    if (cached && groups.length === 0) { groups = cached; renderSidebar(); }
  }
}

export function setGroups(value) {
  groups = value;
}

function loadExpandedGroups() {
  try {
    return new Set(JSON.parse(localStorage.getItem(EXPANDED_GROUPS_KEY) || "[]"));
  } catch { return new Set(); /* private mode or bad cache — all groups start collapsed */ }
}

function saveExpandedGroups() {
  try { localStorage.setItem(EXPANDED_GROUPS_KEY, JSON.stringify([...expandedGroups])); }
  catch { /* private mode — persistence is best-effort */ }
}

// Renders one custom group: the same chevron/name/count header an agent group uses, plus the three
// things an agent group cannot have — rename, delete, and being a drop target. An EMPTY group still
// renders, with a placeholder line: it is a valid target, and that is what first-class group entities
// bought over a name denormalized onto each chat.
export function buildChatGroup(group, convs) {
  const li = document.createElement("li");
  const collapsed = !expandedGroups.has(group.id);
  li.className = "conv-group conv-group--custom" + (collapsed ? " is-collapsed" : "");
  li.dataset.groupId = group.id;

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
  name.textContent = group.name;

  const count = document.createElement("span");
  count.className = "conv-group__count";
  count.textContent = String(convs.length);

  header.append(chevron, name, count);
  header.addEventListener("click", () => {
    if (expandedGroups.has(group.id)) expandedGroups.delete(group.id);
    else expandedGroups.add(group.id);
    saveExpandedGroups();
    renderSidebar();
  });

  // One call does both jobs, and sharing it with the conversation rows is the point:
  //   - reordering is a drag of the group itself, armed by the same per-interaction rule the rows use
  //     (a mouse press arms it, a touch press does not, so the browser's own long-press drag can never
  //     race a tap on a phone — no media query, which would report the PRIMARY pointer and get a
  //     touchscreen laptop wrong);
  //   - and rename/delete are hover-reveals, so without the ADR 0052 press-and-hold they would be
  //     `opacity: 0` and `pointer-events: none` FOREVER on a phone. That is the gap ADR 0045 refused
  //     to ship one level down ("pinning impossible on the installed PWA while unpinning worked").
  // Listening on the HEADER, not the <li>: the <li> contains the group's conversation rows, and a long
  // press on one of those must reveal that row, not steal the reveal for the group around it.
  wireRowReveal(li, header, header);

  const rename = document.createElement("button");
  rename.type = "button";
  rename.className = "conv-group__action conv-group__action--rename";
  rename.setAttribute("aria-label", `Rename group: ${group.name}`);
  rename.textContent = "✎";
  rename.addEventListener("click", (e) => {
    e.stopPropagation();
    openGroupName(group);
  });

  const del = document.createElement("button");
  del.type = "button";
  del.className = "conv-group__action conv-group__action--delete";
  del.setAttribute("aria-label", `Delete group: ${group.name}`);
  del.textContent = "×";
  del.addEventListener("click", (e) => {
    e.stopPropagation();
    deleteGroup(group);
  });

  const body = document.createElement("ul");
  body.className = "conv-group__body";
  if (convs.length === 0) {
    const empty = document.createElement("li");
    empty.className = "conv-group__empty";
    empty.textContent = "empty — drag a chat here";
    body.appendChild(empty);
  }
  // grouped = true: the custom group wins over the agent group, so a filed agent chat is rendered here
  // rather than under its agent, and needs the agent badge it would otherwise have lost.
  for (const conv of convs) body.appendChild(buildConvRow(conv, false, true));

  li.append(header, rename, del, body);
  return li;
}

/* ————— create / rename ————— */

// One dialog for both: pass a group to rename it, or null to create.
function openGroupName(group) {
  renamingGroupId = group?.id ?? null;
  groupNameTitle.textContent = group ? "Rename group" : "New group";
  groupNameInput.value = group?.name ?? "";
  groupNameDialog.showModal();
  groupNameInput.focus();
  groupNameInput.select();
}

async function saveGroupName() {
  const name = groupNameInput.value.trim();
  if (!name) return; // the server rejects a blank name with a 400; don't spend the round trip

  const url = renamingGroupId
    ? `/api/conversation-groups/${encodeURIComponent(renamingGroupId)}`
    : "/api/conversation-groups";

  try {
    const res = await fetch(url, {
      method: renamingGroupId ? "PUT" : "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name }),
    });
    if (!res.ok) return; // 409 = the per-user cap; leave the dialog open so the name isn't lost
  } catch { return; /* silo may be unreachable; leave the dialog as it is */ }

  groupNameDialog.close();
  refreshGroups();
}

async function deleteGroup(group) {
  // Deleting a GROUP never deletes a CONVERSATION — say so, because the × glyph is the same one the
  // conversation rows use for a destructive delete.
  if (!confirm(`Delete the group "${group.name}"? Its chats are kept and become ungrouped.`)) return;

  try {
    const res = await fetch(`/api/conversation-groups/${encodeURIComponent(group.id)}`, { method: "DELETE" });
    if (!res.ok) return;
  } catch { return; /* silo may be unreachable; leave the group in place */ }

  // Both lists move: the group is gone AND its members are now ungrouped.
  refreshGroups();
  refreshConversations();
}

/* ————— move to group ————— */

// The touch path for what a mouse does by dragging — and the only way to CLEAR a filing, since there
// is deliberately no "drag back out" drop region.
export function openMoveToGroup(convId, convTitle, currentGroupId) {
  if (streaming) return;
  movingConvId = convId;
  moveToGroupConvTitle.textContent = `"${convTitle}"`;
  moveToGroupList.innerHTML = "";

  // "No group" always leads, so clearing is reachable in one press from any state.
  moveToGroupList.appendChild(buildGroupOption("", "No group", !currentGroupId));
  for (const group of groups) {
    moveToGroupList.appendChild(buildGroupOption(group.id, group.name, group.id === currentGroupId));
  }

  moveToGroupDialog.showModal();
}

function buildGroupOption(value, label, checked) {
  const option = document.createElement("label");
  option.className = "group-picker__option";

  const radio = document.createElement("input");
  radio.type = "radio";
  radio.name = "moveToGroup";
  radio.value = value;
  radio.checked = checked;

  const text = document.createElement("span");
  // textContent, never innerHTML: a group name is user-supplied text.
  text.textContent = label;

  option.append(radio, text);
  return option;
}

async function saveMoveToGroup() {
  const selected = moveToGroupList.querySelector("input[name='moveToGroup']:checked");
  if (!selected || !movingConvId) return;

  // "" is the "No group" option; the wire value for clearing is null, which is what the server reads
  // as a clear (a non-null id naming no group is a no-op there, deliberately not a clear).
  await setConversationGroup(movingConvId, selected.value || null);
  moveToGroupDialog.close();
}

async function setConversationGroup(convId, groupId) {
  // The same mid-stream refusal openMoveToGroup applies, repeated here because the DRAG path does not
  // go through that function — without it a mouse drag would mutate and re-render the sidebar during a
  // turn while the touch path politely declined.
  if (streaming) return;

  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(convId)}/group`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ groupId }),
    });
    if (!res.ok) return;
  } catch { return; /* silo may be unreachable; leave the filing as it is */ }

  refreshConversations();
}

/* ————— drag and drop (mouse only — see the pointerdown rule above) ————— */

// Delegated on the list, never on a row: wireGroups() runs from main.js before any row exists, and
// renderSidebar rebuilds every row on every call, so per-row listeners would be wired to nothing and
// then thrown away. The rows carry data-conv-id / data-group-id for this reason.
function wireDragAndDrop() {
  conversationList.addEventListener("dragstart", (e) => {
    const row = e.target.closest?.(".conv-row[data-conv-id]");
    if (row) {
      dragging = { kind: "conv", id: row.dataset.convId };
    } else {
      const group = e.target.closest?.(".conv-group--custom[data-group-id]");
      if (!group) return;
      dragging = { kind: "group", id: group.dataset.groupId };
      group.classList.add("is-dragging");
    }
    e.dataTransfer.effectAllowed = "move";
    // Firefox refuses to start a drag with no data set; nothing reads this back.
    e.dataTransfer.setData("text/plain", dragging.id);
  });

  conversationList.addEventListener("dragover", (e) => {
    const group = dragging && e.target.closest?.(".conv-group--custom[data-group-id]");
    // Dragging a group over itself is not a move.
    if (!group || (dragging.kind === "group" && group.dataset.groupId === dragging.id)) return;
    e.preventDefault(); // the only thing that makes this element a valid drop target
    e.dataTransfer.dropEffect = "move";
    if (dropTarget !== group) {
      dropTarget?.classList.remove("is-drop-target");
      dropTarget = group;
      group.classList.add("is-drop-target");
    }
  });

  conversationList.addEventListener("drop", (e) => {
    const group = dropTarget;
    clearDragState();
    if (!group || !dragging) return;
    e.preventDefault();

    const targetId = group.dataset.groupId;
    if (dragging.kind === "conv") setConversationGroup(dragging.id, targetId);
    else reorderGroupTo(dragging.id, targetId);
    dragging = null;
  });

  // Fires whether the drop landed or the drag was abandoned, so the highlight can never be stranded.
  conversationList.addEventListener("dragend", () => {
    clearDragState();
    dragging = null;
  });
}

function clearDragState() {
  dropTarget?.classList.remove("is-drop-target");
  dropTarget = null;
  conversationList.querySelector(".is-dragging")?.classList.remove("is-dragging");
}

// Moves one group to the position currently held by another, and sends the whole resulting order —
// the endpoint takes a full list because a positional patch would be ambiguous against a stale client.
async function reorderGroupTo(groupId, targetId) {
  if (streaming) return; // as above: the drag path gets the same guard as every other mutation
  const ids = groups.map((g) => g.id).filter((id) => id !== groupId);
  const at = ids.indexOf(targetId);
  if (at < 0) return;
  ids.splice(at, 0, groupId);

  try {
    const res = await fetch("/api/conversation-group-order", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ groupIds: ids }),
    });
    if (!res.ok) return;
  } catch { return; /* silo may be unreachable; leave the order as it is */ }

  refreshGroups();
}

export function wireGroups() {
  addGroupBtn.addEventListener("click", () => openGroupName(null));
  groupNameSaveBtn.addEventListener("click", saveGroupName);
  groupNameCancelBtn.addEventListener("click", () => groupNameDialog.close());
  moveToGroupSaveBtn.addEventListener("click", saveMoveToGroup);
  moveToGroupCancelBtn.addEventListener("click", () => moveToGroupDialog.close());
  wireDragAndDrop();
}
