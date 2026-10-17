planned-against: main @ d4396d0

# Plan: per-task tool override for the agent fleet

## Overview

Today a fleet task's agents can use `web_search`/`fetch_web_page` and the user's MCP tools according to the user's
DEFAULT chat tool selection (`UserProfile.EnabledTools`, ADR 0021). Before this change the user can't narrow or widen
that for one fleet task without changing their chat default. The user wants the fleet to **keep the chat default as
its default**, and to be able to **override it per fleet task** from the fleet composer.

## Affected areas

- `src/Agent42.Agents/Fleet/FleetTaskRecord.cs`: a new `EnabledTools` property (Blob JSON, not grain state).
- `src/Agent42.Backend/Fleet/AgentApi.Fleet.cs`: `FleetSubmitRequest` gains `EnabledTools`, and `SubmitFleetTask` passes it on.
- `src/Agent42.Backend/Fleet/FleetTaskService.cs`: `FleetSubmit` gains `EnabledTools`, and `SubmitAsync` sanitizes and stores it.
- `src/Agent42.Backend/Fleet/FleetTaskService.Run.cs`: `BuildRunAsync` passes the record's selection to the catalog.
- `src/Agent42.Backend/Fleet/FleetToolCatalog.cs`: `BuildAsync` takes an override (`override ?? profile ?? all`).
- `src/Agent42.Backend/wwwroot/fleet/fleet.js`, `fleet/index.html` (if markup is needed), `fleet/fleet.css`: the tools picker in the composer.
- Tests: `tests/Agent42.Tests/FleetToolCatalogTests.cs`, `FleetTaskServiceTests.cs`, `FleetApiTests.cs`, `FleetStoreTests.cs`, `FleetPageAssetsTests.cs`.
- Docs: a new ADR amending ADR 0097 decision 6, the CLAUDE.md feature map row for the agent fleet, and `.agentic/gotchas.md` if a trap turns up.

## Context & constraints

- **User decisions (interview):**
  - There is no separate fleet default and no Settings section for it. The fleet reuses the chat default.
  - Each task can override that default from the fleet composer.
  - Copilot fleet agents are unaffected (they have no Agent42 tools, see `FleetCopilotAgentFactory.BuildSessionConfig`).
- **Resolution order:** `task override ?? profile.EnabledTools ?? all`. A task submitted without an override stores
  `EnabledTools = null` and resolves the chat default **when the run starts**, which is today's behaviour (`FleetToolCatalog.BuildAsync`).
- **Only send an override the user actually made.** The composer sends `enabledTools` only when the user changed a
  checkbox. An untouched picker omits it, so the task follows the chat default. This keeps ADR 0021 `null == all`
  semantics intact: a newly added MCP server is still granted to a task that didn't override. The reason is gotcha
  "Selectable tools: `null == all`…" (`.agentic/gotchas.md`, around line 1639). Never send `[]` to mean "default",
  because an empty list means "none".
- **If `/api/tools` failed, never send an override.** This mirrors `profile.js` `saveProfile`: an empty catalog must not
  turn into "no tools".
- **Sanitize on the server.** Use `ToolCatalog.Sanitize` (`src/Agent42.Agents/Tools/ToolCatalog.cs`) in
  `FleetTaskService.SubmitAsync`. It drops unknown static keys, so a client can't invent keys. Keys the fleet doesn't
  use (for example `memory` or `skill:x`) are harmless: `FleetToolCatalog.Build` only consults `web_search` and the MCP
  keys. They are still filtered out on the client.
- **Picker scope:**
  - The picker shows only what the fleet can use: the `web_search` descriptor (in the fleet it gates both `web_search`
    and `fetch_web_page`, see the `ToolNameToKey` map in ToolCatalog) and the MCP server rows (`mcp:{id}`) plus their
    tool rows (`mcp:{id}:{tool}`, `ParentKey`) from `GET /api/tools`.
  - Skills, memory, documents and the rest are not shown, because the fleet has none of them (ADR 0097 decision 6).
  - The workspace tools and `delegate_to_team` are always on and are not shown.
- **Initial checkbox state** = the chat default, i.e. `profile.enabledTools ?? <all keys>` from `GET /api/profile`.
  A key is checked when the selection grants it, using the same semantics as `ToolCatalog.IsMcpToolEnabled`: a tool row
  is on if the selection has the bare `mcp`, its `mcp:{id}`, or its own key. A server row is on if the selection has
  `mcp` or `mcp:{id}`.
- **Reading the picker back** (only when dirty):
  - `web_search` is included if it is checked.
  - For each server, include `mcp:{id}` when the server is checked and either all of its rendered tools are checked or
    it has no tool rows. Otherwise include only the checked `mcp:{id}:{tool}` keys.
  - Unchecking the server unchecks its tools, and checking a tool row of an unchecked server is allowed (it grants
    that tool alone). This mirrors the main app picker's parent/child behaviour in `wwwroot/js/tools.js`
    `renderToolChecks`/`readToolChecks`. fleet.js is a standalone IIFE outside the ES-module graph (ADR 0041,
    `sw.js` skips `/fleet`), so it **cannot import tools.js**. Write a small local picker in fleet.js's own `h()` style
    rather than turning fleet.js into a module.
  - Because fleet.js renders only a subset of keys (`web_search` and `mcp*`), a dirty override **intentionally drops**
    every other key. This is safe because the fleet ignores them, and the stored override is per task, never written
    back to the profile.
  - This is unlike the main picker's carry-forward of unrendered per-tool keys (`.agentic/gotchas.md` around line
    1721). That backstop exists to protect a *persisted default*, which this picker never writes.
  - A server whose tools are all checked, or that is still in the cold-start window (a server row with no tool rows
    yet), is sent as `mcp:{id}`. It therefore also grants tools the server adds later, consistent with ADR 0026.
- **Follow-up and "Run again":**
  - "Run again" re-submits the record's `enabledTools` as is (null stays null).
  - The follow-up composer pre-selects the followed-up task's override when it has one, and is marked dirty so it is
    sent again. Otherwise it starts from the chat default.
  - The task detail summary (`taskSummary`) shows "Tools: your defaults" or "Tools: N selected" so the override is
    visible afterwards.
- **No sw.js `CACHE` bump.** Cross-cutting rule 3 covers `wwwroot/js/`, and the fleet assets are outside SHELL
  (`sw.js` line ~101).
- **The task record is our own Blob JSON** (`FleetStore`), not Orleans state, so the new property needs no `[Id]`. It
  must be optional with a null default so records stored before this change deserialize. Prove that in `FleetStoreTests`.
- **ADR:** ADR 0097 decision 6 says the MCP tools are "gated by their DEFAULT tool selection". This plan changes that
  to "the task's override, else the default". Record it with `/decide` as a new ADR (next free number, 0099 at
  planning time) that **amends** 0097 decision 6, not one that supersedes it. Add the number to the CLAUDE.md feature
  map's agent fleet row.
- **Egress:** this adds no new egress. It only narrows or widens tools that are already gated (cross-cutting rule 13).

## Steps

1. **Backend: record + submit.**
   - Files: `FleetTaskRecord.cs`, `FleetTaskService.cs` (`FleetSubmit`, `SubmitAsync`), `AgentApi.Fleet.cs` (`FleetSubmitRequest`, `SubmitFleetTask`).
   - Add `IReadOnlyList<string>? EnabledTools` (default null) to the record, `FleetSubmit` and `FleetSubmitRequest`.
     `SubmitAsync` stores `ToolCatalog.Sanitize(request.EnabledTools)`, so null stays null.
   - Keep `SubmitAsync` under S3776 15. If needed, extract the record construction.
   - Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetTaskServiceTests|FullyQualifiedName~FleetApiTests|FullyQualifiedName~FleetStoreTests"`.
2. **Backend: catalog resolution.**
   - Files: `FleetToolCatalog.cs`, `FleetTaskService.Run.cs`.
   - `BuildAsync(userId, request, selectionOverride, interaction, ct)` uses `selectionOverride ?? profile.EnabledTools`.
     When an override is given it skips the profile read. `BuildRunAsync` passes `record.EnabledTools`.
   - Update the class doc comment, which currently says "honours the user's DEFAULT tool selection".
   - Verify: `--filter FullyQualifiedName~FleetToolCatalogTests`.
3. **Frontend: composer picker.**
   - Files: `wwwroot/fleet/fleet.js`, `fleet.css`, and `index.html` only if static markup is needed.
   - Load `/api/tools` and `/api/profile` with the other startup fetches.
   - Add a collapsible "Tools" control to the composer (next to the model and token fields) showing "Your defaults" or
     "N selected", with a dirty flag. Send `enabledTools` from `submitTask` only when dirty and the catalog loaded.
   - `submitTask(text, followUpOf)` gets a third parameter, `enabledTools`. When it is passed, even as null, it is
     sent as is and picker state is ignored. "Run again" (`fleet.js` `runAgainCard`) passes `task.enabledTools`.
     Without this, Run again would silently fall back to the chat default.
   - Wire "Run again" and the follow-up pre-selection, and add the tools line to `taskSummary`.
   - Untrusted names (MCP tool names) are set as text only, following the page's existing rule (fleet.js header comment).
   - Verify: `--filter FullyQualifiedName~FleetPageAssetsTests`. Then run the app manually (`dotnet run --project tools/Agent42.DevHost`, `/fleet/`):
     - An untouched picker sends no `enabledTools`.
     - Unticking web search sends `[ ...mcp keys ]` without `web_search`.
     - The task summary shows the override.
4. **Docs.** `/decide` the amending ADR, add it to the CLAUDE.md feature map row, and run `/sync-docs` over the diff.
   Steps 1 and 2 are sequential (2 consumes the record field). Step 3 depends only on the API shape from step 1 and can
   run in parallel with step 2 in a worktree.

## Acceptance criteria → tests

| AC | Test | File |
|----|------|------|
| A task submitted without `EnabledTools` stores null | `Submit_WithoutOverride_StoresNull` | `FleetTaskServiceTests.cs` |
| **End-to-end wiring:** the record's override reaches the run's tools (record → `BuildRunAsync` → catalog). With no override the run's tools follow the profile default. Inject a real `FleetToolCatalog` (a fake `IGrainFactory` whose `IUserProfileGrain` returns a chosen `EnabledTools`, plus fake `IWebSearch`/`IWebPageFetcher`) into the service under test, and capture `FleetRun.Tools`, by tool name, through a test seam or the planner's prompt (`AgentFunctions`). The `Service()` helper currently passes no catalog (`FleetTaskServiceTests.cs:29-37`), so extend it with an optional one | `Run_UsesTaskOverride_ElseProfileDefault` (theory: null → profile; override → override) | `FleetTaskServiceTests.cs` |
| A task with an override stores the sanitized list (unknown keys dropped, null-vs-[] preserved) | `Submit_WithOverride_StoresSanitizedSelection` | `FleetTaskServiceTests.cs` |
| An override beats the profile default: `web_search` off in the override → no `web_search`/`fetch_web_page`, even though the profile allows it | `BuildAsync_Override_BeatsProfileDefault` | `FleetToolCatalogTests.cs` |
| An override can grant a single MCP tool (`mcp:{id}:{tool}`) without its siblings (via `Build(...)` with `McpServerTools`, which needs no `IUserMcpToolProvider`) | `Build_PerToolMcpKey_GrantsOnlyThatTool` | `FleetToolCatalogTests.cs` |
| `BuildAsync` with an override never reads the profile (the existing `Catalog()` helper passes `grains = null!`, so the test proves it) | `BuildAsync_Override_SkipsProfileRead` | `FleetToolCatalogTests.cs` |
| An empty-list override means no selectable tools (not "default") | `BuildAsync_EmptyOverride_IsNone` | `FleetToolCatalogTests.cs` |
| `POST /api/fleet/tasks` accepts `enabledTools` and `GET /api/fleet/tasks/{id}` returns it | `SubmitTask_EnabledTools_RoundTrips` | `FleetApiTests.cs` |
| A record stored before this change (no `EnabledTools`) still loads, with null | `LegacyRecord_WithoutEnabledTools_LoadsAsNull` | `FleetStoreTests.cs` |
| The fleet page sends `enabledTools` only when the picker is dirty and the catalog loaded, filters to `web_search` and `mcp:` keys, sets tool names as text, and Run again passes the record's `enabledTools` to `submitTask` | `FleetJs_ToolOverride_SentOnlyWhenChanged`, `FleetJs_RunAgain_ResendsTaskOverride` (source-text guards, same style as the existing `FleetJs_*` tests) | `FleetPageAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at planning time)

- [ ] `coverage_new_code_pct_min: 80` (`node .agentic/scripts/check-new-code-coverage.mjs`)
- [ ] `cognitive_complexity_max: 15` (S3776, a build error)
- [ ] `vulnerable_packages: 0` (no new packages are planned)
- [ ] `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`

## Out of scope

- A separate fleet default or a Settings section for it (the user rejected this).
- Per-agent tool choice by the user. The planner still picks each agent's tools from the task's pool, and the
  approval card already shows them.
- Tools for Copilot fleet agents (built-in tools only, per ADR 0097 decision 7).
- Adding tools the fleet doesn't have today (skills, memory, documents and so on).
- Changing the main-app picker (`wwwroot/js/tools.js`).

## Review

A critic subagent reviewed this plan on 2026-10-17 and found no ADR contradiction. Its code claims checked out. How
each finding was handled:

1. **(Medium) No test covered record → `BuildRunAsync` → catalog.** Accepted. The AC was split into a storage-only AC
   and an end-to-end AC, `Run_UsesTaskOverride_ElseProfileDefault`, which injects a real catalog. The per-tool MCP
   test now uses `Build(...)`, which needs no MCP provider. A test was added to prove the override path skips the
   profile read.
2. **(Low) Frontend behaviours had only one guard, and Run again had no override parameter.** Accepted. `submitTask`
   gets an explicit `enabledTools` parameter, plus a Run-again source guard. The follow-up pre-selection and the
   summary line stay manual checks in step 3: they are presentation, and a source guard would only restate the code.
3. **(Low) It was unclear what "untouched" means when the catalog changes.** Accepted. `mcp:{id}` is now stated for
   all-checked and cold-start servers.
4. **(Low) The plan didn't say how it relates to the carry-forward gotcha.** Accepted. The plan now says why
   dropping unrendered keys is safe here.
5. **(Info) Gate thresholds.** Copied from `.agentic/quality-gates.yaml` at planning time: 80 and 15.

## Implementation divergences (2026-10-17)

- **`Build_PerToolMcpKey_GrantsOnlyThatTool` was not added.** The existing `McpTools_GatedBySelection`
  (`FleetToolCatalogTests.cs`) already calls `Build([McpToolKey("s1","alpha")], …)` and asserts only `alpha`, which is
  exactly this AC. A second test would only have repeated it.
- **The fake `IGrainFactory` is a `DispatchProxy` helper** in its own file, `tests/Agent42.Tests/FakeProfileGrainFactory.cs`.
  It also counts profile reads. `BuildAsync_EmptyOverride_IsNone` and `Run_UsesTaskOverride_ElseProfileDefault` use
  that count to prove the override path skips the profile.
- **`Run_UsesTaskOverride_ElseProfileDefault` observes the run's tools through the planner's system prompt.** A
  `DelegatingChatClient` in the test records every system prompt, and the test checks the planner prompt's `- web_search:` /
  `- fetch_web_page:` lines. The plan named this route as one option. No production test seam was added.
- **The tools line also shows on the root team's header** (`Level 0 · … · Tools: …`), not only in `taskSummary`. In
  the DevHost run, `taskSummary` is hidden whenever a team view is shown, so the override was invisible on any task that
  had a team.
- **Manual check (DevHost + headless Chromium, `/fleet/`).** The default port 61325 was blocked locally, so it ran on
  `--urls http://localhost:5198`.
  - An untouched picker posted no `enabledTools`.
  - Unticking Web search and one MCP tool posted
    `["mcp:offline:demo_search","mcp:offline:demo_find_links","mcp:offline-cold","mcp:<id>"]`. The cold-start and
    all-ticked servers went as `mcp:{id}`.
  - The picker returned to "your defaults" after the submit.
  - The header showed "Tools: 4 selected" or "Tools: your defaults".
  - Run again re-posted the same list.
  - **Not exercised:** the follow-up pre-selection. DevHost's canned LLM never finishes a task as `done`, so no task
    could be followed up.
- **Critic findings (diff review).**
  - Fixed: `docs/features/agent-fleet.md` still described the DEFAULT-only selection, and the plan's Affected areas had
    missed that file.
  - Fixed: unticking a server's last ticked tool now unticks the server too, so a ticked server box never sends nothing.
  - Accepted as cosmetic: "N selected" counts keys, so a whole server counts as 1.
