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
  RBAC Administrator on `rg-agent42` (a one-time manual elevation — see gotchas). The OpenAI managed-identity
  grant is **no longer required** (ADR 0017 — Foundry chat/embeddings authenticate by API key); leaving any
  existing grant is harmless.
- Document RAG (ADR 0007 + 0008): uploaded documents (PDF/Office/text) are **ingest-only** — extracted,
  chunked, embedded (`text-embedding-3-large`) and stored in a Cosmos NoSQL **vector** container
  (`doc-chunks`), retrieved by an **always-on `TextSearchProvider`** (Agent Framework) that auto-injects
  the top excerpts before each reply — attached only when the conversation has an ingested document
  (ADR 0008 replaced the earlier agentic search tool); they never become chat
  attachments (off the image `UriContent` path). The embedding deployment is a **manual prerequisite**
  in the `Agent42` AI Foundry account (authenticated by the same `AzureOpenAI:ApiKey` as chat — ADR 0017,
  no RBAC), and the Cosmos vector path is prod-only (offline/AppHost use an in-memory index). See gotchas before
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
- Model providers (ADR 0013, credential mechanism amended by ADR 0017, **baseline removed by ADR 0019**):
  **Read this override first.** As of ADR 0019 the app ships **NO baseline chat provider** — `Models:Providers`
  is `{}`, chat is **entirely per-user** (ADR 0018), and there is **no operator default model**. So in the
  bullet below: "the default stays Foundry", "serves on Foundry alone", the baseline GitHub Models secret
  prerequisite and its wiring, the "blank Foundry key is **fatal** — Foundry is the availability path" rule, and the shipped
  `foundry` provider's `Discover:false`/`gpt-5.4`/`AzureOpenAI:DeploymentName` details are all **superseded** —
  the availability path narrowed to the **embedding** key (`AzureOpenAI:ApiKey`, still fatal-if-blank), and a
  Foundry chat model is now something a **user** adds. A user with no provider gets an empty picker + gated
  composer; the grain stays valid via a no-op `NoModelChatClient`. The mechanism facts below still hold. A
  conversation can run on **any
  OpenAI-compatible endpoint**, not just Azure Foundry. The `Models:` config section maps provider keys to
  either an `AzureOpenAI` (Foundry) provider or an `OpenAICompatible` one — **both now reached with an API
  key** (ADR 0017 replaced Foundry's managed identity with an Azure OpenAI key). ~~**GitHub Models** is wired
  in prod~~ (ADR 0019: GitHub Models is now a **user-added** provider, not a baseline one); Ollama/vLLM work
  by config alone. A model is named by a **qualified `provider:model` id split on the FIRST colon**
  (GitHub's own ids contain a `/`); a bare id is legacy and resolves against the default provider, which
  is why this needed no state migration. ~~A GitHub Actions secret for the baseline GitHub Models provider
  was a manual prerequisite~~ (ADR 0019 removed the baseline `github` provider **and** its operator-level
  token wiring — a user supplies their own PAT per-provider now). **The
  headline consequence: picking a third-party model sends the conversation
  — document excerpts, image SAS URLs, the user's street address — outside the Entra tenant.** That is why
  the default stays Foundry and the pick is explicit. Four things to read before touching this: the
  first-colon split and why `DeploymentName` keeps its name; `AgentSkillsProvider` is a **tool** (a
  no-tools model must drop it, while the RAG provider must stay); the vision gate must cover **history**,
  not just the new turn, or old image SAS URLs still leak; and the Foundry provider's endpoint, **API key**
  and ARM coordinates are **inherited** from `AzureOpenAI:*`, never copied. All four are in gotchas — along
  with the measured GitHub caps (16k/8k input tokens, failing with an **HTTP 413**, not a 400). **ADR 0017
  changes for Foundry:** it authenticates by Azure OpenAI **API key** (a blank one is **fatal** — Foundry is
  the availability path, unlike a third-party key), ARM **discovery is off** (a data-plane key can't reach
  ARM, so the provider **declares** its deployments), and the default deployment is **`gpt-5.4`** (`gpt-4o`
  is not deployed in the account).
- User-configurable providers (ADR 0018, amends 0013 + 0017; **availability floor removed by ADR 0019**):
  a signed-in user adds/edits/deletes their own
  model providers — **including Foundry** — from the web UI (Preferences → model providers), per-user, with
  API keys persisted **encrypted**, and changes taking effect **without a restart**. The model is
  **baseline ⊕ per-user overlay**, but as of ADR 0019 the **baseline chat set ships EMPTY**, so chat is
  effectively the overlay alone (a provider must declare **≥1 model**; the resolver's effective default is a
  valid overlay default → the first declared model → `""`). ~~the env/`appsettings` set stays the boot-time
  **availability floor**~~ (only the **embedding** key is a floor now); a `UserProviderConfigGrain` holds the
  overlay, and `UserProviderResolver` merges them — reused by both a per-user chat-client factory (resolved
  on `AgentGrain` activation, cached by overlay **version**) and a per-user `/api/models` catalog. **A user
  override that resolves invalid falls back to baseline — overlays are best-effort, never fatal.** The
  headline consequence is 0013's, now reachable from an endpoint the **user** added: picking a third-party
  model still sends the conversation outside the Entra tenant, so the default stays Foundry. **A
  `dataprotection` blob container is a new prerequisite** (added to `storage.bicep`; the app MI already holds
  the grant). Traps to read before touching this: the overlay is best-effort / baseline wins (and user
  providers do **not** inherit `AzureOpenAI:*`) — and, in the same gotcha, a user endpoint is deliberately
  **not** SSRF-guarded (config, not a model/request — so `localhost` is accepted); secrets are DP-encrypted,
  never echoed, and the DP key ring **must persist** out-of-process in prod (else a restart orphans every
  secret); the per-user factory is resolved on activation **before** its first use, not injected (and every
  grain host must register `IUserChatClientFactoryProvider`). All are in gotchas. Offline (DevHost/tests) uses
  single-user adapters, so the providers **CRUD/UI** is exercisable there but overlay→picker reflection is
  only wired in prod (unit-tested via `UserModelCatalogTests`).
- Responses API on Foundry (ADR 0014; credential mechanism amended by ADR 0017): a provider-level
  `UseResponsesApi` flag routes **every** model of a Foundry provider through the OpenAI **Responses** API run
  **statelessly** (`store=false`) instead of Chat Completions — reached with **no package change** via the
  stable `OpenAI 2.12.0` client pointed at `{endpoint}/openai/v1` with an **`api-key`-header** policy carrying
  the Azure OpenAI key (ADR 0017 replaced ADR 0014's managed-identity bearer policy; the resource key
  authenticates `/openai/v1` via the `api-key` header — spike-confirmed 2026-07-16). It ships **on** in prod
  (`appsettings.json` sets `UseResponsesApi: true`; turning it off reverts to Chat Completions + SearXNG) and
  is a server-side transport choice, **not** user-selectable. Stateless is **mandatory** — the grain holds all
  history client-side (ADR 0004) — so the build uses `AsIChatClientWithStoredOutputDisabled`, and
  `includeReasoningEncryptedContent` is forced **`false`** (its `true` default 400s non-reasoning models like
  the default `gpt-5.4`). Read the "Responses API is stateful by default" gotcha before touching
  `Provider.Foundry`.
- Hosted tools on the Responses path (ADR 0015): a Responses Foundry model reaches OpenAI's **native**
  server-side `web_search` + `code_interpreter` (Chat Completions 400s on them — the reason the SearXNG
  sidecar exists, ADR 0011). The signal is a new `ModelCapabilities.SupportsHostedTools` bit (true only when
  the provider runs Responses); `AgentGrain.BuildAgent` then **swaps** — dropping the SearXNG
  `web_search`/`fetch_web_page` shims and registering `HostedWebSearchTool` + `HostedCodeInterpreterTool`.
  It's a swap, not an add, because `HostedWebSearchTool.Name` is literally `web_search` and would collide
  with the shim (see that gotcha). SearXNG stays for every Chat model, so it is **not** retired here.
- Per-agent history mode (ADR 0016): a stored agent carries a `Continuous` flag (trailing `[Id(7)]` on
  `AgentDefinition`, default `false`). Per-chat (default) mints a fresh `crypto.randomUUID()` per start, as
  before; **continuous** derives a **deterministic** conversation id `` `agent-${agent.id}` `` in the client, so
  every start collapses onto the one grain `{userId}/agent-{agentId}` and shares one ongoing history (a
  re-entry resumes it via `selectConversation`, not a blank thread). The flag is read **client-side and never
  snapshotted into `AgentBinding`** — it's routing, not persona — so a user can flip it for *future* starts
  without disturbing existing chats (ADR 0003), but the flip is inert on a continuous agent's already-bound
  grain. Trailing default = no migration. **The headline consequence: continuous funnels every chat into one
  unbounded grain, so it will eventually hit the 2 MB session cap (ADR 0004) and brick the conversation in
  prod — `DELETE /api/conversations/agent-{agentId}` (delete-to-reset) is the only escape hatch.** See gotchas.
- Image generation (ADR 0020): a new per-model capability bit `SupportsImageGeneration`, declared on an
  OpenAI-compatible model (a user-added provider, ADR 0018), makes a picked model **produce an image
  instead of a chat reply** — text-to-image, or image-to-image (edit) against this turn's attachment, else
  the most recent image in the conversation. It is a **dedicated model, not a tool** the text model calls.
  Generated images reuse the `attachments` blob container on the **assistant** side (extends ADR 0004 —
  **no new container/Bicep**) and re-appear on reload. Four things to read before touching this: image gen
  runs a **different API** (`IImageGenerationService` over the SDK `ImageClient`, not the chat client) via an
  **adapter run** (`ImageGenerationChatClient`) because AF 1.13 has no append-without-inference; the assistant
  image is **id-keyed** (message-level `assistantImageId`), never joined by assistant-message position (they
  are not 1:1 with turns); the branch runs **before** the vision gate (an image model *consumes* images); and
  the interface is **`IImageGenerationService`** because M.E.AI 1.13 ships a colliding `IImageGenerator`. All
  in the "assistant generated image is id-keyed" gotcha. **Headline consequence:** generating sends the prompt
  — and for edits the **input image bytes** — outside the Entra tenant (extends ADR 0013's egress).
- Selectable tools (ADR 0021): a user chooses **which tools** an assistant may use, remembered at three
  layers — a **user default** (Preferences → default tools, every new regular chat), a **per-conversation
  override** (composer "Tools" popover, non-agent chats only), and a **per-agent** set (agent editor,
  snapshotted into the conversation like ADR 0003). The selectable set is a curated catalog of **keys**
  (`ToolCatalog`, the single source of truth, exposed via `GET /api/tools`): `web_search`, `location`,
  `scheduling`, `skills`, `code_interpreter`; `get_date` and document RAG stay always-on. `EnabledTools`
  is a nullable list of keys on `UserProfile`/`AgentDefinition`/`AgentBinding`/`AgentGrainState` where
  **`null == all`** (no migration); the grain resolves **override ?? user-default ?? all** and then
  **intersects with model capability** — selection can only *narrow*, never grant (a text-only model still
  gets none; `code_interpreter` still needs a hosted model). Two things to read before touching this: the
  `null == all` sentinel and the **user-default-fetch-before-first-`BuildAgent`** ordering hazard (both in
  the "Selectable tools" gotcha); and that disabling `skills` must **drop the `AgentSkillsProvider`** (it is
  a tool), while RAG must stay (the "`AgentSkillsProvider` is a TOOL" gotcha).
- Per-agent API key + async run (ADR 0022): each stored agent has a **per-agent API key** (minted on
  create, **shown once**, only its **SHA-256 hash** stored on `AgentDefinition [Id(9)]`, **regenerate**-on-edit)
  and a fire-and-forget **`POST /api/agent-run`** — `multipart/form-data` (a `prompt` field + optional `file`
  parts) + `X-Api-Key` header — that lets external automation (webhook/cron/n8n) run a **specific** agent with
  no browser session. The **key alone** identifies the agent via a global **key-hash → `(userId, agentId)`
  directory grain** (`IAgentKeyDirectoryGrain`, the one cross-user structure; rides `agent-state`, no new
  container); it responds **202** and runs in the **background** (`[OneWay] EnqueuePromptAsync`) into a new
  *listed* conversation the owner opens later. Files reuse the browser pipelines (images → `IAttachmentStore`;
  documents → `DocumentIngestionService` + RAG); a batch with any bad file is **rejected whole (400)**. A new
  **`excludedPaths: ['/api/agent-run']`** Easy-Auth carve-out (`container-app.bicep`, **amends ADR 0002**) is
  the **only** infra change — no new secret. Four things to read before touching this: the directory must be
  kept in sync **per-operation** (regenerate deregisters the OLD hash first) or a revoked key stays live; an
  `excludedPaths` endpoint is **publicly reachable** and must self-enforce + fail closed (userId from the
  directory, **never** `GetUserId`); the background run **drains `ChatCoreAsync`** (not the attachment-blind
  reminder path) so images/image-gen/RAG work — tests **poll** because it's `[OneWay]`; and **`ApiKey`** is
  the single place that generates *and* hashes. All in gotchas. **Headline consequence:** `/api/agent-run` is
  a new **inbound** vector for ADR 0013's egress — a key holder can push a prompt, **image bytes** and
  **document content** that, on a third-party model, leave the Entra tenant.
- Frontend is deliberately vanilla JS in `src/Agent42.Backend/wwwroot/` (no SPA build).
