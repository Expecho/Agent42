/* Agent 42 — token-usage dashboard (ADR 0031). Split out of app.js (ADR 0041). */

import { openUsageBtn, usageApplication, usageCloseBtn, usageModelsBody, usageModelsEmpty, usageRange, usageRefresh, usageTiles, usageToolsBody, usageToolsEmpty, usageUser, usageUsersBody, usageUsersEmpty, usageView } from "./dom.js";
import { formatCost } from "./format.js";

// Handle for the auto-refresh timer, so a new interval selection (or closeUsage) can cancel the
// previous one — a leaked timer would keep polling /api/usage from the console view.
let usageRefreshTimer = null;
// A dedicated full-page overlay (#usageView), not a Preferences tab — fed solely from OTEL GenAI
// spans. No chart library: the only "chart" is a <div> given a percentage width (the .bar__fill
// below), same idiom as the sibling AI.Telemetry.Dashboard this view mirrors. Deliberately no
// total-tokens field anywhere. Cost (ADR 0110) is an estimate the server derives from a price table.

const USAGE_UNREPORTED = "unreported";
const usageNumberFormat = new Intl.NumberFormat();

function setUsageTile(element, value, isErrorTile = false) {
  element.textContent = usageNumberFormat.format(value ?? 0);
  if (isErrorTile) element.dataset.nonzero = String((value ?? 0) > 0);
}

// model/provider/user come back as "" when the span never reported one — render the absence
// instead of a blank cell, which reads as a rendering bug.
function usageKeyCell(value) {
  const cell = document.createElement("td");
  if (!value) {
    cell.textContent = USAGE_UNREPORTED;
    cell.className = "cell--unreported";
  } else {
    cell.textContent = value;
    cell.className = "cell--key";
  }
  return cell;
}

function usageNumberCell(value, isErrorCount = false) {
  const cell = document.createElement("td");
  cell.className = isErrorCount ? "grid__num cell--error" : "grid__num";
  cell.textContent = usageNumberFormat.format(value ?? 0);
  if (isErrorCount) cell.dataset.nonzero = String((value ?? 0) > 0);
  return cell;
}

// Estimated cost (ADR 0110): "—" when nothing in the row could be priced, "≥" when only some of it could.
function usageCostCell(usd, status) {
  const cell = document.createElement("td");
  cell.className = "grid__num";
  cell.textContent = formatCost(usd, status);
  if (status === "unpriced") cell.title = "No price is known for this model";
  if (status === "partial") cell.title = "Some calls had no price, so this is a lower bound";
  return cell;
}

// An aggregate's status from its sum and unpriced-call count (totals and user rows carry no status of their own).
const aggregateCostStatus = (usd, unpricedCalls) =>
  usd == null ? "unpriced" : unpricedCalls > 0 ? "partial" : "priced";

// A proportional bar — the row's value against the max in its table (input+output for
// models/users, count for tools). A zero max means every row is zero; render an empty track
// rather than dividing by it.
function usageBarCell(value, max) {
  const cell = document.createElement("td");
  cell.className = "grid__bar";
  const bar = document.createElement("div");
  bar.className = "bar";
  const fill = document.createElement("span");
  fill.className = "bar__fill";
  fill.style.width = max > 0 ? `${(value / max) * 100}%` : "0%";
  bar.appendChild(fill);
  cell.appendChild(bar);
  return cell;
}

function renderUsageModels(models) {
  usageModelsBody.replaceChildren();
  usageModelsEmpty.hidden = models.length > 0;
  // Share is input+output. Cached input is a SUBSET of input, so folding it in would make the
  // bars describe a total that does not exist.
  const weight = (m) => m.inputTokens + m.outputTokens;
  const max = Math.max(0, ...models.map(weight));
  for (const model of models) {
    const row = document.createElement("tr");
    row.append(
      usageKeyCell(model.application),
      usageKeyCell(model.model),
      usageKeyCell(model.provider),
      usageNumberCell(model.inputTokens),
      usageNumberCell(model.outputTokens),
      usageNumberCell(model.cachedInputTokens),
      usageNumberCell(model.reasoningOutputTokens),
      usageNumberCell(model.callCount),
      usageNumberCell(model.errorCount, true),
      usageCostCell(model.costUsd, model.costStatus),
      usageBarCell(weight(model), max),
    );
    usageModelsBody.appendChild(row);
  }
}

function renderUsageTools(tools) {
  usageToolsBody.replaceChildren();
  usageToolsEmpty.hidden = tools.length > 0;
  const max = Math.max(0, ...tools.map((tool) => tool.count));
  for (const tool of tools) {
    const row = document.createElement("tr");
    row.append(
      usageKeyCell(tool.application),
      usageKeyCell(tool.toolName),
      usageNumberCell(tool.count),
      usageNumberCell(tool.errorCount, true),
      usageBarCell(tool.count, max),
    );
    usageToolsBody.appendChild(row);
  }
}

function renderUsageUsers(users) {
  usageUsersBody.replaceChildren();
  usageUsersEmpty.hidden = users.length > 0;
  const weight = (u) => u.inputTokens + u.outputTokens;
  const max = Math.max(0, ...users.map(weight));
  for (const user of users) {
    const row = document.createElement("tr");
    row.append(
      usageKeyCell(user.user),
      usageNumberCell(user.inputTokens),
      usageNumberCell(user.outputTokens),
      usageNumberCell(user.cachedInputTokens),
      usageNumberCell(user.reasoningOutputTokens),
      usageNumberCell(user.callCount),
      usageNumberCell(user.errorCount, true),
      usageNumberCell(user.toolCallCount),
      usageCostCell(user.costUsd, aggregateCostStatus(user.costUsd, user.unpricedCallCount)),
      usageBarCell(weight(user), max),
    );
    usageUsersBody.appendChild(row);
  }
}

function renderUsage(report) {
  setUsageTile(usageTiles.toolCalls, report.totals?.toolCallCount);
  setUsageTile(usageTiles.input, report.totals?.inputTokens);
  setUsageTile(usageTiles.output, report.totals?.outputTokens);
  setUsageTile(usageTiles.cached, report.totals?.cachedInputTokens);
  setUsageTile(usageTiles.calls, report.totals?.callCount);
  setUsageTile(usageTiles.errors, report.totals?.errorCount, true);
  usageTiles.cost.textContent = formatCost(
    report.totals?.costUsd, aggregateCostStatus(report.totals?.costUsd, report.totals?.unpricedCallCount ?? 0));
  renderUsageModels(report.models ?? []);
  renderUsageTools(report.tools ?? []);
  renderUsageUsers(report.users ?? []);
}

// Fills a <select> from a list endpoint, preserving the "all" option the markup already carries.
// Silent-fail like the other loaders (loadModels/loadConfigHealth): the "all" option still works.
async function loadUsageOptions(url, select) {
  try {
    const res = await fetch(url);
    if (!res.ok) return;
    for (const name of await res.json()) {
      const option = document.createElement("option");
      option.value = name;
      option.textContent = name;
      select.appendChild(option);
    }
  } catch { /* silo may be unreachable; the "all" option still works */ }
}

async function loadUsage() {
  const query = new URLSearchParams({
    application: usageApplication.value,
    user: usageUser.value,
    range: usageRange.value,
  });
  try {
    const res = await fetch(`/api/usage?${query}`);
    if (!res.ok) return;
    renderUsage(await res.json());
  } catch { /* silo may be unreachable; the view keeps its last-rendered numbers */ }
}

// Any prior timer is always cleared first so intervals never stack, and closeUsage() calls this
// too (the leak guard) — a timer left running would keep polling /api/usage from the console view.
function stopUsageAutoRefresh() {
  if (usageRefreshTimer !== null) {
    clearInterval(usageRefreshTimer);
    usageRefreshTimer = null;
  }
}

// Restarts the auto-refresh timer from the current selection. Value is a whole number of
// seconds; 0 (Off) leaves it stopped.
function applyUsageAutoRefresh() {
  stopUsageAutoRefresh();
  const seconds = Number(usageRefresh.value);
  if (seconds > 0) {
    usageRefreshTimer = setInterval(loadUsage, seconds * 1000);
  }
}

async function openUsage() {
  // Reset to just the "all" option before repopulating — reopening must not duplicate entries.
  usageApplication.innerHTML = '<option value="all">All applications</option>';
  usageUser.innerHTML = '<option value="all">All users</option>';
  await Promise.all([
    loadUsageOptions("/api/usage/applications", usageApplication),
    loadUsageOptions("/api/usage/users", usageUser),
  ]);
  await loadUsage();
  usageView.hidden = false;
  applyUsageAutoRefresh();
}

function closeUsage() {
  usageView.hidden = true;
  stopUsageAutoRefresh();
}

export function wireUsage() {
  openUsageBtn.addEventListener("click", () => openUsage());
  usageCloseBtn.addEventListener("click", () => closeUsage());
  usageApplication.addEventListener("change", () => loadUsage());
  usageUser.addEventListener("change", () => loadUsage());
  usageRange.addEventListener("change", () => loadUsage());
  usageRefresh.addEventListener("change", applyUsageAutoRefresh);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !usageView.hidden) closeUsage();
  });
}
