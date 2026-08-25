/* Agent 42 — uploaded agent skills (ADR 0049). Mirrors mcp.js: a per-user list in Preferences whose
   entries become `skill:{name}` keys in the ADR 0021/0048 tool picker.

   Unlike MCP servers there is no editor dialog — a skill IS a file, so "edit" is "upload again", and a
   re-upload of the same name replaces it server-side. */

import { addSkillBtn, skillFileInput, skillList, skillStatus } from "./dom.js";
import { refreshToolCatalog } from "./tools.js";

let skills = [];

export async function loadSkills() {
  try {
    const res = await fetch("/api/skills");
    if (!res.ok) return;
    const data = await res.json();
    skills = data.skills ?? [];
    renderSkills();
  } catch { /* silo may be unreachable; the list refreshes on next open */ }
}

function renderSkills() {
  skillList.innerHTML = "";
  if (skills.length === 0) {
    const li = document.createElement("li");
    li.className = "providers__empty";
    li.textContent = "no skills uploaded";
    skillList.appendChild(li);
    return;
  }
  for (const skill of skills) {
    const li = document.createElement("li");
    li.className = "provider-row";

    const meta = document.createElement("div");
    meta.className = "provider-row__meta";
    const name = document.createElement("span");
    name.className = "provider-row__name";
    name.textContent = skill.name;
    const sub = document.createElement("span");
    sub.className = "provider-row__sub";
    // Description + a paperclip and count when the skill carries bundled resource files. `files`
    // includes SKILL.md itself, so a bare skill is length 1 and shows no glyph.
    const bits = [skill.description];
    if ((skill.files?.length ?? 0) > 1) bits.push(`📎 ${skill.files.length - 1}`);
    sub.textContent = bits.filter(Boolean).join(" · ");
    sub.title = (skill.files ?? []).join("\n");
    meta.append(name, sub);

    const del = document.createElement("button");
    del.type = "button";
    del.className = "provider-row__action";
    del.setAttribute("aria-label", `Delete skill: ${skill.name}`);
    del.textContent = "🗑";
    del.addEventListener("click", () => deleteSkill(skill));

    li.append(meta, del);
    skillList.appendChild(li);
  }
}

async function deleteSkill(skill) {
  if (!confirm(`Delete the skill "${skill.name}"?`)) return;
  try {
    const res = await fetch(`/api/skills/${encodeURIComponent(skill.id)}`, { method: "DELETE" });
    if (!res.ok) {
      setStatus("Could not delete the skill.", true);
      return;
    }
    setStatus("");
    await loadSkills();
    // The picker's key set changed, so the checkbox list has to be rebuilt.
    refreshToolCatalog();
  } catch {
    setStatus("Could not delete the skill.", true);
  }
}

async function uploadSkill(file) {
  const body = new FormData();
  body.append("file", file, file.name);
  setStatus("Uploading…");
  try {
    const res = await fetch("/api/skills", { method: "POST", body });
    if (!res.ok) {
      // The endpoint answers text/plain on 400 (the attachment-upload convention), so the body is a
      // clean unquoted message.
      setStatus(await res.text(), true);
      return;
    }

    const data = await res.json();
    setStatus(summarise(data));
    await loadSkills();
    // A new `skill:{name}` key exists now — rebuild the picker so it is tickable without a reload.
    refreshToolCatalog();
  } catch {
    setStatus("Could not upload the skill.", true);
  }
}

// One line naming what installed, plus everything the server deliberately dropped or replaced. The
// skipped list is not optional detail: dropping a non-conforming zip entry silently is only acceptable
// because it is reported, and a shadowing warning is the one consequence a user cannot predict.
function summarise(data) {
  const parts = [];
  const names = (data.skills ?? []).map((s) => s.name);
  if (names.length > 0) parts.push(`Installed: ${names.join(", ")}.`);
  for (const warning of data.warnings ?? []) parts.push(warning);
  const skipped = data.skipped ?? [];
  if (skipped.length > 0) parts.push(`Skipped ${skipped.length} file(s): ${skipped.join("; ")}`);
  return parts.join(" ");
}

function setStatus(text, isError = false) {
  skillStatus.textContent = text;
  skillStatus.hidden = !text;
  skillStatus.classList.toggle("agent-form__note--error", isError);
}

export function wireSkills() {
  addSkillBtn.addEventListener("click", () => skillFileInput.click());
  skillFileInput.addEventListener("change", () => {
    const file = skillFileInput.files?.[0];
    // Reset the input before awaiting, so re-picking the SAME file fires `change` again — otherwise a
    // failed upload cannot be retried without choosing a different file first.
    skillFileInput.value = "";
    if (file) uploadSkill(file);
  });
}
