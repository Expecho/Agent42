---
id: 0022
date: 2026-07-17
status: accepted
tags: [agents, api-key, easy-auth, external-invocation, attachments, egress, agent-definition]
---

# Per-agent API key + async run endpoint (`POST /api/agent-run`)

## Context
Every way to invoke an agent required a signed-in browser session behind Entra Easy Auth (ADR 0002)
and holding the streaming `/api/chat` connection open. The user wanted external automation (a webhook,
a cron job, an n8n/Zapier flow) to run a *specific* stored agent with a bearer token — no browser, no
streaming socket — and to be able to hand that agent an image or a document in the same call.

## Decision
Give each stored agent a **per-agent API key** and add a fire-and-forget **`POST /api/agent-run`**.

- **Key model.** On agent **create** the registry mints a cryptographically-random key
  (`sk_agent_` + 32 random bytes), returns it **once**, and stores only its **SHA-256 hash**
  (`AgentDefinition [Id(9)] ApiKeyHash`). A key is **regenerated** on an explicit `regenerateKey`
  update, invalidating the old one. The raw key is never persisted and never re-returned; `ApiKey`
  centralizes generation and hashing so the minter and the request-time resolver can never diverge.
- **Global directory.** A new **`IAgentKeyDirectoryGrain`** keyed *by the key-hash* maps a hash →
  `(userId, agentId)`. This is the one deliberately cross-user structure (the registry is per-user with
  no global lookup); resolving an incoming `X-Api-Key` is an O(1) grain get. It rides the shared
  `agent-state` provider — no new Cosmos container. The registry owns the hash lifecycle and keeps the
  directory in sync with a **per-operation** write order (create/delete write-registry-first; regenerate
  **deregisters the old hash first**, so a crash leaves the old key dead, never a live revoked key).
- **The endpoint.** `multipart/form-data` (a `prompt` field + optional `file` parts) so a caller with
  no session and no conversation id can hand over files in one call. It reads `X-Api-Key`, resolves the
  owner (fail closed → **401**), re-checks the agent exists (deletion race → 401, never 404 — don't
  disclose existence), mints a conversation id, stores/ingests each file under
  `{ownerUserId}/{convId}`, binds the persona (ADR 0003), registers any document for retrieval
  (ADR 0008), fires the run **`[OneWay]`** (`IAgentGrain.EnqueuePromptAsync`), and returns **202**.
  The owner opens the resulting *listed* conversation in the UI later.
- **Attachments reuse the browser pipelines.** Images → `IAttachmentStore` (blob bytes, ADR 0004) →
  attachment refs that ride the run; documents → `DocumentIngestionService` → RAG. A batch with any
  oversize/unsupported file is rejected **whole (400)** before a conversation is minted. The background
  run **drains `ChatCoreAsync`**, so it inherits the vision gate, the image-generation branch (ADR 0020)
  and retrieval unchanged.
- **Easy Auth carve-out.** `/api/agent-run` is added to `container-app.bicep`'s
  `globalValidation.excludedPaths`, so a key-only caller isn't redirected to login. It self-enforces the
  key and fails closed. **Amends ADR 0002** (does not supersede): every browser surface stays fully
  Easy-Auth-gated; only this one path is key-gated. On the excluded path there is no Easy Auth principal,
  so the endpoint takes the userId from the directory resolution, **never** from `GetUserId`.

## Alternatives considered
- **A single service key + owner in config** (`ServiceApi:ApiKey`/`OwnerUserId`) — rejected: it can't
  address a *specific* agent, needs a GitHub secret + Bicep wiring, and isn't per-agent revocable. A
  self-identifying per-agent key needs no new secret or infra beyond the ingress carve-out.
- **bcrypt/PBKDF2 for the key hash** — rejected: those defend *low-entropy* passwords. A 256-bit random
  key needs only a single SHA-256, and because auth is a hash-keyed grain lookup there is no secret-string
  comparison in the hot path, so `FixedTimeEquals` is unnecessary too.
- **Two-phase upload-then-run** (a key-gated upload endpoint, then a run referencing ids) — rejected:
  the caller has no conversation id ahead of the run (it's server-minted), and one multipart call is
  simpler and atomic. Files ride the run request.
- **Extract a shared helper for the background turn (from the reminder path)** — rejected: the reminder
  path is attachment-blind (no vision gate, no `UriContent`, no image-gen). Draining `ChatCoreAsync`
  reuses the *entire* real turn path with zero duplication, so images/RAG/image-gen work identically to
  a browser turn (see the "background run drains ChatCoreAsync" gotcha).
- **Reject vs. skip a bad file in a batch** — chose reject-whole-400: predictable for automation, which
  fixes and retries rather than silently losing input.

## Consequences
- **No migration.** `ApiKeyHash` is a trailing `[Id(9)]` defaulting to `""` (7/8 are `Continuous`/
  `EnabledTools`); agents persisted before this deserialize keyless and get a key on their next
  regenerate. There is no backfill (a couple of personal agents).
- **Best-effort, fire-and-forget.** `[OneWay]` returns before the turn runs; failures are logged, not
  surfaced (no caller). No retries, no durability, no streaming/synchronous reply — the owner polls the
  conversation. Tests poll the messages endpoint for the assistant turn.
- **The directory must stay in sync or a revoked key stays live.** The registry pairs every hash change
  with a directory update in a per-operation order; the endpoint's post-resolve agent-existence check is
  the delete backstop. This is the top correctness risk (see gotcha).
- **Headline egress (extends ADR 0013), now inbound.** A key holder — unauthenticated at the ingress —
  can push a **prompt, image bytes and document content** into a conversation that, if the agent uses a
  third-party model, leaves the Entra tenant. `/api/agent-run` is a new inbound vector for that egress,
  reachable with only a per-agent key. The default persona stays Foundry; the pick is the owner's.
- **The vision gate is silent here.** If the agent's model can't see images they're withheld (the reply
  notes it); the caller still gets 202. If the owner has no model provider (ADR 0019) the run no-ops
  (files are stored/ingested but nothing answers) — acceptable given best-effort semantics.
- **Emergent image editing.** Because the run drains `ChatCoreAsync`, an agent-run against an
  image-generation agent (ADR 0020) with an attached image performs image-to-image editing for free.
- **Only the ingress carve-out is not unit-tested** (`excludedPaths` is an ingress concern); the
  key → directory → run logic is exercised in-process via TestServer.
