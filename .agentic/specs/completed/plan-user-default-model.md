# Plan — user-settable default model for new chats

planned-against: main @ 2cf0cc0

## Overview

A signed-in user can pick, in Preferences → model providers, which model new chats start
on — and "New chat" honours it. The storage and the resolution order already exist
(`UserProviderConfig.Default`, `[Id(1)]`, designed in ADR 0018 and read by
`UserProviderResolver.EffectiveDefault`); what is missing is a write path (no endpoint calls
`IUserProviderConfigGrain.SetDefaultAsync`) and the UI. Why now: since ADR 0019 removed the
baseline chat set there is **no operator default model at all** — the effective default is
whatever `declared[0]` happens to be, i.e. an accident of insertion order, and the user has no
way to change it.

## Affected areas

| Path | Change |
|---|---|
| `src/Agent42.Backend/AgentApi.cs` | new `PUT /api/default-model`; new `DefaultModelRequest` DTO next to `ProviderUpsertRequest` |
| `src/Agent42.Backend/wwwroot/index.html` | default-model `<select>` + label/hint in the providers tab panel |
| `src/Agent42.Backend/wwwroot/app.js` | populate/persist the select; `startNewConversation()` resets the picker |
| `src/Agent42.Backend/wwwroot/styles.css` | only if the existing providers-panel classes don't cover the new field |
| `tests/Agent42.Tests/ProvidersApiTests.cs` | endpoint contract facts |
| `tests/Agent42.Tests/UserModelCatalogTests.cs` | overlay default → `/api/models` `default` (the chain that makes the feature real) |
| `tests/Agent42.Tests/ModelPickerAssetsTests.cs` | frontend source-text facts |
| `tools/Agent42.DevHost/Program.cs` | **read only** — explains why part of this is not observable offline (see constraints) |
| `CLAUDE.md` | ADR 0018 bullet: the overlay default is now user-settable |
| `.agentic/specs/completed/plan-user-configurable-model-providers.md:219` | stale claim: "not yet exposed via the API/frontend" |

No Bicep, no new secret, no new Cosmos container, no state migration (the field already exists
and is already persisted).

## Context & constraints

**Everything server-side below the endpoint already works — do not rebuild it.**

- `IUserProviderConfigGrain.SetDefaultAsync(string?)` exists, blank-to-null normalises, bumps
  `Version`, and is already unit-tested (`src/Agent42.Agents/UserProviderConfigGrain.cs`,
  `tests/Agent42.Tests/UserProviderConfigGrainTests.cs`). `SaveAsync`/`DeleteAsync` deliberately
  leave `Default` untouched, so provider CRUD already preserves it. **The new endpoint is a
  one-line pass-through to this method.**
- `UserProviderResolver.EffectiveDefault` already prefers the overlay default and already
  guards it with `NamesBuiltProvider` — a default naming a provider the user later deleted
  falls through to the next candidate on its own
  (`src/Agent42.Backend/UserProviderResolver.cs`). **No validation of the submitted id is
  needed server-side**, and none should be added: validating at write time would reject an id
  that is temporarily unresolvable and duplicate the resolver's rule in a second place.
- `UserModelCatalogProvider` builds the catalog **fresh per call** with no cache
  (`src/Agent42.Backend/UserModelCatalogProvider.cs`), so a changed default is visible on the
  next `GET /api/models` with no invalidation step.

**Offline (DevHost/tests) cannot demonstrate half of this — do not chase it as a bug.**
`tools/Agent42.DevHost/Program.cs` registers `SingleUserModelCatalogProvider` over a
`StaticModelCatalog` (three providers: `foundry`, `github`, `images`), and
`UserModelCatalogProvider.cs` returns that same fixed catalog for every user, ignoring the
overlay. `ProvidersApiTests` registers the same single-user adapter. So offline: the PUT
persists and `GET /api/providers` echoes it, but `/api/models`'s `default` stays the
**hardcoded** one. This is the known ADR 0018 shape — CLAUDE.md already records that
"overlay→picker reflection is only wired in prod". Consequences for this plan:
- The **saved default → composer picker** chain is prod-only and is covered by a unit test
  (AC9) against `UserModelCatalogTests`' `FakeResolver`, not by an integration test.
- The **New-chat reset** (AC8) *is* fully observable offline — it re-pins to whatever
  `/api/models` reports, static or not.

**A default change also retargets conversations that have not yet bound a model.**
`src/Agent42.Agents/AgentGrain.cs` binds a not-yet-pinned conversation to
`_chatClientFactory.DefaultModelId` (three call sites). So changing the preference also moves
unbound conversations, ADR 0022 `/api/agent-run` runs, and pending reminders onto the new
model. This is bounded (the first turn calls `RebindModelAsync` and pins it) and is the
desirable reading of "default", but it is a **wider blast radius than the composer** — it is
recorded here so it isn't discovered mid-execution. It needs no code change.

**Decisions made in the interview / during investigation:**

- **Placement: the providers tab**, above the provider list — chosen by the user. It is also
  where the value actually lives (the provider overlay grain), so the tab matches the storage.
- **Reuse `UserProviderConfig.Default`; do NOT add a field to `UserProfile`.** Rejected
  alternative: a `DefaultModel` on `UserProfile` alongside `Theme`/`EnabledTools`. It would
  make the preferences save flow uniform, but it creates a **second** source of truth for a
  value the resolver already reads from the overlay — two fields that could disagree, with the
  resolver silently winning. The tab placement decision above resolves the cosmetic mismatch.
- **`PUT /api/default-model`, not `PUT /api/providers/default`.** A provider key is
  **user-chosen**, so `/api/providers/{key}` can legitimately be asked to address a provider
  literally keyed `default` — and ASP.NET Core routing gives the literal segment precedence
  over the parameter regardless of mapping order, which would make that provider silently
  un-editable. A distinct path has no collision surface. GET stays on `/api/providers` (the
  existing `ProvidersResponse` already carries `Default`); the read/write asymmetry is
  accepted deliberately.
- **"New chat" resets the picker to the default** — chosen by the user. Today
  `startNewConversation()` (app.js) never touches `modelSelect`, so a new chat inherits
  whatever the previously-opened conversation was pinned to and the preference would appear to
  work only on a page reload.
- **No new ADR.** This wires a field ADR 0018 already designed, through a mechanism ADR 0018
  already specified, and changes no resolution order or egress property. If the executor finds
  it is changing any of those, stop and run `/decide` instead.

**Governing docs to read before touching this** (paths, not summaries):

- `.agentic/decisions/0018-user-configurable-model-providers.md` — the overlay model.
- `.agentic/decisions/0019-remove-baseline-chat-providers.md` — why `_baselineDefault` is `""`
  in prod, and why the picker/composer can legitimately be empty.
- `.agentic/gotchas.md` — "A GitHub model id contains a `/`, so the provider separator is `:`
  … split on the FIRST colon". The stored default is a **qualified id** and must be passed
  around opaquely; never parse it past the first colon, never normalise its case.
- `.agentic/gotchas.md` — the ADR 0018 overlay gotcha (overlay is best-effort, baseline wins,
  secrets never echoed). Nothing here echoes a secret, but the endpoint sits in that family.

**Frontend shapes to copy, not invent** (`src/Agent42.Backend/wwwroot/app.js`):

- `fillModelOptions(select)` — fills any `<select>` with the catalog, grouped by provider.
- `selectModel(select, id)` — shared append-if-missing helper; an id no longer in the catalog
  lands under an "Unavailable" optgroup. **Reuse it for the new select**: a stale default then
  shows as visibly unavailable instead of silently resetting, and
  `ModelPickerAssetsTests.BothPickers_ShareTheSameSelectHelper` pins the no-duplicate rule
  (`duplication_pct_max: 3`).
- `loadProviders()` / `renderProviders()` — the providers tab's load-and-render pair.
- `saveProvider()` already calls `loadModels()` after a successful save, so adding a provider
  refreshes `modelsData` before the default select is next filled. Fill the select from
  `modelsData`, never from a second fetch.

**Ordering hazards — two of them:**
1. The new select must be filled **after** `loadModels()` has populated `modelsData` — filling
   it on dialog-open from an empty `modelsData` yields a blank picker with no error. The safe
   point is where `loadProviders()` is invoked on preferences-dialog open.
2. `saveProvider()` calls `loadProviders()`, but **`deleteProvider()` does not** — it filters
   `userProviders` locally, then calls `renderProviders()` + `loadModels()`. So hooking the
   fill into `loadProviders()` alone leaves the default select listing a deleted provider's
   models until the dialog is reopened. Refill from `deleteProvider()` too.

## Steps

Steps 1–2 are backend, 3–4 frontend; **3 may run in parallel with 1–2** (disjoint files), but
step 5's tests need both. Steps 6–7 are docs.

1. **Add `PUT /api/default-model`.** In `AgentApi.cs`, next to the providers block: read
   `DefaultModelRequest(string? Default)`, reject an id longer than **200 chars** with a 400
   (`text/plain`, matching `ValidateProvider`'s error style), otherwise
   `await grains.GetGrain<IUserProviderConfigGrain>(http.GetUserId()).SetDefaultAsync(request.Default)`
   and return `Results.NoContent()`. Add `DefaultModelRequest` beside `ProviderUpsertRequest`.
   Comment the *why* of the path choice (collision with a user-chosen provider key).

   The 200 is a **fresh choice, not a precedent** — do not describe it as matching
   `UserProfileGrain`, which *clamps* (at 35, in the grain) rather than rejecting. Rationale to
   put in the comment: a qualified id is `provider:model` and both halves are bounded by their
   sources, so 200 is far above any real id; the cap exists solely to keep unbounded text out
   of a persisted grain state, **not** to validate the id — validity is the resolver's
   read-time job (see constraints).
   *Verify:* `dotnet build Agent42.slnx`.
2. **Endpoint tests** in `tests/Agent42.Tests/ProvidersApiTests.cs` (existing fixture — real
   Orleans `TestCluster` + `TestServer`, `X-MS-CLIENT-PRINCIPAL-ID` for identity). See the AC
   table for the facts.
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~ProvidersApiTests`.
3. **Markup** in `index.html`: inside the providers tab panel, above the provider list, a
   labelled `<select id="defaultModelSelect">` whose first `<option value="">` reads as
   "first available", plus a short hint that it applies to new chats only. Match the
   surrounding field markup/classes; add CSS only if nothing existing fits.
   *Verify:* visual — `dotnet run --project tools/Agent42.DevHost`, open Preferences →
   model providers (DevHost's static catalog spans three providers, so the grouped options are
   meaningful offline).
4. **Wire it** in `app.js`:
   - fill the select via `fillModelOptions` + the blank option, then
     `selectModel(defaultModelSelect, <default>)`, at the point `loadProviders()` runs (see the
     ordering hazards above, including the `deleteProvider()` one); source the current value
     from the `GET /api/providers` response's `default` field, which is already fetched there
     and currently discarded.
   - on `change`, `PUT /api/default-model` with the selected value (`""` → clears), then
     refresh `defaultModel` **without re-pinning the composer**. `loadModels()` currently ends
     with `if (defaultModel) setModel(defaultModel);` — calling it here as-is would retarget
     the **currently open** conversation, because `send()` transmits `modelSelect.value` (that
     is explicitly out of scope). Give it an opt-out — `loadModels({ pin = true } = {})` — and
     call `loadModels({ pin: false })` from the change handler. Keep the single fetch path;
     do not clone it. Comment the *why* on the parameter.
   - in `startNewConversation()`, `if (defaultModel) setModel(defaultModel);` — placed with the
     other new-chat resets, next to the ADR 0021 `chatTools` line, and commented in that style.
   *Verify:* step 5's tests + manual DevHost check of AC8 (open an existing chat pinned to
   another model, press New chat → picker returns to the default). Do **not** try to verify the
   saved-default → picker chain offline; it is prod-only (see constraints) and AC9 covers it.
5. **Frontend source-text tests** in `tests/Agent42.Tests/ModelPickerAssetsTests.cs` (the repo
   has no JS runner by design — these prove the source *says* the right thing; the file's own
   class comment states this and the new facts must not overclaim).
   *Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~ModelPickerAssetsTests`.
6. **Docs:** amend the ADR 0018 bullet in `CLAUDE.md` to say the overlay default is now
   user-settable from Preferences → model providers via `PUT /api/default-model`, and that new
   chats reset to it. Fix the stale claim at
   `.agentic/specs/completed/plan-user-configurable-model-providers.md:219`
   ("not yet exposed via the API/frontend") — that file is a completed spec, so mark the line
   as superseded by this plan rather than rewriting its history.
   *Verify:* `node .agentic/scripts/check-doc-paths.mjs` — the doc-path gate is a **node
   script** run by `.github/workflows/pr-checks.yml`, **not** a dotnet test; a `dotnet test
   --filter` for it matches nothing and exits green. Then re-read the edited paragraphs.
7. **Full gate:** `dotnet test Agent42.slnx` and `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | `PUT /api/default-model` persists the id; a following `GET /api/providers` returns it as `default` | `PutDefaultModel_PersistsAndIsReturnedByGet` | `tests/Agent42.Tests/ProvidersApiTests.cs` |
| AC2 | A blank/null body clears the default back to null | `PutDefaultModel_Blank_ClearsTheDefault` | `tests/Agent42.Tests/ProvidersApiTests.cs` |
| AC3 | The default is **per user** — one user's default is invisible to another | `PutDefaultModel_IsPerUser` | `tests/Agent42.Tests/ProvidersApiTests.cs` |
| AC4 | Adding, editing and deleting a provider all **preserve** the default | `ProviderCrud_PreservesTheDefault` | `tests/Agent42.Tests/ProvidersApiTests.cs` |
| AC5 | An over-long id is rejected with 400, and a following `GET /api/providers` shows the stored default unchanged | `PutDefaultModel_TooLong_Rejected_AndLeavesStoredValue` | `tests/Agent42.Tests/ProvidersApiTests.cs` |
| AC6 | A default naming a **deleted** provider does not brick the picker — the catalog still resolves a usable default | **already covered** — cite `OverlayDefault_NamingAMissingProvider_FallsBackToBaselineDefault`, do not duplicate | `tests/Agent42.Tests/UserProviderResolverTests.cs:92` |
| AC7 | The preferences select PUTs to the endpoint and reuses the shared select helper (no second picker implementation) | `DefaultModelPicker_PutsToTheEndpoint_AndSharesTheSelectHelper` | `tests/Agent42.Tests/ModelPickerAssetsTests.cs` |
| AC8 | "New chat" resets the composer picker to the default | `StartNewConversation_ResetsPickerToTheDefault` | `tests/Agent42.Tests/ModelPickerAssetsTests.cs` |
| AC9 | **The chain that makes the feature real:** an overlay default surfaces as `/api/models`' `default` | `OverlayDefault_SurfacesAsTheCatalogDefault` (extend the existing `FakeResolver` — this file already asserts on `result.Default`) | `tests/Agent42.Tests/UserModelCatalogTests.cs` |
| AC10 | Changing the preference does **not** retarget the currently open conversation (the `pin: false` path) | `DefaultModelChange_DoesNotRepinTheComposer` | `tests/Agent42.Tests/ModelPickerAssetsTests.cs` |

**AC1 is deliberately thin** — `GET /api/providers` is a pure grain echo, so AC1–AC5 prove the
endpoint contract only. AC9 is the one that proves the feature works, and it cannot live in
`ProvidersApiTests` (that fixture registers `SingleUserModelCatalogProvider`).

AC7, AC8 and AC10 are **source-text** assertions — `ModelPickerAssetsTests`' own class comment
says the repo has no JS runner by design and that these prove the code *says* the right thing,
not that a browser behaves. Do not word the new tests as if they prove behaviour.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`
- `dependencies.vulnerable_packages: 0` (hard block) — this plan adds **no** package
- Branch: `feature/*` + MR; `main` is protected (`branches.protected`)
- CI hard-blocks on: build, tests, `dotnet format`, vulnerable packages, doc-path integrity

## Out of scope

- Any change to `EffectiveDefault`'s resolution order (ADR 0018/0019) — read only.
- A per-**agent** default model (agents already snapshot a model, ADR 0003) and the agent
  editor's own model picker.
- Changing an **existing** conversation's model, or migrating conversations pinned to a model
  the user has since removed.
- Re-introducing an operator-level default (ADR 0019 removed it deliberately).
- Server-side validation that the default names a currently-resolvable model — the resolver's
  read-time guard is the single place that rule lives.
- Any new ADR or gotcha entry unless step 6 uncovers a genuine contradiction.

## Divergence from the plan (recorded at execution)

Executed 2026-07-20 on `feature/user-default-model`. HEAD matched `planned-against` (`2cf0cc0`) — no drift.
All 10 ACs implemented as mapped. Four deliberate departures:

1. **Hazard 2 closed by an idempotent repaint, not by "refill from `deleteProvider()` too".** The plan
   prescribed adding a refill call to `deleteProvider()`. Instead a single `syncDefaultModelSelect()` is
   hooked into **both** loaders (`loadProviders()` owns the stored value, `loadModels()` owns the catalog),
   so every call combination lands correct — preferences-open (`loadProviders` alone), `deleteProvider`
   (`loadModels` alone) and `saveProvider` (both). Strictly more robust and one call site fewer.
2. **A second state variable, `userDefaultModel`, was needed.** The plan spoke of "the default" as one
   value; there are two. `defaultModel` is the server-**resolved** default (`/api/models`) that the composer
   and new-chat reset use; `userDefaultModel` is the user's **stored preference** (`GET /api/providers`) that
   the preferences select shows. They differ precisely when the stored id no longer resolves — which is the
   case the "Unavailable" optgroup exists to make visible.
3. **`persistDefaultModel()` snaps the select back on a failed PUT.** Not in the plan (critic finding 2).
   With no Save button on this tab there is no other failure signal, and leaving the rejected pick on screen
   reads as saved.
4. **The blank "First available" option is built in JS, not declared in markup.** A static `<option>` in
   `index.html` is dead: `fillModelOptions()` starts with `select.innerHTML = ""` and wipes it on first
   paint (critic finding 7).

Doc debt paid in the same diff, beyond step 6's two targets: `ModelPickerAssetsTests`' class comment claimed
DevHost's catalog "spans two providers" (it is three) and that there are two grouped pickers (now three).

**No** `/decide` or `/gotcha` was raised: the plan's "no new ADR" test held — no resolution order, egress
property or persisted shape changed, and the two ordering hazards were already written down here rather than
being new traps. The 200-char cap and the `PUT /api/default-model` path choice are commented at their call
sites, where the next reader meets them.

## Review

Critic verdict on the first draft: **revise before execution** — 3 blocking, 5 should-fix.
All 8 accepted and folded in; none rejected.

| # | Finding | Disposition |
|---|---|---|
| 1 | Step 6's `dotnet test --filter ~DocPath` matches nothing and exits green — the doc-path gate is `node .agentic/scripts/check-doc-paths.mjs` | **Fixed** — step 6 now names the node script and warns about the false pass |
| 2 | Step 4's post-PUT `loadModels()` re-pins `modelSelect`, silently retargeting the **open** conversation — contradicting this plan's own "out of scope" | **Fixed** — `loadModels({ pin: false })` opt-out, plus AC10 to pin it |
| 3 | DevHost/`ProvidersApiTests` use `SingleUserModelCatalogProvider`, so the saved-default→picker chain is not observable offline; "spans two providers" was wrong (three) | **Fixed** — new constraints subsection, corrected count, step 4 verification scoped to what DevHost can actually show |
| 4 | No AC covered overlay `Default` → `/api/models` `default`; AC1 only echoes the grain | **Fixed** — AC9 added against `UserModelCatalogTests`' `FakeResolver`; that file added to affected areas |
| 5 | The 200-char cap cited `UserProfileGrain` as precedent — which *clamps at 35 in the grain*, the opposite posture | **Fixed** — false precedent removed; 200 restated as a fresh choice with its rationale |
| 6 | AC5's "does not reach the grain" is unobservable in the fixture (no grain spy) | **Fixed** — reworded to the observable fact |
| 7 | `deleteProvider()` doesn't call `loadProviders()`, so the new select goes stale after a delete | **Fixed** — second ordering hazard added |
| 8 | A default change also retargets unbound conversations, `/api/agent-run` runs and pending reminders (`AgentGrain` binds to `DefaultModelId`) | **Fixed** — recorded in constraints; no code change, bounded by `RebindModelAsync` |

The critic separately **verified** (no action): the route-collision argument for
`PUT /api/default-model`; that `SetDefaultAsync` exists with no API caller;
`UserModelCatalogProvider`'s lack of a cache; `startNewConversation()` not touching
`modelSelect`; `selectModel`'s append-if-missing; `ProvidersResponse.Default` being discarded
by `loadProviders()`; AC6 already covered; no new `MapAgentApi`-host registration ripple; gate
thresholds matching `quality-gates.yaml`; and **no contradiction with ADRs 0013/0016/0017/0018/
0019/0021** or Easy Auth.
