# Plan — Tab for tool selection in the agent setup dialog

planned-against: main @ 06a89ce

## Overview

The agent setup dialog (`#agentDialog`) is a single flat form. Its tool-selection
section (ADR 0021) sits at the bottom alongside name/model/system-prompt. The
Preferences dialog (`#profileDialog`) is **already tabbed** (General / Model providers)
using an established, styled tab pattern. This plan moves the agent dialog to the same
tabbed layout: a **General** tab (name, description, model, system prompt, continuous)
and a **Tools** tab (the existing tool-selection section). This is a pure UI
reorganization — no fields added or removed, no backend, no persisted-state change.

## Affected areas

- `src/Agent42.Backend/wwwroot/index.html` — `#agentDialog`/`#agentForm` markup
  (lines 151–195): wrap content in a tablist + two `role="tabpanel"` panels.
- `src/Agent42.Backend/wwwroot/app.js` — tab controller wiring (currently
  `selectProfileTab` + keyboard handler at 1267–1293, bound to `profileTabs` at 46–49);
  agent DOM refs (20–35, 69); `openAgentDialog` (429–444).
- `src/Agent42.Backend/wwwroot/sw.js` — bump the shell cache name (`CACHE =
  "agent42-shell-v4"`, line 5): both `index.html` and `app.js` are in the cached `SHELL`
  (line 6), so a shell change REQUIRES a cache bump (see Context).
- `tests/Agent42.Tests/PwaAssetsTests.cs` — add an agent-dialog-tabs source-text test
  mirroring `Preferences_HasGeneralAndProvidersTabs` (175–187), AND update
  `ServiceWorker_CacheNameIsCurrent` (170) to the bumped literal.
- `src/Agent42.Backend/wwwroot/styles.css` — **comment only**: the tab-CSS comment
  (`styles.css:1139–1140`, "Tabs split the profile dialog…") becomes stale once the agent
  dialog shares the rules; update it (hard rule 2). No CSS *rules* change:
  `.agent-form__tablist/__tab/__panel` already exist (`styles.css:1141–1180`) and are
  dialog-agnostic.

## Context & constraints

- **Mirror the Preferences tab pattern exactly** — it is the in-repo exemplar for a
  tabbed `agent-form`. HTML: `agent-form__tablist[role=tablist]` **with `aria-label`**
  holding `agent-form__tab[role=tab]` buttons (`aria-selected`, `aria-controls`, roving
  `tabindex`), and `agent-form__panel[role=tabpanel]` panels **each carrying
  `aria-labelledby="<its tab id>"`** (inactive one ships `hidden`). Do not drop the
  `aria-label`/`aria-labelledby` back-references — the exemplar carries them
  (`index.html:204`, `:211`, `:248`). Evidence: `index.html:204–257`.
- **SW shell-cache bump is mandatory on any shell change.** `sw.js`'s `SHELL` array
  caches `/index.html` and `/app.js`; the convention (unbroken across `f9a64d4` v2→v3 and
  the direct exemplar `c4173ef` v3→v4) is to bump `CACHE = "agent42-shell-vN"` in the same
  MR, else returning PWA users serve the stale shell. **The test does NOT guard this** —
  `ServiceWorker_CacheNameIsCurrent` (`PwaAssetsTests.cs:164–171`) only asserts the literal
  string is *present*, so it passes green against an un-bumped v4; the bump + test-literal
  update must be done by hand. Bump v4 → v5.
- **Every `<button>` inside `#agentForm` MUST carry `type="button"`** or it submits the
  form and closes the dialog — the tab buttons especially. This is called out in the
  existing HTML comment at `index.html:197–199` for the profile form; the same applies
  to the agent form (its only submit button is `#agentSaveBtn`). Evidence: agent form
  buttons at `index.html:191–192`.
- **The tool section must keep its ids and wiring untouched.** `#agentToolChecks` is
  read/written by `renderToolChecks(agentToolChecks, …)` (`app.js:437`) and
  `readToolChecks(agentToolChecks)` (`app.js:458`). Moving the `<div id="agentToolChecks">`
  into a panel changes nothing about those calls — keep the id. Do **not** touch the
  `toolCatalog.length > 0` guard in `saveAgent` (comment at `app.js:454–457`).
- **Tab controller — extract one shared helper, don't duplicate.** The profile tab logic
  (`selectProfileTab` + the click/keydown loop, `app.js:1267–1293`) is bound to the
  module-level `profileTabs` pair-array. Duplicating it verbatim for the agent dialog
  risks tripping the `duplication_pct_max: 3` gate. Extract a factory
  (e.g. `function wireTabs(tabPairs)` returning a `select(index, focus)` closure that also
  attaches the click + arrow/Home/End keydown handlers), then build both `profileTabs`
  and a new `agentTabs` through it. `openProfileDialog`'s `selectProfileTab(0)` call
  (`app.js:1302`) becomes the returned `select(0)`. Keep the keyboard semantics identical
  (ArrowRight/Down → next, ArrowLeft/Up → prev, Home/End, wrap-around).
- **Reset to the first tab on open.** `openAgentDialog` (`app.js:429`) must select the
  General tab each time (mirrors `openProfileDialog`'s `selectProfileTab(0)` at
  `app.js:1302`), so a reopen never lands on a stale Tools tab.
- **Frontend has no JS test runner** — the C# asset tests are source-text heuristics;
  tab switching / keyboard nav is verified by driving the DevHost UI, not asserted
  (see the comment at `PwaAssetsTests.cs:173–174` and the WebKit-permissions and
  Chrome-Android gotchas on the same limitation). The new test asserts the presence of
  the tablist + tab/panel ids only.
- ADRs/gotchas reviewed: ADR 0021 (selectable tools — this is a *presentation* change to
  its per-agent layer, no decision impact; `null == all` sentinel and the empty-catalog
  guard are untouched); ADR 0003 (persona snapshot — unaffected). No gotcha governs the
  agent-dialog markup layout. No ADR needed (no architectural decision; purely visual).

## Steps

1. **Tablist + panels in the agent form.** In `index.html`, inside `#agentForm` after
   `#agentFormTitle`: add an `agent-form__tablist[role=tablist]` with two
   `type="button"` tabs — `#tabAgentGeneral` (aria-selected="true", tabindex="0",
   aria-controls="panelAgentGeneral") and `#tabAgentTools` (aria-selected="false",
   tabindex="-1", aria-controls="panelAgentTools"); give the tablist an `aria-label`
   (e.g. "Agent sections"). Wrap the name/description/model/system-prompt/continuous
   fields in `<div class="agent-form__panel" role="tabpanel" id="panelAgentGeneral"
   aria-labelledby="tabAgentGeneral">`, and the existing tools `<section>` (with
   `#agentToolChecks`) in `<div class="agent-form__panel" role="tabpanel"
   id="panelAgentTools" aria-labelledby="tabAgentTools" hidden>`. For spacing parity with
   the exemplar (whose panel children are each an `agent-form__section`), wrap the General
   panel's bare `agent-form__field` labels in a `<section class="agent-form__section">` —
   the panel's own `gap:14px` (`styles.css:1176–1180`) otherwise reflows label↔note
   spacing. Keep `#agentToolChecks` and all existing field ids unchanged. Leave the actions
   row (`#agentCancelBtn`/`#agentSaveBtn`) outside the panels, as the profile form does.
   *Verify:* `dotnet build Agent42.slnx` succeeds; the dialog shows two tabs with spacing
   unchanged from today (AC4).
2. **Extract the shared tab controller in `app.js`.** Refactor `selectProfileTab` + the
   `profileTabs.forEach` keyboard wiring (1267–1293) into a `wireTabs(tabPairs)` factory
   returning a `select(index, focus=false)` closure. Rebuild `profileTabs` through it and
   update `openProfileDialog`'s `selectProfileTab(0)` to the returned selector. Add module
   refs for the two agent tabs/panels and build `agentTabs = wireTabs([...])`. In
   `openAgentDialog`, call the agent selector `select(0)` so it always opens on General.
   *Verify:* `dotnet test Agent42.slnx` green (existing `Preferences_HasGeneralAndProvidersTabs`
   still passes); manual DevHost check that both dialogs' tabs switch by click and arrow keys.
3. **Bump the SW shell cache + its test.** In `sw.js` change `CACHE = "agent42-shell-v4"`
   → `"agent42-shell-v5"` (line 5); update the literal in `ServiceWorker_CacheNameIsCurrent`
   (`PwaAssetsTests.cs:170`) to match. Also update the stale tab-CSS comment in
   `styles.css:1139–1140` to say the tabs are shared by the agent dialog too (hard rule 2).
   *Verify:* `dotnet test Agent42.slnx --filter ServiceWorker_CacheNameIsCurrent` passes.
4. **Asset test.** Add `AgentDialog_HasGeneralAndToolsTabs` to `PwaAssetsTests.cs`
   mirroring the Preferences test: assert `id="tabAgentGeneral"`, `id="tabAgentTools"`,
   `id="panelAgentGeneral"`, `id="panelAgentTools"` are present in `/`.
   *Verify:* `dotnet test Agent42.slnx --filter PwaAssetsTests` passes.

Steps are sequential (2 depends on 1's ids; 4 asserts 1's ids) — not parallelizable.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | Agent dialog renders a General and a Tools tab (tablist + two panels present) | `AgentDialog_HasGeneralAndToolsTabs` (new) | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC2 | Existing Preferences tabs still render (no regression from the shared-helper refactor) | `Preferences_HasGeneralAndProvidersTabs` (existing) | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC3 | Build + full suite green (tool save/load wiring intact) | `dotnet build` + `dotnet test Agent42.slnx` | — |
| AC4 | Tab switching + keyboard nav work in both dialogs; Tools content is the same checkbox set as before; General-panel spacing unchanged | Manual DevHost drive (no JS runner — per `PwaAssetsTests.cs:173–174`) | — |
| AC5 | SW shell cache bumped so returning PWA users get the new shell | `ServiceWorker_CacheNameIsCurrent` (updated literal) | `tests/Agent42.Tests/PwaAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- new_issues: 0
- new_security_issues: 0
- duplication_pct_max: 3  ← the reason step 2 extracts a shared helper instead of copying
- coverage_new_code_pct_min: 80 (new test covers the new markup; JS is untestable in-repo)
- cognitive_complexity_max: 15
- vulnerable_packages: 0 (no dependency change)
- `dotnet format Agent42.slnx --verify-no-changes` clean

## Out of scope

- The per-conversation Tools popover (`#toolsDialog`) and the Preferences "default tools"
  section — unchanged; only the *agent* dialog gains tabs.
- Any change to the tool catalog, `EnabledTools` semantics, `null == all`, or backend
  (`AgentApi`/`AgentDefinition`).
- Adding/removing/renaming any agent field, or adding further tabs beyond General/Tools.
- A JS unit-test harness (none exists in-repo; not introduced here).

## Review (critic pass)

Verdict: one MEDIUM + three LOW findings, all accepted and folded in.

- **MEDIUM — missing SW shell-cache bump.** `index.html` + `app.js` are in `sw.js`'s
  cached `SHELL`; the exemplar commit `c4173ef` bumped `v3→v4` in-MR. The test only checks
  the literal is *present*, so it can't catch a stale bump. → Fixed: added to Affected
  areas, new step 3 (bump v4→v5 + update the test literal), AC5.
- **LOW — panels missing `aria-labelledby`, tablist missing `aria-label`.** The exemplar
  carries both. → Fixed: added to the mirror-exactly constraint and step 1 markup.
- **LOW — stale tab-CSS comment** (`styles.css:1139–1140` says "the profile dialog").
  → Fixed: step 3 updates the comment (hard rule 2).
- **LOW/INFO — General-panel spacing.** Exemplar panels wrap children in
  `agent-form__section`; the bare agent fields would inherit the panel's `gap:14px`.
  → Fixed: step 1 wraps General fields in a section; AC4 watches spacing on the manual drive.

Cleared with no finding: ADR 0021 / ADR 0003 (presentation-only, sentinel + snapshot +
empty-catalog guard untouched), the step-2 `wireTabs` refactor (no ordering hazard, no
Preferences regression), the `type="button"` constraint, and the `planned-against` header.

## Implementation divergence (execute-plan)

Executed on `feature/agent-dialog-tools-tab` off `main @ 940bf35` (the squash-merge of ADR 0022,
`#40`). Two deliberate deviations from the plan, both confirmed sound by the pre-MR critic pass:

1. **`#agentKeySection` placed in the General panel (plan was silent on it).** The plan's
   "Affected areas" and step-1 field list omit the ADR 0022 per-agent API key section, even though
   it already existed in the `planned-against` tree — the plan's text lagged the code. The key
   section is agent-level *access* config, not a selectable tool, and a third tab is out of scope,
   so it went into **General** (after the continuous field). `saveAgent`'s key-reveal branch now
   calls `agentTabs.select(0)` so the once-shown key is visible even when Save was clicked from the
   Tools tab (the reveal keeps the dialog open). No ADR/gotcha impact — ADR 0022 governs key
   *lifecycle*, not dialog layout.

2. **General fields kept bare — NOT wrapped in `agent-form__section` (reverses plan step 1's
   spacing instruction).** Measured: `.agent-form` gap = 14px, `.agent-form__panel` gap = 14px,
   `.agent-form__section` gap = 10px. Because the panel gap equals the old flat-form gap, bare
   children render *identically* to today (satisfying AC4 "spacing unchanged"), whereas wrapping
   would have tightened the continuous-checkbox→note gap from 14px to 10px — a change AC4 forbids.
   The plan's step-1 wrap instruction thus contradicted its own AC4; keeping fields bare resolves
   that. Consequence (accepted): the two dialogs' "General" panels differ internally — Preferences
   wraps each field in a section (10px), the agent General panel uses bare fields (14px). The
   reason is captured in an inline HTML comment at the panel.

Test placement: the new `AgentDialog_HasGeneralAndToolsTabs` went into `PwaAssetsTests.cs` beside
`Preferences_HasGeneralAndProvidersTabs` (per the plan), keeping both tabbed-dialog assertions
together, rather than in the ADR-0022-era `AgentEditorAssetsTests.cs`.

Manual step still owed (no JS test runner in-repo, by design): drive DevHost and confirm the agent
dialog's tabs switch by click + arrow/Home/End keys and that General-panel spacing looks unchanged
(AC4) — the C# asset tests assert only id/markup presence.
