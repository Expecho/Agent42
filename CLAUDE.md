# Agent42

> Personal AI chat agent ("Agent 42"): Orleans-hosted conversations wrapping Microsoft
> Agent Framework agents on Azure OpenAI, with a static web UI, deployed to Azure Container Apps.

**Stack:** .NET 10 · Orleans 10.2 (grains, Cosmos persistence + reminders) · Microsoft Agent
Framework 1.13 · ASP.NET Core minimal APIs · Aspire 13.4 (local orchestration) · vanilla-JS
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
  attachments (off the image `UriContent` path). **The embedding model is per-user (ADR 0030), not an
  operator prerequisite:** a user declares an embedding model on a provider (`SupportsEmbeddings` opt-in) and
  picks it as the **default embedding model** in Preferences (`PUT /api/default-embedding-model`); ingestion
  and retrieval resolve it via **`IChatClientFactory.GetEmbeddingGenerator()`** (mirrors ADR 0020 image gen —
  no new interface/host registration). With none configured the composer **blocks attaching documents** (images
  still allowed) and retrieval **degrades to empty — never throws**. The vector model must produce **3072-dim**
  vectors (ingest **validates-and-rejects** a mismatch); changing the embedding model is **delete-to-reingest**.
  `AzureOpenAI:ApiKey`/`EmbeddingDeploymentName` and the `AZURE_OPENAI_API_KEY` secret are **gone** (0030 removed
  the last operator floor 0019 kept; `AzureOpenAI:Endpoint`/`ApiKey` survive only as the dormant Foundry-inheritance
  source, `TenantId` still binds `DefaultAzureCredential`). The Cosmos vector path is prod-only (offline/AppHost
  use an in-memory index). See gotchas before touching the vector container or embedding config.
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
  `fetch_web_page`'s allowlist is a prompt-injection exfiltration control (it pins whole URLs, not
  hosts — a host allowlist is bypassable); and a named `HttpClient` silently inherits
  `AddServiceDefaults`' retry pipeline. All three are in gotchas.
- Hosted page scrape (ADR 0027, **supersedes ADR 0011's SSRF-guard mechanism only**): `fetch_web_page`
  no longer dials the model's URL from our container — it calls the hosted **context.dev** scrape API,
  which returns clean markdown, and can **follow depth-1 links** whose text matches a model-supplied
  `followLinkFilter` (up to `WebToolLimits.MaxFollowedLinks`). Because the outbound request is context.dev's,
  the old `PrivateAddressGuard`/`WebPageFetcher`/`AngleSharp` **SSRF sink is gone** (deleted); the whole-URL
  **allowlist stays** (it is an exfiltration, not SSRF, control) and applies to the **root** URL only.
  **The headline consequences:** (1) every fetched URL and its content now transit a third party outside
  the Entra tenant (ADR 0013's egress shape); (2) followed links are **allowlist-exempt** — an accepted
  residual risk the ADR records honestly (opt-in, hard-capped, credit-billed, labelled untrusted, logged —
  none of which closes the channel). A **`CONTEXT_API_KEY` GitHub Actions secret is a manual prerequisite**,
  but deliberately **not on the availability path** (like `GOOGLE_MAPS_API_KEY`, not `SEARXNG_SECRET`): with
  no key the app starts and `fetch_web_page` fails per call. Read the two new gotchas (the sink is gone;
  following is an accepted residual risk) and note the return budget is now `MaxTotalPageTextLength` across
  root + followed, not `MaxPageTextLength` alone.
- User location (ADR 0012): a `get_current_location` tool returning the user's coordinates and a
  reverse-geocoded street address. `navigator.geolocation` is a *browser* API and tools run server-side,
  and the chat path is a one-way NDJSON stream — so a tool cannot ask the browser for a fix mid-turn. The
  position therefore **rides the chat request** (`ChatRequest.Location` → `ChatAsync(…, location)`) into a
  **turn-scoped grain field that must be cleared explicitly** (it lives for the *activation*, not the call
  — see gotchas; that is the bug the reminder-path test exists to catch). A **`GOOGLE_MAPS_API_KEY` GitHub
  Actions secret is a manual prerequisite** — a Google Cloud key with the **Geocoding API** enabled and
  restricted to it — but unlike `SEARXNG_SECRET` it is deliberately **not on the availability path**: with
  no key the app starts normally and the tool reports coordinates without an address. Three things to read
  before touching this: the tool is **not** an SSRF sink and must not grow an SSRF address-guard (two
  narrow properties keep that true, and a test pins them); the browser's authorization gate is
  **two-legged** because WebKit/iOS has no geolocation Permissions API; and this feature puts the user's
  **street address** into a conversation that the ADR 0011 exfiltration channel can still drain — the
  `websearch` skill only *mitigates* that. All three are in gotchas.
- Model providers (ADR 0013, credential mechanism amended by ADR 0017, **baseline removed by ADR 0019**,
  **embedding floor removed by ADR 0030**):
  **Read this override first.** As of ADR 0019 the app ships **NO baseline chat provider** — `Models:Providers`
  is `{}`, chat is **entirely per-user** (ADR 0018), and there is **no operator default model**. So in the
  bullet below: "the default stays Foundry", "serves on Foundry alone", the baseline GitHub Models secret
  prerequisite and its wiring, the "blank Foundry key is **fatal** — Foundry is the availability path" rule, and the shipped
  `foundry` provider's `Discover:false`/`gpt-5.4`/`AzureOpenAI:DeploymentName` details are all **superseded** —
  and as of **ADR 0030** even the **embedding** floor is gone: there is now **no** operator model of any kind,
  `AzureOpenAI:ApiKey` is **no longer fatal** (blank boots), and the embedding model is a **user-added** model
  too (`SupportsEmbeddings` + a per-user default embedding model). A Foundry chat model is now something a
  **user** adds. A user with no provider gets an empty picker + gated
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
  only wired in prod (unit-tested via `UserModelCatalogTests`). The overlay `Default` is now **user-settable**
  from the same tab (a select above the provider list) via **`PUT /api/default-model`** — a distinct path, not
  `/api/providers/default`, because a provider key is user-chosen and the literal segment would out-rank
  `{key}`; the read side stays on `GET /api/providers` (`ProvidersResponse.Default`). The submitted id is
  **not** validated against the catalog (the resolver's read-time `NamesBuiltProvider` guard is the single
  place that rule lives; the 200-char cap only keeps unbounded text out of grain state), and **"New chat"
  resets the composer picker to it**. Separately — and whether or not the composer is involved — changing the
  default **retargets every conversation that has not yet bound a model**, because `AgentGrain` binds to
  `_chatClientFactory.DefaultModelId`; that reaches `/api/agent-run` runs and pending reminders, which never
  touch the composer at all. It is bounded: the first turn calls `RebindModelAsync` and pins the model.
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
- Web Share Target (ADR 0023): an installed PWA appears in Android's system **Share sheet** and receives
  shared text/URL/images/documents. **Android/Chromium only** — iOS PWAs can't register as share targets.
  The manifest declares a `share_target` (`POST /share`, multipart), but there is **no server `/share`
  route and no `excludedPaths`/Bicep change** — the deliberate contrast with ADR 0022. The **service
  worker intercepts `POST /share` client-side**, stashes the payload in **IndexedDB**, and 303-redirects to
  a GET landing (`/?share=1`); the POST never hits the network, so Easy Auth (ADR 0002, `RedirectToLoginPage`)
  never sees it and can't drop the body on a login redirect. `app.js` reads the stash on load, opens a
  **per-share destination picker** (new chat / an agent, incl. continuous — ADR 0016), prefills the composer
  and runs shared files through the existing `handleFiles` upload path (images → attachments, documents →
  RAG), then the user **reviews and presses Send — never auto-sent** (egress consent, ADR 0013). Two things
  to read before touching this: the share `respondWith` must sit **after** the `/api` guard and **before**
  the non-GET early-return, and the SW must be **active** for the share to work (else the POST reaches the
  server and fails — accepted degradation, no server fallback). Both are in gotchas. **Headline consequence:**
  sharing is a new **inbound** vector for ADR 0013's egress, and sharing a large file into a **continuous**
  agent is a new low-friction path to the 2 MB grain brick (ADR 0016/0004).
- Config-driven remote MCP servers (ADR 0024): a signed-in user adds their own **remote HTTP MCP servers**
  (Preferences → MCP servers) and their tools appear to the agent with **no per-server code** — every MCP
  server speaks the same discovery protocol and its tools convert to `AITool` (`McpClientTool`), merging into
  the `List<AITool>` `AgentGrain.BuildAgent` already builds. It is the MCP analogue of user-configurable
  model providers (ADR 0018): a per-user overlay grain (`IUserMcpConfigGrain`, riding `agent-state` on a new
  `mcp-servers` state — **no new Cosmos container**), secrets **encrypted** via a dedicated
  `IMcpSecretProtector` (own DP purpose), masked to `hasHeaderValue` on read, blank-on-update = keep. A
  singleton `IUserMcpToolProvider` connects to the user's enabled servers (version-cached, best-effort with a
  timeout) and `AgentGrain` resolves the tools **once in `OnActivateAsync` before the first `BuildAgent`**,
  gated **per server** (ADR 0026 amends this: was a single `mcp` group key) — each server kept when the
  selection contains the `mcp` **wildcard** or the server's `mcp:{id}` key (`ToolCatalog.IsMcpServerEnabled`),
  intersected with capability. Auth is **staged by risk**, all shipped: None/Header (Phase 1), MSAL
  ClientCredentials (Phase 2, the Entra ID → Dataverse path — a dynamic token stamped by an `AuthHeaderHandler`
  over a plain `HttpClient`), and **InteractiveOAuth (Phase 3, ADR 0025)** — an app-driven authorization-code +
  PKCE flow with **PRM discovery** across `GET /api/mcp/oauth/start` + `/callback` (both **behind Easy Auth**,
  never `excludedPaths`), tokens persisted **encrypted** in `StoredMcpServer.TokenCiphertext` and refreshed at
  connect. The SDK's OAuth is NOT used (its redirect delegate blocks). Manual prereq: register the redirect URI
  `https://chat.agent42.nl/api/mcp/oauth/callback` in each provider's app registration. **Every host that maps
  `MapAgentApi` must register `IMcpSecretProtector`** (a route-matcher dependency) and every silo that
  activates `AgentGrain` must register `IUserMcpToolProvider`. Four things to read before touching this: the
  tool provider's cache holds **live sockets** so it is bounded (not the unbounded provider-cache copy); MCP
  tools are the **per-server-gated exception** to the catalog-key guard (dynamic names, absent from the catalog
  map); the endpoint is deliberately **not SSRF-guarded** (config, not a request — may be `localhost`); and a
  tool's **return value is persisted** into the 2 MB session so each is truncation-wrapped (ADR 0004). All in
  gotchas. **Headline consequence:** an MCP server sees conversation content and its tool results are a new
  inbound prompt-injection channel — ADR 0013's egress, reached from a server the user added.
- Per-MCP-server selectable tools (ADR 0026, amends 0024/0021): each management-enabled MCP server is
  **individually selectable** in the ADR 0021 picker as an `mcp:{serverId}` key (was one all-or-none `mcp`
  checkbox). The bare `mcp` key is repurposed into a **wildcard "all servers"** meta-key kept in
  `AllSelectableToolKeys` (so `null == all` and legacy selections keep enabling every server — **zero
  migration**) but dropped from `Descriptors`. `GET /api/tools` is now **per-user** for the MCP portion
  (appends one descriptor per enabled server). Two traps: `GetEnabledToolsAsync` must **expand** the `mcp`
  wildcard into `mcp:{id}` keys before the picker sees it (else a no-op Save silently disables every server —
  the picker has no `mcp` checkbox), and the provider emits an **empty group per skipped server** so the
  expansion id-list is complete. Both in the null==all gotcha.
- Web Push when an agent replies while away (ADR 0028): a **Web Push** notification titled with the
  **agent's name** is delivered to the user's installed Android/Chromium PWA when an agent finishes a reply
  on one of the two **asynchronous** paths — a fired reminder (`ReceiveReminder`) or an agent-run
  (`EnqueuePromptAsync`, ADR 0022). "Not online" is approximated by **one fact — we send only on those two
  async paths, never from `ChatCoreAsync`** (the live stream the user is already watching); a server-side
  presence/heartbeat was considered and **rejected**. The send lives in the two async **callers** — inside
  `EnqueuePromptAsync`'s `try` after the drain (gated on `_modelId` so the no-model notice path doesn't fire
  a false push) and after `ReceiveReminder`'s `WriteStateAsync` — and the SW **always shows** the
  notification on a received push (**no** suppress-if-focused: that violates Chromium's `userVisibleOnly`
  budget contract and risks subscription revocation). Subscriptions live in a **dedicated per-user grain**
  (`IUserPushGrain`, state `"push-subs"` on `agent-state` — **no new container**, not `UserProfile` which
  ADR 0005 keeps scalar), stored **cleartext** (a device push capability usable only against our VAPID pair,
  not a third-party credential — encryption deferred). The interface/impl split follows `IGeocoder`:
  `IPushNotificationSender`/`PushMessage`/`NullPushNotificationSender` in Agents, `WebPushNotificationSender`
  (over the `WebPush` package, isolated behind an `IWebPushClient` seam) in Backend. **Every AgentGrain host
  must register `IPushNotificationSender`** (else activation fails) and **every host that maps `MapAgentApi`
  must register the `PushConfig` singleton** (a route-matcher dependency, like `IMcpSecretProtector`). The
  **VAPID private key is the only secret and is OPTIONAL** (the `contextApiKey`/`googleMapsApiKey` shape, not
  `searxngSecret`: no key → app boots, `GET /api/push/config` returns `enabled:false`, UI hides the toggle) —
  a `VAPID_PRIVATE_KEY` GitHub secret → `Push__VapidPrivateKey`; the public key + `mailto:` subject are
  **non-secret and committed** in `appsettings.json`. Three things to read before touching this: the send
  belongs in the two async callers, never `ChatCoreAsync` (the ADR 0028 gotcha); the sender bounds a
  `SendBudget` timeout and calls `RemoveAllResilienceHandlers()` because `AgentGrain` is non-reentrant; and
  the sender POSTs to the browser-supplied subscription endpoint — an **accepted blind-SSRF property** of
  Web Push (authenticated, encrypted body, no response returned), deliberately **not** host-guarded.
  **Headline consequence:** the notification transits a third-party push service (FCM/Mozilla/WNS), but the
  payload is **RFC 8291 end-to-end encrypted** — a materially smaller egress than ADR 0013's, though the
  title is the user-chosen agent name. Android/Chromium only; iOS-in-browser is feature-detected out.
- Configuration health tab (ADR 0029): a read-only **Preferences → Configuration** tab, backed by
  `GET /api/config-health`, that reports which config inputs are present/malformed/(opt-in) unreachable — so a
  silently degraded feature (geocoding off, context.dev 401, Web Push off) is visible instead of mysterious.
  **The load-bearing invariant: the report never contains a secret value** — only a status
  (`ok`/`warning`/`error`/`skipped`/`na`) + a **curated** message per check, the same `Mask`/`hasKey`/
  `PushConfig.Enabled` discipline. `ConfigHealth.BuildStatic` is a **pure function** of `IConfiguration` +
  `IHostEnvironment` + `PushConfig` (no HTTP, no DI); live probes are a separate **opt-in** `ProbeAsync` path
  (a "Run live checks" button — a geocoding/context.dev probe is **billable**), so a static call runs **zero**
  probes. Behind Easy Auth like every `/api/*` read (never `excludedPaths`); all allowlisted users may view
  (no role system). Four things to read before touching this: **the PROBE path is the real leak channel, not
  the static one** — the geocoding key rides in the request query string (`GoogleGeocoder`), so `ProbeAsync`'s
  catch **discards the exception** and every probe returns a curated constant, never `ex`/a body/a target URL;
  the endpoint injects **`IServiceProvider`, deliberately NOT `IHttpClientFactory`** (unregistered in DevHost —
  a minimal-API param it can't resolve breaks routing for the whole host) and resolves probe deps optionally,
  adding **no new mandatory both-hosts registration**; the **geocoding probe is built only when its key is
  present** (else `CreateClient` returns a retry-pipeline-inheriting client — the named-client trap) while
  embeddings use `IEmbeddingGenerator` (no named client) and SearXNG/context.dev are unconditional; and a
  **SearXNG 403 is a HEALTHY sidecar** (JSON API off), not an auth failure. The tab is **appended LAST** in the
  Preferences tablist — the MCP-OAuth return deep-links to `profileTabs.select(3)`, so Configuration is index 4
  and MCP stays 3.
- Token-usage dashboard (ADR 0031): a **dedicated full-page usage view** (opened from the sidebar footer,
  a fixed overlay — not a Preferences tab) that reproduces the sibling `AI.Telemetry.Dashboard` — headline
  tiles + tokens-by-model + tool-calls-by-name + usage-by-user tables, with 24h/7d/30d/all filters +
  auto-refresh, in Agent42's own amber/phosphor style. It is **GLOBAL / cross-user** (any signed-in user sees
  aggregate spend incl. other users' email/usage — accepted for the ~2 allowlisted users, behind Easy Auth,
  no role system) via `GET /api/usage` (+ `/usage/applications`, `/usage/users`). Data is fed **solely from
  the OpenTelemetry gen_ai spans already emitted** (no manual counter in `AgentGrain`): a Backend
  `UsageSpanProcessor` (`OnEnd`, non-blocking, bounded drop-on-full channel) projects finished spans and a
  `UsageIngestionService` (≥30 s flush, Backend-only) batches them into a **single global `UsageRollupGrain`**
  (well-known key `"global"`) riding `agent-state` on a `"usage-rollups"` state — **no new Cosmos container**,
  the `UserPushGrain` pattern. Read the "usage dashboard counts tokens from `chat` spans ONLY" gotcha before
  touching this: the projector must whitelist `chat`/`execute_tool` and drop `orchestrate_tools` (its usage is
  the SUM of child chats — double-count); cached/reasoning tokens are always 0 (OTel emits neither — the
  dashboard measures chat spend, **not** embedding spend); async paths land in an **unreported** user bucket;
  the **`application` dimension is per-agent** (the grain publishes the agent name into `RequestContext`, the
  identity span processor stamps it as the `agent42.application` tag — never override `gen_ai.operation.name`;
  non-agent chats fall back to `"Agent42"`); and the global grain **prunes on every write** (hourly ~48 h / daily ~92 d) to hold the 2 MB item cap
  (ADR 0004), so `range=all` = the retained window. Empty offline (DevHost has no OTel pipeline).
- Offline read cache (ADR 0032): the installed PWA renders the user's **agents and chats offline** via a
  **client-side IndexedDB mirror in `app.js`** (DB `agent42-cache`, store `kv`, keys `agents` /
  `conversations` / `messages:<id>`; helpers `cacheGet`/`cachePut`/`cacheDelete`). Every successful
  `/api/agents` · `/api/conversations` · `.../messages` fetch writes the mirror; `hydrateFromCache()`
  paints cached lists at startup (length-guarded so the network fast-path wins) and the loaders fall back
  to the cache on failure — a chat is viewable offline only if it was opened at least once online
  (`selectConversation` caches its history and replays it through the extracted `renderTurns`). **The SW
  is deliberately NOT involved** — it stays shell-only and never touches `/api` (ADR 0002); "fixing" empty
  offline lists by caching `/api` in the SW breaks that invariant (see the gotcha). Only infra-free change
  beyond `app.js` is the shell version bump `agent42-shell-v9` → `-v10`. **Consequence:** conversation text
  + agent definitions now persist **unencrypted** in origin IndexedDB (data-at-rest, cleared with site
  data); no new egress. Image thumbnails don't load offline (separate attachment proxy) — text shows.
- Ephemeral / incognito chat (ADR 0034): a composer **incognito toggle** starts a chat whose history is
  **never persisted** (the grain skips its `WriteStateAsync`) and that **never appears in the sidebar** (the
  `POST /api/chat/{id}` handler skips `ConversationIndexGrain.TouchAsync`). It reuses the non-persisted
  `_turnLocation` pattern exactly: a trailing `bool Ephemeral` on `ChatRequest` → a trailing `bool ephemeral`
  on `IAgentGrain.ChatAsync` → a turn-scoped **`_ephemeral`** field cleared in `ChatAsync`'s `finally` (and
  reset on the two always-persisting async entrypoints). **Scope is session-only:** documents are **blocked
  at the composer** (RAG ingestion writes durable Cosmos vector chunks), images stay allowed (their blob
  bytes persist — accepted), and **scheduling is refused inside `SchedulePromptAsync`** (a durable reminder
  would fire the persisting/pushing `ReceiveReminder`, ADR 0028). The toggle is fixed at chat creation
  (settable only on a fresh empty regular chat, hidden under the picker lock on agent chats). No new grain,
  no state migration, no new Cosmos surface. **The guarantee is "not persisted, not listed", not "not in
  memory"** — a live activation still holds the chat; the trace-free property shows up on deactivation. Egress
  (ADR 0013) is unchanged: incognito is about durability, not privacy from a third-party model. See the
  ephemeral-chat gotcha before touching `_ephemeral` or the scheduling guard.
- Read-only share links (ADR 0035): an owner mints a **public, no-login** link to any conversation (regular
  or agent chat) from a **↗ Share** action on each sidebar row (`buildConvRow`) → `#shareLinkDialog`
  (create/copy/revoke). It mirrors the ADR 0022 two-grain split: **`ShareToken`** (copy of `ApiKey`, `shr_`
  prefix) → a global hash-keyed **`ShareLinkDirectoryGrain`** (token hash → `(userId, conversationId)`) for
  O(1) public resolution, plus a per-user **`UserShareLinksGrain`** (state `"share-links"`) for
  list/create/revoke — both ride `agent-state`, **no new Cosmos container/secret/DI**. Owner routes
  `POST/GET/DELETE /api/conversations/{id}/share` (Easy Auth); public routes `GET /api/share/{token}` and
  `/api/share/{token}/attachments/{attachmentId}` are on **`excludedPaths`** (with `/shared`, the
  self-contained static viewer under `wwwroot/shared/`), **self-enforce** the token, take userId+conversation
  from the directory (**never `GetUserId`**), and **fail closed → 404**. **Two deliberate inversions of ADR
  0022** (see the share-link gotcha): the **raw** token is stored (owner re-copies it repeatedly; low
  sensitivity), and revoke **deregisters the directory FIRST** (unsharing has no conversation-deletion
  backstop, so fail-toward-dead). **Headline consequence (extends ADR 0013):** a token exposes the
  conversation's **full current live history** — text, images, the user's street address (ADR 0012),
  document/web content — to anyone holding the link, outside the Entra tenant; the view is live until revoked,
  and **revocation is the only control** (deleting the conversation also revokes it).
- Follow-up suggestion prompts (ADR 0036): after an **interactive** reply, `AgentGrain` makes one extra
  model call (`GenerateFollowUpsAsync`) — **streamed via `GetStreamingResponseAsync` and aggregated, the same
  path/cached client the reply uses** (the non-streaming `GetResponseAsync` returned nothing on the Foundry
  Responses adapter, ADR 0014) — to produce up to 3 short
  follow-up prompts, streamed as a new **`AgentStreamEventType.Suggestions`** event (`[Id(11)]`) before
  `Done`; the browser renders them as chips that **send on click**. Gated to the live path by a turn-scoped
  `_interactiveTurn` flag (OFF on the agent-run/reminder drains of the same `ChatCoreAsync` — no wasted call),
  **best-effort** (its `try/catch` returns null on failure; the reply/`Done` are never affected) and
  **session-free** (a throwaway message list, never `_session`, no state write). Controlled by a **default-ON
  Preferences toggle** — `UserProfile.SuggestFollowUps` is a **null-sentinel `bool?`** (`null`/`true` = ON,
  `false` = OFF; legacy state stays ON, zero migration), read once on activation into
  `_suggestFollowUpsEnabled` and settable via `PUT /api/profile`. **Headline consequence:** it spends an extra
  small model call and re-sends recent conversation text to the model per interactive turn (ADR 0013 egress) —
  which is why it is opt-out. Chips are live-only (not replayed by `renderTurns`; cleared on the next `send`).
- Hands-free voice conversation (ADR 0037, **extends ADR 0006**): a dedicated composer `#voiceBtn` (distinct
  from `#micBtn` dictation) starts a **hands-free loop** — auto-listen → auto-send → **speak the reply** →
  **talk over it to interrupt** → re-listen — with **no buttons between turns**. Browser-native and
  **frontend-only** (all in `src/Agent42.Backend/wwwroot/app.js` + `index.html` + `styles.css`; no endpoint/grain/Bicep/Azure/JS
  dependency, exactly like ADR 0006): `SpeechRecognition` to listen + **`speechSynthesis` to speak** (the new
  output half — ADR 0006 was input-only). A **dedicated second `SpeechRecognition` instance** drives voice mode
  so the dictation instance's Android accumulation fixes stay untouched; the two mic uses are mutually exclusive
  (`startVoice()` calls `stopSpeech()`). A controller runs a state machine `idle → listening → thinking →
  speaking → listening`: recognition runs continuously (restarted on Chrome's silence `end`), a **~1.2 s silence
  debounce** ends the utterance and auto-sends, and the reply is spoken **sentence-by-sentence as it streams**
  (fed from `send()`'s `textDelta` chunks, markdown-stripped) so speech starts early; the last utterance's `end`
  re-arms listening. **Barge-in** keeps the mic **live while speaking** — a genuine result cancels TTS and
  **aborts generation mid-stream** via a new `AbortController` on `send()`'s `fetch` (`sendAbort`); an
  `AbortError` is a clean interruption, **not** a "Transmission failed" (the `catch` special-cases it). The
  button is hidden unless **both** speech APIs exist (Firefox/older iOS degrade out), mirroring ADR 0006. Two
  things to know: **egress (ADR 0013) is unchanged** — TTS output never leaves the browser, and recognition
  reuses ADR 0006's already-accepted vendor-cloud residual (no new one); and the **assistant-hears-itself echo**
  is an **accepted residual** — mitigated by a best-effort word-overlap heuristic (`isSelfOrEcho`, ≥ 0.6) but
  **headphones are the clean guarantee**. No runtime test (speech APIs can't run headlessly) —
  `VoiceConversationAssetsTests` guards the wiring by source-text, behaviour verified manually in DevHost.
- Server-side voice / speech model (ADR 0038, **amends ADR 0006/0037, extends 0013/0018/0020/0030**): when a
  user picks a **default speech model** in Preferences, the hands-free voice reply (ADR 0037) is spoken by an
  Azure/OpenAI **text-to-speech** model instead of the browser's `speechSynthesis`; with none set (or on any
  failure) it **falls back to the browser voice**. Scope is **assistant voice (TTS) only** — the user still
  speaks through the browser recognizer. It **mirrors the per-user embedding model (ADR 0030) almost exactly**:
  a per-model **`SupportsSpeech`** flag threaded through the five model representations
  (`StoredModel [Id(6)]`/`ConfiguredModel`/`ModelInfo`/DTOs/resolver) that **only filters the picker** —
  deliberately **NOT** on `ModelCapabilities` (like embeddings; sidesteps the Foundry `GetCapabilities→Full`
  shortcut) — plus a **`UserProviderConfig.SpeechDefault [Id(4)]`** set via **`PUT /api/default-speech-model`**
  (read on `GET /api/providers`), validated once by the resolver's **`EffectiveSpeechDefault`** →
  `ResolvedProviderSet.SpeechDefaultModelId` (no baseline/first-model fallback). The service seam is
  **`ISpeechService.SynthesizeAsync` → `IChatClientFactory.GetSpeechService()`/`DefaultSpeechModelId`** (the
  `GetEmbeddingGenerator()` shape); prod **`OpenAISpeechService`** wraps the SDK **`AudioClient`**
  (`GenerateSpeechAsync`, voice `Alloy`, MP3), built off the **same** provider client as chat — Foundry over
  **`/openai/v1` + `api-key` header** (NOT the api-version data plane the embedding client uses, which caps at
  2024-10-21), never a named `HttpClient`, endpoint from config only. **`POST /api/speech`** (text→audio)
  resolves the factory **optionally via `IServiceProvider`** (the `/api/config-health` pattern — no new
  mandatory both-hosts registration) and **409s when unconfigured**. The voice controller plays `/api/speech`
  audio through an **ordered queue** (fetches pipeline, playback sequential) with a dedicated **`speechAbort`**
  for barge-in; **`ISpeechService` is named to dodge M.E.AI's `ISpeechToTextClient`** (the `IImageGenerationService`
  CS0104 trap). **Headline consequence:** picking a speech model sends the **reply text** outside the tenant
  (ADR 0013 egress) — the reason it is opt-in and the browser voice stays default. Offline can't reach a real
  TTS endpoint (like image gen, ADR 0020); the Foundry audio wire path is spike-informed, prod-smoke verified.
- Frontend is deliberately vanilla JS in `src/Agent42.Backend/wwwroot/` (no SPA build).
- Project layout (ADR 0039): `src/Agent42.Agents` is organised into **16 top-level feature folders**
  (`Conversations/`, `Web/`, `Documents/`, `Mcp/`, `StoredAgents/`, …), and `AgentGrain` is split into
  **10 `partial` files that live in those folders**. **Every type stays in the flat `Agent42.Agents`
  namespace — folders and namespaces deliberately DISAGREE, and that is load-bearing, not sloppiness.**
  Orleans Cosmos grain storage uses `JsonGrainStorageSerializer`, which writes the full CLR type name
  into every document (`"$type":"Agent42.Agents.AgentGrainState, Agent42.Agents"`), so giving a
  persisted type a folder-matching namespace makes **every existing document unreadable** — it throws on
  the root type, taking out every conversation, agent, profile and setting at once. That is not
  hypothetical: the folder-matching-namespace version of this refactor was fully built and passed the
  entire suite, lint, an adversarial review and CI before the flaw was found, because the serialization
  test exercised Orleans' *binary* serializer, which Cosmos does not use. `.editorconfig` disables
  IDE0130 so the IDE stops suggesting the change, and `ProjectLayoutTests` +
  `GrainStorageSerializerTests` catch it if made by hand. `[Alias]` is a **separate** mechanism (the
  binary path) and is **not** what protects data at rest. `ReceiveReminder` stays in the core `AgentGrain`
  part beside `ChatAsync` (the `_turnLocation`/`_ephemeral` clears must be read together); `Skills/` does
  not move (csproj `Content` glob); `Agent42.Backend` and `Agent42.Tests` stay flat on purpose.
