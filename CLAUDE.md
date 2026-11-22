# Agent42

> Personal AI chat agent ("Agent 42"): Orleans-hosted conversations wrapping Microsoft
> Agent Framework agents on Azure OpenAI, with a static web UI, deployed to Azure Container Apps.

**Stack:** .NET 11 · Orleans 10.4 (grains; Cosmos or Postgres persistence + reminders, `Storage:Provider`) · Microsoft Agent
Framework 1.28 · ASP.NET Core minimal APIs · Aspire 17.0 (local orchestration) · vanilla-JS
frontend in `wwwroot/` · Bicep + GitHub Actions → Azure Container Apps

## Commands

| Task | Command |
|------|---------|
| Install | `dotnet restore Agent42.slnx` |
| Run dev (offline) | `dotnet run --project tools/Agent42.DevHost` — canned LLM, in-memory grains; UI at http://localhost:61326 |
| Run full stack | `dotnet run --project src/Agent42.AppHost` — Aspire + Cosmos emulator (needs Docker and Aspire CLI 17+) |
| Self-host (no Azure) | `docker compose up -d` after `cp .env.example .env` (pulls the published images, ADR 0130; `-f compose.yaml -f compose.build.yaml up -d --build` builds from source) — Postgres + a volume, UI at http://localhost:8080, no auth (ADR 0127) |
| Test | `dotnet test Agent42.slnx` |
| Lint | `dotnet format Agent42.slnx --verify-no-changes` |
| Type check | `dotnet build Agent42.slnx` |
| Android app (test, lint, build) | `./gradlew test lint assembleDebug` in `tools/Agent42.Android` — needs JDK 21 + Android SDK (`ANDROID_HOME`); CI runs it in `.github/workflows/android.yml` |

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
- Deploy: push to `main` → `.github/workflows/deploy.yml` (`preflight` → shared infra → build/push → Bicep).
  Tenant-specific values (registry, environment, resource group, domain, client id) are **repo variables**, not code
  (ADR 0128). The variables are documented in `docs/features/azure-deployment.md` (prod's values live only in the repo's Actions variables), which also covers deploying to another
  tenant with `infra/bootstrap.sh`. Prod runs behind Entra ID Easy Auth at chat.agent42.nl. Read gotchas before
  touching IAM/infra.
- The AI Foundry account `Agent42` is in a *different* RG (`agent42`) and is dormant (ADR 0017/0019); the deploy no
  longer grants anything on it.
- **No operator model of any kind** (ADR 0019 + 0030): chat, embedding, image and speech models are all
  per-user providers (ADR 0018). A user with none gets an empty picker and a gated composer.
- Frontend is vanilla JS: native ES modules in `src/Agent42.Backend/wwwroot/js/` with no build step,
  and `main.js` is the composition root (ADR 0041).
- Both projects use feature folders with a **flat namespace** (ADR 0039/0040). See the cross-cutting rules below.

### Manual prerequisites (GitHub Actions secrets and config)

| Input | Missing means |
|-------|---------------|
| Repo variables `AZURE_RESOURCE_GROUP` + `AUTH_CLIENT_ID` (and, for prod, the rest of the table in `docs/features/azure-deployment.md`) | The deploy fails `preflight`, by design: an absent variable expands to an empty string, and empty means "off" (ADR 0128) |
| `SEARXNG_SECRET` | **App down.** The sidecar crash-loops, and ACA gates revision health on every container (ADR 0011) |
| `GOOGLE_MAPS_API_KEY` | Location returns coordinates without an address (ADR 0012) |
| `HINDSIGHT_API_KEY` | No long-term memory, zero calls are made, and the picker shows no memory row (ADR 0131). This is the operator's egress switch (ADR 0056/0057) |
| `VAPID_PRIVATE_KEY` | Web Push is hidden (ADR 0028) |
| `MAILGUN_SIGNING_KEY` + MX for the in.agent42.nl subdomain → Mailgun + a Route forwarding to `/api/email-in` | `/api/email-in` answers 503 and no inbound email is accepted. The app boots (ADR 0096) |
| `GH_OAUTH_CLIENT_SECRET` + `GitHub:OAuth:ClientId` (committed) + OAuth App callback `https://chat.agent42.nl/api/providers/github/callback` | "Sign in with GitHub" is hidden and `/api/providers/github/start` answers 503. The app boots (ADR 0108) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No App Insights export. The app boots and sends telemetry to OTLP only (ADR 0077) |
| `ANDROID_KEYSTORE_BASE64` + `ANDROID_KEYSTORE_PASSWORD` + `ANDROID_KEY_ALIAS` + `ANDROID_KEY_PASSWORD` | The Android app's release job fails and no APK is published. PR checks still run. The key must never change, or installed apps can't update |
| `signalEnabled` (Bicep param, default `false`) | Signal isn't offered and no signal-cli runs. Turn it on only after the plan's Azure Files measurement (Step 24) and confirming the Copilot-shell risk in ADR 0111's phase 3 amendment |
| `Access:Owners` (Entra object ids; ships empty, set from the `ACCESS_OWNERS` repo variable) | An empty list makes nobody an owner, so `/dashboard`, `/logs`, `/api/diagnostics` and `/api/config-health` answer 404 to everyone. **Never list `local-dev`** (ADR 0062/0121) |
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
6. **Per-run state is a `TurnContext`, never a grain field (ADR 0106).** Pass it on the grain side, and read
   `TurnContext.CurrentOrClosed` in tools and providers; a missing context fails closed. Two runs can be in flight
   on one activation, so a field would be shared by both. A handle that names a running turn (`_turnCts`) is
   published by identity.
7. **Registrations (ADR 0105):** a registration identical in every host goes in `AddAgent42Silo()` (grain side) or
   `AddAgent42Api()` (endpoint side); one that varies is a port each host (Backend, DevHost, test silos) registers
   before calling them. `AddAgent42Silo()` fails startup on a missing **direct** grain constructor dependency, but
   not on a transitive one. For endpoint dependencies, prefer optional `IServiceProvider.GetService` resolution (the
   `/api/speech` pattern). A new `AgentApi.*` partial that `MapAgentApi` never calls silently deletes its
   endpoints; `AgentApiReachabilityTests` catches it.
8. **A named `HttpClient` inherits `AddServiceDefaults`' retry pipeline.** Opt out explicitly, and give a
   proxy its own client.
9. **S3776 cognitive complexity is a build error at 15** (ADR 0050/0051). Split a feature's registration
   into its own folder, as `HindsightRegistration` does. Don't game the metric.
10. **Never reference the community `OrleansDashboard` package.** It crashes the silo on Orleans 10.x (ADR 0062).
11. **A new selectable tool key lands OFF for anyone who ever pressed Save** (`null == all`, ADR 0021).
    Wildcards (`mcp`, `skills`) must be expanded before the picker sees them. The rule the model reads is the
    **tool description**, not a `SKILL.md`.
12. **Single replica is load-bearing** (in-process registries, the `/logs` ring buffer, the local-bridge
    socket registry, the Copilot SessionFs write gate, the local file stores and Postgres advisory-lock gateway lease (ADR 0127), the agent fleet's running-task registry, the Copilot chat workspaces
    share mounted into that one replica, the chat gateway's sessions, inbound dedupe and per-chat queues, the signal-cli
    child process and its store). `infra/main.bicep` pins min = max = 1. A revision rollover still overlaps two replicas
    briefly; the chat gateway covers that with a Blob lease (ADR 0111), and anything else that holds an outbound
    connection or a single-writer store needs the same (the signal-cli daemon runs only while the lease is held).
13. **Egress is a design property.** Anything that sends conversation content to a third party is an ADR 0013
    extension and must be recorded as such in its ADR. Don't write "following ADR 0008" for an always-on,
    ungated provider: 0008 rejected that shape. Chat-app replies (ADR 0111) are one: opt-in per connection, and stored
    by the chat platform under the user's own bot.
14. **The owner tier is advisory, never isolation (ADR 0121).** `IOwnerPolicy` (`Access:Owners`, shipped empty and set from the `ACCESS_OWNERS` repo variable, post-alias id,
    fails closed) gates `/dashboard`, `/logs`, `/api/diagnostics` and `/api/config-health` through `OwnerGate`, and decides whose
    span content `NonOwnerContentRedactor` keeps. Any signed-in user can run the Copilot shell, which can forge the principal
    header, so never write "only owners can" as a security claim. Usage is scoped by the object id, never the email.
### Feature map (area → ADRs)

| Area | ADRs |
|------|------|
| Core: grain per conversation, auth, persona snapshot, blob attachments, profile | 0001, 0002, 0003, 0004, 0005 |
| Telemetry, `/logs` console, usage dashboard + estimated cost (checked-in price table), Orleans dashboard, App Insights export | 0009, 0010, 0031, 0054, 0062, 0077, 0104, 0110 |
| Identity alias (second Entra account, an **authorization grant**); Easy Auth session lifetime, login hint, sign out; **invite-only sign-in (assignment required + B2B guests, no `allowedPrincipals`) + the advisory owner tier** (`Access:Owners`, `OwnerGate`, own-data-only usage, non-owner telemetry redaction) | 0074, 0090, 0121 |
| Model providers: multi-provider, Foundry key, per-user overlay, no baseline, Responses API, hosted tools, Anthropic (Claude) kind + compatibility shim, prompt-prefix caching (stable prefix, Azure cache key, cached tokens), GitHub Copilot kind (Sign in with GitHub, copilot-agents-only models), Auto model (a per-user Decision-1 router kind, sticky per-turn routing, reserved `auto` value; picture requests route to an image model as a one-off turn; Auto can be the default model) | 0013, 0014, 0015, 0017, 0018, 0019, 0033, 0103, 0104, 0108, 0137, 0138, 0139 |
| Image generation, speech (STT/TTS/voice loop), server-side transcription model (chat voice notes), reasoning output (+ summary fallback, visible empty replies) | 0006, 0020, 0037, 0038, 0071, 0113, 0136 |
| Document RAG, per-user embeddings, web documents into RAG, sequential `read_document` (re-ingest replaces), fleet workspace documents | 0007, 0008, 0030, 0066, 0098, 0101 |
| Web search (SearXNG sidecar, engines, proxied UI); no page fetch (removed, 0125) | 0011, 0059, 0060, 0125 |
| Location (a client tool since 0093) | 0012, 0093 |
| Selectable tools: three layers, skills, uploaded skills (the two shipped skills follow their tool key, no picker row), memory | 0021, 0048, 0049, 0057, 0126 |
| Long-term memory (Hindsight; **no delete path**; picker row hidden without a key) | 0056, 0057, 0131 |
| MCP client: config, OAuth (per-request refresh + in-turn reconnect + auto re-send), per-server/per-tool selection, test, URL seeding, local bridge, Chrome extension bridge client + MCP image results, elicitation (form + URL), MCP Apps (sandboxed `ui://` views, per-server switch, CSP-declared egress) | 0024, 0025, 0026, 0058, 0064, 0067, 0069, 0076, 0081, 0087, 0095, 0109, 0133 |
| MCP server (agents exposed via `/api/agent-mcp`) | 0070 |
| Stored agents: history mode, API key + `/api/agent-run`, retention, outbound webhook (signed, SSRF-guarded egress), inbound email address (Mailgun, untrusted-in) | 0016, 0022, 0068, 0094, 0096 |
| Research agent: seeded built-in `Kind`, todo/loop/judge, history fold, live checklist | 0082 |
| Copilot agent kind: GitHub Copilot SDK loop, BYOK, Agent42 tools as custom tools, native skills, SessionFs in Blob, history mirror, phase-1 trust boundary (set aside for fleet Copilot agents by 0097); built-in shell/file/URL tools in chats, auto-approved, kill switch, Azure Files workspaces (0107); on the user's own GitHub Copilot plan via a per-session token (0108) | 0085, 0097, 0107, 0108 |
| Agent fleet (recursive agent teams, `/fleet/`): in-process runner (not a grain), Blob store + SSE replay, judge/fix/new team, Copilot agents with built-in tools, per-task tool override, attached files + workspace-scoped document search, shared canvas (versioned pages, live pane), started from a chat (`start_fleet_task`, auto-approved, result as a background turn), live graph view, team todo list (checklist in Details + graph badge), optional clarify interview before planning, optional per-task self-review loop on the result-writing agent, read-only public share link (request, result, canvas, workspace files), analyzer panel on `/fleet/` (a fixed-tool `fleet-analyzer` agent kind: diagnose a task, its events, files and own spans; stop or re-run it, without a new team), nightly opt-in "dream" that learns lessons about team design and applies them to every planner (hourly reminder, a per-user lessons Blob, Lessons view), per-user Advanced settings (25 overridable `Fleet:*` limits, resolved once per task) | 0097, 0099, 0101, 0102, 0115, 0116, 0117, 0118, 0119, 0120, 0122, 0123, 0124, 0135 |
| Calendar: now an external MCP server (0053 superseded) | 0063 |
| Push: away replies, model-invoked, user-initiated test push | 0028, 0075, 0100 |
| Chat apps: one in-process gateway, each user's own bots (Telegram, Slack, Discord now; Signal built but dormant until `signalEnabled`; WhatsApp planned), outbound connections, owner pairing, Blob lease, files and images, voice notes, groups, delivery to the origin or home chat; Signal as a dedicated number in a lease-gated signal-cli child process on Azure Files | 0111, 0113 |
| Conversations: incognito, share links, follow-ups, pins, groups, chat search, stream recovery, stop generation, owner export (Markdown/JSON/ZIP, paged export-all), edit/regenerate last turn, fork, past chats as a model tool (search + read, ranked) | 0034, 0035, 0036, 0045, 0046, 0047, 0061, 0073, 0078, 0091, 0092, 0112, 0134 |
| PWA and UI: share target, offline cache, composer, topbar, long press, prefs rail, tool-call folding, rich inline rendering (mermaid/code/math/charts), collapsible sidebar, ten colour palettes (a second axis beside light/dark, one shared `src/Agent42.Backend/wwwroot/shared/themes.css`) | 0023, 0032, 0043, 0044, 0052, 0065, 0072, 0080, 0083, 0086, 0129 |
| Config health tab | 0029 |
| Artifacts / canvas: model-created versioned pages, SVG, docs and code in a sandboxed pane; CSP sandbox header; session compaction | 0083, 0084 |
| Client tools and shared state: browser-run tools (confirm, artifact pane, browser context, location) paused on the stream, per-run broker, shared JSON state with snapshots; why not AG-UI | 0089, 0088, 0093 |
| Android companion app (`tools/Agent42.Android`): WebView sign-in + Easy Auth cookie, NDJSON contract golden file, full-duplex voice on the call audio path (amends 0079 for the app only), sideloaded signed APK | 0114 |
| Self-hosting: `Storage:Provider` = `azure` (default, prod) or `local` (Postgres + a filesystem volume, `compose.yaml`, no Azure account); ADO.NET grain storage with the same `$type` JSON, pgvector (no ANN index), file stores, attachment reference URLs inlined as bytes per request, no auth, no migration | 0127 |
| Deployment: tenant-portable IaC, create-or-reuse shared infra, repo variables, `preflight`, `infra/bootstrap.sh`; published multi-arch images on GHCR (compose pulls them, `az acr import` for Azure) | 0128, 0130 |
| Layout and quality: feature folders, ES modules, gates, complexity, test clusters, shared silo registration + reachability test, AgentGrain services + per-run TurnContext | 0039, 0040, 0041, 0050, 0051, 0055, 0105, 0106 |
