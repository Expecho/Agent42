# Plan — ToolForge MCP, slice 1: prove the architecture and measure the caches

planned-against: main @ a2448ed

> **Constraint labels in this file are `K1…K13`, deliberately NOT `C1…`.** The design note
> `.agentic/specs/plan-agent-authored-tools.md` uses `C1…C13`, and this plan cites both. Every
> bare `C<n>` here means **the design note's**; every `K<n>` means this plan's.

## Overview

**Goal:** build `ToolForge.Mcp` in the sibling repo `Expecho/mcpserver` (cloned at
`Z:\Sources\mcpserver`) with an authoring surface, a promoting runtime surface, a per-caller-shaped
persisted registry, and a **deliberately stubbed executor that runs nothing**. Then wire it into
Agent42 as two MCP servers and **measure** the design note's C1 and C2.

**Why now:** C1 and C2 are verified **by reading** — `.agentic/gotchas.md` point (6) names the call
sites — but have **never been observed end to end**. If they are wrong, every decision downstream
changes, and that should be discovered with a stub rather than after a sandbox is built.

**Slice 2 — the real Copilot-SDK containerized executor and its permission policy — is out of
scope.** Slice 1 may be publicly deployed *only because* it executes nothing (K9).

## Decisions taken in the planning interview

- **D1 — Pure stub executor.** `run_tool` returns a canned result naming the tool and arguments and
  stating plainly that nothing ran. Tools written in slice 1 are throwaway fixtures.
- **D2 — Per-caller registry — REVISED after review; see K10.** The user chose "per-caller from day
  one" on the stated premise that it was free. The review disproved that premise: in `McpHost`'s
  API-key mode there is exactly one valid credential and no principal is plumbed to a tool. **What
  is built instead is the per-caller *storage shape* with a single caller id** — which delivers the
  actual intent (no migration later) without inventing an auth mechanism this slice does not need.
- **D3 — Deploy behind the gateway**, chosen over local-only with the trade stated. Safe here only
  because of D1 (K9).
- **D4 — Base is `main` after PR #121 merged** (`a2448ed`).
- **D5 — Minimal promotion is IN scope — added after review.** Not a preference: the review proved
  the measurement is impossible without it (see Review, Blocker 1). It does **not** weaken D1 —
  promotion is about *advertising* a tool, not running one.

## Affected areas

### Repo A — `Z:\Sources\mcpserver` (new code; separate git repo, no `.agentic/` process)

Verified against the tree at `aed18d8`.

**New:**
- `src/ToolForge.Core/ToolForge.Core.csproj` + `ToolRecord.cs`, `ToolRegistry.cs`,
  `FileToolStore.cs`, `CallerId.cs`, `IToolExecutor.cs`, `StubToolExecutor.cs`, `ForgeLimits.cs`.
- `src/ToolForge.Mcp/ToolForge.Mcp.csproj` + `Program.cs`, `ToolForgeAuthoringTools.cs`,
  `RuntimeToolPublisher.cs`.
- `tests/ToolForge.Mcp.Tests/` — `ToolForge.Mcp.Tests.csproj`, `GlobalUsings.cs`,
  `ToolForgeServer.cs`, `McpCall.cs` (copied — K5), and the test classes in the AC table.

**Modified — every one of these is load-bearing; four of them break EXISTING green things if missed:**
- `Dockerfile` — the hard-coded csproj `COPY` list at `:13-18` (**omit this and the existing
  gateway image build breaks too**, once the gateway references ToolForge), and the
  `ASPNETCORE_URLS` / `HEALTHCHECK` interaction in K3.
- `src/Mcp.Gateway/Program.cs` — two `McpHost.Build` calls, two `BackendRoute` entries, and two
  `RequireHost` calls (**`RequireHost` lives here at `:36-41`, NOT in `GatewayOptions.cs`** — K2).
- `docker-compose.yml` — two host env vars, the forge key, a **named** volume (K4).
- `.env.example` — the two new host vars.
- `.github/workflows/ci.yml` — **the existing gateway job at `:126-136` starts the image with only
  the scraper and calendar host vars and will FAIL** once two more are required; plus a ToolForge
  image smoke job following the per-server pattern.
- `McpServer.slnx`, `README.md`.

### Repo B — `Z:\Sources\Agent42` (branch `docs/toolforge-slice1-measurements`)

**No product code.** But this repo is **not** untouched, and it must be on a branch — see K12 for
the two independent reasons (the commit hook, and hard rule 1). The edits are step B5's
measurement record plus any correction it forces.

## Context & constraints

### K1 — Two servers means two HOSTS, not two paths.

`Mcp.Gateway` is YARP routing on the `Host` header (`src/Mcp.Gateway/GatewayRoutes.cs:39-44`).
There is no path routing at all; `README.md:260-267` records why (RFC 8414 publishes
`.well-known/oauth-authorization-server` at fixed **root** paths, so two OAuth backends collide).
ToolForge uses API-key auth and would not personally collide, but there is no path-routing
machinery to reuse. Two hostnames also become two certificate SANs automatically: the `backends`
list feeds `dnsNames` (`GatewayCertificates.cs:104`, `Program.cs:106`).

### K2 — Adding a gateway backend touches `Program.cs`, not `GatewayOptions.cs`.

`GatewayOptions.Read()` (`GatewayOptions.cs:74-82`) reads email, TOS, staging, cert dir, cert
password and two ports — **no host name at all**. Host vars come from the local `RequireHost`
helper in `src/Mcp.Gateway/Program.cs:36-41`, and `RequireHost` **returns exit code 2 on a missing
var**, which is what makes the `ci.yml` and `docker-compose.yml` edits mandatory rather than tidy.
`README.md:391-394` calls adding a backend "three lines".

### K3 — Containerisation defeats the loopback default. Set ports explicitly.

`Dockerfile:48` sets `ENV ASPNETCORE_URLS=http://0.0.0.0:8080`, and `McpHost.cs:128-129` applies
its `127.0.0.1:5099` default **only when that variable is unset**. So two `McpHost.Build` calls in
one container both bind `0.0.0.0:8080` and the second throws. **Pass `--urls` explicitly per
surface** (the gateway already carries two ports in `GatewayOptions` — follow that).

`HEALTHCHECK CMD ["/app/server","--health-probe"]` resolves its port from `ASPNETCORE_URLS`
(`HealthProbe.cs:58-72`), so a ToolForge on two other ports is reported unhealthy forever — the
exact failure `HealthProbe.cs:12-16` documents. **In production ToolForge is hosted by the gateway
image, whose health story already works**; a standalone ToolForge image is for the CI smoke job and
local runs only, and its health probe must be made to agree with whatever ports it binds.

### K4 — Repo A has no application-persistence precedent. Follow the ACME shape.

Every server in Repo A is stateless. The only durable data is ACME certificates at `/data/certs`
(`GatewayOptions.cs:55`), configurable and mounted as a **named** volume —
`docker-compose.yml:53-56` warns that an anonymous volume is orphaned by `docker compose down`,
burning Let's Encrypt's rate limit on the next `up`. So: `MCP_TOOLFORGE_DATA_DIR`, default
`/data/toolforge`, JSON files, **named** volume. Not SQLite; nothing here uses a database.

### K5 — The test shape is `McpHost.Build` + a real listener + real JSON-RPC.

Model on `tests/Calendar.Mcp.Tests`, not on unit-testing the tool classes:
`ToolForgeServer : IAsyncDisposable` with a static `StartAsync` calling
`McpHost.Build(["--urls","http://127.0.0.1:0"], …)` then `app.StartAsync()`
(`Calendar.Mcp.Tests/CalendarServer.cs:75-95`); drive `tools/list` / `tools/call` over real
JSON-RPC with `MCP-Protocol-Version: 2025-06-18` via `McpCall.cs`, which must be **copied** (no
shared test project exists, and creating one is out of scope). xUnit 2.9.3, `Assert.*`, `sealed`
classes, no base class, `global using Xunit;`, names `{Subject}_{Condition}_{Expected}`.
`Scraper.Mcp` has no tests, so "every project has tests" is not a rule here — ToolForge needs them
anyway, being the first stateful server in the repo.

### K6 — Repo A's gates are its own.

`.github/workflows/ci.yml` runs restore, `dotnet build -c Release`, `dotnet test McpServer.slnx`, a
CLI smoke test, and per-server docker image build + auth checks (`:38-41`, `:60-67`). There is **no
coverage gate, no `dotnet format` check, no complexity analyzer.** Do not import Agent42's
thresholds. Do add the ToolForge image job — that is the established pattern.

### K7 — `McpHost` details that would otherwise be rediscovered.

stdio by default, `--http` for HTTP; `--health-probe` must stay first; API-key mode **fails
closed**; in stdio mode **stdout is the transport**, so logging goes to stderr only;
`McpHost.Build` exists as the test seam.

**And the trap this constraint exists to collect:** `README.md:387-389` — once the gateway
references two projects that each ship an `appsettings.json`, the duplicate in one publish output
is a hard **`NETSDK1152`**, "not a warning". A `dotnet new web` scaffold ships one. Delete it.

### K8 — DevHost CANNOT measure this. The measurement runs on the AppHost.

`tools/Agent42.DevHost/Program.cs:87` registers `SingleUserMcpToolProvider` (never reads the config
grain) and `:142` registers `OfflineMcpConnectionFactory` (canned tools, no handshake). Neither
exercises `UserMcpToolProvider` — **the class whose version-keyed cache is the subject of the
measurement**. The real seams are in `src/Agent42.Backend/Program.cs:184-185`, and
`src/Agent42.AppHost` runs the real Backend (`AppHost.cs:48`, and its comment at `:58`).

**The measurement target is ToolForge on loopback via the AppHost** (needs Docker for the Cosmos
emulator). Step A7's public deployment is required before *production* Agent42 can use ToolForge;
it is **not** a prerequisite for B4.

### K9 — Public exposure is safe in slice 1 ONLY because the executor is a stub.

D3 puts an authoring surface on the public internet before slice 2's gate exists. With D1 that is
acceptable: `write_tool` stores text, nothing executes, so the worst an API-key holder achieves is
storage abuse bounded by the key and `ForgeLimits`.

**Hard constraint on slice 2: the moment a real executor lands, the authoring host must not be
publicly reachable until the permission policy is in place.** Slice 2 must not treat "it is already
deployed" as licence.

### K10 — There is exactly ONE credential, and no principal reaches a tool.

`McpHost.cs:112-125` resolves a single `resolvedKey`; `:173-188` → `ApiKey.cs:14-21` rejects
anything else before a tool runs. In API-key mode `UseAuthentication` is **never called**
(`McpHost.cs:165-169`) and no `IHttpContextAccessor` is registered, so there is no principal.
`CalendarCaller.cs:36-38`'s claim-reading precedent is **bearer**-mode and does not transfer.

Therefore (D2): `FileToolStore` lays out `{dataDir}/{callerId}/{name}.json` and `CallerId` returns a
**single constant** for this slice, documented as the seam a future auth mode fills. The raw
credential still never reaches disk or a log — the hashing discipline of
`GoogleTokenValidator.cs:49,170-171` and `CalendarCaller.cs:7-16` stands. AC3 is therefore a
**Core-level** test over two caller ids, not an MCP-protocol test, because the protocol cannot
present a second caller.

### K11 — Agent42 must use **Header** auth for both servers, and the tool names must not collide.

Two independent reasons, both measurable failures:

1. **Auth mode.** `gotchas.md:1674-1678` — Test on a near-expiry **InteractiveOAuth** server bumps
   the overlay version and invalidates the socket cache. That is precisely the independent variable
   in M2–M5, so an OAuth choice would silently confound the measurement. Pin **Header**.
2. **Name collision.** `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:164-169` does a flat
   `tools.AddRange(...)` with **no name de-duplication**. If both surfaces exported `run_tool` and
   `list_tools`, an agent with both servers ticked receives two AF tools of each name — the ADR 0015
   `HostedWebSearchTool`/shim collision shape. **This is why the runtime surface carries NO dispatch
   verbs** (D5): it advertises promoted tools only, so the two surfaces share no name at all.

### K12 — Agent42 must be on a branch, for one reason now.

**The cross-repo half of this is FIXED and no longer applies.** `.claude/hooks/protect-main.mjs`
used to pin its branch read to `CLAUDE_PROJECT_DIR`, so with Agent42 sitting on `main` every Repo A
write was refused too — the hook did not know which repository the command was for. It now resolves
the repositories a command actually targets (a `-C` path, a leading `cd` path, else the session
root) and judges each on its own branch, refusing if **any** of them is protected. So Repo A work
on a feature branch is unaffected by whatever branch Agent42 sits on, and the mirror case a single
read could not catch — a write landing on `main` here while another checkout looks clean — is now
caught as well. See `tests/Agent42.Tests/ProtectMainHookTests.cs`.

What survives is the independent reason: B5 is a real Agent42 edit, so hard rule 1 (branch + MR)
applies on its own merits. Create `docs/toolforge-slice1-measurements` **before step A1**.

### K13 — Governing documents, and the doc debt B5 owes.

Design note `.agentic/specs/plan-agent-authored-tools.md` (C1–C13). Gotcha "Remote MCP servers: a
live-socket cache…", **point (6)**, `.agentic/gotchas.md:1626-1639` — which states C1's content as a
`Verified: 2026-09-07` claim with named call sites. **If B4 contradicts C1, that gotcha is equally
wrong and must be corrected in the same commit as the design note.** Active ADRs ridden and not
contradicted: 0024, 0026, 0058, 0064, 0067. No `/decide` is due in this slice.

## Steps

### Repo B first — `Z:\Sources\Agent42`

- **B0** Create branch `docs/toolforge-slice1-measurements`. **Before any Repo A work** (K12).

### Repo A — `Z:\Sources\mcpserver` (branch `feature/toolforge-mcp`)

- **A1 — `ToolForge.Core`.** `ToolRecord` (name, description, JSON-schema string, source, stamps),
  `CallerId` (K10), `FileToolStore` (K4), `ToolRegistry` with `ForgeLimits` caps
  (`MaxToolSourceChars = 32_768`, `MaxToolsPerCaller = 100`, `MaxNameChars = 64`),
  `IToolExecutor` + `StubToolExecutor` (D1).
  *Verify:* `dotnet build McpServer.slnx`.
- **A2 — Authoring surface.** `ToolForgeAuthoringTools`: `write_tool`, `list_tools`, `delete_tool`,
  `run_tool`. Follow `Scraper.Mcp/ScraperTools.cs` shape; descriptions carry the operating rules
  including the 8 KB result budget (design note C8). *Verify:* `dotnet build`.
- **A3 — Runtime surface (promotion).** `RuntimeToolPublisher` advertises each stored tool as a
  real MCP tool with its stored JSON schema, dispatching to the stub executor. **No dispatch verbs
  — no shared name with A2** (K11). *Verify:* the A5 tests.
- **A4 — `Program.cs`** hosting both surfaces on two **explicitly-passed** ports in one process over
  one registry singleton (K3). *Verify:* run it; `curl` `/health` on both ports.
- **A5 — `tests/ToolForge.Mcp.Tests`** per K5, covering the AC table.
  *Verify:* `dotnet test McpServer.slnx`.
- **A6 — Gateway wiring:** two `RequireHost` calls, two `McpHost.Build` calls, two `BackendRoute`
  entries (K1/K2). *Verify:* `dotnet test McpServer.slnx` — gateway tests still green.
- **A7 — Dockerfile, compose, `.env.example`, CI (K3/K4/K6, and the EXISTING-job breakage).**
  *Verify:* `docker build -t mcp-gateway --build-arg SERVER=src/Mcp.Gateway .` still succeeds, the
  existing gateway CI job passes with the new required vars, and the new ToolForge image job
  passes its 401-without-key check as `ci.yml:41-58` does for the scraper.
- **A8 — Deploy** behind the gateway (D3); confirm both hostnames answer.

### Repo B — configuration and measurement

- **B1** Add both servers in Preferences → MCP servers, **auth mode Header** (K11), leaving
  `SeedsFetchableUrls` **off** on both (ADR 0067, design note step list). Press **Test** on each;
  confirm per-tool checkboxes (ADR 0064).
- **B2** Create the **Tool Builder** agent: `EnabledTools = [mcp:{authoringId}]` only, continuous,
  no web tools, **no memory** (design note C12). Do **not** mint an API key (design note C7).
- **B3** Create a **non-continuous** consumer agent with the coarse `mcp:{runtimeId}` key —
  non-continuous is load-bearing for M3 (K11/M3 note below).
- **B4 — THE MEASUREMENT.** On the AppHost (K8), record actual values:
  - **M1** Builder calls `write_tool` then `run_tool` in the **same turn**. *Note what a failure
    would mean:* dispatch immunity is true by construction, so M1 failing implicates model
    behaviour, the 8 KB budget or grain non-reentrancy — **not** a cache property. M1 is a
    smoke test of the loop, not the falsifier.
  - **M2** The tool now exists on `forge-runtime`'s `tools/list` (verify with `curl` first —
    that is the independent variable). Does the consumer agent see it with **no** config change?
    *Design note C1 predicts no.*
  - **M3** Does a **brand-new conversation** with the non-continuous consumer change M2? *C1
    predicts no.* This is the half most likely to be wrong. A continuous agent would re-enter the
    **same** grain (ADR 0016's deterministic `agent-{agentId}` id) and measure nothing — hence B3.
  - **M4** After a no-op re-save of `forge-runtime`, does a new conversation see it? *C1 predicts
    yes.*
  - **M5** Does a Backend restart alone make it appear? *C1 predicts yes — and that this is the
    misleading part.* Note Orleans idle collection keeps a grain warm, so this is the only
    reliable deactivation lever available.
  - **M6** Does the consumer see any authoring verb? *Predicts no.* (Its converse is vacuous —
    the runtime surface has no verb of its own by design, K11.)
- **B5** Append a `## Measurements` section to `.agentic/specs/plan-agent-authored-tools.md` with
  the values. **If a measurement contradicts C1 or C2, correct the design note AND
  `.agentic/gotchas.md` point (6) in the same commit** (K13). Open the MR.

### Landing order

B0 → A1–A8 → B1–B5. Repo A must be running and reachable on loopback before B4; the **public**
deployment (A8) gates only production use, not the measurement (K8).

## Acceptance criteria → test mapping

| AC | Proven by | Where |
|----|-----------|-------|
| AC1 `write_tool` then `run_tool` in one MCP session returns the stub result | `WriteThenRun_ReturnsTheStubResult_InOneSession` | `tests/ToolForge.Mcp.Tests/DispatchTests.cs` |
| AC2 The runtime surface advertises a written tool by name and schema | `AWrittenTool_IsAdvertisedByTheRuntimeSurface` | `tests/ToolForge.Mcp.Tests/PromotionTests.cs` |
| AC3 The runtime surface exposes **no** authoring verb and shares no tool name with it | `TheTwoSurfaces_ShareNoToolName` | `tests/ToolForge.Mcp.Tests/SurfaceSeparationTests.cs` |
| AC4 A tool stored under one caller id is invisible under another (**Core-level** — K10) | `AToolStoredUnderOneCallerId_IsNotVisibleUnderAnother` | `tests/ToolForge.Mcp.Tests/RegistryScopeTests.cs` |
| AC5 The registry survives a restart | `AWrittenTool_SurvivesAServerRestart` | `tests/ToolForge.Mcp.Tests/PersistenceTests.cs` |
| AC6 The raw credential never reaches the store | `TheRawCredential_IsNeverWrittenToTheStore` | `tests/ToolForge.Mcp.Tests/RegistryScopeTests.cs` |
| AC7 `run_tool` on an unknown name is a curated error, not an exception | `RunTool_OnAnUnknownName_ReturnsACuratedError` | `tests/ToolForge.Mcp.Tests/DispatchTests.cs` |
| AC8 A source over `ForgeLimits.MaxToolSourceChars` (32,768) is rejected | `AnOversizeToolSource_IsRejected` | `tests/ToolForge.Mcp.Tests/DispatchTests.cs` |
| AC9 The stub executor reports that nothing ran | `StubExecutor_ReportsThatNothingRan` | `tests/ToolForge.Mcp.Tests/DispatchTests.cs` |
| AC10 A deleted tool disappears from the runtime surface | `ADeletedTool_IsNoLongerAdvertised` | `tests/ToolForge.Mcp.Tests/PromotionTests.cs` |
| M1–M6 | **Manual, on the AppHost — recorded, not automated** | step B5 |

**AC6 note:** assert on the *store contents*, not on start-up behaviour. A `NoApiKey_RefusesToStart`
test would depend on ambient `MCP_API_KEY` (`McpHost.cs:114`) — green in CI, red on a machine with
`.env` exported — and would exercise `Mcp.Hosting`, not ToolForge.

**M1–M6 are deliberately not automated.** They exercise Agent42's grain activation and cache
lifecycle against a live server; there is no in-repo harness and DevHost cannot do it (K8). This
follows the repo's treatment of untestable legs — ADRs 0048/0052/0058/0064 run the manual pass and
record the measured values.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Applies? |
|------|-----------|----------|
| `coverage_new_code_pct_min` | 80 | **SKIPPED** — measured by `.agentic/scripts/check-new-code-coverage.mjs` on an Agent42 diff. This plan adds zero Agent42 code; the gate is vacuous, not passing. |
| `cognitive_complexity_max` | 15 | **SKIPPED** — `SonarAnalyzer.CSharp` S3776 in Agent42's build; Repo A does not reference the analyzer. |
| `vulnerable_packages` | 0 | Repo A CI has no equivalent job. |

**Repo A's actual gate** (K6): `dotnet build -c Release` clean, `dotnet test McpServer.slnx` green,
the existing gateway image job still passing, and the new ToolForge image job passing. Report
SKIPPED with this reason for the Agent42 gates — never an estimated number (ADR 0050).

## Out of scope

- **The real executor, the Copilot SDK, any permission policy** — slice 2, constrained by K9.
- Any Agent42 **product code**, including the MCP tool-list refresh endpoint: B4 is precisely what
  should decide whether that is worth building.
- **Real multi-caller auth** — K10. The storage shape is built; the auth mode is not.
- An ADR (deferred to slice 2 per the design note's step 10).
- Bumping Repo A's pinned package versions; a shared test-helper project in Repo A (K5).

## Review

A `critic` subagent reviewed this plan. Findings and disposition:

- **Blocker 1 — B4 could not falsify C1; the independent variable was out of scope. ACCEPTED, and
  it restructured the plan.** The original runtime surface had a constant tool list, so M2–M5 would
  have recorded "no change" trivially, for reasons unrelated to either cache — and B5 would then
  have written a **false correction** into the design note. The dependency was also circular
  (promotion waited on B4; B4 needed promotion). Fixed by D5: minimal promotion is now in scope,
  which makes the runtime tool list actually change and the experiment real.
- **Blocker 2 — `Dockerfile` unnamed; A7's verify not executable. ACCEPTED.** Now K3 and an
  explicit `Dockerfile` entry: the hard-coded csproj `COPY` list, the `ASPNETCORE_URLS` override
  that defeats the loopback default and would make the second `McpHost.Build` throw, and the
  `HEALTHCHECK` port resolution.
- **Blocker 3 — per-caller registry had no mechanism. ACCEPTED, and D2 is revised.** One credential
  exists in API-key mode and no principal reaches a tool. K10 keeps the storage *shape* and drops
  the invented mechanism; AC4 became a Core-level test.
- **High 4 — the EXISTING gateway CI job and compose service break. ACCEPTED.** `RequireHost`
  exits 2 on a missing var. Now named in Affected areas and A7, with `.env.example`.
- **High 5 — `GatewayOptions.cs` holds no host options. ACCEPTED** — corrected to `Program.cs`'s
  `RequireHost` (K2).
- **High 6 — the commit hook blocks Repo A commits while Agent42 is on `main`. ACCEPTED** — K12 and
  the new step B0.
- **Medium 7 — `C1/C2` label collision. ACCEPTED** — this plan's constraints renumbered `K1…K13`,
  with a banner at the top.
- **Medium 8 — "nothing is edited here" contradicted B5. ACCEPTED** — Repo B now has a branch and an
  MR (which K12 required anyway).
- **Medium 9 — gotcha point (6) is equally doc debt; "nothing has measured either" overstated.
  ACCEPTED** — K13 names the gotcha; the Overview now says "verified by reading, never observed".
- **Medium 10 — "new chat" ≠ fresh activation for a continuous agent. ACCEPTED** — B3 pins a
  non-continuous consumer; M3 and M5 record why.
- **Medium 11 — M6 half-vacuous, and it missed a real collision. ACCEPTED** — the `BuildAgent`
  flat `AddRange` with no de-duplication is now K11, and is the reason the runtime surface carries
  no dispatch verbs.
- **Medium 12 — M1 a weak falsifier. ACCEPTED** — M1 now states what its failure would and would
  not mean.
- **All six LOW findings ACCEPTED**: AC8 carries a number; AC6 rewritten off the env-sensitive
  start-up path; the loopback-vs-deployed contradiction resolved in K8; `ToolForge.Core.csproj`
  listed; the `NETSDK1152` duplicate-`appsettings.json` trap added to K7; the
  `SeedsFetchableUrls` instruction restored to B1.
- **Nothing rejected.** The critic separately verified ~20 file:line citations as correct, and
  found no ADR contradiction and no error in the gate table.
