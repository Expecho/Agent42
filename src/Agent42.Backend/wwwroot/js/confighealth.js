/* Agent 42 — configuration health (ADR 0029). Split out of app.js (ADR 0041). */

import { configHealthList, runConfigProbesBtn, searxngUiLink } from "./dom.js";

// Fetches the health report and renders it. probe=false (the default, on tab open) is the STATIC report
// and runs no metered calls; probe=true runs the opt-in live probes (button). Mirrors loadProviders.
export async function loadConfigHealth(probe = false) {
  try {
    const res = await fetch(`/api/config-health${probe ? "?probe=true" : ""}`);
    if (!res.ok) return;
    renderConfigHealth(await res.json());
  } catch { /* silo may be unreachable; the list refreshes on next open */ }
}

// Groups the checks by their Group label and renders each as a status pill + key + curated message. The
// report never carries a secret, so every field here is safe to paint verbatim.
function renderConfigHealth(report) {
  configHealthList.innerHTML = "";
  const checks = report.checks ?? [];
  if (checks.length === 0) {
    const empty = document.createElement("p");
    empty.className = "providers__empty";
    empty.textContent = "no checks";
    configHealthList.appendChild(empty);
    return;
  }

  const env = document.createElement("p");
  env.className = "config-health__env";
  env.textContent = `environment: ${report.environment ?? "?"}`;
  configHealthList.appendChild(env);

  const groups = [];
  for (const check of checks) {
    let group = groups.find((g) => g.name === check.group);
    if (!group) { group = { name: check.group, items: [] }; groups.push(group); }
    group.items.push(check);
  }

  for (const group of groups) {
    const heading = document.createElement("h3");
    heading.className = "config-health__group";
    heading.textContent = group.name;
    configHealthList.appendChild(heading);

    const list = document.createElement("ul");
    list.className = "config-health__checks";
    for (const check of group.items) {
      const li = document.createElement("li");
      li.className = "config-check";

      const pill = document.createElement("span");
      pill.className = `config-check__pill config-check__pill--${check.status}`;
      pill.textContent = check.status;

      const body = document.createElement("div");
      body.className = "config-check__body";
      const key = document.createElement("span");
      key.className = "config-check__key";
      key.textContent = check.key + (check.billable ? " · billable" : "");
      const msg = document.createElement("span");
      msg.className = "config-check__msg";
      msg.textContent = check.message ?? "";
      body.append(key, msg);

      li.append(pill, body);
      // The SearXNG reachability check reports reachable/unreachable and nothing more; the proxied UI
      // (ADR 0059) is how you see what the instance actually returned and which engines answered. Move
      // the link onto that row so the two sit together. appendChild MOVES the existing anchor rather
      // than cloning it, so it survives this function wiping configHealthList on every repaint.
      if (check.key === "probe:searxng" && searxngUiLink) li.appendChild(searxngUiLink);

      list.appendChild(li);
    }
    configHealthList.appendChild(list);
  }
}

export function wireConfigHealth() {
  // Opt-in live checks (billable). Disable + relabel while probing so a double-click can't fire two runs,
  // then repaint with the probed report.
  runConfigProbesBtn.addEventListener("click", async () => {
    runConfigProbesBtn.disabled = true;
    const label = runConfigProbesBtn.textContent;
    runConfigProbesBtn.textContent = "Running…";
    try {
      await loadConfigHealth(true);
    } finally {
      runConfigProbesBtn.textContent = label;
      runConfigProbesBtn.disabled = false;
    }
  });
}
