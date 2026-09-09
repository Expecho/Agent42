# Plan — ToolForge: an agent that writes, tests and publishes its own tools

planned-against: main @ c2958c5

> **Status 2026-09-09: A1–A8 and B5 are done; B1–B4 are NOT run.** The spec stays here rather than
> moving to `completed/` for exactly that reason. Everything buildable is built, tested and verified
> in the built images; what remains needs an operator session with a configured chat model. See
> "Where the implementation diverged from this plan" at the end of this file.

> **Label scheme, because this plan cites two documents.** Decisions here are **`P1…P10`** and constraints
> **`K1…K18`**. The merged design note `.agentic/specs/plan-agent-authored-tools.md` uses `D1…D6` and
> `C1…C13`. So a bare `D<n>` or `C<n>` below **always means the design note's**; this plan never uses those
> letters for its own. (An earlier draft reused `D`, and plan-D6 vs note-D6 meant two different things.)

## Overview

**Goal:** a dedicated Tool Builder agent writes a Python tool, runs it to test it, reads the traceback,
fixes it, and publishes it — after which every other agent of that user can call it. No repo edit, no deploy.

**Why now:** the design note settled the architecture and was merged (PR #121). This is the buildable plan.
It supersedes the abandoned `plan-toolforge-mcp-slice1.md`, whose review findings are carried into K1–K8.

**Shape:**

```
  ┌─ Agent42 (NO product code change) ────┐   ┌─ gateway host ──────────────────────────────┐
  │                                        │   │  toolforge container (own, K13)              │
  │  "Tool Builder" agent                  │   │    authoring surface  :8080  ─┐              │
  │   model: already-selected (P5)         │──►│    runtime  surface   :8081  ─┤ one process, │
  │   EnabledTools = [mcp:{authoringId}]   │   │                               │ one registry │
  │                                        │   │         ▼ subprocess, no sandbox             │
  │                                        │   │    python3, venv on the data volume          │
  │  every other agent                     │   │                                              │
  │   EnabledTools = [mcp:{runtimeId}, …] │◄──│  gateway routes 2 hostnames → 2 addresses    │
  └────────────────────────────────────────┘   └──────────────────────────────────────────────┘
```

## Decisions

The user's explicit calls. **Two reverse the merged design note** — recorded as reversals, not drift. Do not
relitigate.

- **P1 — Executable tools, out-of-process behind MCP.** Design note D1, unchanged.
- **P2 — A dedicated builder agent**, privilege-separated by `AgentDefinition [Id(8)] EnabledTools`.
- **P3 — Two MCP servers**, two hostnames, two ports (K1, K11, K17).
- **P4 — Dispatch for the builder, promotion for consumers.** Design note D4, unchanged.
- **P5 — REVERSES design note D5. The GitHub Copilot SDK is NOT the executor.** The builder's intelligence
  is **any model the user has already selected** (ADR 0018/0013); ToolForge contains **no LLM**. Deletes the
  `Microsoft.Agents.AI.GitHub.Copilot` package, the `COPILOT_GITHUB_TOKEN` secret, the Copilot CLI runtime
  and the design note's C10 packaging question.
- **P6 — REVERSES the design note's execution-gate scope. No sandbox, no gate.** Generated code runs as a
  plain subprocess with full network, filesystem and arbitrary dependencies. This **supersedes design-note
  step 7** (deny-by-default `OnPermissionRequest`, container-per-run, no-network default, resource caps)
  **and design-note AC4** ("a generated tool attempting a shell command outside the whitelist is denied"),
  neither of which survives. Blast radius in K14; accepted by the user.
- **P7 — Python** is the tool runtime (over C# file-based apps, for model fluency). Cost: K12.
- **P8 — ToolForge runs in its OWN container**, not as an in-process gateway backend. Not a sandbox — K13.
- **P9 — Exposure:** both hosts public behind the gateway API key; the **authoring** server's `Enabled`
  checkbox is switched off between building sessions (ADR 0064: a disabled server is never dialled). K18
  records why that is weaker than it sounds for the builder specifically.
- **P10 — CodeAct / Hyperlight rejected**, with a re-entry trigger. See "Rejected alternatives".

## Affected areas

### Repo A — `Z:\Sources\mcpserver` (separate git repo, no `.agentic/` process)

Verified against the tree at `aed18d8`.

**New:**
- `src/ToolForge.Core/` — `ToolForge.Core.csproj`, `ToolRecord.cs`, `ToolRegistry.cs`, `FileToolStore.cs`,
  `CallerId.cs`, `PythonEnvironment.cs`, `PythonToolExecutor.cs`, `ForgeLimits.cs`.
- `src/ToolForge.Mcp/` — `ToolForge.Mcp.csproj`, `Program.cs`, `ToolForgeAuthoringTools.cs`,
  `RuntimeToolPublisher.cs`.
- `tests/ToolForge.Mcp.Tests/` — csproj, `GlobalUsings.cs`, `ToolForgeServer.cs`, `McpCall.cs` (copied, K5),
  and the test classes in the AC table.
- **`Dockerfile.toolforge`** — a second Dockerfile; this is the one server needing Python (K12).

**Modified:**
- **`src/Mcp.Gateway/BackendRoute.cs`** — `Address` is **hardcoded** to
  `$"http://127.0.0.1:{Port}/"` (`:22-23`) and `Port` is documented as "the loopback port this backend's own
  `WebApplication` is bound to" (`:19`). A cross-container backend (P8/K13) is impossible without changing
  this record, and its own remarks call it the single source of truth for two subsystems. **Also touches
  `tests/Mcp.Gateway.Tests/GatewayRoutingTests.cs:110`**, which constructs `BackendRoute[]`.
- `src/Mcp.Gateway/Program.cs` — two `RequireHost` calls (helper defined at `:111-121`, called at `:36-41`)
  and two `BackendRoute` entries (K2, K17).
- `docker-compose.yml` — a `toolforge` service, two gateway host vars, a **named** volume (K4).
- `.env.example` — the two new host vars.
- `.github/workflows/ci.yml` — **the existing gateway job at `:126-136` will FAIL** once two more host vars
  are required (K2); plus a ToolForge image smoke job.
- `README.md` — the deployment shape (K17), `McpServer.slnx`.

**Deliberately NOT modified (K16):** the root `Dockerfile`. Under P8 the gateway does **not** reference
ToolForge's projects, so the hardcoded csproj `COPY` list at `:13-18` needs no entry and the
`NETSDK1152` duplicate-`appsettings.json` trap (`README.md:387-389`) does not arise on this branch. Both
were carry-over from the abandoned slice plan, where ToolForge lived inside the gateway process.

### Repo B — `Z:\Sources\Agent42` (branch `docs/toolforge-measurements`)

**No product code**, but three real edits — and it must be on a branch for two reasons (K15):
- `.agentic/specs/plan-agent-authored-tools.md` — the `## Measurements` section, and the C1 correction K9
  requires.
- **A new `.agentic/decisions/00NN-*.md`** recording P5, P6, P7 and P10 — subject to K10's CI trap.
- **A `CLAUDE.md` "Repo-specific notes" bullet**, which every ADR in this repo earns.

## Context & constraints

### K1 — Two servers means two HOSTS, not two paths.

`Mcp.Gateway` is YARP routing on the `Host` header (`GatewayRoutes.cs:39-44`). There is no path routing;
`README.md:260-267` records why (RFC 8414 publishes `.well-known/oauth-authorization-server` at fixed
**root** paths, so two OAuth backends collide). ToolForge is API-key auth and would not personally collide,
but there is no path-routing machinery to reuse. Two hostnames also become two certificate SANs — the
`backends` list feeds `dnsNames` (`GatewayCertificates.cs:104`, `Program.cs:106`).

### K2 — Gateway backends are wired in `Program.cs`, and a missing var exits 2.

`GatewayOptions.Read()` (`GatewayOptions.cs:74-82`) reads email, TOS, staging, cert dir, cert password and
two ports — **no host name at all**. Host vars come from `RequireHost` (defined `Program.cs:111-121`, called
`:36-41`), which **returns exit code 2 when one is missing**. That is what makes the `ci.yml`,
`docker-compose.yml` and `.env.example` edits mandatory: two new required vars break the **existing**
gateway CI job and compose service.

### K3 — Containerisation defeats `McpHost`'s loopback default.

`Dockerfile:48` sets `ENV ASPNETCORE_URLS=http://0.0.0.0:8080`, and `McpHost.cs:128-129` applies its
`127.0.0.1:5099` default **only when that variable is unset**. Two `McpHost.Build` calls would both bind
`0.0.0.0:8080` and the second would throw. **Pass `--urls` explicitly per surface** — authoring `:8080`,
runtime `:8081` (K17 depends on these being distinct). `HEALTHCHECK` resolves its port from
`ASPNETCORE_URLS` (`HealthProbe.cs:58-72`), so make the probe agree with what is actually bound.

### K4 — No application-persistence precedent in Repo A. Follow the ACME shape, BOTH halves.

The only durable data is ACME certs at `/data/certs` (`GatewayOptions.cs:55`), and the pattern has two
halves the slice plan only carried one of:
1. A **named** volume — `docker-compose.yml:53-56` warns an anonymous one is orphaned by
   `docker compose down`, burning Let's Encrypt's rate limit on the next `up`.
2. **Ownership before the user switch** — `Dockerfile:41` is
   `RUN mkdir -p /data/certs && chown $APP_UID /data/certs`, *before* `USER $APP_UID` at `:66`, precisely
   because a fresh named volume inherits the image directory's ownership. `Dockerfile.toolforge` must do
   the same for `/data/toolforge`, or the venv (K12) is unwritable.

So: `MCP_TOOLFORGE_DATA_DIR`, default `/data/toolforge`, JSON files, named volume, chowned before `USER`.

### K5 — Test shape: `McpHost.Build` + a real listener + real JSON-RPC.

Model on `tests/Calendar.Mcp.Tests`: `ToolForgeServer : IAsyncDisposable` with a static `StartAsync` calling
`McpHost.Build(["--urls","http://127.0.0.1:0"], …)` then `app.StartAsync()` (`CalendarServer.cs:75-96`);
drive `tools/list` / `tools/call` over real JSON-RPC with `MCP-Protocol-Version: 2025-06-18` via
`McpCall.cs`, which must be **copied** (no shared test project; creating one is out of scope). xUnit 2.9.3,
`Assert.*`, `sealed` classes, no base class, `global using Xunit;`, names `{Subject}_{Condition}_{Expected}`.

### K6 — Repo A's gates are its own, and its unit tests are offline by contract.

`ci.yml` runs restore, `dotnet build -c Release`, `dotnet test McpServer.slnx`, a CLI smoke test, and
per-server image build + auth checks (`:38-41`, `:60-67`). No coverage gate, no `dotnet format`, no
complexity analyzer — do not import Agent42's thresholds. **And `ci.yml:25-26` states the unit tests never
touch the network**; a `pip install` test violates that, which is why AC4 is quarantined (K12).

### K7 — `McpHost` details.

stdio by default, `--http` for HTTP; `--health-probe` must stay first; API-key mode **fails closed**; in
stdio mode **stdout is the transport**, so logging goes to stderr only; `McpHost.Build` is the test seam.

### K8 — DevHost cannot measure the Agent42 half; the AppHost can.

`tools/Agent42.DevHost/Program.cs:87` registers `SingleUserMcpToolProvider` (never reads the config grain)
and `:142` registers `OfflineMcpConnectionFactory` (canned tools, no handshake). The real seams are in
`src/Agent42.Backend/Program.cs:184-185`, and `src/Agent42.AppHost` runs the real Backend (`AppHost.cs:48`).
The measurement target is ToolForge on **loopback via the AppHost** (needs Docker for the Cosmos emulator).
A8's public deployment gates production use, not B4.

### K9 — ADR 0069 made promotion ONE action, and the design note is stale about it.

A successful `POST /api/mcp-servers/{id}/test` now calls `IUserMcpConfigGrain.InvalidateToolCacheAsync`, so
**pressing Test both records the new tool names and re-resolves the cached tool set**. The design note's C1
says the unlock is "a no-op re-save, *then* Test" — **stale; B5 must correct it.** ADR 0069's carve-outs
still apply: a **failed** test and a **zero-tool** connect invalidate nothing.

### K10 — A new ADR that backticks a sibling-repo path is a HARD CI FAILURE.

`.agentic/scripts/check-doc-paths.mjs:11-17` scans `CLAUDE.md`, `gotchas.md` and `.agentic/decisions/*.md` —
**not** `.agentic/specs/` — and its `PATH_RE` matches any backticked `dir/file.ext`. So a spec may write
`` `src/ToolForge.Mcp/Program.cs` `` freely (this file does), but **the ADR in B5 may not**: those paths do
not exist in Agent42 and `pr-checks.yml:88` blocks on it. ADR 0063 solved this by naming sibling-repo types
bare (`Calendar.Mcp`, no backticks, no path). Do the same.

### K11 — A promoted tool name can collide with ANY tool, not just the other server's.

`src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:164-169` merges MCP tools into the **same flat list** as
the static and always-on tools, with **no name de-duplication**. So a promoted tool called `get_date`,
`web_search`, `fetch_web_page`, `read_web_document`, `schedule_prompt`, `load_skill` or `run_tool` collides
— and `ToolCatalog`'s unmapped-name rule (`ToolCatalog.cs:150-154`) means a dynamic MCP name is kept
**unconditionally**, so nothing filters it out. Two consequences:
1. The runtime surface carries **only promoted tools** — never `run_tool`/`list_tools` (P3).
2. `ForgeLimits` needs a **reserved-name list** rejecting every static Agent42 tool name at `write_tool`
   time. The builder chooses the name, so this is the only place to stop it.

### K12 — Python needs a SECOND Dockerfile, and its tests need quarantining.

The build stage is `mcr.microsoft.com/dotnet/sdk:10.0`, runtime `mcr.microsoft.com/dotnet/aspnet:10.0`, and
the root Dockerfile deliberately has **no `SERVER`-conditional branches** (its own comment says so).
Installing `python3`/`pip` unconditionally would bloat the scraper, calendar and gateway images. So
`Dockerfile.toolforge`.

**Two consequences the slice plan missed:**
- The venv lives on the data volume (K4) so it is writable by the non-root user *and* survives a restart —
  otherwise every boot reinstalls from each record's stored `requirements`.
- **`python3` is not on the Windows dev machine and `pip install` needs the network**, which `ci.yml:25-26`
  says the unit suite never does. So the executor tests are **skippable by a capability probe** (skip when
  no interpreter is resolvable) and AC4 additionally runs only in the image smoke job, never in
  `dotnet test`.

### K13 — ToolForge gets its own container so generated code does not inherit the calendar's secrets.

`Mcp.Gateway` runs Scraper, Calendar and the proxy as three `WebApplication`s in **one process**, and
`docker-compose.yml:66-68` puts `MCP_API_KEY`, `GOOGLE_OAUTH_CLIENT_ID` and `GOOGLE_OAUTH_CLIENT_SECRET` in
that container's environment, plus the ACME volume at `/data/certs`. Hosting ToolForge that way would put
unsandboxed generated code in a process where `/proc/self/environ` is the Google OAuth client secret.

**This is not a sandbox and does not weaken P6** — inside its own container the code still has full network,
filesystem and dependencies. It simply is not handed another feature's credentials.

### K14 — The accepted blast radius, recorded once.

With P6 and P9: generated code runs as the ToolForge process user with its network and filesystem, and the
**gateway API key is the only thing between the public internet and arbitrary code execution on the
operator's hardware**. The design note's C9 chain (injected text → a written tool → durable, re-run forever)
is fully open; P2's privilege separation is the only mitigation. Accepted explicitly. Do not re-argue it in
review, and do not silently add a gate.

### K15 — Agent42 must be on a branch, for two reasons.

`.claude/hooks/protect-main.mjs:10` pins `ROOT = CLAUDE_PROJECT_DIR`, `:29` runs `git branch --show-current`
**there**, and `:72-77` blocks commits on a protected branch — while `isCommit` is a regex on the command
string. So with Agent42 on `main`, **every Repo A commit is blocked too**; the hook cannot tell which repo a
commit is for. And B5 is a real Agent42 edit, so hard rule 1 applies anyway. Branch **before A1**.

### K16 — The gateway must NOT reference ToolForge's projects.

Under P8 they are separate containers reached over HTTP, so no `ProjectReference`, no entry in the root
`Dockerfile`'s csproj `COPY` list, and no `NETSDK1152` exposure. Stated explicitly because the abandoned
slice plan required all three and an executor reading both would reinstate them.

### K17 — Two surfaces need two addresses; `BackendRoute` currently cannot express one.

Two hostnames must reach **two different ports** (`:8080` authoring, `:8081` runtime, K3). Routing both to
one address would send the consumer's hostname to the authoring surface and silently destroy P2/P3's
privilege separation — while every test still passed. `BackendRoute.Address` hardcodes
`http://127.0.0.1:{Port}/`, so it must gain a host component (see Affected areas).

### K18 — Agent42's auth mode, the 8 KB budget, and two design-note constraints that still bite.

- **Header auth**, not InteractiveOAuth — for simplicity and no interactive sign-in. **Not** because it
  avoids a measurement confound: since ADR 0069 (K9) *every* successful Test bumps the version regardless of
  auth mode, so that older rationale (`gotchas.md:1685-1686`) no longer distinguishes the modes.
- **8 KB.** `McpToolLimits.MaxToolResultLength` is `8 * 1024` and a tool's return value is persisted into
  the 2 MB session (ADR 0004). Per ADR 0067's lesson, **the rule the model obeys lives in the tool
  descriptions** — so the budget, and the stdout/stderr contract (K12/A1), go there.
- **Design note C13** — a continuous chat's 24 h window bounds **age, not size**, and the 8 KB truncation
  covers only the tool *result*. A builder that pastes a large tool **source** can still brick its grain
  inside the day. `ForgeLimits.MaxToolSourceChars` is the mitigation and it is not a guarantee.
- **Design note C11** — `ConnectTimeout` is 10 s per server, sequential, on non-reentrant
  `OnActivateAsync`. Two cold ToolForge servers cost up to 20 s on the first chat after an idle period.

## Steps

### Repo B first
- **B0** Create branch `docs/toolforge-measurements` in Agent42 (K15), before any Repo A work.

### Repo A — branch `feature/toolforge-mcp`

Tests are written **in the step that creates the code**, never batched (CLAUDE.md hard rule 3).

- **A1 — Spike the promotion API before building on it.** The plan's largest unverified assumption. Every
  existing server registers tools with `WithTools<T>()` over `[McpServerTool]` attributes
  (`Scraper.Mcp/ScraperTools.cs:13-20`) — reflection over a *static* type. A runtime-varying list is a
  different API. `ModelContextProtocol.Core 2.2.0` (pinned in `Mcp.Hosting.csproj`) appears to expose
  `McpServerOptions.ToolCollection` — a mutable `McpServerPrimitiveCollection<McpServerTool>` with
  `Add`/`Remove`/`RaiseChanged` — plus `McpServerTool.Create(AIFunction, McpServerToolCreateOptions)`.
  **Verify by running it**, and settle three things: `ToolCollection` vs a `ListToolsHandler`; how a
  **stored JSON-schema string** becomes an `AIFunction` (`AIFunctionFactory` does not do this); and how
  either reaches through `McpHost.Build`'s `Action<IMcpServerBuilder> configureTools` seam.
  *Verify:* a throwaway server advertising one tool added after start, listed over real `tools/list`.
  **If this does not work, stop — A3 and P4 depend on it entirely.**
- **A2 — `ToolForge.Core` + its tests.** `ToolRecord` (name, description, JSON-schema string, python
  source, `requirements`, stamps); `CallerId` (a single constant this slice, documented as the seam a
  future auth mode fills); `FileToolStore` at `{dataDir}/{callerId}/{name}.json` (K4); `ToolRegistry` with
  `ForgeLimits` (`MaxToolSourceChars = 32_768`, `MaxToolsPerCaller = 100`, `MaxNameChars = 64`,
  **`ReservedToolNames`** per K11, `RunTimeout`); `PythonEnvironment`; `PythonToolExecutor`.
  **The executor contract, which is not obvious and must also go in the tool descriptions (K18):**
  arguments in as JSON on **stdin**; the result on **fd 3 or a result file, NOT stdout** — a generated tool
  calling `print()` is the single most likely thing a model writes and would otherwise corrupt the result,
  the same hazard K7 records for `McpHost`'s own stdio; **stdout and stderr both captured and returned to
  the builder**, because the traceback is what the fix loop runs on. `PythonEnvironment` installs a record's
  `requirements` into the shared venv under a **file lock plus an installed-marker**, since concurrent
  `run_tool` calls otherwise race — "idempotent" is not a design.
  **`RunTimeout` must be shorter than Agent42's per-call budget**: design-note step 7 required shorter than
  the 10 s `ConnectTimeout`, and `AgentGrain` is non-reentrant for the whole call. Establish the real
  per-call budget in A1's spike (`HttpClientTransportOptions.ConnectionTimeout` documents only *connection*
  establishment) and set `RunTimeout` under it; if no per-call budget exists, say so and pick a value with
  the non-reentrancy cost stated.
  *Verify:* `dotnet test McpServer.slnx` — AC8, AC9, AC10, plus the executor ACs behind the K12 skip.
- **A3 — Authoring surface + tests.** `write_tool`, `run_tool`, `list_tools`, `delete_tool` in the
  `Scraper.Mcp/ScraperTools.cs` shape; descriptions carry the 8 KB budget, the stdout/stderr contract and
  the reserved-name rule (K18, K11). *Verify:* `dotnet test` — AC1, AC2, AC3.
- **A4 — Runtime surface + tests**, using A1's mechanism. Promoted tools only; **no name shared with A3**
  (K11). *Verify:* `dotnet test` — AC5, AC6, AC7.
- **A5 — `Program.cs`** hosting both surfaces on `:8080`/`:8081` in one process over one registry (K3, K17).
  *Verify:* run it; `curl /health` on both ports.
- **A6 — Gateway wiring.** Extend `BackendRoute` with a host component (K17, and update
  `GatewayRoutingTests.cs:110`), two `RequireHost` calls, two `BackendRoute`s to `http://toolforge:8080` and
  `:8081`. **No `ProjectReference` to ToolForge** (K16). *Verify:* `dotnet test McpServer.slnx` — gateway
  tests green.
- **A7 — `Dockerfile.toolforge`** (Python, `/data/toolforge` chowned before `USER`, health probe agreeing
  with the bound ports — K12, K4, K3), compose service + named volume, `.env.example`, CI.
  *Verify:* the **existing** gateway image build and CI job still pass with the new required vars; the new
  ToolForge image job passes its 401-without-key check as `ci.yml:41-58` does for the scraper; AC4 runs here.
- **A8 — Deploy and settle the network shape.** `README.md:292-302` documents a bare
  `docker run -d --name mcp-gateway … -p 443:8443`, and **on the default bridge network there is no
  service-name DNS**, so `http://toolforge:8080` will not resolve. Either move the documented deployment to
  `docker compose up` or add a user-defined network with `--network`; update `README.md` to whichever is
  actually run. *Verify:* both hostnames answer through the gateway.

### Repo B — configuration and measurement
- **B1** Add both servers in Preferences → MCP servers, **auth mode Header** (K18), `SeedsFetchableUrls`
  **off** on both (ADR 0067). Press **Test** on each.
- **B2** Create the **Tool Builder** agent: `EnabledTools = [mcp:{authoringId}]` only, no web tools, **no
  memory** (design note C12), model = any already-configured one (P5). Do **not** mint an API key (design
  note C7). **Non-continuous unless K18/C13 is accepted** — a continuous builder both risks the oversized-
  source brick and, per K10 of the design note, keeps a permanently-warm grain that will not see P9's
  re-enable until it deactivates.
- **B3** Create a **non-continuous** consumer agent with the coarse `mcp:{runtimeId}` key. Non-continuous is
  load-bearing for M3: a continuous agent re-enters the **same** grain (ADR 0016's deterministic
  `agent-{agentId}` id) and would measure nothing.
- **B4 — Measure and record:**
  - **M1** The builder writes a Python tool with a deliberate bug, runs it, reads the traceback, fixes it,
    runs it green — one conversation. *This is the feature.*
  - **M2** A tool whose `requirements` name a third-party package (e.g. `httpx`) installs and runs.
  - **M3** After pressing **Test** on the runtime server (K9), does a **new conversation** with the consumer
    agent see and call the tool?
  - **M4** Does a **warm** consumer chat see it without deactivating? *Predicted no.* This is the one cache
    nothing pins — ADR 0069's own residual says an `AgentGrain` picking up a new set has never been observed,
    because the test silo injects `SingleUserMcpToolProvider`.
  - **M5** With the authoring server's **`Enabled` checkbox off**, is the builder's write path gone while
    the runtime tools still work? (P9's control. Note this is the Enabled checkbox, *not* the tools
    picker's `mcp:{id}` key — different mechanisms, different results.)
- **B5** Record the values in a `## Measurements` section on `.agentic/specs/plan-agent-authored-tools.md`,
  **correct that note's C1 for ADR 0069** (K9), then `/decide` the ADR — recording P5, P6 (naming the
  superseded design-note step 7 and AC4), P7 and P10, and citing design note C5 (the ADR 0049 script-ban
  revisit) and C9 (durable injection). **Obey K10: no backticked sibling-repo paths in the ADR.** Add the
  `CLAUDE.md` bullet. Open the MR.

### Landing order

B0 → A1 → (stop if A1 fails) → A2–A8 → B1–B5.

## Acceptance criteria → test mapping

| AC | Proven by | Where |
|----|-----------|-------|
| AC1 `write_tool` then `run_tool` in one session returns the tool's real output | `WriteThenRun_ExecutesThePythonAndReturnsItsOutput` | `ExecutionTests.cs` |
| AC2 A failing tool returns its **traceback** — the fix loop depends on it | `AFailingTool_ReturnsItsTraceback` | `ExecutionTests.cs` |
| AC3 A tool printing to stdout does **not** corrupt its result (K12/A2) | `ATooLThatPrints_DoesNotCorruptItsResult` | `ExecutionTests.cs` |
| AC4 A record's `requirements` install once and are reused | `RequirementsAreInstalledOnce_ThenReused` | image smoke job only (K12) |
| AC5 A written tool is advertised by the runtime surface with its stored schema | `AWrittenTool_IsAdvertisedByTheRuntimeSurface` | `PromotionTests.cs` |
| AC6 A deleted tool disappears from the runtime surface | `ADeletedTool_IsNoLongerAdvertised` | `PromotionTests.cs` |
| AC7 The two surfaces share no tool name (K11) | `TheTwoSurfaces_ShareNoToolName` | `SurfaceSeparationTests.cs` |
| AC8 A reserved Agent42 tool name is rejected at write time (K11) | `AReservedToolName_IsRejected` | `RegistryTests.cs` |
| AC9 A source over `MaxToolSourceChars` (32,768) is rejected | `AnOversizeToolSource_IsRejected` | `RegistryTests.cs` |
| AC10 `run_tool` on an unknown name is a curated error, not an exception | `RunTool_OnAnUnknownName_ReturnsACuratedError` | `ExecutionTests.cs` |
| AC11 A tool exceeding `RunTimeout` is killed and reported | `ALongRunningTool_IsKilledAtTheTimeout` | `ExecutionTests.cs` |
| AC12 The registry survives a restart | `AWrittenTool_SurvivesAServerRestart` | `PersistenceTests.cs` |
| M1–M5 | **Manual, on the AppHost — recorded, not automated** | step B5 |

Executor ACs (1, 2, 3, 10, 11) skip when no interpreter is resolvable (K12).

**M1–M5 are deliberately not automated.** They exercise Agent42's grain activation and cache lifecycle
against a live server; there is no in-repo harness and DevHost cannot do it (K8). This follows the repo's
treatment of untestable legs — ADRs 0048/0052/0058/0064 run the manual pass and record the values.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Applies? |
|------|-----------|----------|
| `coverage_new_code_pct_min` | 80 | **SKIPPED** — an Agent42 gate on an Agent42 diff; this plan adds zero Agent42 product code, so it is vacuous, not passing. |
| `cognitive_complexity_max` | 15 | **SKIPPED** — S3776 in Agent42's build; Repo A does not reference the analyzer. |
| `vulnerable_packages` | 0 | Repo A CI has no equivalent job. |
| **doc-path integrity** | pass | **APPLIES to B5** — `pr-checks.yml:88` hard-blocks, and it is the one gate that will run against the ADR. See K10. |

**Repo A's actual gate** (K6): `dotnet build -c Release` clean, `dotnet test McpServer.slnx` green, the
existing gateway image job still passing, the new ToolForge image job passing. Report SKIPPED with this
reason for the Agent42 code gates — never an estimated number (ADR 0050).

## Out of scope

- Any Agent42 **product code**.
- A sandbox, execution gate or permission policy (P6, K14).
- **Real multi-caller auth.** `McpHost` API-key mode has exactly one valid credential
  (`McpHost.cs:112-125`) and plumbs no principal to a tool — in API-key mode `UseAuthentication` is never
  called (`:165-169`). The store is laid out per-caller with a single constant `CallerId` so adding an auth
  mode later is not a migration.
- The Copilot cloud-agent REST promotion path (the design note's optional slower loop).
- Bumping Repo A's pinned package versions; a shared test-helper project in Repo A (K5).

## Rejected alternatives

- **The GitHub Copilot SDK as executor** (design note D5). Reversed by P5: a package, a PAT secret and a CLI
  runtime to do what the user's already-selected model does.
- **CodeAct + Hyperlight** (`Microsoft.Agents.AI.Hyperlight`; the Python `agent-framework-hyperlight` is
  alpha). Rejected on three counts: it needs **KVM or Hyper-V** on the host; it reimposes the isolation P6
  rejected, and harder (no libraries, no network without an `allowed_domains` allowlist); and decisively,
  its capability model is `call_tool()` bridging **back to host-registered tools**, so generated code can
  only compose capabilities already written in C# — inverting a forge whose purpose is adding capabilities
  without a repo edit. **Re-entry trigger: if P6 is ever walked back, re-evaluate this before any
  hand-rolled sandbox.**
- **C# file-based apps** (`dotnet run app.cs`, precedent in `mcpserver/filebased/`). Rejected for model
  fluency (P7); it would have avoided K12 entirely.
- **A stubbed-executor slice first.** Abandoned by the user: the registry, both surfaces, gateway wiring,
  Docker and CI are identical either way, so slicing saved only the executor while costing a throwaway
  build and a second planning round.

## Review

A `critic` subagent reviewed the previous draft; ~30 citations in both repos verified correct. Disposition
of its findings — **all accepted, none rejected**:

- **H1 — A6 routed both hostnames at one address while A4 bound two ports**, which would have sent the
  consumer's hostname to the authoring surface and destroyed P2/P3 silently, with every test green. Now K17,
  with explicit `:8080`/`:8081`.
- **H2 — `BackendRoute.Address` is hardcoded to loopback** and was not in Affected areas. Verified
  (`BackendRoute.cs:22-23`); now listed, along with `GatewayRoutingTests.cs:110`.
- **H3 — bare `docker run` has no service-name DNS**, so `http://toolforge:8080` would not resolve. Now A8.
- **H4 — promotion was one sentence with no mechanism.** Now A1, a spike step with its own verify and an
  explicit stop condition.
- **M1** — K10's evidence proved the *provider* cache, not `AgentGrain._mcpServers`; now cites ADR 0069's
  own residual, in M4.
- **M2** — gotcha miscited (`:1685-1686`, not `:1674-1678`) **and** the Header rationale was stale: since
  ADR 0069 every successful Test bumps regardless of auth mode. K18 now gives the real reason.
- **M3** — all tests were batched into one step, against hard rule 3. Now distributed per step.
- **M4** — five executor gaps: stdout-as-result-channel (the hazard this plan flags for `McpHost` and missed
  in its own executor), volume ownership before `USER`, pip locking, `RunTimeout` exceeding the caller's
  budget, and `python3`/network in the unit suite. All in A2, K4, K12.
- **M5** — K11 was one case too narrow; a promoted tool colliding with a *static* Agent42 tool is never
  filtered. Now a reserved-name list and AC8.
- **M6/M7** — the ADR and `CLAUDE.md` bullet were missing from Repo B, and the doc-path gate — the only one
  that runs against B5 — was missing from the table. Both added; K10 records the backtick trap.
- **L1–L7** — decision labels renumbered `P<n>` to end the `D<n>` collision; design-note step 7 and AC4
  named as superseded by P6; C11 and C13 carried into K18; `CallerId` restored; vacuous M5 dropped and the
  Enabled-vs-picker conflation split; the A6 architecture fork resolved by K16; citation drift corrected.

**One finding was investigated and found FALSE:** the reviewer reported a cleartext GitHub PAT in
`Z:\Sources\mcpserver\NuGet.config`. No such file exists in either repo, tracked or untracked.

## Where the implementation diverged from this plan

Executed 2026-09-09. Repo A on `feature/toolforge-mcp`, Repo B on `docs/toolforge-measurements`.

**A1 passed, so nothing downstream was blocked.** All three of its questions were settled by running
the spike: `ToolCollection` (not a `ListToolsHandler`); a stored schema string becomes an `AIFunction`
**subclass** overriding `JsonSchema`, because `McpServerToolCreateOptions` carries `OutputSchema` but
no input-schema property and `AIFunctionFactory` reflects over parameters a runtime tool does not
have; and the host seam is `builder.Services.Configure<McpServerOptions>(…)`.

### Facts the plan got wrong

- **K12's premise is half wrong.** `python3` is absent from this machine, but a real Python **3.11.9**
  is at `C:\Python311\python.exe` — the `python3` on PATH is the Windows Store stub, which is why an
  earlier probe failed. So the executor tests **ran locally** rather than skipping. The skip mechanism
  is kept, as a capability probe rather than a Windows check; K12's *network* half is untouched and a
  pip install still runs only in the image job.
- **There is no per-call budget to fit `RunTimeout` under.** `HttpClientTransportOptions` documents
  `ConnectionTimeout` as covering only the initial connection and exposes no per-request property, so
  the real ceiling is the transport `HttpClient`'s 100 s default. `RunTimeout` is therefore **chosen**
  (30 s) with the non-reentrancy cost written down — and it bounds **execution only**, so a first call
  installing a new requirement can still exceed the caller's ceiling. Both facts are in the code.
- **`GatewayRoutingTests.cs:110` needed no change.** `BackendRoute` gained `Target` as a *defaulted*
  fourth parameter, so every existing three-argument construction still compiles.

### Choices the plan left open

- **The result channel is a file named by `TOOLFORGE_RESULT`, not fd 3** — portable across both
  platforms this builds on, and simpler for the runner to write.
- **A harness is appended below the written source**, so a tool defines `main(args)` and *returns*.
  Appended rather than wrapped so a traceback's line numbers still point at the lines the model wrote.
- **A `main` returning `None` is an error, not a null result.** Found by a failing test: the runner
  originally dumped `None` as `null`, which made the "you printed instead of returning" branch
  unreachable — the single least useful answer to give a model in a fix loop.
- **`RuntimeToolPublisher` was written during A3, not A4.** The shared test fixture stands up both
  surfaces, so the runtime half had to exist for A3's tests to compile. A4's *tests* stayed in A4.
- **`Program.cs` (A5) was written during A3** for the same reason: a Web SDK project does not build
  without an entry point.

### Things that had to change and were not foreseen

- **A source-generated JSON context cannot serialize a promoted tool's arguments.** They arrive boxed
  as `JsonElement`, which is the polymorphic case source generation refuses. Found by a real call
  failing, not by a test; the arguments are now written with an explicit `Utf8JsonWriter`.
- **Class-level test parallelism is not survivable here.** Three classes each running two Kestrel
  listeners plus Python subprocesses produced socket errors inside unrelated assertions. Serialised
  (the calendar suite's precedent, different reason), and it is also *faster*: 1m14s against 2m26s.
- **The root `Dockerfile` was modified after all**, against this plan's "deliberately NOT modified".
  K16's substance is intact — no `ProjectReference`, no `COPY` entry, no `NETSDK1152` exposure — but
  its documented gateway `docker run` now exits 2, so the comment was replaced rather than left
  broken. A comment, not a build change.
- **ToolForge got its own API key.** The plan had it reading `MCP_API_KEY`, which is the scraper's;
  sharing it would hand arbitrary code execution to every holder of a scraping key and tie their
  rotations together. One compose line.

### Review findings fixed before the MR

A `critic` pass found two defects that tests did not: `run_tool`/`list_tools` **threw** on a badly
shaped name (the most reachable state in the fix loop, since `write_tool` has just rejected that
name), and the runtime surface **never applied the 8 KB budget** it documented, leaving an oversize
result to be cut silently on Agent42's side. Both are fixed, tested, and verified in the built image.
It also found four overstated comments and a batch of Repo A doc debt, all corrected.

### Not done

- **B1–B4 (M1–M5) were not run**, and the reason first written down was wrong: it said "real DNS",
  contradicting K8. The actual blocker is a **configured chat model** — the measurements target
  loopback via the AppHost, but every one of them needs a model that can reason through a fix loop,
  which needs the operator's own provider credentials. Recorded as open in the design note.
- **A8's public deployment** likewise waits on the operator. Its network shape *is* settled and
  verified: the gateway resolves `toolforge` on the compose network and reaches both surfaces, and
  the same name does not resolve on the default bridge.

### Residuals accepted, not closed

- The three ToolForge port constants (its own `Program`, the gateway's, the image's `EXPOSE`) cannot
  be shared, because K16 forbids the project reference that would let them. Comments in both places
  name each other; nothing asserts it.
- The container health probe dials one port, so it reports on the authoring surface only.
- The production `backends` array is top-level statements and is not directly asserted; the tests
  build their own.
- A written tool can still collide with **another MCP server's** tool name. The reserved list covers
  this repo's static names only.
