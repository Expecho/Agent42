# Agent42

> Personal AI chat agent ("Agent 42"): Orleans-hosted conversations wrapping Microsoft
> Agent Framework agents on Azure OpenAI, with a static web UI, deployed to Azure Container Apps.

**Stack:** .NET 10 · Orleans 10.2 (grains, Cosmos persistence + reminders) · Microsoft Agent
Framework 1.13 · ASP.NET Core minimal APIs · Aspire 13.3 (local orchestration) · vanilla-JS
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
`.claude/hooks/protect-main.mjs` for branch protection; no extra Roslyn analyzers yet).
If you find yourself about to document a mechanical rule, add a lint rule instead.

## Repo-specific notes

- Personal/experimental project (Peter Bons); no external consumers.
- Deploy: push to `main` → `.github/workflows/deploy.yml` → ACR `crexpechohosting`,
  Bicep to RG `rg-agent42`; app is behind Entra ID Easy Auth at chat.agent42.nl.
- The AI Foundry account `Agent42` lives in a *different* RG (`agent42`); the deploy
  SP only holds role-assignment rights there — see gotchas before touching IAM/infra.
- Image attachments live in an Azure Storage account (private `attachments` container,
  provisioned in `infra/modules/storage.bicep`); bytes stay out of the 2 MB Cosmos session
  item (ADR 0004). Its Storage Blob Data Contributor grant is now in Bicep
  (`infra/modules/storage-role-assignment.bicep`), which works only because the deploy SP holds
  RBAC Administrator on `rg-agent42` (a one-time manual elevation — see gotchas). The OpenAI
  grant stays manual (different RG).
- Document RAG (ADR 0007 + 0008): uploaded documents (PDF/Office/text) are **ingest-only** — extracted,
  chunked, embedded (`text-embedding-3-large`) and stored in a Cosmos NoSQL **vector** container
  (`doc-chunks`), retrieved by an **always-on `TextSearchProvider`** (Agent Framework) that auto-injects
  the top excerpts before each reply — attached only when the conversation has an ingested document
  (ADR 0008 replaced the earlier agentic search tool); they never become chat
  attachments (off the image `UriContent` path). The embedding deployment is a **manual prerequisite**
  in the `Agent42` AI Foundry account (needs no new RBAC — the existing OpenAI User grant covers it),
  and the Cosmos vector path is prod-only (offline/AppHost use an in-memory index). See gotchas before
  touching the vector container or embedding config.
- Web search (ADR 0011): two model-invoked tools — `web_search` and `fetch_web_page` — over a
  **self-hosted SearXNG sidecar** in the backend's Container App, reached over `localhost:8888`
  (8080 is the backend's own ingress). Agent Framework's `HostedWebSearchTool` does **not** work
  here: our client is Chat Completions, which rejects `web_search_options` with HTTP 400 (that is
  what `f14de0e`/`81b2f93` were about). The sidecar image is `infra/searxng/` (thin image over a
  pinned upstream tag, config baked in). Self-hosted ≠ private: SearXNG is a *meta*search engine and
  forwards every query to DuckDuckGo/Bing — what it buys is no API key, no bill, no per-user
  attribution. A **`SEARXNG_SECRET` GitHub Actions secret is a manual prerequisite** (any long random
  string): the sidecar won't start without it, and since ACA gates revision health on *every*
  container in the replica, a crash-looping sidecar takes the **backend** down too. Three things to
  read before touching this: a tool's *return value* is persisted into the 2 MB session;
  `fetch_web_page` is an SSRF sink **and** a prompt-injection exfiltration channel (its allowlist pins
  whole URLs, not hosts — a host allowlist is bypassable); and a named `HttpClient` silently inherits
  `AddServiceDefaults`' retry pipeline. All three are in gotchas.
- User location (ADR 0012): a `get_current_location` tool returning the user's coordinates and a
  reverse-geocoded street address. `navigator.geolocation` is a *browser* API and tools run server-side,
  and the chat path is a one-way NDJSON stream — so a tool cannot ask the browser for a fix mid-turn. The
  position therefore **rides the chat request** (`ChatRequest.Location` → `ChatAsync(…, location)`) into a
  **turn-scoped grain field that must be cleared explicitly** (it lives for the *activation*, not the call
  — see gotchas; that is the bug the reminder-path test exists to catch). A **`GOOGLE_MAPS_API_KEY` GitHub
  Actions secret is a manual prerequisite** — a Google Cloud key with the **Geocoding API** enabled and
  restricted to it — but unlike `SEARXNG_SECRET` it is deliberately **not on the availability path**: with
  no key the app starts normally and the tool reports coordinates without an address. Three things to read
  before touching this: the tool is **not** an SSRF sink and must not grow a `PrivateAddressGuard` (two
  narrow properties keep that true, and a test pins them); the browser's authorization gate is
  **two-legged** because WebKit/iOS has no geolocation Permissions API; and this feature puts the user's
  **street address** into a conversation that the ADR 0011 exfiltration channel can still drain — the
  `websearch` skill only *mitigates* that. All three are in gotchas.
- Model providers (ADR 0013): a conversation can run on **any OpenAI-compatible endpoint**, not just Azure
  Foundry. The `Models:` config section maps provider keys to either an `AzureOpenAI` provider (managed
  identity) or an `OpenAICompatible` one (API key) — **GitHub Models** is wired in prod; Ollama/vLLM work
  by config alone. A model is named by a **qualified `provider:model` id split on the FIRST colon**
  (GitHub's own ids contain a `/`); a bare id is legacy and resolves against the default provider, which
  is why this needed no state migration. A **`GH_MODELS_TOKEN` GitHub Actions secret is a manual
  prerequisite** — a PAT with the `models:read` scope, and note the name: `GITHUB_*` is a **reserved
  prefix** Actions will not let you create. Like `GOOGLE_MAPS_API_KEY` and unlike `SEARXNG_SECRET`, it is
  **not on the availability path**: with no token the provider is skipped with a warning and the app
  serves on Foundry alone. **The headline consequence: picking a third-party model sends the conversation
  — document excerpts, image SAS URLs, the user's street address — outside the Entra tenant.** That is why
  the default stays Foundry and the pick is explicit. Four things to read before touching this: the
  first-colon split and why `DeploymentName` keeps its name; `AgentSkillsProvider` is a **tool** (a
  no-tools model must drop it, while the RAG provider must stay); the vision gate must cover **history**,
  not just the new turn, or old image SAS URLs still leak; and the Foundry provider's endpoint/ARM
  coordinates are **inherited** from `AzureOpenAI:*`, never copied. All four are in gotchas — along with
  the measured GitHub caps (16k/8k input tokens, failing with an **HTTP 413**, not a 400).
- Frontend is deliberately vanilla JS in `src/Agent42.Backend/wwwroot/` (no SPA build).
