# Plan — Per-agent API key + async run endpoint (`POST /api/agent-run`, 202)

`planned-against: main @ a6d6603`

> **Refresh note (2026-07-17).** This spec was originally planned against `a6d6603`; 34
> commits have landed since (ADRs 0011–0021). Two **blocking** staleness fixes have been
> folded in: the new `AgentDefinition` field is **`[Id(9)]`** (7 and 8 are now `Continuous`
> and `EnabledTools`), and the new ADR number is **`0022`** (0004 is `attachments-in-blob`;
> ADRs now run to 0021). **Still outstanding:** every line-number pointer below
> (`AgentGrain.cs:378`, `AgentApiTests.cs:117…`, `app.js:32`, etc.) predates those 34
> commits and must be re-anchored by the executor — treat them as approximate.
>
> **Extension (2026-07-17): file & image attachments on `/api/agent-run`.** The endpoint
> now accepts a prompt **plus attachments** (images *and* documents). Confirmed scope:
> **both pipelines**; a batch with any unsupported/oversize file is **rejected whole (400)**.
> See "Attachment extension" below and the flagged changes in the constraints/steps.

## Overview

Two coupled deliverables:

1. **Per-agent API keys.** When a stored agent is **created**, mint a
   cryptographically-random API key, return it **once** in the create response, and
   store only its **SHA-256 hash**. The key can be **regenerated while editing** the
   agent (invalidating the old one). The raw key is never persisted and never returned
   by the list/get endpoints again.

2. **`POST /api/agent-run`** — **`multipart/form-data`**: a `prompt` field, an optional
   set of `file` parts, header `X-Api-Key: <key>`. The **key alone identifies the agent**
   (via a global key-hash → agent directory grain). Responds **202 Accepted** immediately
   and runs the prompt (with its attachments) against that agent **in the background**,
   landing the reply in a new *listed* conversation the owner can open in the UI later.

All four shape decisions were confirmed with the user in the planning interview:
key **alone** identifies the agent (body drops `agentId`; no owner config) · key stored
**hashed, shown once** · **regenerate on edit** · response sink is a **new listed
conversation** · execution is **OneWay / best-effort**.

Why now: lets external automation (webhook, cron, n8n) invoke a specific agent with a
per-agent bearer token, without a signed-in browser session and without holding the
streaming `/api/chat` connection open — and now to hand that agent an **image or document**
in the same call (a screenshot to describe, a PDF to answer questions about).

## Attachment extension (2026-07-17)

**Shape.** The endpoint takes **`multipart/form-data`** (not the original JSON body): a
`prompt` form field + zero-or-more `file` parts, `.DisableAntiforgery()` (Easy-Auth-excluded
path, ADR 0002 — no cookies/forms). A **single** request carries prompt + files because the
external caller has **no session and no conversation id** to do the browser's two-phase
upload-then-send-id dance — the conversation id is server-minted *inside* the endpoint, so
the files must ride the same call and be stored under `AgentKey(owner.UserId, convId)` after
the key resolves.

**Reuse, don't reinvent.** The per-file branch is *exactly* the existing upload endpoint's
(`AgentApi.cs:105`): resolve media type by extension (`DocumentMediaTypes.ResolveMediaType`),
then — image → `IAttachmentStore.SaveAsync` (blob bytes, ADR 0004) collecting an
`AttachmentRef`; supported document → `DocumentIngestionService.IngestAsync` → if it yielded
chunks, `grain.RegisterDocumentAsync(fileName)` (RAG, ADR 0007/0008). Both services are
already DI-registered and injected into sibling endpoints. Image refs flow into the turn;
documents do **not** (they are retrieval-only — never sent back as an attachment).

**The one architectural change vs. the original plan (constraint 5, below).** The original
plan extracted the background run from the **reminder path**, which pushes an *always-empty*
attachment list (`AgentGrain.cs:1425`) and therefore has **no vision gate, no `UriContent`
building, no image-gen branch**. To carry images the shared helper must be factored from
`ChatCoreAsync`'s **rich turn-building** (`AgentGrain.cs:586` vision gate, `:627` UriContent,
`:570` image-gen branch) — a larger extraction than the original "≤3% duplication" note
assumed. **Bonus:** routing through that machinery means an agent-run against an
image-generation agent (ADR 0020) with an attached image is **image-to-image editing** for
free — but *only* via `ChatCoreAsync`, never the reminder path.

## Affected areas (verified against the tree @ a6d6603)

| Path | Change |
|------|--------|
| `src/Agent42.Agents/AgentDefinition.cs` | Add **`[Id(9)] string ApiKeyHash`** (hash only — never the raw key; 7/8 are `Continuous`/`EnabledTools`) |
| `src/Agent42.Agents/IAgentKeyDirectoryGrain.cs` **(new)** | Global directory: grain key = key-hash → `(userId, agentId)` |
| `src/Agent42.Agents/AgentKeyDirectoryGrain.cs` **(new)** | Impl + `AgentKeyDirectoryState`; rides the `agent-state` provider |
| `src/Agent42.Agents/IAgentRegistryGrain.cs` | `SaveAsync` returns a result carrying the once-only raw key; add a `regenerateKey` path; `DeleteAsync`/regenerate must deregister the old hash |
| `src/Agent42.Agents/AgentRegistryGrain.cs` | Mint key on insert, regenerate on update, keep the directory grain in sync |
| `src/Agent42.Agents/ApiKey.cs` **(new)** | Small helper: generate a random key + compute its hash (one place, so gen and verify never diverge) |
| `src/Agent42.Agents/IAgentGrain.cs` | New `[OneWay] EnqueuePromptAsync(string message, IReadOnlyList<AttachmentRef>? attachments)` — **attachments (images) ride the run** |
| `src/Agent42.Agents/AgentGrain.cs` | Implement it; extract a shared **turn-building + run + persist** helper from **`ChatCoreAsync`** (vision gate + `UriContent` + image-gen branch — *not* the attachment-blind reminder path), reused by `ReceiveReminder` (empty attachments) and the enqueue path |
| `src/Agent42.Backend/AgentApi.cs` | **`multipart`** `POST /api/agent-run` (prompt + files); per-file branch reusing `IAttachmentStore` + `DocumentIngestionService` (same as `:105`); return the once-key from `POST`/`PUT /api/agents`; new request/response records |
| `src/Agent42.Backend/wwwroot/` (`app.js` + agent editor) | Show the key **once** after create/regenerate ("copy it now"); add a regenerate control to the edit form |
| `tests/Agent42.Tests/AgentRegistryGrainTests.cs` | Key minted + hashed on create; regenerate invalidates old; delete deregisters |
| `tests/Agent42.Tests/AgentApiTests.cs` | run-by-key → 202; unknown/missing key → 401; create returns key once; list omits raw key; **image attachment → run sees it; document attachment → RAG registered; unsupported/oversize file → 400** |
| `tests/Agent42.Tests/AgentGrainTests.cs` | `EnqueuePromptAsync` runs + persists a turn; **with an image attachment the turn records it** |
| `infra/modules/container-app.bicep` | **Only** the Easy Auth `excludedPaths` carve-out for `/api/agent-run` (re-verify current `authConfig` shape — SearXNG sidecar reshaped this file, ADR 0011) |
| `.agentic/decisions/0022-*.md` | New ADR: per-agent key model + Easy Auth carve-out + **inbound attachment egress** (amends ADR 0002; extends ADR 0013 egress) |

**Note the shrink vs. the earlier draft:** because the key is self-identifying and
lives (hashed) in Cosmos, there is **no** `ServiceApi:ApiKey` secret, **no**
`ServiceApi:OwnerUserId` config, and therefore **no** change to `main.bicep` /
`deploy.yml` / GitHub secrets. The only infra change is the ingress path exclusion.

**Attachment reuse (no new services):** `IAttachmentStore` and `DocumentIngestionService`
already exist and are injected into the browser upload endpoint (`AgentApi.cs:105`). The
extension adds **no** new blob container, ingestion service, or Bicep — it reuses both under
the resolved `AgentKey(owner.UserId, convId)`.

## Context & constraints (what the executor can't cheaply re-derive)

1. **`IAgentRegistryGrain` is keyed per user with no global lookup** (`AgentRegistryGrain.cs`).
   The endpoint has no signed-in user and no `agentId` in the body, so it needs a
   **cross-user** index to resolve an incoming key. That is the new
   `IAgentKeyDirectoryGrain`: its **grain key is the key-hash**, its state is
   `(userId, agentId)`. Resolution is an O(1) grain get — no enumeration. A hash with no
   registered grain resolves to null → **401** (fail closed). This directory is the one
   deliberately global/cross-user structure; call it out in the ADR.

2. **Directory integrity is the top correctness risk** (mirrors the durable-reminder
   "must explicitly unregister" trap, `gotchas.md`/`AgentGrain.PurgeAsync`,
   `AgentGrain.cs:242-260`). The registry grain owns the hash lifecycle, so it must pair
   every hash change with a directory update. There is **no single safe write order** —
   the invariant is *"a revoked key must never keep resolving,"* which forces a
   **per-operation** ordering (do not generalize one rule across all three):

   - **Create:** write registry state (new `ApiKeyHash`) **then** register the hash. A
     crash in between leaves the key *unusable* (agent has a hash, directory has no
     entry) → user regenerates. Safe.
   - **Regenerate:** **deregister the OLD hash FIRST**, then write registry state (new
     hash), then register the new hash. This is the opposite of the create/delete order,
     and it is deliberate: any crash then leaves the old key **dead** (worst case a brief
     window where neither key resolves, plus a never-handed-out orphan). Registry-write-
     first here would strand a live `oldHash` entry that the stored (now `newHash`) can
     never clear — a **permanently live revoked key**. Old-key-death dominates.
   - **Delete:** remove from registry (write) **then** deregister. The endpoint also
     re-checks the agent exists after resolving (constraint below / step 5), so even an
     orphan directory entry from a mid-failure fails closed (resolves → agent null →
     401). Safe.

   The registry grain holds the agent's stored `ApiKeyHash`, so on delete/regenerate it
   knows exactly which directory grain to clear.

3. **Hashing:** keys are high-entropy (32 random bytes), so a single **SHA-256** is the
   correct hash — do *not* reach for bcrypt/PBKDF2 (those defend low-entropy passwords).
   Because auth is a **hash-keyed grain lookup**, there is no secret string comparison in
   the hot path, so `FixedTimeEquals` is unnecessary here. Centralize *both* generation
   and hashing in `ApiKey.cs` so a caller can never hash differently than the minter.
   Suggested format: `sk_agent_` + `Base64Url(RandomNumberGenerator.GetBytes(32))`; hash
   = lowercase hex of `SHA256(rawKeyUtf8)`; the directory grain key is that hex string.

4. **Never persist or re-return the raw key.** `AgentDefinition` stores only
   `ApiKeyHash`. The raw key is returned exactly once, from `POST /api/agents` (create)
   and from `PUT /api/agents/{id}` **when `regenerateKey` is set**. `GetAllAsync`/
   `GetAsync` return the definition (hash is non-sensitive; may be projected out, but
   must **never** carry a raw key). This forces a response shape change: create/update
   return `AgentSaveResponse(AgentDefinition Agent, string? ApiKey)` where `ApiKey` is
   non-null only when a key was just minted.

5. **The execution exemplar — extract from `ChatCoreAsync`, not the reminder path
   (revised for attachments).** The *original* plan pointed at `ReceiveReminder`
   (`AgentGrain.cs:1388`, was `:378`) because it runs a prompt non-streaming via
   `_agent.RunAsync([user msg], _session)` then serializes+persists. **That is no longer
   sufficient:** the reminder path pushes an *always-empty* attachment list
   (`AgentGrain.cs:1425`) and has **no vision gate, no `UriContent` materialization, and no
   image-gen branch**. To carry the image attachments the run must go through the rich
   turn-building in **`ChatCoreAsync`** (`:586` `SupportsVision` gate → withhold images from
   a non-vision model; `:627` `GetReadUrlAsync` → `UriContent`; `:570` image-gen branch →
   `GenerateImageTurnAsync`). So the extraction target is the **non-streaming core of
   `ChatCoreAsync`** — build the user message (text + materialized image `UriContent`,
   honoring the vision gate and image-gen branch) → `RunAsync` → serialize → `WriteStateAsync`
   → record `TurnAttachments`. Reuse it from three callers: `ChatCoreAsync` (streaming
   wrapper), `ReceiveReminder` (empty attachments), and `EnqueuePromptAsync`. Keep the shared
   helper non-streaming; `ChatCoreAsync` keeps its own streaming surface (it may stay a
   thin duplication if a full merge would blow the cognitive-complexity gate — prefer sharing
   the message-building + persist, not necessarily the streaming loop). **Extraction caveat
   (unchanged):** `ReceiveReminder` bumps `ScheduledPrompts[reminderName].TimesFired`
   (`AgentGrain.cs:1434`) before its single `WriteStateAsync`; have it mutate the counter in
   `_state.State` **before** calling the helper so the one write flushes it. Assert the
   counter still increments in the reminder test.

6. **Easy Auth still gates the path at the ingress (ADR 0002).**
   `container-app.bicep`'s `authConfig` sets
   `globalValidation.unauthenticatedClientAction: 'RedirectToLoginPage'`, so a key-only
   external caller (no Entra session) is redirected to login before reaching the handler.
   `/api/agent-run` must be added to `globalValidation.excludedPaths`; the app then
   self-enforces via the key. This is a deliberate hole in ADR 0002 → **new ADR** (step 8).
   On an excluded path there is no `X-MS-CLIENT-PRINCIPAL-ID`, so `GetUserId` would return
   `local-dev` (`CurrentUser.cs:15`) — which is exactly why the endpoint takes the
   **userId from the directory resolution, never from `GetUserId`**.

7. **No new Cosmos container.** The directory grain rides the existing `agent-state`
   provider (`AgentGrain.StorageProviderName`); Orleans partitions by grain type, so no
   Bicep container change is needed (same reasoning the registry grain documents,
   `AgentRegistryGrain.cs:13-17`, and the pre-declared-containers gotcha).

8. **Test harness has no Easy Auth / no ingress** (`AgentApiTests.cs`): requests map to
   `local-dev`, and `excludedPaths` is an ingress concern not exercised in-process.
   Consequences: the endpoint's **key → directory → run** logic *is* fully unit-testable
   in `TestServer` (create an agent via `POST /api/agents`, capture the once-key from the
   response, call `/api/agent-run` with it). The Bicep `excludedPaths` change is **not**
   unit-tested — verify at deploy. Call that out, don't silently drop it.

9. **`conversationId` is server-minted:** `Guid.NewGuid().ToString()` (string —
   `TouchAsync`/grain keys are string-typed, `IConversationIndexGrain.cs:25`); the
   lowercase-hyphenated form matches the client's `crypto.randomUUID()` (`app.js:32`), so
   server- and client-created ids coexist cleanly.

10. **`[OneWay]` detaches from the caller** — it sends the message and returns
    immediately; the grain processes the turn regardless of the HTTP scope disposing.
    That is what yields clean 202 semantics with no dangling `Task`. Tests must **poll**
    `/api/conversations/{id}/messages` (bounded retry, ~5s) for the assistant turn — they
    cannot await completion. Wrap the grain's background run in try/catch that **logs**
    failures (best-effort — no caller to surface them to).

11. **Existing agents predate keys.** Old Cosmos `AgentDefinition`s deserialize with a
    null/empty `ApiKeyHash` (new `[Id(9)]`), so they have no usable key until re-saved
    via a **regenerate**. Acceptable (a couple of personal agents); note it in the ADR /
    doc debt rather than building a backfill.

12. **Multipart, single request, store-then-run ordering (attachments).** The endpoint is
    `multipart/form-data` — `prompt` field + `file` parts — because the external caller has
    no session and no conversation id for the browser's two-phase upload. Order inside the
    handler: resolve key → `owner` → **re-check agent exists** (constraint 2 backstop) → mint
    `convId` → **process every file** under `AgentKey(owner.UserId, convId)` → `TouchAsync`
    (title from prompt, else first file name, else "Run") → `BindAgentAsync` →
    `RegisterDocumentAsync` for each ingested doc (**before** enqueue, so RAG is on for the
    turn) → `EnqueuePromptAsync(prompt, imageRefs)` (OneWay) → 202. Files are stored/ingested
    **synchronously in the request** (so a bad file can 400 before the 202); only the model
    run is backgrounded.

13. **Reject the whole batch on any bad file (confirmed).** Reuse `MaxAttachmentBytes` and
    `DocumentMediaTypes.ResolveMediaType`/`IsSupported` exactly as `AgentApi.cs:105`. If any
    part is oversize or an unsupported type, return **400** (text/plain, naming the file) and
    do **not** mint a conversation or enqueue — fail fast and atomic, so automation retries
    cleanly. (This differs from the browser endpoint, which handles one file at a time.)

14. **The vision gate is silent here.** If the resolved agent's model is not vision-capable,
    `ChatCoreAsync`'s gate withholds the images (the assistant turn notes "N attachment(s)
    were not sent"); the caller still gets 202. The owner chose the model, so this is their
    concern — state it, don't try to 400 on it (the endpoint can't cheaply know the bound
    model's capability before the run, and an image-gen or RAG-only intent may not need
    vision anyway).

15. **Attachment egress widens ADR 0013 — inbound this time.** A key holder (unauthenticated
    at the ingress) can now push **image bytes** and **document content** that, if the agent
    uses a third-party model, leave the Entra tenant. The original egress note was
    outbound-only; the ADR (step 10) must record that `/api/agent-run` is now an **inbound**
    channel for that egress, reachable with only a per-agent key.

16. **Image-gen agents get editing for free (ADR 0020).** Because the run routes through
    `ChatCoreAsync`, an agent bound to an image-generation model + an attached image performs
    image-to-image editing; text-only + such an agent is text-to-image. No extra code — it
    falls out of reusing the rich path (constraint 5). Worth one test asserting the branch is
    reachable via enqueue, but not a headline feature.

## Steps

1. **`ApiKey.cs` helper.** `static (string raw, string hash) Generate()` and
   `static string Hash(string raw)` — random 32-byte key + SHA-256 hex. *Verify:* unit
   test that `Hash(Generate().raw) == Generate's hash` and two calls differ.

2. **`AgentDefinition` + directory grain.** Add `[Id(7)] string ApiKeyHash` (default
   `""`). Create `IAgentKeyDirectoryGrain` (grain key = hash) with
   `RegisterAsync(string userId, string agentId)`, `Task<AgentKeyOwner?> ResolveAsync()`,
   `DeregisterAsync()`, backed by `AgentKeyDirectoryState` on the `agent-state` provider.
   Add `record AgentKeyOwner(string UserId, string AgentId)`. **Every cross-grain-
   boundary type here needs `[GenerateSerializer]` + `[Alias]` + `[Id(n)]` members**
   (copy the `AgentDefinition`/`AgentRegistryState` shape) — this applies to
   `AgentKeyOwner`, `AgentKeyDirectoryState`, **and** the `AgentSaveResult` added in
   step 3. A missing attribute fails at test time (it serializes across the silo boundary
   even in the in-memory TestCluster), not at compile time. *Verify:* `dotnet build`;
   directory-grain unit test (register → resolve → deregister → null).

3. **Registry mint/regenerate/deregister.** In `AgentRegistryGrain`:
   - **Insert:** mint key, set `ApiKeyHash`, **write state, then**
     `directory(newHash).RegisterAsync(grainKeyUserId, id)` (constraint 2 create order).
   - **Update:** default keeps the hash. When `regenerateKey` is requested, mint a new
     key and follow constraint 2's **regenerate** order: `directory(oldHash).DeregisterAsync()`
     **first**, then write state with the new hash, then `directory(newHash).RegisterAsync(...)`.
   - **Delete:** remove the agent, write state, **then** `directory(hash).DeregisterAsync()`.
   Change `SaveAsync` to `Task<AgentSaveResult> SaveAsync(AgentDefinition agent, bool
   regenerateKey = false)` returning `AgentSaveResult(AgentDefinition Agent, string?
   PlaintextApiKey)` (raw key non-null only when minted; `[GenerateSerializer]` per
   step 2). The grain reads its own user id from `this.GetPrimaryKeyString()`. *Verify:* `AgentRegistryGrainTests` — create yields
   a raw key + stored hash + a resolvable directory entry; regenerate returns a new key,
   old hash no longer resolves, new one does; delete makes the hash stop resolving; and
   the existing CRUD/order tests still pass (update their `SaveAsync` return-type usage).

4. **Grain background execution (attachment-carrying).**
   `IAgentGrain.EnqueuePromptAsync(string message, IReadOnlyList<AttachmentRef>? attachments)`
   (`[Alias]` + `[OneWay]`). Extract the non-streaming turn core from `ChatCoreAsync` per
   constraint 5 (message-building with vision gate + `UriContent` + image-gen branch → run →
   serialize → `WriteStateAsync` → `TurnAttachments`); call it from `ChatCoreAsync`,
   `ReceiveReminder` (pass empty attachments), and `EnqueuePromptAsync`; log-and-swallow
   failures in the background path. *Verify:* `AgentGrainTests` — `EnqueuePromptAsync(text, [])`
   then poll `GetHistoryAsync` shows the user prompt + assistant reply; `EnqueuePromptAsync`
   with an image `AttachmentRef` records it in the turn (history shows the attachment); a
   separate reminder test still sees `TimesFired` increment.

5. **Endpoint (multipart, prompt + files).** In `AgentApi.cs`: `POST /api/agent-run`
   accepting `[FromForm] string prompt` + `IFormFileCollection files` (or equivalent),
   `.DisableAntiforgery()`. Logic (flat, early-return): read `X-Api-Key` → 401 if
   missing/blank; `hash = ApiKey.Hash(key)`; `owner = directory(hash).ResolveAsync()` → 401
   if null; `agent = registry(owner.UserId).GetAsync(owner.AgentId)` → **401** if null
   (deletion race; 401 not 404 — don't disclose agent existence, keep auth status uniform,
   backstop the delete ordering in constraint 2); `convId = Guid.NewGuid().ToString()`;
   `ownerKey = AgentKey(owner.UserId, convId)`. **Process files (constraint 12/13):** for each
   file — resolve media type; oversize/unsupported → **400** (before any Touch/enqueue);
   image → `attachments.SaveAsync(ownerKey, …)` collect `AttachmentRef`; document →
   `ingestion.IngestAsync(ownerKey, …)`, and if `chunkCount > 0` note it for
   `RegisterDocumentAsync`. Then `await index(owner.UserId).TouchAsync(convId, title)` (title:
   prompt, else first file name, else "Run"); `await agentGrain.BindAgentAsync(...)`; for each
   ingested doc `await agentGrain.RegisterDocumentAsync(name)`; fire
   `agentGrain.EnqueuePromptAsync(prompt, imageRefs)` (OneWay); `return
   Results.Accepted($"/api/conversations/{convId}/messages", new { conversationId = convId })`.
   *Verify:* endpoint tests (step 7).

6. **CRUD endpoints return the once-key.** Update `POST /api/agents` and `PUT
   /api/agents/{id}` to return `AgentSaveResponse(AgentDefinition Agent, string? ApiKey)`
   (raw key only on create / regenerate). Add `bool RegenerateKey = false` to
   `AgentUpsertRequest` (PUT passes it through). Ensure `GET /api/agents` never emits a
   raw key. *Verify:* API tests (step 7).

7. **API tests** (`AgentApiTests.cs`): create an agent → response carries a non-empty
   `apiKey`; `GET /api/agents` carries **no** raw key; `POST /api/agent-run` with that key
   (multipart, `prompt` only) → **202**, `Location` → messages endpoint, body has
   `conversationId`, then poll history for user prompt + assistant reply, and
   `/api/conversations/{id}/agent` reports the bound persona; missing or unknown `X-Api-Key`
   → **401** (fail closed); a **regenerated** key works and the **old** key → 401.
   **Attachment tests:** `agent-run` multipart with an **image** part → 202 and the new
   conversation's history shows the attachment on the user turn; with a supported **document**
   part → 202 and the conversation is document-registered (RAG on — assert via the same signal
   the upload tests use / in-memory index); a batch with an **unsupported or oversize** file →
   **400** and **no** conversation is created (assert the index is unchanged).
   **Fix the existing consumers of the create/update response shape** (they read it as a
   bare `AgentDefinition`; after step 6 the body is `{agent, apiKey}`, so they'd silently
   deserialize to an all-default definition with an empty `Id` and fail at *runtime*, not
   compile): `AgentsEndpoints_CrudRoundTrip_AsJson` (`AgentApiTests.cs:117,128`),
   `ChatWithAgentId_BindsAgent_AndReportsIt` (`:147`), and
   `ChatAfterAgentDeleted_StillUsesPinnedPersona_NoError` (`:195`) — each must read
   `AgentSaveResponse` and use `.Agent.Id`. *Verify:* `dotnet test Agent42.slnx`.

8. **Frontend (minimal, trimmable).** After create / regenerate, surface the returned
   `apiKey` once in a "copy it now — it won't be shown again" affordance; add a
   "regenerate key" control to the agent edit form (sends `regenerateKey: true`). Keep it
   plain per the vanilla-JS convention (no framework). *Verify:* `/run` the DevHost UI,
   create an agent, confirm the key shows once and `/api/agent-run` with it returns 202.

9. **Infra — Easy Auth exclusion** (`container-app.bicep`): add
   `excludedPaths: [ '/api/agent-run' ]` under `globalValidation`. *Verify:* `az bicep
   build --file infra/main.bicep`; functional check at deploy (not unit-tested).

10. **ADR** (`/decide`): `0022-per-agent-api-key-and-easy-auth-carveout.md` — records the
    per-agent hashed-key model, the global key-hash directory grain (the one cross-user
    structure), show-once + regenerate-on-edit, SHA-256-not-bcrypt rationale, fail-closed
    behavior, that existing keyless agents must regenerate, the relationship to ADR
    0002 (**amends**, not supersedes: the browser surface stays fully Easy-Auth-gated;
    only `/api/agent-run` is key-gated), **and the attachment channel** — `/api/agent-run`
    accepts multipart images+documents, so it is an **inbound** vector for ADR 0013's egress
    (image bytes / document content can reach a third-party model), reachable with only a
    per-agent key.

11. **Doc debt** (`/sync-docs` + `/gotcha`): gotchas — "the key directory grain must be
    kept in sync on create/regenerate/delete; a missed deregister leaves a revoked key
    live"; "an Easy-Auth `excludedPaths` endpoint is publicly reachable and must
    self-enforce auth + fail closed"; and **"the background run must reuse `ChatCoreAsync`'s
    turn-building (vision gate / `UriContent` / image-gen), not the attachment-blind reminder
    path, or `agent-run` images silently vanish."** Update `CLAUDE.md`/frontend notes if they
    enumerate routes or the auth model, and note that `/api/agent-run` is a new inbound
    attachment egress path (extends the ADR 0013 CLAUDE.md bullet).

Ordering: 1→2→3 (registry depends on helper + directory); 4 (grain enqueue + ChatCore
extraction) is independent of 1–3; 5 depends on 2–4; 6 depends on 3; 7 depends on 5–6; 8
depends on 6; 9/10/11 after the design is fixed. The attachment work concentrates in 4
(extraction) and 5 (multipart + per-file branch); do 4 before 5.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | Creating an agent returns a non-empty raw `apiKey` **once**; only its hash is stored | `AgentRegistry_Create_MintsKey_StoresHashOnly` | `tests/Agent42.Tests/AgentRegistryGrainTests.cs` |
| AC2 | `GET /api/agents` never returns a raw key | asserted in the create API test | `AgentApiTests.cs` |
| AC3 | `POST /api/agent-run` with a valid key → **202** + `Location` + `conversationId` body | `AgentRun_ValidKey_Returns202` | `AgentApiTests.cs` |
| AC4 | After 202, the new conversation's history holds the prompt + an assistant reply, bound to the agent persona | same test (poll history + `/agent`) | `AgentApiTests.cs` |
| AC5 | Missing or unknown `X-Api-Key` → **401** (fail closed) | `AgentRun_MissingOrUnknownKey_Returns401` | `AgentApiTests.cs` |
| AC6 | Regenerating a key invalidates the old one: new key works, old key → 401; directory reflects the swap | `AgentKey_Regenerate_InvalidatesOldKey` | `AgentRegistryGrainTests.cs` + `AgentApiTests.cs` |
| AC7 | Deleting an agent deregisters its key (hash no longer resolves) | `AgentDelete_DeregistersKey` | `AgentRegistryGrainTests.cs` |
| AC8 | `EnqueuePromptAsync` runs + persists a turn at the grain level | `EnqueuePrompt_RunsAndPersistsTurn` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC9 | `agent-run` multipart with an **image** → 202, and the run's user turn records the attachment | `AgentRun_WithImage_AttachesToTurn` | `AgentApiTests.cs` (+ grain-level in `AgentGrainTests.cs`) |
| AC10 | `agent-run` multipart with a supported **document** → 202, and the conversation is document-registered (RAG on) | `AgentRun_WithDocument_RegistersForRag` | `AgentApiTests.cs` |
| AC11 | `agent-run` batch with an **unsupported/oversize** file → **400**, no conversation created | `AgentRun_BadFile_Rejects400_NoConversation` | `AgentApiTests.cs` |

(Ingress `excludedPaths` reachability is **not** unit-testable here — verified at deploy.
Stated, not dropped. The **vision gate** on a non-vision model (constraint 14) withholds
images silently — covered by existing `ChatCoreAsync` vision-gate tests, not re-asserted per
AC here.)

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ plan time)

- `new_issues: 0`, `new_security_issues: 0`
- `duplication_pct_max: 3` → `RunAndPersistAsync` extraction + single `ApiKey` helper honor this
- `coverage_new_code_pct_min: 80` → directory grain, registry mint/regenerate/delete, endpoint, and grain method all covered by steps 2–8; cover the background try/catch branch or note the exclusion
- `cognitive_complexity_max: 15` → keep the endpoint + `SaveAsync` flat; early-return on auth/404
- `vulnerable_packages: 0`; no new packages expected (BCL `RandomNumberGenerator`/`SHA256`)
- Branch: `feature/*` (never `main` — protected); MR required
- `dotnet format Agent42.slnx --verify-no-changes` clean before MR

## Out of scope

- Key-gated **read** API: `GET /api/conversations/**` stays Easy-Auth-only (owner reads
  results in the UI). No key-authenticated retrieval.
- Durable/restart-surviving execution (reminders) — explicitly OneWay best-effort.
- Rate limiting, per-key scopes/expiry, multiple keys per agent, request signing, an
  audit log of key use.
- Backfilling keys for pre-existing agents (they regenerate on demand).
- A polished key-management UI beyond show-once + a regenerate control.
- Streaming or synchronous return of the agent's reply (the point is 202 + async).
- Appending to an existing conversation (each call creates a new one).
- A two-phase (upload-then-run) attachment flow or a key-gated upload endpoint — files ride
  the single multipart `agent-run` call only.
- Per-file partial success on a bad batch (rejected whole, 400 — constraint 13).
- Returning attachment ids / a media endpoint to the key caller (owner reads results, incl.
  generated images, in the Easy-Auth UI as today).

## Divergence from plan (implementation notes, 2026-07-17)

Implemented on `feature/agent-run-endpoint` off current `main` (`fd89bf1`), not the stale
`a6d6603` baseline. Where the shipped code differs from the plan text:

1. **Background run drains `ChatCoreAsync` — no extracted helper (step 4/5, constraint 5).**
   The plan imagined extracting a shared turn-core. The realized implementation is simpler and
   has **zero duplication**: `EnqueuePromptAsync` clears `_turnLocation` then `await foreach`-drains
   `ChatCoreAsync(message, null, attachments)`, discarding the streamed events. `ChatCoreAsync`
   already *is* the full turn core (no-model guard, image-gen branch, vision gate,
   `UriContent` materialization, RAG, run, persist), so images/image-gen/RAG work identically to a
   browser turn for free. Recorded in ADR 0022 and a dedicated gotcha ("must drain `ChatCoreAsync`,
   not the reminder path").
2. **Tests poll for the OneWay result (grain AND HTTP).** An initial no-poll attempt failed
   deterministically (OneWay returns before the grain runs), confirming the plan's poll guidance —
   both `AgentGrainTests` and `AgentApiTests` poll history with a bounded retry.
3. **A FOURTH existing consumer of the create/update response shape** needed fixing beyond the three
   the plan named: `ToolSelectionApiTests.Agent_RoundTripsEnabledTools` reads `enabledTools` off the
   POST/PUT body — re-nested under `.agent` (now `AgentSaveResponse`).
4. **Added a frontend asset test** (`AgentEditorAssetsTests.AgentEditor_HasApiKeyRegenerateAndReveal`)
   — source-text heuristic guarding the key UI, matching the repo's no-JS-runner convention. Not in
   the plan's test list.
5. **ADR 0002 amended in place** (critic finding): added the `> **Amended by [ADR 0022]**` head-note
   and qualified its "no auth code in handlers" consequence — the carve-out makes that false for the
   one excluded path.
6. **Minor endpoint hardening** not spelled out in the plan: a `HasFormContentType` guard (→ 400 on a
   non-multipart body) and a prompt-and-files-both-empty guard (→ 400).
7. **Complexity gate is self-reported.** The repo has no cognitive/cyclomatic complexity tool wired
   (Roslyn `Metrics` target absent, `lizard` needs Python which isn't installed), so the
   `cognitive_complexity_max` gate is a manual assessment (see the quality-gate table in the MR), not
   a measured number — flagged for the "gate with no measuring tool" cleanup.
8. **Manual DevHost `/run` verification deferred:** a running DevHost instance held the build lock
   throughout, so the UI was not exercised by hand. Coverage is the 25 `AgentApiTests` (202/401/
   attachments/regenerate) + grain tests + the frontend asset test; the browser flow should be
   sanity-checked in DevHost after merge.

## Review

The plan was reviewed twice by a fresh-context critic (once before the per-agent-key
redesign, once after). Final verdict on the current spec: **architecture sound, 4
findings, all applied.** The critic confirmed: the directory grain rides `agent-state`
with no new Cosmos container and no TestSilo change; SHA-256-no-salt / no-`FixedTimeEquals`
is sound for 32-byte random keys (GitHub-PAT model, hash-keyed lookup); no raw-key leak
path (exposing `ApiKeyHash` is harmless); `[Id(7)] … = ""` is correct additive Orleans
versioning and all existing 7-arg `new AgentDefinition(...)` call sites still compile;
the frontend `saveAgent` ignores the response body today so the shape change is purely
additive; the text/plain JSON gotcha is not tripped (responses are JSON objects); the
"amends ADR 0002" framing matches the ADR-0003 precedent; `[OneWay]`/202 and the
server-minted id are sound; the AC→test map is complete.

Findings, all fixed:
1. **(High)** The one write-order rule was unsafe for *regenerate* — registry-write-first
   could strand a live `oldHash` the stored `newHash` can never clear (a permanently
   live revoked key), and step 3 silently contradicted the stated rule. → Constraint 2
   is now **per-operation**: create/delete write-registry-first; **regenerate
   deregisters the old hash FIRST**; step 3 rewritten to match; the endpoint's post-
   resolve agent-existence check documented as the delete backstop.
2. **(Moderate)** Three existing API tests read the create/update response as a bare
   `AgentDefinition` and would break silently at runtime → step 7 now names all three
   (`AgentApiTests.cs:117,128,147,195`) and requires reading `AgentSaveResponse.Agent`.
3. **(Minor–Mod)** `AgentSaveResult`/`AgentKeyDirectoryState` lacked the
   `[GenerateSerializer]` note → step 2 now makes it a blanket rule for every
   cross-boundary type, naming both.
4. **(Minor)** Deletion-race null agent was "401/404" → pinned to **401**
   (don't disclose agent existence; uniform auth status).

No findings rejected.
