---
id: 0065
date: 2026-09-04
status: accepted
tags: [frontend, ux, mobile, preferences, tools, no-migration]
supersedes: []
extends: [0041]
amends: [0021]
---

# Preferences becomes a rail with a mobile drill-down

> **Amends [ADR 0021](0021-selectable-tools.md)** in one narrow way: `ToolDescriptor` gains a
> display-only `Description`, so `GET /api/tools` grows a field and the tool picker gains a
> description line under each built-in's label. No selection semantics change — not the `null == all`
> sentinel, not the wildcards, not the capability intersection.

## Context

`#profileDialog` was a `min(520px, 92vw)` box whose six sections were a single **non-wrapping flex
row** of tabs (`.agent-form__tablist`, `display: flex` with no `flex-wrap` and no `overflow-x`). The
dialog had never had one line of responsive CSS.

Measured on `main` at a 390×844 viewport, before this change: the tablist's `scrollWidth` is **525px**
inside a `clientWidth` of **313px**, `overflow-x` computes to `visible`, and the last tab (**Skills**)
has a right edge at **636px** in a 390px viewport. There is no scroller and no wrap, so on the
installed Android PWA roughly half the sections were simply **unreachable**. That is the bug.

## Decision

The shape from the two supplied mockups: a **header** (title + subtitle + close), a **vertical nav
rail** with icons and a count badge on Tools, a **scrolling content pane** with a per-panel heading
and lead description, and a **footer** carrying a saved/unsaved status beside Cancel + Save. At
≤760px the *same markup* becomes a **two-level drill-down** — the rail is the list view, and picking a
section swaps to a detail view headed by `‹ Back` + the section name.

The existing amber/phosphor palette is kept verbatim; every new rule uses the existing
`--bg-raised`/`--bg-inset`/`--line`/`--ink*`/`--amber*` tokens, which already flip in
`:root[data-theme="light"]`. **No new colour is introduced.**

### The rail is a restyle, not a new navigation mechanism

The six `role="tab"` buttons, their ids, and the `wireTabs` pairing in
`src/Agent42.Backend/wwwroot/js/dom.js` are all unchanged. That is a **constraint, not a preference**:
`src/Agent42.Backend/wwwroot/js/mcp.js` deep-links the interactive-OAuth return with
`profileTabs.select(3)`, and two existing tests parse the `profileTabs = wireTabs([…]);` block out of
the module graph to assert MCP sits at index 3. A bespoke nav would cost the OAuth return, the
arrow-key navigation `wireTabs` already implements, and two green tests.

**Consequence worth stating for the next reader:** "MCP is index 3" now also means **"MCP is the 4th
rail item"**. The ordering constraint did not move; it acquired a second, visual reading.

### The drill-down is entered by a rail CLICK, never by tab selection

`profileDialog.classList.toggle("is-detail")`, meaningful only inside the ≤760px block. It is set by a
**delegated `click` listener on `#profileNav`** resolving the row with `closest('[role="tab"]')` — the
buttons carry an inline `<svg>` icon, so `e.target` is usually a `<path>`.

It is deliberately **not** driven from `wireTabs`' `select()`. `openProfileDialog()` calls
`select(0)` on **every** open, so a phone would land in General's detail view on every single open — a
section the user never chose. `wireTabs` is also shared with the agent editor and must stay free of
prefs concepts. `openProfileDialog()` therefore calls `exitPrefsDetail()` before `showModal()`, and the
one programmatic case that *does* want detail — the OAuth return — calls `enterPrefsDetail(3)` beside
its own untouched `profileTabs.select(3)`.

`src/Agent42.Backend/wwwroot/js/profile.js` and `src/Agent42.Backend/wwwroot/js/mcp.js` already import
each other; ADR 0041 §5 permits the residual cycle explicitly, so `enterPrefsDetail` is a plain hoisted
`export function` with no setter-slot indirection.

### The prefs CSS is split across two places in styles.css

This is the ugliest thing in the MR and it is forced from **both** ends:

- `.prefs` must be authored **after** `.agent-dialog`. Both are single classes, so specificity **ties**
  and source order decides. Above it, the dialog silently stays 520px and the entire redesign is inert
  on desktop — with every other assertion still green.
- The prefs `@media (max-width: 760px)` block must **not** be the first such block in the file.
  `tests/Agent42.Tests/TopbarActionsAssetsTests.cs` takes the first one and requires the base
  `.topbar__brand` rule to appear before it.

So the base rules sit after the shared `.agent-form__*` family, and the media block sits at the **end
of the file**. Each half carries a comment pointing at the other, because they are no longer adjacent.

**A trap discovered while writing those comments:** both guards are a plain `IndexOf` over the whole
stylesheet, and `IndexOf` does not skip comments. A comment that spells out the at-rule, or a selector
followed by its opening brace, becomes the first match and the guard reads the prose instead of the
rule. Both were hit and both are now recorded in `.agentic/gotchas.md`.

### Tool descriptions are server-owned

`ToolDescriptor` gains a trailing `string? Description = null`. Its own doc comment already records
that it is **"a response DTO only — never grain state"**, which is what makes this cheap: no `[Id]`, no
migration, and the three construction sites in `src/Agent42.Backend/Tools/AgentApi.Tools.cs` all use
named arguments for the trailing parameters, so they compile untouched and leave the field null.

Skill rows and MCP server/tool rows render **label-only** — their labels are user-uploaded skill names
and server-supplied tool names, so there is nothing to describe.

The copy was written from the ADRs rather than the mockup, which shows a **Calendar** tool that ADR
0063 deleted. The `location` and `memory` lines deliberately name the egress: they are the two widest
per-turn egress surfaces in the app (ADR 0012, ADR 0056) and the picker is where a user decides about
them.

### Switches replace checkboxes in all three containers

`profileToolChecks`, `agentToolChecks` (agent editor) and `chatToolChecks` (composer popover) all carry
`class="tools-checks"` and all render through the one `renderToolChecks`. One renderer, one look — no
variant flag, no new branch. **The agent editor and the composer popover therefore change visually.
That is intended scope, not a leak**, and it is measured below.

Rows are grouped into cards by a **closed-over `currentGroup` element** that `addCheck`/`addServer`
append into, never by threading a parameter: `tests/Agent42.Tests/SkillPickerAssetsTests.cs` pins
`addGroup("Skills", skills)`, `addHeading("MCP servers")` and
`for (const server of mcpServers) addServer(server);` as literal source text, so `addServer`'s arity and
those three lines are fixed. Built-ins gain a `Built-in` heading; they used to render headingless.

### The footer status can only ever show two of its three states

`saveProfile()` ends `profileSaved = true; profileDialog.close();` — a successful save **closes the
dialog**. So "All changes saved" is observable as the *opening* state and after a reset, never as a
post-save confirmation. Accepted: the line's job is to warn that edits are pending, and close-on-save
is existing behaviour this MR does not change.

Preferences also mixes two persistence models. **Save-on-Save:** `profileLanguage`, `profileTheme`,
`profileSuggest`, `profileToolChecks` — the four fields `saveProfile()` PUTs. **Save-immediately:** the
three model selects, and all provider / MCP / skill CRUD. `#prefsStatus` is therefore wired to those
**four elements only**; a form-level `input`/`change` listener would announce "Unsaved changes" for a
default model that is already stored.

## Alternatives considered

- **A bespoke nav replacing `wireTabs`.** Rejected — see above; it costs the OAuth deep link, the
  arrow-key navigation and two green tests, and buys nothing the restyle does not.
- **Redesigning all seven `.agent-dialog` dialogs, or the agent editor's frame too.** The
  `.agent-form__*` rules are flat BEM — standalone selectors, not descendants — so every one of the
  seven shares them. User scope call: **Preferences only.** Every new nav rule is written
  `.prefs .agent-form__tab…`. This is the highest-risk silent regression in the MR, because
  **nothing in `tests/` asserts `.agent-form__tab`** — an unscoped edit would mangle the agent editor
  with the whole suite green. AC12 guards it structurally, over **all five** members plan §7 names
  (`__tablist`, `__tab`, `__panel`, `__title`, `__actions`) and by parsing selector lists on **brace**
  boundaries rather than line endings — a "lines ending in `{`" filter waves through a single-line rule,
  and single-line rules are a live idiom in this stylesheet *and* inside the prefs block itself.
- **A client-side description map in `tools.js`.** Rejected: copy drifting away from the catalog it
  describes, and invisible to every backend test.
- **The mockup's "Search settings" box.** Not built, and no placeholder ships (user decision).
- **Count badges on every section.** Rejected: there is no defensible meaning for a "1/2" on General,
  Model providers or Configuration. Tools alone gets one, counting **top-level rows only** —
  per-MCP-tool rows are collapsed by default and counting them would make the badge read as a number
  the user cannot see.
- **A form-level dirty listener.** Rejected — see the two persistence models above.
- **Keeping the dialog open after Save** so the footer could confirm. Rejected: a behaviour change
  with no acceptance criterion behind it.

## Consequences

- **`GET /api/tools` grows a field.** Display-only, on an existing response DTO. No migration.
- **Switches appear in the composer popover and the agent editor**, not only in Preferences.
- **The prefs CSS lives in two places in `styles.css`**, because two existing tests constrain it from
  opposite ends. Whoever edits one half must check the other.
- **"MCP is index 3" now also reads as "MCP is the 4th rail item"** — and that is a **code dependency**,
  not only a reading. `enterPrefsDetail` resolves its tab by **DOM position**
  (`#profileNav [role="tab"]` in document order) while every other consumer — `profileTabs.select(3)`
  and the two tests parsing the `wireTabs([…])` block — goes through the **`dom.js` array** order. They
  agree, and nothing made them agree until now: reordering the six buttons in the markup alone would
  leave `select(3)` showing `panelMcp` while `enterPrefsDetail(3)` titled the detail bar with a different
  section. `RailMarkupOrder_MatchesTheProfileTabsArrayOrder` pins the two orders together.
- **The badge sits inside the Tools tab button**, so it joins that button's accessible name. The label
  is in its own `<span class="prefs__navlabel">` and the drill-down title reads **that span** — reading
  `button.textContent` would title the detail view "Tools 9/9". Measured: `tabTextContent` is
  `"Tools 9/9"`, `detailTitle` is `"Tools"`.
- **No backend behaviour change**: no endpoint, grain, secret, Bicep, `excludedPaths` or Cosmos change.
- Shell cache bumped `agent42-shell-v30` → `-v31`. No new JS module, so `SHELL` itself is unchanged.

## Verification

- **1108/1108 tests green**; `dotnet format Agent42.slnx --verify-no-changes` clean; `dotnet build`
  clean (S3776 at `error`).
- Every pre-existing markup and picker assertion passes **unedited** — the ids, both ARIA roles, the
  `panelTools` < `profileToolChecks` < `panelProviders` order, MCP at index 3, and the four pinned
  `renderToolChecks` call sites.
- **Three existing test files were edited, and only one of those edits was unplanned.**
  `tests/Agent42.Tests/PwaAssetsTests.cs` and `tests/Agent42.Tests/SearchAssetsTests.cs` carry the two
  hard pins on the shell-cache literal; moving them from `v30` to `v31` is the planned cost of the bump.
  The unplanned one is worth naming:
  `McpServersAssetsTests.TabResultLine_IsClearedOnPreferencesOpenAndOnDelete` sliced a **fixed
  1200-character window** off `openProfileDialog`'s declaration. The added comments pushed
  `loadMcpServers()` past that window, so `IndexOf` returned `-1` and the ordering assertion failed for
  a reason that had nothing to do with ordering. It now uses `StaticAssetsServerFixture.Body(...)` —
  the same assertion over the actual function body. Strictly more precise, and it removes a latent trap
  for the next person to add a line there.
- **The three guards an adversarial review found weaker than this ADR claimed are now mutation-checked**,
  which is the only reason the claims above are worth anything. Measured, each of these fails the suite
  and passed before the fix: deleting the mobile `max-height: none`; deleting the mobile `height: 100dvh`
  (it was matched inside a *comment* — the new gotcha's own trap, recurring); adding an unscoped
  **single-line** `.agent-form__tab` rule; adding an unscoped `.agent-form__actions` rule (a family
  member the first version of the guard never covered); moving the prefs media block to be the first
  760px block; and swapping two rail buttons in the markup without touching `dom.js`.

### The DevHost browser leg was run

Headless Chrome over CDP against DevHost on `localhost:61326` (the ADR 0044/0052/0058/0064 precedent);
throwaway driver, deliberately not committed. Both themes, both widths.

**Desktop, 1280×900.** Dialog `1040×720`. Rail `232px`, `flex-direction: column`, detail bar
`display: none`, footer visible, Save reads `Save changes`. All six sections walk cleanly — exactly one
panel visible at each step, in order General / Tools / Model providers / MCP servers / Configuration /
Skills. Active item settles to `rgb(255,177,61)` on `rgba(255,177,61,0.14)` with a **2px** left bar;
inactive is `rgb(109,101,85)`.

**`is-detail` is inert above 760px** — set by the rail click at every width, and measured with the
class present: rail `display: flex` and visible, pane `flex`, detail bar `none`, panel title `block`,
Cancel `block`.

**Tool rows.** 3 group cards (`Built-in` / `Skills` / `MCP servers`), 11 rows, 5 description lines
(one per built-in). The switch computes `appearance: none`, `38px × 21px`, `border-radius: 999px`, knob
`matrix(1, 0, 0, 1, 17, 0)` when checked. Badge `9/9`, `aria-label="9 of 9 tools enabled"`.

**Badge and status.** Toggling one tool moved the badge `9/9` → `8/9` and the status to
`Unsaved changes` (`is-dirty`). Firing `change` on `defaultModelSelect` **and** `speechModelSelect` left
the status at `All changes saved` — the two-persistence-models rule, measured.

**The agent editor's tab strip is unchanged:** `flex-direction: row`, `border-bottom: 1px`,
`border-right: 0px`, tab `border-bottom: 2px`, `border-radius: 0px`, `padding: 8px 12px`, dialog width
`520px`. Its **tool rows** do change, as intended: `appearance: none`, 3 groups, 5 descriptions —
identical in the composer's Tools popover.

**Light theme, desktop.** Dialog `rgb(251,247,238)`, rail `rgb(233,226,210)`, active
`rgb(168,102,17)` on `rgba(168,102,17,0.12)`, switch fill `rgb(127,78,10)`, description
`rgb(92,86,71)` — all token-derived, no literal colour.

**Mobile, 390×844.** `matchMedia("(max-width: 760px)")` true. Dialog `390×844` against a layout
viewport of `390×844` — `fillsWidth` and `fillsHeight` both true, computed `max-height: none`,
`border-radius: 0`, `border-width: 0`. **`max-height: none` is load-bearing**: `<dialog>` carries a UA
default `max-height: calc(100% - 6px - 2em)` that would clip `100dvh` into a centred box with gaps.

List view: rail visible, pane `display: none`, `#profileCancelBtn` `display: none`, close button
visible. Drilling in by dispatching the click on the **`<svg>` icon** (not the button) yielded
`isDetail: true`, `detailTitle: "Tools"`, rail `none`, pane `flex`, detail bar `flex`, panel title
`none`, Save width `358px`. Back returned to the list and cleared the title. All six sections drill in
and title correctly. Closing from a detail view and reopening lands on the **list** with `tabGeneral`
selected and the status reset.

**The OAuth return** (`/?mcp_oauth=connected`): dialog open, `isDetail: true`,
`detailTitle: "MCP servers"`, `panelMcp` shown, `tabMcp` selected, query string cleaned.

**The bug is fixed, measured both ways.** On `main` at 390px the tablist had
`scrollWidth 525 / clientWidth 313`, `overflow-x: visible`, and Skills' right edge at **636px**. After
the change the rail is `scrollWidth 390 / clientWidth 390`, nothing inside the dialog overflows
(`overflowingInsideDialog: []`), and all six items sit inside it. The pane is the only scroller: at
390×520 the General panel is `scrollHeight 563 / clientHeight 289`, scrolls to 274, and the header and
footer both move **0px**.

### Unverified residuals

- **A real Android device.** Everything above is Chrome's mobile emulation. `100dvh`, the safe-area
  inset on the footer, and the touch ergonomics of the rail rows are device facts — ADR 0037's
  untestable-half treatment applies.
- **The document overflows horizontally at 390px, and it is pre-existing.** `documentElement.scrollWidth`
  is **534** against a 390px viewport **on `main`, with the dialog closed** — traced to
  `.composer__hint` and its `<kbd>` elements (right edge 534), the gap ADR 0043 already records as
  deliberately unfixed. Its visible effect on a modal `<dialog>` is that UA `margin: auto` centres in
  the **534px** initial containing block rather than the viewport, so the dialog sits offset: **88px on
  `main`, 72px after this change**. Not introduced here, slightly reduced here, and **out of scope** —
  but anyone chasing "the dialog is not flush left on a phone" should fix `.composer__hint`, not
  `.prefs`.
- **No JS runtime in this repo** (by design), so the drill-down, the badge and the status line are
  guarded by source-text assertions plus the measured browser pass above, never by a unit test.
