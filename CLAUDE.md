# Agent42

> Personal AI chat agent ("Agent 42"): Orleans-hosted conversations wrapping Microsoft
> Agent Framework agents on Azure OpenAI, with a static web UI, deployed to Azure Container Apps.

**Stack:** .NET 10 · Orleans 10.3 (grains, Cosmos persistence + reminders) · Microsoft Agent
Framework 1.19 · ASP.NET Core minimal APIs · Aspire 13.5 (local orchestration) · vanilla-JS
frontend in `wwwroot/` · Bicep + GitHub Actions → Azure Container Apps

## Commands

| Task | Command |
|------|---------|
| Install | `dotnet restore Agent42.slnx` |
| Run dev (offline) | `dotnet run --project tools/Agent42.DevHost` — canned LLM, in-memory grains; UI at http://localhost:61326 |
| Run full stack | `dotnet run --project src/Agent42.AppHost` — Aspire + Cosmos emulator (needs Docker) |
| Test | `dotnet test Agent42.slnx` |
| Lint | `dotnet format Agent42.slnx --verify-no-changes` |
| Type check | `dotnet build Agent42.slnx` |

## Hard rules

1. **Never commit or push to a protected branch.** The list lives in ONE place —
   `branches.protected` in `.agentic/quality-gates.yaml` — and is enforced from there
   by the agent hook (`.claude/hooks/protect-main.mjs`) and the human hook (`lefthook.yml`).
   Always a branch (`feature/*`, `fix/*`, `hotfix/*`) + MR.
2. **Code is the source of truth.** Docs in this repo are pointers and rationale, never
   authority on structure. On conflict, trust code and fix the doc in the same MR.
3. **Quality gates** live in `.agentic/quality-gates.yaml` — read them, never hardcode
   thresholds. Tests are written alongside implementation, never deferred.
   **Every gate there must name a measuring tool in this repo (ADR 0050)** — a threshold
   nothing measures cannot fail, so it is not a gate. Adding one without tooling fails
   `QualityGateTests`. Never estimate an "Actual" value to fill a gate table; report
   SKIPPED and fix the gate.
4. **Dependency versions are resolved, never recalled.** When adding a package, never
   write a version number from memory — let the tooling resolve the latest stable
   (`dotnet add package X`, `npm install X@latest`) and pin it where the repo pins
   versions (e.g. `Directory.Packages.props` under Central Package Management).
5. **Azure RBAC changes are never executed by the agent.** Prepare the exact
   `az role assignment` / `az rest` command and hand it to the user to run.

## Where knowledge lives

| What | Where | When to use |
|------|-------|-------------|
| Decisions + rationale | `.agentic/decisions/` (ADRs) | Before proposing any architectural change, grep here for prior art and supersessions |
| Non-obvious constraints | `.agentic/gotchas.md` | Read the entries for your area before changing it |
| Canonical code shapes | Exemplar paths listed in `.agentic/gotchas.md` | Copy the exemplar's shape for new code; never invent structure |
| Multi-session plans | `.agentic/specs/` | Active plans; move to `specs/completed/` when done |

## Working agreement (the loop)

**Choosing a workflow:** nontrivial change → `/plan`, then `/execute-plan` (per-step
test gating) — each in a fresh session (`/clear`); the spec in `.agentic/specs/` is
the complete handoff between them. Small urgent fixes go direct — minimal diff,
regression test mandatory, always a `fix/*` or `hotfix/*` branch. Run `/audit-docs`
periodically and after large refactors.

**Before a change:** read `gotchas.md` entries for the touched area → open the relevant
exemplar file → validate assumptions with search, not memory.

**After a change:** made a decision with alternatives? → `/decide`. Hit a trap the code
can't express? → `/gotcha`. Diff invalidates a doc claim? → fix it in the same MR
(`/sync-docs`). Then run test + lint before reporting completion.

**Parallel subagents:** for read-only fan-out (verifying claims, locating shapes,
mining history) dispatch `scout` subagents (`.claude/agents/scout.md` — cheap model
tier, evidence-or-unverified discipline) and keep judgment in the main session.
Dispatching two or more write-capable subagents concurrently requires worktree
isolation (`isolation: "worktree"`) — or run them sequentially. Parallel writers
in one checkout corrupt each other's edits. For plan steps, dispatch
`step-implementer` subagents (`.claude/agents/step-implementer.md`) — they carry
the per-step discipline; plain subagents don't.

**Convention enforcement:** conventions here are enforced by tooling
(`dotnet format` whitespace/IDE analyzers — checked in CI; lefthook pre-push +
`.claude/hooks/protect-main.mjs` for branch protection; **`SonarAnalyzer.CSharp` for
cognitive complexity, and exactly that one rule** — ADR 0050). If you find yourself about
to document a mechanical rule, add a lint rule instead.

## Repo-specific notes

This section only points to where things are and lists the rules that apply everywhere. Detail belongs in the
ADR (the *why*) and in `.agentic/gotchas.md` (the *trap*). Before touching a feature, read its ADRs
from the map below, plus any ADR that amends them (grep for the number). Don't add feature write-ups here.

### Project facts

- Personal/experimental project (Peter Bons). There's exactly one external consumer surface: the
  `Hindsight.AgentFramework.Net` package from https://github.com/Expecho/Hindsight.AgentFramework.Net
  (ADR 0056). Its public API and issue tracker are owned. Everything inside Agent42 is private and can
  change freely.
- Deploy: push to `main` → `.github/workflows/deploy.yml` → ACR `crexpechohosting`, Bicep to RG
  `rg-agent42`. App runs behind Entra ID Easy Auth at chat.agent42.nl.
- The AI Foundry account `Agent42` is in a *different* RG (`agent42`), where the deploy SP holds only
  role-assignment rights. Read gotchas before touching IAM/infra.
- **No operator model of any kind** (ADR 0019 + 0030): chat, embedding, image and speech models are all
  per-user providers (ADR 0018). A user with none gets an empty picker and a gated composer.
- Frontend is vanilla JS: native ES modules in `src/Agent42.Backend/wwwroot/js/` with no build step,
  and `main.js` is the composition root (ADR 0041).
- Both projects use feature folders with a **flat namespace** (ADR 0039/0040). See the cross-cutting rules below.

### Manual prerequisites (GitHub Actions secrets and config)

| Input | Missing means |
|-------|---------------|
| `SEARXNG_SECRET` | **App down.** The sidecar crash-loops, and ACA gates revision health on every container (ADR 0011) |
| `CONTEXT_API_KEY` | `fetch_web_page` fails on each call. The app boots (ADR 0027) |
| `GOOGLE_MAPS_API_KEY` | Location returns coordinates without an address (ADR 0012) |
| `HINDSIGHT_API_KEY` | No long-term memory, and zero calls are made. This is the operator's egress switch (ADR 0056/0057) |
| `VAPID_PRIVATE_KEY` | Web Push is hidden (ADR 0028) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No App Insights export. The app boots and sends telemetry to OTLP only (ADR 0077) |
| `Dashboard:OwnerOid` (committed, currently blank) | `/dashboard` denies everyone. **Never set it to `local-dev`** (ADR 0062) |
| MCP OAuth redirect URI `https://chat.agent42.nl/api/mcp/oauth/callback` | Must be registered in each provider's app registration (ADR 0025) |

### Cross-cutting rules (each has a gotchas entry)

1. **Agents/Backend types stay in the flat root namespace.** Cosmos grain storage writes `$type` with the
   full CLR name, so a folder-shaped namespace makes every stored document unreadable (ADR 0039).
   IDE0130 is disabled on purpose. `[Alias]` protects only the binary path.
2. **New persisted fields are trailing `[Id(n)]` with a default, and need no migration.** Prove them on the Cosmos
   JSON path (`GrainStorageSerializerTests`), not only the binary one. Never write a collection expression
   into `IReadOnlyList<T>` grain state.
3. **Every MR that touches `wwwroot/js/` bumps `CACHE` in `sw.js`.** `SHELL` is all-or-nothing, and a module
   unreachable from `main.js` is invisible to every source-text guard (ADR 0041). The SW never caches `/api`.
4. **Easy Auth `excludedPaths`** in `infra/modules/container-app.bicep` matches **exactly** unless the entry
   ends in `/*` (ADR 0042), which `EasyAuthExclusionTests` pins. An excluded endpoint is public: it
   self-enforces, fails closed, and takes the userId from a directory grain, **never** `GetUserId()`.
   `UseForwardedHeaders` must stay the first middleware.
5. **A tool's return value is persisted into the 2 MB session** (ADR 0004). Truncate it. A continuous
   agent chat is one unbounded grain (ADR 0016/0068).
6. **Turn-scoped grain fields live for the activation.** Clear them explicitly. `ReceiveReminder` does not
   drain `ChatCoreAsync`, so each reset is needed in both places (ADR 0012/0034/0075).
7. **Registrations:** a new mandatory grain dependency must be registered in every silo host (Backend,
   DevHost, tests). For endpoint dependencies, prefer optional `IServiceProvider.GetService` resolution (the
   `/api/speech` pattern). A new `AgentApi.*` partial that `MapAgentApi` never calls silently deletes its
   endpoints.
8. **A named `HttpClient` inherits `AddServiceDefaults`' retry pipeline.** Opt out explicitly, and give a
   proxy its own client.
9. **S3776 cognitive complexity is a build error at 15** (ADR 0050/0051). Split a feature's registration
   into its own folder, as `HindsightRegistration` does. Don't game the metric.
10. **Never reference the community `OrleansDashboard` package.** It crashes the silo on Orleans 10.x (ADR 0062).
11. **A new selectable tool key lands OFF for anyone who ever pressed Save** (`null == all`, ADR 0021).
    Wildcards (`mcp`, `skills`) must be expanded before the picker sees them. The rule the model reads is the
    **tool description**, not a `SKILL.md`.
12. **Single replica is load-bearing** (in-process registries, the `/logs` ring buffer, the local-bridge
    socket registry, the Copilot SessionFs write gate). `infra/main.bicep` pins min = max = 1.
13. **Egress is a design property.** Anything that sends conversation content to a third party is an ADR 0013
    extension and must be recorded as such in its ADR. Don't write "following ADR 0008" for an always-on,
    ungated provider: 0008 rejected that shape.

### Feature map (area → ADRs)

| Area | ADRs |
|------|------|
| Core: grain per conversation, auth, persona snapshot, blob attachments, profile | 0001, 0002, 0003, 0004, 0005 |
| Telemetry, `/logs` console, usage dashboard, Orleans dashboard, App Insights export | 0009, 0010, 0031, 0054, 0062, 0077 |
| Identity alias (second Entra account, an **authorization grant**); Easy Auth session lifetime, login hint, sign out | 0074, 0090 |
| Model providers: multi-provider, Foundry key, per-user overlay, no baseline, Responses API, hosted tools | 0013, 0014, 0015, 0017, 0018, 0019, 0033 |
| Image generation, speech (STT/TTS/voice loop), reasoning output | 0006, 0020, 0037, 0038, 0071 |
| Document RAG, per-user embeddings, web documents into RAG | 0007, 0008, 0030, 0066 |
| Web search (SearXNG sidecar, engines, proxied UI), page scrape (context.dev) | 0011, 0027, 0059, 0060 |
| Location (a client tool since 0093) | 0012, 0093 |
| Selectable tools: three layers, skills, uploaded skills, memory | 0021, 0048, 0049, 0057 |
| Long-term memory (Hindsight; **no delete path**) | 0056, 0057 |
| MCP client: config, OAuth (per-request refresh + in-turn reconnect + auto re-send), per-server/per-tool selection, test, URL seeding, local bridge | 0024, 0025, 0026, 0058, 0064, 0067, 0069, 0076, 0081, 0087 |
| MCP server (agents exposed via `/api/agent-mcp`) | 0070 |
| Stored agents: history mode, API key + `/api/agent-run`, retention | 0016, 0022, 0068 |
| Research agent: seeded built-in `Kind`, todo/loop/judge, history fold, live checklist | 0082 |
| Copilot agent kind: GitHub Copilot SDK loop, BYOK, Agent42 tools as custom tools, native skills, SessionFs in Blob, history mirror, phase-1 trust boundary | 0085 |
| Calendar: now an external MCP server (0053 superseded) | 0063 |
| Push: away replies, model-invoked | 0028, 0075 |
| Conversations: incognito, share links, follow-ups, pins, groups, chat search, stream recovery, stop generation, owner export (Markdown/JSON/ZIP, paged export-all), edit/regenerate last turn, fork | 0034, 0035, 0036, 0045, 0046, 0047, 0061, 0073, 0078, 0091, 0092 |
| PWA and UI: share target, offline cache, composer, topbar, long press, prefs rail, tool-call folding, rich inline rendering (mermaid/code/math/charts), collapsible sidebar | 0023, 0032, 0043, 0044, 0052, 0065, 0072, 0080, 0083, 0086 |
| Config health tab | 0029 |
| Artifacts / canvas: model-created versioned pages, SVG, docs and code in a sandboxed pane; CSP sandbox header; session compaction | 0083, 0084 |
| Client tools and shared state: browser-run tools (confirm, artifact pane, browser context, location) paused on the stream, per-run broker, shared JSON state with snapshots; why not AG-UI | 0089, 0088, 0093 |
| Layout and quality: feature folders, ES modules, gates, complexity, test clusters | 0039, 0040, 0041, 0050, 0051, 0055 |
