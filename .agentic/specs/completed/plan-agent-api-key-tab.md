# Plan — Agent editor: dedicated "API key" tab with always-visible run URL

planned-against: main @ 9913bc4

## Overview

The agent editor dialog (ADR 0021 tabs: **General**, **Tools**) currently keeps the
per-agent API key controls (ADR 0022) inside the **General** panel, and the
`POST /api/agent-run` URL appears only inside the once-shown reveal note. Move the key
controls into a **new third tab ("API key")**, and in that tab **always show the run
endpoint URL** (it identifies no secret — the same URL for every agent; the key alone
identifies the agent per ADR 0022). Only the **raw API key value** stays hidden /
revealed-once. Purely a frontend re-layout of existing ADR 0022 behaviour — no server,
grain, or infra change, no new ADR.

## Affected areas (verified against the tree)

- `src/Agent42.Backend/wwwroot/index.html` — agent dialog tablist + panels
  (`#tabAgentGeneral`/`#tabAgentTools`, `#panelAgentGeneral`/`#panelAgentTools`,
  `#agentKeySection` at lines ~162–221).
- `src/Agent42.Backend/wwwroot/app.js` — `agentTabs = wireTabs([...])` (lines 56–59);
  `openAgentDialog` (445–467); `saveAgent` reveal branch (`agentTabs.select(0)` at 512);
  key-control consts (35–39).
- `src/Agent42.Backend/wwwroot/styles.css` — `.agent-key` / `.agent-key__value`
  (1255–1271) reused for the URL block; optional small addition.
- `tests/Agent42.Tests/AgentEditorAssetsTests.cs` — source-text assertions
  (`AgentEditor_HasApiKeyRegenerateAndReveal`).
- `tests/Agent42.Tests/PwaAssetsTests.cs` — stale structural comment/test
  (`AgentDialog_HasGeneralAndToolsTabs`, ~line 292–304) says "General + Tools tabs";
  correct the comment for the added third tab (doc debt, hard rule 2).

## Context & constraints

- **Test style is source-text heuristics**, not a browser runner. `AgentEditorAssetsTests`
  serves real `wwwroot` via `StaticAssetsServerFixture` and asserts on key strings in the
  served `index.html` / `app.js` (evidence: `tests/Agent42.Tests/AgentEditorAssetsTests.cs`).
  New assertions must match literal source strings the implementation will contain.
- **Tab wiring contract:** `wireTabs([[tab,panel],…])` takes pairs in tablist order and
  returns `select(index, focus)`. Adding a tab means: a `<button role="tab">` +
  `<div role="tabpanel">` pair in `index.html` **and** a matching entry appended to the
  `agentTabs` array in `app.js` (evidence: `app.js:56-59`). Order chosen:
  **General (0), Tools (1), API key (2)** — API key last.
- **The run URL is not a secret and is agent-independent.** ADR 0022: "the key alone
  identifies the agent"; the endpoint path is `POST /api/agent-run` for every agent. So the
  URL can be shown **always**, including for a not-yet-saved new agent. Show the **absolute**
  URL (`` `${location.origin}/api/agent-run` ``) since it exists for off-browser automation;
  populate it in `openAgentDialog` (relative-only HTML would be less useful to copy).
- **Key controls stay gated to a saved agent.** `#agentKeySection` (Regenerate + the
  once-shown reveal) keeps its `hidden = !agent` gate (evidence: `app.js:454`). Only the
  **URL block** is always visible. Do not weaken the reveal-once behaviour (ADR 0022: raw
  key never re-returned).
- **Post-save reveal must land on the new tab.** `saveAgent`'s reveal branch currently does
  `agentTabs.select(0)` with the comment "the key lives in the General panel" (`app.js:512`).
  After the move the key lives in the API-key panel → switch to `agentTabs.select(2)` and
  update the comment. `openAgentDialog` keeps `agentTabs.select(0)` (always open on General).
- **No ADR / no gotcha touched.** This refines ADR 0022's UI only; the key lifecycle,
  directory sync, and endpoint are untouched. No gotcha entry governs the editor layout.
- **`type="button"` is mandatory on every new button in `agentForm`.** The form submits to
  `saveAgent` (`app.js:1792-1795`); a button defaulting to `type=submit` submits/closes the
  dialog on click instead of switching tabs or copying. Documented for the sibling form at
  `index.html:232`. Both new buttons (the API-key tab, the URL copy button) **must** carry
  `type="button"`. Source-text tests cannot catch a missing one — pin it in the markup.
- **Copy the full exemplar attribute set, don't hand-write a minimal button.** The new tab
  must mirror `index.html:165-166`: `class="agent-form__tab" type="button" role="tab"
  id="tabAgentApi" aria-selected="false" aria-controls="panelAgentApi" tabindex="-1"`. The
  new panel must mirror `index.html:215`: `class="agent-form__panel" role="tabpanel"
  id="panelAgentApi" aria-labelledby="tabAgentApi" hidden`. The copy button mirrors the
  existing `agentKeyCopyBtn` at `index.html:209`. Omitting `class` drops panel layout/gap and
  tab styling; omitting `aria-labelledby` is a11y drift the two shipping panels avoid.

## Steps

1. **index.html — add the API-key tab + panel; move the key section.** In the agent
   dialog tablist add a third tab **copying the full exemplar attribute set** from
   `index.html:165-166` — `class="agent-form__tab" type="button" role="tab" id="tabAgentApi"
   aria-selected="false" aria-controls="panelAgentApi" tabindex="-1">API key` (after the
   Tools tab). Add a new panel copying `index.html:215` — `class="agent-form__panel"
   role="tabpanel" id="panelAgentApi" aria-labelledby="tabAgentApi" hidden` — containing:
   (a) an **always-visible** URL block, placed **outside** `#agentKeySection` so it is never
   under that section's `hidden = !agent` gate — a `// run endpoint` label, a selectable
   `<code id="agentRunUrl" class="agent-key__value">` and a copy button
   (`class="agent-form__btn" type="button" id="agentRunUrlCopyBtn"`, mirroring
   `index.html:209`), plus a one-line note that automation sends the key as the `X-Api-Key`
   header; (b) the existing `#agentKeySection` (Regenerate + `#agentKeyReveal`) moved verbatim
   out of `#panelAgentGeneral`. Remove the key section (and its ADR-0022 comment) from the
   General panel. **Every new button must carry `type="button"`** (see constraints). *Verify:*
   `dotnet build`, grep the served page for `id="panelAgentApi"` / `id="agentRunUrl"`, and
   confirm both new buttons have `type="button"`.

2. **app.js — wire the new tab, populate the URL, retarget the reveal.** Add
   `[document.getElementById("tabAgentApi"), document.getElementById("panelAgentApi")]` as
   the third pair in the `agentTabs` array. Add a `const agentRunUrl = …` (and copy-button
   const). In `openAgentDialog`, set `agentRunUrl.textContent = `${location.origin}/api/agent-run`;`
   (always, before `showModal`). Wire `agentRunUrlCopyBtn` to `navigator.clipboard.writeText`
   mirroring the existing `agentKeyCopyBtn` handler (with the same try/catch fallback). In
   `saveAgent`'s reveal branch, change `agentTabs.select(0)` → `agentTabs.select(2)` and fix
   the comment to "the key lives in the API-key panel". *Verify:* `dotnet build`; exercise by
   hand in DevHost (open a saved agent → API key tab shows URL; create → key revealed on the
   API-key tab).

3. **styles.css — reuse/extend.** Reuse `.agent-key` (column + gap) to wrap the URL block
   so the URL, copy button and note stack like the key reveal; add a minimal class only if
   the URL block needs distinct spacing. No new tokens. *Verify:* visual check in DevHost.

4. **Tests — extend `AgentEditorAssetsTests`.** Add a fact
   `AgentEditor_HasApiKeyTab_WithAlwaysVisibleRunUrl` asserting the served `index.html`
   contains `id="tabAgentApi"`, `id="panelAgentApi"`, and `id="agentRunUrl"`, and that
   `app.js` contains `` `${location.origin}/api/agent-run` `` and `agentTabs.select(2)`.
   Keep `AgentEditor_HasApiKeyRegenerateAndReveal` (the IDs it checks survive the move).
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentEditorAssetsTests`.
   Source-text tests cannot prove the URL block sits **outside** `#agentKeySection`
   (AC2) — verify that placement by hand in DevHost (new/unsaved agent → API-key tab still
   shows the URL).

5. **Fix stale doc in `PwaAssetsTests.cs`.** Update the comment (~line 292) that says the
   dialog is "split into General + Tools tabs" to reflect the third API-key tab.
   `AgentDialog_HasGeneralAndToolsTabs` still passes (it asserts only the two existing IDs);
   this is doc debt paid in the same MR (hard rule 2). *Verify:* `dotnet test`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | The agent editor has a third "API key" tab with its own panel | `AgentEditor_HasApiKeyTab_WithAlwaysVisibleRunUrl` (asserts `tabAgentApi`/`panelAgentApi`) | `tests/Agent42.Tests/AgentEditorAssetsTests.cs` |
| AC2 | The run endpoint URL is always shown in that tab (independent of key reveal) | same test (asserts `agentRunUrl` + `${location.origin}/api/agent-run`) | same |
| AC3 | The raw API key stays revealed-once; regenerate/reveal controls preserved and shown on the API-key tab after save | `AgentEditor_HasApiKeyRegenerateAndReveal` (unchanged IDs) + `agentTabs.select(2)` assertion | same |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)

- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80 (new JS is not coverlet-measured; the asset tests are the
  coverage surface for this change — the codebase's established pattern for `wwwroot`)
- cognitive_complexity_max: 15
- vulnerable_packages: 0 · no dependency change
- `dotnet format Agent42.slnx --verify-no-changes` clean · `dotnet build` · `dotnet test`

## Review (critic pass)

- **HIGH — new buttons must carry `type="button"`; pin full exemplar markup.** Accepted.
  Step 1 now copies the full tab/panel attribute set (`index.html:165-166`, `:215`, `:209`)
  and a constraint mandates `type="button"` on both new buttons (form submits to `saveAgent`
  → a `type=submit` button would save/close on click). Source-text tests are blind to this,
  so it's pinned in the markup instructions.
- **LOW — stale "General + Tools" comment in `PwaAssetsTests.cs`.** Accepted. Added to
  affected areas and as Step 5 (hard rule 2, same-MR doc fix).
- **LOW/informational — AC2 can't be proven by a string assert.** Accepted as a note: Step 4
  requires by-hand DevHost verification that the URL block sits outside the key-section gate.

## Implementation notes (divergence from plan)

Executed on `feature/agent-api-key-tab` from `main @ 9913bc4` (no drift). All five steps
landed as written. Two intentional additions beyond the literal plan:

- **Stale copy-label reset (from the critic pass).** The critic flagged that the run-URL
  copy button — unlike the key copy button, which lives inside the `hidden` `#agentKeyReveal`
  — is *always visible*, so a "Copied" label from a prior open would persist across dialog
  reopens (user-facing, not masked). Fixed by resetting `agentRunUrlCopyBtn.textContent = "Copy"`
  in `openAgentDialog` (`app.js`). Not in the original plan; the exemplar handler was copied
  verbatim per the plan, then this reset added because the always-visible placement changed its
  behaviour.
- **Doc-accuracy on `.agent-key` CSS comment.** The comment claimed the class was for "the
  once-shown per-agent API key"; it now also wraps the always-visible run-URL block, so the
  comment was broadened (hard rule 2). Not an enumerated step, but same-MR doc debt from the reuse.

Otherwise: tab/panel/button markup copied from the exemplars as instructed; `agentTabs` gained
its third pair; `openAgentDialog` populates the absolute URL always; `saveAgent` reveal switched
to `agentTabs.select(2)`; `AgentEditor_HasApiKeyTab_WithAlwaysVisibleRunUrl` added; the stale
`PwaAssetsTests` comment corrected. `dotnet build` clean, 366/366 tests pass, `dotnet format
--verify-no-changes` clean.

## Out of scope

- No server / grain / `AgentApi` / Bicep change; the endpoint and key lifecycle are untouched.
- No change to Preferences (`profileTabs`) — only the agent dialog gains a tab.
- No new ADR (UI refinement of ADR 0022, not an architectural decision).
- No per-agent URL customisation — the URL is the single static `/api/agent-run` for all agents.
