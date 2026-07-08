# Plan — User profile with preferred language

`planned-against: main @ a5ce1de`

## Overview

Add a per-user **profile** that persists a **preferred language** (BCP-47 tag, e.g.
`nl-NL`). This is the first *user-level* preference store in the app — distinct from the
existing per-conversation and per-agent state. The profile is exposed through an
**extensible profile/settings panel** in the web UI with the language selector as its
first field.

Why now: the user wants a durable, cross-device language preference. It is intended to
seed speech-input dictation later, but **speech input does not exist in the app yet**
(verified: no `SpeechRecognition`/mic code anywhere in `wwwroot/`). So this plan stores
the preference *ready to be consumed*; wiring an actual mic/dictation button is a
deliberate follow-up (see Out of scope). The panel is built to be extended, not as a
one-off language toggle.

## Affected areas (verified against the tree)

New files:
- `src/Agent42.Agents/UserProfile.cs` — the profile record (domain type).
- `src/Agent42.Agents/IUserProfileGrain.cs` — grain interface.
- `src/Agent42.Agents/UserProfileGrain.cs` — grain implementation.
- `tests/Agent42.Tests/UserProfileGrainTests.cs` — grain tests.

Changed files:
- `src/Agent42.Backend/AgentApi.cs` — add `GET`/`PUT /api/profile` endpoints + request record.
- `tests/Agent42.Tests/AgentApiTests.cs` — add profile endpoint contract + persistence tests.
- `src/Agent42.Backend/wwwroot/index.html` — profile-open button + profile modal markup.
- `src/Agent42.Backend/wwwroot/app.js` — language list, load/save wiring, modal open/close.
- `src/Agent42.Backend/wwwroot/styles.css` — styling for the new button/modal (reuse existing modal styles where possible).
- `src/Agent42.Backend/wwwroot/sw.js` — bump the shell cache version so returning PWA users don't get a stale UI (Step 3).
- `.agentic/decisions/0005-user-level-preferences-grain.md` — new ADR (see Step 5).
- Possibly `.agentic/gotchas.md` — add `UserProfileGrain`/profile modal exemplars only if the reviewer judges them canonical (Step 6).

## Context & constraints (what the executor can't cheaply re-derive)

- **Decisions made in the interview (scope is fixed — do not re-expand):**
  1. Preferred language controls **speech-input default only** — and speech input is a
     *later* feature. This plan stores the value; it does **not** add a mic button.
  2. Persistence is a **server-side per-user grain**, not `localStorage` — the user
     explicitly wanted a real cross-device "profile". `localStorage` may be used only as
     an optional cache of the server value, never as the source of truth.
  3. The UI is an **extensible profile panel** (language is field #1), not a bare toggle.

- **Canonical shape to copy — per-user registry grain:**
  `src/Agent42.Agents/AgentRegistryGrain.cs` + `IAgentRegistryGrain.cs`. The new
  `UserProfileGrain` mirrors it: `IGrainWithStringKey` keyed by the user id, one
  `IPersistentState<T>` injected with `[PersistentState("profile", AgentGrain.StorageProviderName)]`.
  Riding the **shared `agent-state` storage provider** means **no new Cosmos container** is
  needed — Orleans partitions by grain type (see the "Cosmos containers must be pre-declared
  in Bicep" gotcha and ADR 0001; the registry grain's class comment states this explicitly).
  **Do not add a Bicep container.** Use a distinct `PersistentState` name (`"profile"`) as the
  registry uses `"agents"` and the index uses `"conversations"`.

- **User identity:** `HttpContext.GetUserId()` (`src/Agent42.Backend/CurrentUser.cs`) returns
  the Entra object id from `X-MS-CLIENT-PRINCIPAL-ID`, falling back to `"local-dev"`. The
  profile grain is keyed with this id directly (like `IConversationIndexGrain`/
  `IAgentRegistryGrain`), **not** the `AgentKey(user, conv)` composite.

- **JSON contract gotcha (must-follow):** the frontend calls `response.json()` on every
  endpoint. A handler returning a bare `string` serves `text/plain` and breaks the UI; and
  `Results.Json(null)` writes no body/content-type. See the "Minimal-API handlers returning a
  bare string" gotcha and the `/model` + `/agent` endpoints in `AgentApi.cs`. **Mitigation
  here:** `GET /api/profile` returns the **`UserProfile` object** (never null — the grain
  returns a default profile with an empty language when unset), so `Results.Json(profile)` is
  safe. `PUT` returns the saved `UserProfile` object the same way. Do not return a bare string.

- **Frontend exemplars to copy:**
  - Modal + form + save: the agent dialog — `openAgentDialog()` (`app.js:262`), `saveAgent()`
    (`app.js:284`), `agentForm` submit (`app.js:725`), markup `agentDialog`
    (`index.html:109`). Mirror its open/close, fetch-with-JSON, and try/catch-silent style.
  - GET-and-populate on load: `loadModels()` (`app.js:72`) — same `fetch → res.ok → res.json()`
    shape; init calls live at `app.js:748-753`.
  - Placement: topbar (`index.html:57`) right side is symmetrical to the brand mark and is
    visible on both desktop and mobile; the sidebar footer `DON'T PANIC` (`index.html:51`) is
    the mobile-drawer alternative. Executor picks one; topbar-right is the recommended default.
  - **No i18n framework exists and none is being added** — the language list is a curated
    static table of BCP-47 tag → display name in `app.js`. This selects the *preferred
    language value*; it does **not** translate the UI.

- **Testing note:** there is **no JS test harness** (vanilla JS, no build). Backend contract
  tests in `AgentApiTests.cs` (TestServer over a real Orleans TestCluster) cover the HTTP
  surface; the **UI itself is verified by running DevHost** (`dotnet run --project
  tools/Agent42.DevHost`, offline canned LLM, in-memory grains, UI at http://localhost:61326)
  and exercising the panel. `AgentApiTests` requests currently send no principal header, so they
  all resolve to `local-dev`; the per-user isolation test must set `X-MS-CLIENT-PRINCIPAL-ID`
  explicitly on the request.

## Steps

### Step 1 — Domain type + profile grain (+ grain test)
Files: `src/Agent42.Agents/UserProfile.cs`, `IUserProfileGrain.cs`, `UserProfileGrain.cs`,
`tests/Agent42.Tests/UserProfileGrainTests.cs`.

- `UserProfile`: a `[GenerateSerializer]` `sealed record` with `[Id(0)] string PreferredLanguage`
  (BCP-47; empty string = unset). Shape it like `AgentDefinition.cs` (aliased, `[property: Id(n)]`).
  **Design for extension:** a record with room for future preference fields — add new fields with
  the next `[Id(n)]`, never renumber. Provide a canonical empty default (`new UserProfile("")`).
- `IUserProfileGrain : IGrainWithStringKey` with `GetAsync() : Task<UserProfile>` (returns the
  stored profile, or the empty default when never saved) and `SaveAsync(UserProfile) : Task<UserProfile>`
  (persists and returns it). Alias the interface + methods like `IAgentRegistryGrain`.
- `UserProfileGrain`: inject `[PersistentState("profile", AgentGrain.StorageProviderName)]
  IPersistentState<UserProfileState>`. `UserProfileState` holds the `UserProfile` (default empty).
  `SaveAsync` writes state and returns the value. Keep it minimal — no id/timestamp bookkeeping
  (a profile is a singleton per user, unlike the agent list).
- Optional light validation in `SaveAsync`: trim the tag and cap its length (e.g. ≤ 35 chars — BCP-47
  tags are short); do not attempt full BCP-47 parsing (the curated dropdown is the real gate).
- **Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~UserProfileGrainTests` —
  round-trip + survives `DeactivateOnIdle()` (mirror `AgentRegistryGrainTests`).

### Step 2 — API endpoints (+ contract/persistence tests)
Files: `src/Agent42.Backend/AgentApi.cs`, `tests/Agent42.Tests/AgentApiTests.cs`. Depends on Step 1.

- `GET /api/profile` → `Results.Json(await grains.GetGrain<IUserProfileGrain>(http.GetUserId()).GetAsync())`.
- `PUT /api/profile` taking a `ProfileUpdateRequest(string PreferredLanguage)` record →
  `SaveAsync(new UserProfile(request.PreferredLanguage))`, returns `Results.Json(saved)`.
- Both return the **object** (never a bare string / never null) — honour the JSON-contract gotcha.
- Add the `ProfileUpdateRequest` record next to the other request records at the bottom of `AgentApi.cs`.
- **Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentApiTests`:
  - `ProfileEndpoint_ReturnsJson_WithDefaultWhenUnset` — GET is `application/json`, empty language.
  - `ProfileEndpoint_PersistsPreferredLanguage` — PUT `nl-NL`, then GET reflects it.
  - `ProfileEndpoint_IsScopedPerUser` — PUT under one `X-MS-CLIENT-PRINCIPAL-ID`, GET under another
    returns the default (isolation).

### Step 3 — Frontend profile panel (extensible) + language selector
Files: `src/Agent42.Backend/wwwroot/index.html`, `app.js`, `styles.css`. Depends on Step 2.

- Add a **profile-open control** in the topbar-right (recommended) mirroring existing button styles.
- Add a **profile modal** mirroring `agentDialog` markup: a titled panel with a **Preferences**
  section whose first field is a **Preferred language** `<select>`. Structure the markup so more
  fields/sections can be added later without reflow surgery.
- In `app.js`: a curated `LANGUAGES` const (BCP-47 tag → display name; include an explicit
  "Use device language" option = empty tag). On open (or on init like `loadModels`), `GET /api/profile`
  and select the stored tag; on save, `PUT /api/profile` with the chosen tag. Reuse the agent
  dialog's fetch-with-JSON + silent try/catch conventions. Optionally cache the value in
  `localStorage` for instant paint, but **always** reconcile against the server response.
- **Do not** wire the value into any speech/mic behaviour (none exists; out of scope) — storing +
  round-tripping is the deliverable.
- **Bump the service-worker cache version.** `wwwroot/sw.js` caches the shell assets
  (`index.html`, `app.js`, `styles.css`) under a versioned cache name (`const CACHE =
  "agent42-shell-v1"`) with stale-while-revalidate; without a version bump, returning PWA users
  get the *old* shell first and only see the profile panel on a second load. Bump `CACHE` to
  `agent42-shell-v2` so the `activate` handler purges the stale cache. This does **not** change the
  SW's caching *strategy* — it stays network-first for navigations and bypasses all `/api` paths
  (`url.pathname.startsWith("/api")` already covers `/api/profile`), so the SW gotcha and ADR 0002
  are untouched. Add `wwwroot/sw.js` to the changed-files list for this step.
- **Verify (no JS harness):** run `dotnet run --project tools/Agent42.DevHost`, open
  http://localhost:61326, open the profile panel, pick a language, save, reload the page, reopen
  the panel — the choice persists (proves the full grain↔API↔UI round-trip against in-memory grains).

### Step 4 — Lint/format + full test + build gate
- `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`.

### Step 5 — Record the decision (ADR)
Files: `.agentic/decisions/0005-user-level-preferences-grain.md` (via `/decide`).

- This introduces the first **user-level scalar-preferences** store and picks a **server-side grain
  over `localStorage`** (a real alternative the user weighed). Record: context, the decision, the
  rejected `localStorage`-only option and why (cross-device durability = a real profile), and that it
  rides the shared `agent-state` provider (no new container) consistent with ADR 0001. Grep
  `.agentic/decisions/` for conflicts first (none expected — this is additive).

### Step 6 — Doc sweep (`/sync-docs`)
- Run `/sync-docs` over the diff. Judgement calls: add a gotchas **exemplar** row for
  `UserProfileGrain` (per-user singleton preference grain) and/or the profile modal **only if**
  they are genuinely canonical shapes worth copying; otherwise skip (the size guardrail favours
  restraint). Update `CLAUDE.md` repo notes only if a claim there is now stale (none expected).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | Preferred language persists across grain reactivation | `UserProfile_RoundTrips_AndSurvivesReactivation` | `tests/Agent42.Tests/UserProfileGrainTests.cs` |
| AC2 | `GET /api/profile` returns `application/json`; default (empty language) when never set | `ProfileEndpoint_ReturnsJson_WithDefaultWhenUnset` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC3 | `PUT /api/profile` persists; next `GET` reflects the new language | `ProfileEndpoint_PersistsPreferredLanguage` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC4 | Profile is isolated per user (Entra object id) | `ProfileEndpoint_IsScopedPerUser` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC5 | UI profile panel lets the user pick + save the language; choice survives reload; panel is extensible | Manual DevHost verification (Step 3) — no JS test harness exists | — |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80` (grain + API are covered by Steps 1–2 tests; the vanilla-JS UI
  has no coverage tooling — AC5 is manual, consistent with the repo's existing frontend approach)
- `cognitive_complexity_max: 15`
- `vulnerable_packages: 0`; no new dependencies are added by this plan.
- Branch: create `feature/user-profile-preferred-language`; never commit to `main` (hard rule 1). MR required.

## Out of scope

- **Speech / mic / dictation input** — none exists today; this plan only stores the language *ready*
  for it. A follow-up plan adds the Web Speech API mic button (incl. iOS-PWA caveats & degradation).
- **Agent response language / UI translation (i18n)** — the user selected speech-input default only.
- **Any new Cosmos container or Bicep change** — the grain rides the shared `agent-state` provider.
- **General preferences beyond language** — the panel is *built to extend*, but no other fields ship now.

## Review

Adversarial `critic` pass (fresh context, took no part in writing this spec): **sound — no
blockers, no should-fixes.** It independently verified: no ADR contradiction (0001/0002/0004);
the Cosmos-container gotcha is honoured (rides `agent-state`, distinct `"profile"` state name, no
new container); grain registration is *not* a gap (Orleans auto-discovers grains — `Program.cs`
wires only the storage provider); the JSON-contract gotcha is handled (non-null object via
`Results.Json`); the SW `/api` bypass is a generic prefix match so `/api/profile` needs no SW
change; ADR 0005 is the right next number; all file/line refs and the AC→test mapping are accurate;
AC5-as-manual is a legitimate gap (no JS harness exists).

Two nits, both handled:
1. **PWA stale-asset transient** — a returning PWA user could get one load pairing a fresh
   network-first `index.html` against stale-while-revalidate `app.js`/`styles.css`. The critic
   noted this is pre-existing (the cache key was not bumped in PRs #7/#9/#10) and skipping it
   matches repo practice. **Resolved anyway** by the SW cache-version bump added to Step 3 (clean
   first paint), which does not alter the SW's caching strategy.
2. **BCP-47 length cap (≤35 chars) could reject some valid extension-laden tags** — immaterial:
   the cap is explicitly approximate defense-in-depth and the curated dropdown is the real gate.
   Left as-is. Executor may relax/drop the cap without consequence.

Underscored for the executor: the AC4 per-user isolation test must build an explicit
`HttpRequestMessage` with `X-MS-CLIENT-PRINCIPAL-ID` (the shared `_client` in `AgentApiTests` is
header-less → `local-dev`); this reaches `GetUserId` because `TestServer` has no ingress stripping
`X-MS-CLIENT-*`. Already flagged in Context & constraints.

## Implementation notes — where execution diverged from the plan

1. **Profile-open control placed in the sidebar footer, not topbar-right.** The plan
   recommended topbar-right as "visible on both desktop and mobile", but the code contradicts
   that premise: `styles.css` has `.topbar { display: none; }` and reveals the topbar only in
   the `@media (max-width: 760px)` block — it is **mobile-only**. Placing the control there
   would make it unreachable on desktop. It was placed in the **always-visible sidebar footer**
   (the plan's own listed alternative), reachable on desktop directly and on mobile via the
   drawer. Code-truth over plan-text (CLAUDE.md rule 2).

2. **A shared-static-mutation bug was found and fixed during Step 1.** The first draft made
   `UserProfile.Empty` a cached static (`{ get; } = new("")`) and used it as the grain-state
   default. The `UserProfile_IsIsolatedPerUser` test caught cross-grain leakage: Orleans' copier
   mutated the shared static, so an unrelated grain read another user's value. Fixed by making
   `Empty` a **factory** (`=> new("")`, fresh instance per access); the state field defaults
   through it. Captured as a new gotcha (*"A shared static instance must never be a grain-state
   default"*). This is functionally the plan's intended `new UserProfile("")` default.

3. **Doc-sweep judgment calls (Step 6) resolved as ADD.** Both a `UserProfileGrain` exemplar row
   (distinct per-user *singleton* get/save shape — the ADR notes more preference grains are
   coming) and the shared-static-default gotcha were added to `.agentic/gotchas.md`. `CLAUDE.md`
   needed no change (no claim went stale).

4. **`localStorage` client cache: not added.** The plan allowed it as an optional render cache;
   it was skipped to avoid a second source-of-truth surface. The panel reconciles against the
   server on open and after save instead.
