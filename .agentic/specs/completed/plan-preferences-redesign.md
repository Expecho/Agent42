# Plan — Redesign the Preferences dialog (rail + drill-down, mobile-capable)

planned-against: main @ 6b9a08d

## Overview

`#profileDialog` is a 520px-wide box with six tabs in a single non-wrapping flex row. At a
360px viewport that strip overflows horizontally with no scroll and no wrap, so half the
sections are unreachable on the installed Android PWA — the dialog has never had a single
line of responsive CSS. This replaces it with the shape in the two supplied mockups: a
header (title + subtitle + close), a **vertical nav rail** with icons and a count badge on
Tools, a scrolling content pane with a per-panel heading and lead description, and a footer
carrying a saved/unsaved status beside Cancel + Save. At ≤760px the same markup becomes a
**two-level drill-down** — the rail is the list view, picking a section swaps to a detail
view headed by `‹ Back` + the section name.

The existing amber/phosphor palette is kept verbatim: every new rule uses the existing
`--bg-raised`/`--bg-inset`/`--line`/`--ink*`/`--amber*` tokens, which already flip for the
light theme in `:root[data-theme="light"]`. No new colour is introduced.

## Affected areas

Verified against the tree at 6b9a08d.

| File | Change |
|---|---|
| `src/Agent42.Backend/wwwroot/index.html` | `#profileDialog` restructured (header / rail / pane / footer); panels gain a heading + lead paragraph |
| `src/Agent42.Backend/wwwroot/styles.css` | New `.prefs__*` block; `.tools-checks` / `.tool-check` restyled to card rows + switches; a third `@media (max-width: 760px)` block |
| `src/Agent42.Backend/wwwroot/js/dom.js` | New refs: `profileNav`, `prefsCloseBtn`, `prefsBackBtn`, `prefsDetailTitle`, `prefsStatus`, `toolsCount` |
| `src/Agent42.Backend/wwwroot/js/profile.js` | Drill-down state, close/back wiring, dirty-status wiring, Tools count badge; exports `enterPrefsDetail(index)` |
| `src/Agent42.Backend/wwwroot/js/tools.js` | `renderToolChecks` emits a description line and group wrappers; badge refresh in `refreshToolCatalog`; new `toolCounts(container)` export |
| `src/Agent42.Backend/wwwroot/js/mcp.js` | OAuth return calls `enterPrefsDetail(3)` after the existing `profileTabs.select(3)` |
| `src/Agent42.Backend/wwwroot/sw.js` | `CACHE` bump `agent42-shell-v30` → `-v31` |
| `src/Agent42.Agents/Tools/ToolCatalog.cs` | `ToolDescriptor` gains `Description`; the five static descriptors gain copy |
| `tests/Agent42.Tests/PwaAssetsTests.cs` | **Edit line 255** — the `agent42-shell-v30` pin |
| `tests/Agent42.Tests/SearchAssetsTests.cs` | **Edit lines 120-121** — the second `v30` pin and its `v29` negative |
| `tests/Agent42.Tests/ToolCatalogTests.cs` | AC8 |
| `tests/Agent42.Tests/ToolSelectionApiTests.cs` | AC9 (cluster-backed; see Context §6) |
| `tests/Agent42.Tests/PreferencesLayoutAssetsTests.cs` | **New** — the asset-guard ACs |

`src/Agent42.Backend/Tools/AgentApi.Tools.cs` is deliberately **not** in this list: its three
`ToolDescriptor` construction sites use named arguments for the trailing parameters, so they
keep compiling untouched (Context §5).

## Context & constraints

Findings the executor cannot cheaply re-derive, and the decisions already made. **Everything
in §1–§4 was verified against the cited file:line by an adversarial review after the first
draft, which found four blocking errors in it. Trust these paths; re-check anything you add.**

### 1. MCP must stay tab index 3, and two tests enforce it by parsing source text

`js/mcp.js:316` deep-links the OAuth return with `profileTabs.select(3)`. **Two** tests parse
the `const profileTabs = wireTabs(` … `]);` block out of the walked module graph and assert
ordering inside it: `ConfigTab_IsWiredIntoPreferences_AppendedLast`
(`tests/Agent42.Tests/ConfigHealthAssetsTests.cs:36-54`) and
`SkillsTab_IsAppendedAfterConfiguration_LeavingMcpAtIndexThree`
(`tests/Agent42.Tests/UserSkillPickerAssetsTests.cs:40-59`). Both also
`Assert.Contains("profileTabs.select(3)", app)`.

**Therefore: keep `wireTabs`, keep the `profileTabs` declaration in `dom.js` byte-compatible
with those parsers, and keep the six `[tab, panel]` pairs in their current order.** The rail
is a restyle of the same `role="tab"` buttons, not a new navigation mechanism. This is a
constraint, not a preference — a bespoke nav would cost the OAuth return, the arrow-key
navigation `wireTabs` already implements, and two green tests.

### 2. The tab and panel ids are pinned across ten test files

`tabGeneral/tabTools/tabProviders/tabMcp/tabConfig/tabSkills`, `panelGeneral/panelTools/
panelProviders/panelMcp/panelConfig/panelSkills`, `profileToolChecks`, `profileTheme`,
`profileSuggest`, `locationStatus`, `locationEnableBtn`, `defaultModelSelect`,
`speechModelSelect`, `configHealthList`, `runConfigProbesBtn`, `skillList`, `addSkillBtn`,
`skillFile`, `accept=".md,.zip"`, plus `role="tablist"` and `role="tabpanel"` are asserted as
literal substrings of `GET /`. `PwaAssetsTests.cs:485-492` additionally pins the document
**order** `panelTools` < `profileToolChecks` < `panelProviders`.

**Therefore: every id survives, every `role` survives, and the panels stay in source order.**

**Two panels' trailing `.agent-form__note` text is pinned as well, and this is the one that
bites:**

- `UserSkillPickerAssetsTests.cs:130-139` slices `id="panelSkills"` → the first `</section>`
  and requires the phrases `next new chat`, `only skills you trust`, `replaces it` (the note
  at `index.html:464`).
- `McpServersAssetsTests.cs:110-114` requires `id="mcpTabResult"` to sit **after** the `</ul>`
  of `#mcpServerList`. `panelMcp` has **two** notes — `#mcpTabResult` (`index.html:431`) and
  the descriptive one (`:432`) — and `panelSkills` has the same two-note shape (`:463`,
  `:464`).

**Therefore: do not move or condense any note in `panelMcp` or `panelSkills`.** Those two
panels get a *new, short* lead sentence and keep their trailing notes verbatim in place. For
the other four panels a note may be moved up to become the lead — but grep `tests/` for a
distinctive phrase from it first.

### 3. Source-text guards constrain *how* the JS is written, not only what it does

This is the trap that broke the first draft of this plan. `readToolChecks`' runtime contract
is tolerant, but several **call sites are pinned as literal strings** in
`tests/Agent42.Tests/SkillPickerAssetsTests.cs:49-52`:

```
Assert.Contains("tool-check__heading", app);
Assert.Contains("addGroup(\"Skills\", skills)", app);
Assert.Contains("addHeading(\"MCP servers\")", app);
Assert.Contains("for (const server of mcpServers) addServer(server);", app);
```

**Therefore `addServer` must keep its one-argument signature and that loop must stay
byte-identical.** Group wrapping is done with a **closed-over `currentGroup` element** that
`addCheck`/`addServer` append into — never by threading a parameter through the call sites.
Same for `addGroup("Skills", skills)` and `addHeading("MCP servers")`.

### 4. CSS placement is constrained from both ends, and the two constraints conflict

- `.agent-dialog` (`styles.css:1531-1539`) sets `padding: 0; width: min(520px, 92vw)`. `.prefs`
  is also a single class, so specificity **ties** and source order decides. **`.prefs` must be
  authored after line 1539**, or the dialog silently stays 520px and the redesign does not
  apply at all on desktop.
- `TopbarActionsAssetsTests.cs:185-194` takes the **first** `@media (max-width: 760px)` in the
  file and asserts `.topbar__brand {` (currently `styles.css:2169`) and `.topbar__menu {`
  (`:2121`) appear **before** it. The first such block today is at `:2197`. **A prefs media
  block placed near the dialog rules would become the first one and fail that test.**

**Therefore the prefs CSS is deliberately split in two:** the base `.prefs__*` rules go after
the dialog rules (after `:1539`), and the prefs `@media (max-width: 760px)` block goes at the
**end of the file**, after the existing `:2482` usage-dashboard block. Leave a comment at each
half pointing at the other, because they are no longer adjacent.

### 5. Tool descriptions are a real backend change, deliberately

`ToolDescriptor` is `(string Key, string Label, bool RequiresHostedTools, string? ParentKey
= null)` at `src/Agent42.Agents/Tools/ToolCatalog.cs:304`, and its own doc comment records
that it is **"a response DTO only — never grain state"**. That is what makes this cheap: a
trailing `string? Description = null` needs no `[Id]`, no migration, and the three call sites
at `src/Agent42.Backend/Tools/AgentApi.Tools.cs:47,66,67` all use named arguments for the
trailing parameters, so they compile untouched. `ToolCatalogTests` makes no whole-record
equality assertion.

**User decision: server-owned, in the catalog beside the label.** Rejected: a client-side map
in `tools.js` (copy drifting away from the catalog it describes, invisible to backend tests).
Skill rows and MCP server/tool rows pass `null` and render label-only — their labels are user-
and server-supplied names with no description to give.

Copy must be accurate to *this* repo, not the mockup — the mockup shows a **Calendar** tool
that ADR 0063 deleted. Write it from the ADRs:

| Key | Description |
|---|---|
| `web_search` | `Searches the web and fetches pages through the SearXNG sidecar.` |
| `location` | `Shares your device location and street address with the model.` |
| `scheduling` | `Lets a chat schedule a prompt to run later, on its own.` |
| `code_interpreter` | `Runs Python in a hosted sandbox. Needs a Responses-API model.` |
| `memory` | `Recalls and retains memories across your conversations, via a third party.` |

The `location` and `memory` lines deliberately name the egress — these are the two widest
per-turn egress surfaces in the app (ADR 0012, ADR 0056) and the picker is where a user
decides about them.

### 6. The asset fixture cannot serve `/api/tools` — AC9 needs a different seam

`tests/Agent42.Tests/StaticAssetsServerFixture.cs:28-35` builds a `CreateSlimBuilder` host with
**only** `UseDefaultFiles()` + `UseStaticFiles()`. There is no `MapAgentApi`, no Orleans
cluster, no `IGrainFactory`; `GET /api/tools` returns 404 from that client. The catalog
endpoint is exercised only by `ToolSelectionApiTests` (`:19`), which is
`SharedClusterTests<SharedApiFixture>` — a seam `SharedClusterSeamTests` deliberately guards.

**Therefore AC9 lives in `ToolSelectionApiTests`, not in the new assets class**, and AC8 (a
plain unit fact about `ToolCatalog.Descriptors`) lives in `ToolCatalogTests`. Do not try to
reach the API from the assets fixture.

### 7. What the `.agent-form__*` family may and may not absorb

`.agent-dialog` wraps **seven** dialogs (`index.html` lines 207, 299, 477, 495, 512, 544, 605)
and the `.agent-form__*` rules are flat BEM — standalone selectors, not descendants of
`.agent-form` — so every one of them is shared. **User decision: redesign Preferences only.**

- `#profileDialog` becomes `class="agent-dialog prefs"` — it *keeps* `.agent-dialog` so the
  border, radius, `--bg-raised`, shadow and `::backdrop` are inherited unchanged, and `.prefs`
  overrides width/height/padding (after `:1539`, per §4).
- `#profileForm` drops `.agent-form` for `.prefs__form`. Verified safe: no test asserts
  `.agent-form` on the profile form; `UserSkillPickerAssetsTests.cs:125` (`.agent-form__note--error`
  present in `styles.css`) is the only styling pin on the family.
- **Kept verbatim, shared with six other dialogs:** `__section`, `__field`, `__field--check`,
  `__label`, `__input`, `__select`, `__textarea`, `__checkbox`, `__note`, `__note--error`,
  `__note--ok`, `__btn`, `__btn--primary`, `__error`.
- **The nav family must be restyled UNDER `.prefs`, never globally:** `__tablist`, `__tab`,
  `__panel`, `__title`, `__actions`. `#agentDialog` uses `.agent-form__tablist` /
  `.agent-form__tab` at `index.html:211`, and **nothing in `tests/` asserts `.agent-form__tab`**
  — so an unscoped edit mangles the agent editor's tabs and the whole suite stays green. Every
  new nav rule is written as `.prefs .agent-form__tab…` (or on a dedicated `.prefs__nav-item`
  class carried alongside). This is the highest-risk silent regression in the MR.

### 8. `renderToolChecks` — what is safe to change

`readToolChecks` reads `container.querySelectorAll("input[type=checkbox]")` and
`box.closest(".tool-server__head")`: both tolerate a wrapper `<div>` and an extra `<span>`
inside the label. What must not change: the disclosure stays a `<button>` (a checkbox there
becomes a phantom saved key, ADR 0058); the four-way `cb.checked` OR keeps all four terms; the
master-derived-from-children line and the two-way master/child sync stay; `unrenderedKeys`
stays keyed per-container; and the §3 call sites stay byte-identical.

**User decision: switches replace checkboxes in all three containers** — `profileToolChecks`,
`agentToolChecks` (agent editor) and `chatToolChecks` (composer popover) all use
`class="tools-checks"` (`index.html:257, 374, 481`) and all render through this one function.
One renderer, one look; no variant flag, no new branch in this function. This means the agent
editor and composer popover **do** change visually — intended scope, not a leak.

### 9. Search box and per-section counts

**User decision: build the Tools count badge only. The "Search settings" box in the mockup is
NOT built** and no placeholder ships. Rejected: badges on every section — there is no
defensible meaning for a "1/2" on General, Model providers or Configuration.

Badge rule: `on/total` counting **top-level rows only** — checkboxes not inside
`.tool-server__tools`. Nested per-MCP-tool rows are collapsed by default and counting them
would make the badge read as a number the user cannot see.

**The badge sits inside the Tools tab button, so it joins that button's accessible name.** Put
the label in its own `<span class="prefs__navlabel">` and give the badge
`aria-label="3 of 5 tools enabled"`; the drill-down title reads the **label span**, never
`button.textContent`, which would say "Tools 3/5".

### 10. The footer status can only ever show two of its three states

`saveProfile()` ends `profileSaved = true; profileDialog.close();`
(`js/profile.js:88-90`) — a successful save **closes the dialog**. So "All changes saved" is
observable as the *opening* state and after a reset, never as a post-save confirmation. That
is accepted: the line's job is to warn that edits are pending, and close-on-save is existing
behaviour this MR does not change. Do not add a post-save write, and do not "fix" it by
keeping the dialog open — that is a behaviour change with no AC.

Preferences also mixes two persistence models. Save-on-Save: `profileLanguage`,
`profileTheme`, `profileSuggest`, `profileToolChecks` (the four fields `saveProfile()` PUTs).
Save-immediately: the three model selects, and all provider / MCP / skill CRUD.
**Therefore `#prefsStatus` is wired to those four elements only** — never a form-level `input`
listener, which would claim "Unsaved changes" when a user picks a default model that has
*already* been persisted.

### 11. Mobile is one class, and `enterPrefsDetail` must not fire on programmatic select

The drill-down is `#profileDialog.classList.toggle("is-detail")`, meaningful only inside the
≤760px block. It is entered by a **delegated click listener on the rail** using
`e.target.closest('[role="tab"]')` (the inline SVG icons mean `e.target` is often a `<path>`),
not by `wireTabs`' `select` — because `openProfileDialog()` calls `profileTabs.select(0)` on
every open, and on a phone that must land on the **list**, not on General's detail view.
`wireTabs` is shared with the agent dialog, so it must stay free of prefs concepts.

The one programmatic case that *does* want detail is the MCP OAuth return: `js/mcp.js:316`
keeps `profileTabs.select(3)` verbatim (§1) and gains `enterPrefsDetail(3)` beside it.
`js/mcp.js:4` already imports `openProfileDialog` from `./profile.js` and `js/profile.js:7`
imports from `./mcp.js` — **the cycle exists today and works**, so this adds nothing new. ADR
0041 permits it explicitly (`.agentic/decisions/0041-frontend-es-modules.md:88-91`: modules
import each other "for runtime calls and live read-bindings… Residual cycles are then
harmless"). Export it as a hoisted `export function`. Do not add a setter-slot indirection.

### 12. Cache — and the two existing pins that must move with it

`sw.js:5` is `agent42-shell-v30`; `/`, `/index.html`, `/styles.css` and all 30 JS modules are
in `SHELL`, cache-first. This MR touches all three kinds, so `CACHE` must go to
`agent42-shell-v31` or returning PWA users get new markup against old CSS.

**Two existing tests hard-pin the literal and will fail:** `PwaAssetsTests.cs:255`
(`Assert.Contains("agent42-shell-v30", sw)`) and `SearchAssetsTests.cs:120-121` (the same, plus
`Assert.DoesNotContain("agent42-shell-v29", sw)`). Update both to `v31`/`v30`. Do **not** add a
third pin in the new test file — one bump should not cost three edits.

No new JS module is added — the shell wiring lives in `profile.js` — so `SHELL` itself is
unchanged and `ProjectLayoutTests.ServiceWorkerShell_ListsExactlyTheModuleDirectory` needs no
row.

### 13. Two claims from the first draft that were WRONG — do not reinstate them

- There is **no nested scroller to defeat** in the panels. `max-height: 40vh; overflow-y: auto`
  at `styles.css:1664-1665` belongs to **`.share-agents`** (the share-target picker), not to
  `.providers__list`, which appears once at `:1811` with no height or overflow at all. Any
  `.prefs__scroll .providers__list { max-height: none }` override would be dead CSS.
- `.agent-dialog` sets no `max-height`, so the **UA default `max-height: calc(100% - 6px - 2em)`
  applies to `<dialog>`**. The mobile full-bleed rule must set `max-height: none` explicitly or
  `height: 100dvh` is clipped into a centred box with gaps top and bottom.

### Governing docs to read before starting

- `.agentic/decisions/0041-frontend-es-modules.md` (CACHE rule, source-text guards, §5 cycles)
- `.agentic/decisions/` 0021, 0026, 0048, 0058 (the tool picker's semantics)
- `.agentic/decisions/` 0029 (Configuration tab position), 0049 (Skills tab position)
- `.agentic/decisions/` 0025, 0064 (the MCP OAuth return that pins index 3)
- `.agentic/gotchas.md` — the `null == all` / wildcard-expansion entry and the ADR 0041 entries

## Steps

Steps 1 and 2 touch no common file and are parallelizable (`isolation: "worktree"` if run
concurrently). Steps 3–10 are sequential.

### Step 1 — `ToolDescriptor.Description` and the catalog copy

Files: `src/Agent42.Agents/Tools/ToolCatalog.cs`, `tests/Agent42.Tests/ToolCatalogTests.cs`.

Add `string? Description = null` as the **last** parameter of the record (after `ParentKey`),
with an XML `<param>` explaining that skill and MCP rows leave it null because their labels are
user- and server-supplied names. Fill in the five descriptions from Context §5. Add AC8.

Verify: `dotnet build Agent42.slnx`;
`dotnet test Agent42.slnx --filter "FullyQualifiedName~ToolCatalog"`.

### Step 2 — Switches, description lines and group cards in `renderToolChecks`

Files: `src/Agent42.Backend/wwwroot/js/tools.js`, `src/Agent42.Backend/wwwroot/styles.css`.

1. `addCheck` appends a `<span class="tool-check__text">` holding a
   `<span class="tool-check__name">` (existing label text, `(Responses)` suffix rule unchanged)
   and, when `tool.description` is set, a `<span class="tool-check__desc">`. The input stays a
   bare `<input type="checkbox">` inside the `<label>`.
2. Group cards via a **closed-over `currentGroup` element** that `addCheck`/`addServer` append
   into. **`addServer` keeps its one-argument signature and the three pinned call sites stay
   byte-identical (Context §3).** Render a `Built-in` heading above the first group — today
   built-ins render headingless.
3. CSS (base rules, after `styles.css:1539` per §4): `.tools-checks` becomes the card family —
   group card on `--bg-raised` with a `--line` border and `var(--radius)`, rows separated by
   `--line-soft`; `.tool-check` becomes `justify-content: space-between; align-items: flex-start;
   padding: 12px 14px`; `.tool-check__heading` sits **outside** the card as an uppercase
   `--ink-dim` label; `.tool-server__tools` keeps its indent inside the card.
4. The switch **replaces the existing `.tool-check input` rule at `styles.css:1455`** — keep
   that exact selector so there is one rule, not two competing ones, and write AC7 against
   whichever selector you actually ship. `appearance: none` + `-webkit-appearance: none`,
   38×21 pill, `--bg-inset` + `--line` at rest, `--amber-deep` fill when `:checked`, `::after`
   knob translating 17px, and the repo's standard focus ring
   (`box-shadow: 0 0 0 1px var(--amber-deep), 0 0 18px var(--amber-glow)`).
5. Export `toolCounts(container)` → `{ on, total }` over top-level rows only (Context §9).

Do not touch: the disclosure `<button>`, the four-way `isChecked` OR, the master-derived-from-
children line, the master/child two-way sync, `unrenderedKeys`, or `readToolChecks`.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ToolPicker|FullyQualifiedName~SkillPicker"`
green **with no assertion edits** (they pin the call sites, §3). Then check the two *other*
containers in DevHost — this step restyles them too: the agent editor's Tools tab and the
composer's Tools popover.

### Step 3 — The Preferences shell markup

Files: `src/Agent42.Backend/wwwroot/index.html`.

Restructure `#profileDialog` to header / body(rail + pane) / footer per the mockup, keeping
every id, every `role`, and the panel source order (§2). New elements: `#prefsCloseBtn`,
`#profileNav` (the element carrying `role="tablist"`, now also `aria-orientation="vertical"`),
`#toolsCount` inside the Tools tab button, `#prefsBackBtn`, `#prefsDetailTitle`, `#prefsStatus`.
Each tab button gains a decorative inline `<svg aria-hidden="true">` icon and wraps its text in
`<span class="prefs__navlabel">` (§9). Each panel gains `<h3 class="prefs__panel-title">` and
`<p class="prefs__panel-desc">` at the top.

**Note-moving rule (§2):** `panelMcp` and `panelSkills` keep their trailing notes verbatim and
in place — get a short new lead sentence instead. The other four panels may promote their note
to the lead, but grep `tests/` for a distinctive phrase from it first.

`#profileSaveBtn` label becomes `Save changes`. Every button inside `#profileForm` must carry
`type="button"` except the submit.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~Assets"` — every existing markup
assertion passes unedited.

### Step 4 — The Preferences shell CSS, desktop

Files: `src/Agent42.Backend/wwwroot/styles.css`.

**Placed after `styles.css:1539` (§4).** `.prefs { width: min(1040px, 94vw); height: min(720px,
88vh); max-height: none; }`; `.prefs__form` a column flex with the body `flex: 1; min-height: 0`
and `.prefs__scroll { overflow-y: auto }` so header and footer stay put. Rail: fixed ~232px
column on `--bg-inset`, items as icon+label+badge rows; the active item is the existing amber
treatment re-expressed as a **left** amber bar plus `--amber-glow` ground rather than a bottom
underline — **written as `.prefs .agent-form__tab…`, scoped (§7)**. `#prefsBackBtn` and
`#prefsDetailTitle` are `display: none` at this width.

Verify: `dotnet run --project tools/Agent42.DevHost`, walk all six sections in both themes at
≥1100px, then **open the agent editor and confirm its tab strip is unchanged** (§7).

### Step 5 — The ≤760px drill-down

Files: `src/Agent42.Backend/wwwroot/styles.css`, `src/Agent42.Backend/wwwroot/js/dom.js`,
`src/Agent42.Backend/wwwroot/js/profile.js`, `src/Agent42.Backend/wwwroot/js/mcp.js`.

CSS, in a new `@media (max-width: 760px)` block at the **end of the file**, after the existing
`:2482` block (§4): `.prefs` goes full-bleed — `width: 100vw; height: 100dvh; max-width: none;
max-height: none; border-radius: 0` (**`max-height: none` is load-bearing**, §13). The rail is
full width and the pane hidden by default; `.prefs.is-detail` inverts that and reveals the
`‹ Back | title` header; `.prefs__panel-title` is hidden in detail view to avoid showing the
section name twice; the footer stacks with `#profileSaveBtn` full width and `#profileCancelBtn`
hidden — the header `×` is the cancel affordance on mobile and calls the same `dialog.close()`,
so the existing theme-revert-on-close handler still runs.

JS: `dom.js` exports the six new refs. `profile.js` gains a delegated `click` listener on
`#profileNav` using `closest('[role="tab"]')` that sets `is-detail` and writes the tab's
`.prefs__navlabel` text (not `button.textContent`, §9) into `#prefsDetailTitle`; `#prefsBackBtn`
clears it; `openProfileDialog()` clears it **before** `showModal()`; `#prefsCloseBtn` calls
`profileDialog.close()`. Export `enterPrefsDetail(index)`; `mcp.js` calls it right after its
existing, untouched `profileTabs.select(3)`.

Verify: DevHost at 390×844 — list → detail → Back, the dialog genuinely fills the viewport with
no top/bottom gap, and the OAuth-return path lands in the MCP detail view.

### Step 6 — Tools count badge and the saved/unsaved status

Files: `src/Agent42.Backend/wwwroot/js/profile.js`, `src/Agent42.Backend/wwwroot/js/tools.js`.

Badge: write `toolCounts(profileToolChecks)` into `#toolsCount` after every `renderToolChecks`
on that container — **including the one inside `refreshToolCatalog()` at `js/tools.js:28-33`**,
which re-renders it after MCP/skill CRUD — and on a delegated `change` listener over
`#profileToolChecks` (the listener survives re-renders because it is on the container).

Status: listeners on `profileLanguage`, `profileTheme`, `profileSuggest` and
`#profileToolChecks` only (§10) set `#prefsStatus` to `Unsaved changes`; `openProfileDialog()`
resets it to `All changes saved`. **No post-save write** — `saveProfile()` closes the dialog
(§10).

Verify: DevHost — toggling a tool moves both badge and status; picking a default model (which
persists immediately) moves neither; adding an MCP server updates the badge.

### Step 7 — Cache bump

Files: `src/Agent42.Backend/wwwroot/sw.js`, `tests/Agent42.Tests/PwaAssetsTests.cs`,
`tests/Agent42.Tests/SearchAssetsTests.cs`.

`CACHE` → `agent42-shell-v31`; update `PwaAssetsTests.cs:255` and `SearchAssetsTests.cs:120-121`
(§12). Add no third pin.

Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~Pwa|FullyQualifiedName~Search"`.

### Step 8 — The new test file

Files: `tests/Agent42.Tests/PreferencesLayoutAssetsTests.cs` (new),
`tests/Agent42.Tests/ToolSelectionApiTests.cs` (AC9).

Follow `tests/Agent42.Tests/ConfigHealthAssetsTests.cs`: `[Collection("StaticAssets")]`,
`fixture.Client` for `/` and `/styles.css`, `GetAppScriptAsync()` for the module graph,
`StaticAssetsServerFixture.Body(app, decl)` for one function's source. AC9 goes in
`ToolSelectionApiTests` instead (§6).

Verify: `dotnet test Agent42.slnx` (full), `dotnet format Agent42.slnx --verify-no-changes`.

### Step 9 — `/decide`: ADR 0065

Alternatives to record as rejected: a bespoke nav replacing `wireTabs` (§1); redesigning all
seven dialogs, or the agent editor too (user scope call); a client-side description map (§5);
the mockup's search box and per-section badges (§9); a form-level dirty listener (§10); keeping
the dialog open after save so the footer can confirm (§10). Consequences to record:
`GET /api/tools` grows a field; switches appear in the composer popover and agent editor as
well as Preferences; the prefs CSS is **split across two places in `styles.css`** because two
existing tests constrain it from opposite ends (§4); and "MCP is index 3" now also means "MCP is
the 4th rail item".

**Write every file path in full from the repo root** (`src/Agent42.Backend/wwwroot/js/mcp.js`,
never `js/mcp.js`) — `.agentic/scripts/check-doc-paths.mjs` resolves every backticked
path-shaped token against the repo root and hard-blocks CI on a miss.

### Step 10 — `/sync-docs`

Add a `CLAUDE.md` repo-notes bullet (namespace split and the `.prefs`-scoped nav rules, the
index-3 constraint restated for the rail, the `Description` field, switches everywhere, the
split CSS). Add `.agentic/gotchas.md` entries for the two traps not visible in the code:

1. **`enterPrefsDetail` must not be driven from `wireTabs`' `select`** — `openProfileDialog()`
   calls `select(0)` on every open, so a phone would always land in a detail view the user never
   chose. Evidence: `src/Agent42.Backend/wwwroot/js/profile.js`.
2. **The prefs CSS cannot be one contiguous block** — `.prefs` must follow `.agent-dialog`
   (specificity tie) while its media block must follow `.topbar__brand` (a test reads the
   *first* `@media (max-width: 760px)`). Evidence: `src/Agent42.Backend/wwwroot/styles.css`,
   `tests/Agent42.Tests/TopbarActionsAssetsTests.cs`.

Full paths only, per Step 9.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | The dialog ships the rail/pane/footer shell: `#profileNav`, `#prefsCloseBtn`, `#prefsBackBtn`, `#prefsDetailTitle`, `#prefsStatus`, `#toolsCount` all present in `GET /` | `Preferences_ShipsTheRailShell` | `PreferencesLayoutAssetsTests.cs` |
| AC2 | Every pre-existing tab/panel id and both ARIA roles survive, in source order `panelTools` < `profileToolChecks` < `panelProviders` | `Preferences_HasGeneralToolsAndProvidersTabs` (existing, unedited) | `PwaAssetsTests.cs:474-493` |
| AC3 | MCP stays index 3 and the OAuth return still deep-links to it | `ConfigTab_IsWiredIntoPreferences_AppendedLast`, `SkillsTab_IsAppendedAfterConfiguration_LeavingMcpAtIndexThree` (existing, unedited) | `ConfigHealthAssetsTests.cs:36`, `UserSkillPickerAssetsTests.cs:40` |
| AC4 | The OAuth return also opens the mobile detail view | `McpOAuthReturn_EntersThePreferencesDetailView` — `enterPrefsDetail` appears beside `profileTabs.select(3)` in `initMcpOAuthReturn`'s body | `PreferencesLayoutAssetsTests.cs` |
| AC5 | The drill-down is driven by the rail click, never by tab selection — `wireTabs` stays free of prefs concepts and `openProfileDialog` does not enter detail | `DetailView_IsEnteredByRailClick_NotByProgrammaticSelect` — `Body(app, "export function wireTabs(tabPairs)")` **and** `Body(app, "async function openProfileDialog()")` both `DoesNotContain("enterPrefsDetail")`, and the rail listener uses `closest` | `PreferencesLayoutAssetsTests.cs` |
| AC6 | `.prefs` follows `.agent-dialog`, and the prefs media block is **not** the first `@media (max-width: 760px)` | `PrefsCss_IsOrderedAgainstBothConstraints` — index checks over `GET /styles.css` | `PreferencesLayoutAssetsTests.cs` |
| AC7 | Tool rows render as switches: the `.tool-check` input rule sets `appearance: none` | `ToolRows_RenderAsSwitches` — pin the selector actually shipped in Step 2.4 | `PreferencesLayoutAssetsTests.cs` |
| AC8 | `ToolDescriptor` carries a description and every static built-in has non-empty copy | `EveryStaticDescriptor_HasADescription` | `ToolCatalogTests.cs` |
| AC9 | Skill and MCP descriptors leave `Description` null | `SkillAndMcpDescriptors_HaveNoDescription` — over `GET /api/tools` on the cluster fixture | `ToolSelectionApiTests.cs` |
| AC10 | `renderToolChecks` gains the description and the group wrapper without moving a pinned call site | `SkillsRenderUnderTheirOwnHeading`, `McpToolPickerAssetsTests` (existing, unedited) | `SkillPickerAssetsTests.cs:45-53`, `McpToolPickerAssetsTests.cs` |
| AC11 | The status line is wired only to the four Save-persisted fields | `SavedStatus_TracksOnlyTheSavePersistedFields` — no `profileForm.addEventListener("input"` / `"change"` in the app source (the existing `"submit"` listener must not trip it) | `PreferencesLayoutAssetsTests.cs` |
| AC12 | The agent editor's tab strip is not restyled — every new nav rule is `.prefs`-scoped | `AgentDialogTabs_AreNotRestyled` — each new nav selector in `styles.css` is prefixed `.prefs` | `PreferencesLayoutAssetsTests.cs` |

The cache bump is covered by the two existing pins updated in Step 7 — deliberately no AC of
its own (§12).

Manual (no automated proof possible — no JS runtime in this repo, ADR 0037's untestable-half
treatment): a **headless Chrome over CDP pass against DevHost is required**, not optional, at
390×844 and ≥1100px in both themes, walking all six sections, the drill-down, the OAuth-return
deep link, and **all three** tool containers (Preferences, agent editor, composer popover) plus
the agent editor's tab strip (§7). Record the measured values in ADR 0065. ADR 0052's lever
applies in reverse: below 760px the sidebar is an off-canvas drawer, so open Preferences from
the topbar menu, not the desktop rail.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time.

| Gate | Threshold | Measuring tool |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura ∩ `git diff` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time — **blocks**, S3776 is an `error` |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `outdated_packages` | report (soft) | CI |
| Branch policy | never commit to `main` | `.claude/hooks/protect-main.mjs` + `lefthook.yml`, both reading `branches.protected` |

Coverage note: this MR is overwhelmingly HTML/CSS/JS, which coverlet does not see. The only C#
added is in `ToolCatalog.cs` — a record parameter and five string literals, covered by AC8.
Expect a very small denominator; report the actual figure, never an estimate.

## Out of scope

- The **"Search settings" box** from the mockup (user decision, §9). No placeholder ships.
- **Per-section count badges** other than Tools (no defensible meaning, §9).
- The **agent editor dialog frame** and the five other `.agent-dialog` dialogs. Their *tool
  rows* do change, because `renderToolChecks` is one function (§8) — intended, not a leak.
- **Descriptions for skill and MCP rows.** Skill frontmatter carries a `description`, but
  surfacing it means threading it through `SkillCatalog`, `IUserSkillsGrain` state and
  `GET /api/tools` — its own MR.
- **Keeping the dialog open after Save** so the footer could confirm (§10) — a behaviour change.
- Any **backend behaviour change**: no endpoint, grain, secret, Bicep, `excludedPaths` or Cosmos
  change. `ToolDescriptor` gains a display-only field on an existing response DTO.
- `index.html`'s other dialogs, the composer, the sidebar, and the `/logs` and `/usage` pages.

## Review

A `critic` subagent reviewed the first draft and returned **"not ready — four blocking
findings"**. All four, and every S2 mechanism hole, were verified against the cited file:line
and **accepted**; the spec above is the corrected version. Nothing was rejected. What changed:

| Finding | Verified | Fix |
|---|---|---|
| The `v30` cache literal is pinned by two existing tests (`PwaAssetsTests.cs:255`, `SearchAssetsTests.cs:120-121`), which the draft did not list while promising tests pass unedited | ✓ | §12, Step 7; the draft's third pin (its own AC12) dropped |
| AC9 targeted `GET /api/tools` from a fixture that maps no API (`StaticAssetsServerFixture.cs:28-35` — static files only) | ✓ | §6; AC9 moved to `ToolSelectionApiTests`, AC8 to `ToolCatalogTests` |
| `SkillPickerAssetsTests.cs:52` pins `for (const server of mcpServers) addServer(server);` verbatim — the draft's "`addServer` appends to the group" changes that call site while the same step told the executor a failure means they overreached | ✓ | §3; group wrapping via a closed-over `currentGroup`, arity unchanged |
| `TopbarActionsAssetsTests.cs:185-194` reads the **first** `@media (max-width: 760px)` and requires `.topbar__brand {` before it; a prefs block near the dialog rules becomes the first and fails | ✓ | §4; media block moved to end of file, base rules stay after `:1539` |
| Draft §9 cited `styles.css:1664-1665` as `.providers__list` — it is **`.share-agents`**; `.providers__list` (`:1811`) has no height or overflow, so the prescribed override was dead CSS | ✓ | §13; the whole section deleted |
| `.prefs` and `.agent-dialog` tie on specificity — source order decides, and the draft never said "after" | ✓ | §4 |
| The mobile full-bleed rule omitted `max-height: none`, so the UA `<dialog>` default clips `100dvh` | ✓ | §13, Step 5 |
| `saveProfile()` closes the dialog (`js/profile.js:88-90`), so the draft's post-save "All changes saved" write is unobservable | ✓ | §10; no post-save write, limitation stated |
| The draft kept `.agent-form__tab`/`__tablist`/`__panel` off its "shared, keep as-is" list while Step 4 restyled them — and **no test asserts them**, so the agent editor breaks silently | ✓ | §7, Step 4, new AC12 |
| Step 9's `js/profile.js` shorthand fails `.agentic/scripts/check-doc-paths.mjs` (hard CI block) | ✓ | Steps 9–10 mandate full paths |
| Step 6 required a write inside `refreshToolCatalog()` but did not list `tools.js` | ✓ | Step 6 file list |
| AC5 asserted the wrong function; AC11 would trip on the existing `submit` listener; AC7's selector disagreed with Step 2.4 | ✓ | all three ACs rewritten |
| Draft said "three tests" parse the `profileTabs` block (two) and "a second" `@media (max-width: 760px)` (it is the third) | ✓ | §1, Affected areas |
| Draft called the profile↔mcp import cycle "closed by this change" — it already exists (`js/mcp.js:4`, `js/profile.js:7`); the ADR 0041 §5 claim itself checks out (`0041-frontend-es-modules.md:88-91`) | ✓ | §11 restated |
| Rail delegation: `e.target` will be an `<svg>`/`<path>`, and `button.textContent` would read "Tools 3/5" | ✓ | §9, §11, Step 5 |
| Step 3's "move the trailing note" would break `UserSkillPickerAssetsTests.cs:130-139` and `McpServersAssetsTests.cs:110-114` | ✓ | §2 note-moving rule |

Claims the critic checked and confirmed sound: the seven `.agent-dialog` dialogs and three
`.tools-checks` containers; `ToolDescriptor`'s call sites compiling untouched under a trailing
optional parameter; no test asserting `.agent-form` on the profile form; AC2's citation; the
`SHELL`/`ProjectLayoutTests` no-op; the quality-gate table matching the yaml verbatim; and
every ADR 0021/0026/0048/0058 tool-picker invariant surviving the wrapper div and extra span.

## Divergence from the plan (recorded at execution)

Executed 2026-09-04 on `feature/preferences-redesign` off `main @ 6b9a08d` — no drift; the spec's
`planned-against` ref was HEAD exactly. All ten steps shipped. Five deviations, all small:

1. **Step 2.3's "base rules after `styles.css:1539`" was not followed for the tool-row family, on
   purpose.** §4's ordering constraint is specifically about `.prefs` vs `.agent-dialog` tying on
   specificity; `.tools-checks` / `.tool-check` / the new `.tool-group` have no such conflict, and
   Step 2.4 separately *mandates* editing the existing `.tool-check input` rule **in place** so there
   is one rule rather than two competing ones. The tool-row rules therefore stay in the tool-row
   block where they already lived. Only the `.prefs__*` rules moved.

2. **`.prefs` sits after the whole shared `.agent-form__*` family, not immediately after
   `.agent-dialog`.** The plan's constraint ("after line 1539") is satisfied either way. It was moved
   down one block because AC12 is written structurally — *every nav selector below `.prefs {` must be
   `.prefs`-scoped* — and with `.prefs` inserted above `.agent-form__tablist`, the **original,
   correct, unscoped** base rules fell below it and the assertion flagged them. Anchoring `.prefs`
   below the family it specializes makes "everything after `.prefs {` is prefs code" true, which is
   what makes AC12 meaningful rather than merely passing.

3. **Two pointer comments had to be reworded, and this became gotcha #2 rather than a footnote.**
   The plan says to "leave a comment at each half pointing at the other". The natural wording of that
   comment contains the literal `@media (max-width: 760px)` and the literal `.topbar__brand {` — and
   `TopbarActionsAssetsTests` finds both with a plain `IndexOf` over the whole stylesheet, which does
   not skip comments. Both tripped. The same hazard hit `openProfileDialog`'s comment naming
   `enterPrefsDetail`, which AC5 asserts is absent from that body. The rule is now recorded.

4. **One existing test was edited, which the plan did not budget for.**
   `McpServersAssetsTests.TabResultLine_IsClearedOnPreferencesOpenAndOnDelete` sliced a **fixed
   1200-character window** off `openProfileDialog`'s declaration rather than using
   `StaticAssetsServerFixture.Body(...)`. The comments this MR adds pushed `loadMcpServers()` past
   that window, so `IndexOf` returned `-1` and the *ordering* assertion failed for a reason unrelated
   to ordering. Switched to `Body(...)`: the same assertion, over the actual function body, strictly
   more precise. No other pre-existing assertion was touched — the cache-bump pins in Step 7 were
   planned edits.

5. **`refreshToolsBadge()` lives in `src/Agent42.Backend/wwwroot/js/tools.js`, not `profile.js`.**
   Step 6 requires a badge write inside `refreshToolCatalog()`, which is in `tools.js`; putting the
   helper there keeps it beside `toolCounts` and avoids `tools.js` importing `profile.js`. Both files
   were already in Step 6's file list, so this is placement, not scope.

Also worth recording, because the plan asked for a measured browser pass and one measurement came back
ambiguous: **the document already overflows horizontally at 390px on `main`** (`scrollWidth 534` with
the dialog closed, traced to `.composer__hint` and its `<kbd>` elements — the gap ADR 0043 records as
deliberately unfixed). Its visible effect is that a modal `<dialog>`'s UA `margin: auto` centres in the
534px initial containing block rather than the viewport, offsetting the dialog by **88px on `main` and
72px after this change**. Not introduced here, slightly reduced here, out of scope, and recorded in ADR
0065's Unverified residuals so the next person does not debug `.prefs` for it.

### Post-review corrections (critic subagent, /execute-plan step 5)

A `critic` subagent reviewed the finished diff and returned **no ADR contradiction and no gotcha
violation** — the picker invariants, the pinned call sites, MCP at index 3, `ToolDescriptor` being
response-DTO-only, and the `SHELL`/`CACHE` handling all checked out. Its six findings were all about
**guards being weaker than the documents claimed**, plus one shape violation. All six accepted and
fixed; none rejected.

| Finding | Fix |
|---|---|
| **S2** AC6's mobile assertions scanned `styles[prefsAt..]`, so `max-height: none` matched the **base** rule and `100dvh` matched a **comment** — both mobile declarations were deletable with the test green, while the ADR calls one of them "load-bearing" | Scoped to the mobile block, comments stripped, asserted with the semicolon. **Mutation-checked both ways.** |
| **S2** "not the FIRST 760px block" compared a rule *inside* the block against `firstMediaAt` — moving the block to the top would make `firstMediaAt` point at its own at-rule and still pass | Compare the block's **own** opener (`LastIndexOf` back from the rule), plus a direct `.topbar__brand` ordering assertion so this test fails on its own. **Mutation-checked by actually moving the block.** |
| **S3** AC12 filtered on lines *ending* in `{`, missing single-line rules — a live idiom in this file and in the prefs block itself — and covered only 2 of the 5 family members §7 names | Brace-boundary selector parsing over all five members. **Mutation-checked with an unscoped single-line `.agent-form__tab` rule and an unscoped `.agent-form__actions` rule**; both would have passed the first version. |
| **S3** `.prefs #profileCancelBtn` / `.prefs #profileSaveBtn` were the only two `#id` selectors in a 2900-line BEM stylesheet, and four class hooks in the markup had no CSS behind them | Addressed by role instead (`:not(.agent-form__btn--primary)` / `.agent-form__btn--primary`); `prefs__nav`, `prefs__navitem` and `prefs__actions` dropped from the markup; `prefs__heading` given the `min-width: 0` it actually wants. Re-measured in DevHost: `cancelDisplay: "none"`, `saveWidth: 358`. |
| **S3** New unguarded coupling: `enterPrefsDetail` resolves by **DOM order** while everything else uses the **`dom.js` array** order | `RailMarkupOrder_MatchesTheProfileTabsArrayOrder`. **Mutation-checked** by swapping two buttons in the markup alone. |
| **S4** Three doc inaccuracies: ADR said "one existing test was changed" (three files), an `index.html` comment said `"Tools 3/5"` against a measured `"Tools 9/9"`, and an assertion commented as pinning the theme handler was satisfied by a different handler | All three corrected; the theme assertion now reads that handler's own body. |

The whole browser pass was re-run after these edits and every measured value in ADR 0065 is unchanged.
Final: **1109/1109** tests, `dotnet format` clean, `dotnet build` clean, `check-doc-paths` clean.
