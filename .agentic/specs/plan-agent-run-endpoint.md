# Plan — Per-agent API key + async run endpoint (`POST /api/agent-run`, 202)

`planned-against: main @ a6d6603`

## Overview

Two coupled deliverables:

1. **Per-agent API keys.** When a stored agent is **created**, mint a
   cryptographically-random API key, return it **once** in the create response, and
   store only its **SHA-256 hash**. The key can be **regenerated while editing** the
   agent (invalidating the old one). The raw key is never persisted and never returned
   by the list/get endpoints again.

2. **`POST /api/agent-run`** — body `{ "prompt": "..." }`, header `X-Api-Key: <key>`.
   The **key alone identifies the agent** (via a global key-hash → agent directory
   grain). Responds **202 Accepted** immediately and runs the prompt against that agent
   **in the background**, landing the reply in a new *listed* conversation the owner can
   open in the UI later.

All four shape decisions were confirmed with the user in the planning interview:
key **alone** identifies the agent (body drops `agentId`; no owner config) · key stored
**hashed, shown once** · **regenerate on edit** · response sink is a **new listed
conversation** · execution is **OneWay / best-effort**.

Why now: lets external automation (webhook, cron, n8n) invoke a specific agent with a
per-agent bearer token, without a signed-in browser session and without holding the
streaming `/api/chat` connection open.

## Affected areas (verified against the tree @ a6d6603)

| Path | Change |
|------|--------|
| `src/Agent42.Agents/AgentDefinition.cs` | Add `[Id(7)] string ApiKeyHash` (hash only — never the raw key) |
| `src/Agent42.Agents/IAgentKeyDirectoryGrain.cs` **(new)** | Global directory: grain key = key-hash → `(userId, agentId)` |
| `src/Agent42.Agents/AgentKeyDirectoryGrain.cs` **(new)** | Impl + `AgentKeyDirectoryState`; rides the `agent-state` provider |
| `src/Agent42.Agents/IAgentRegistryGrain.cs` | `SaveAsync` returns a result carrying the once-only raw key; add a `regenerateKey` path; `DeleteAsync`/regenerate must deregister the old hash |
| `src/Agent42.Agents/AgentRegistryGrain.cs` | Mint key on insert, regenerate on update, keep the directory grain in sync |
| `src/Agent42.Agents/ApiKey.cs` **(new)** | Small helper: generate a random key + compute its hash (one place, so gen and verify never diverge) |
| `src/Agent42.Agents/IAgentGrain.cs` | New `[OneWay] EnqueuePromptAsync(string message)` |
| `src/Agent42.Agents/AgentGrain.cs` | Implement it; extract a shared `RunAndPersistAsync` reused by `ReceiveReminder` |
| `src/Agent42.Backend/AgentApi.cs` | `POST /api/agent-run`; return the once-key from `POST`/`PUT /api/agents`; new request/response records |
| `src/Agent42.Backend/wwwroot/` (`app.js` + agent editor) | Show the key **once** after create/regenerate ("copy it now"); add a regenerate control to the edit form |
| `tests/Agent42.Tests/AgentRegistryGrainTests.cs` | Key minted + hashed on create; regenerate invalidates old; delete deregisters |
| `tests/Agent42.Tests/AgentApiTests.cs` | run-by-key → 202; unknown/missing key → 401; create returns key once; list omits raw key |
| `tests/Agent42.Tests/AgentGrainTests.cs` | `EnqueuePromptAsync` runs + persists a turn |
| `infra/modules/container-app.bicep` | **Only** the Easy Auth `excludedPaths` carve-out for `/api/agent-run` |
| `.agentic/decisions/0004-*.md` | New ADR: per-agent key model + Easy Auth carve-out (amends ADR 0002) |

**Note the shrink vs. the earlier draft:** because the key is self-identifying and
lives (hashed) in Cosmos, there is **no** `ServiceApi:ApiKey` secret, **no**
`ServiceApi:OwnerUserId` config, and therefore **no** change to `main.bicep` /
`deploy.yml` / GitHub secrets. The only infra change is the ingress path exclusion.

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

5. **The reminder path is the execution exemplar.** `AgentGrain.ReceiveReminder`
   (`AgentGrain.cs:378`) already runs a prompt non-streaming via
   `_agent.RunAsync([user msg], _session)` then serializes+persists. The background turn
   does exactly this → extract a private `RunAndPersistAsync(string message)` and call it
   from both (duplication gate ≤ 3%). **Extraction caveat:** `ReceiveReminder` also bumps
   `ScheduledPrompts[reminderName].TimesFired` between the serialize and its single
   `WriteStateAsync` (`AgentGrain.cs:388-394`). Let the helper own RunAsync + serialize +
   `WriteStateAsync`; have `ReceiveReminder` mutate the counter in `_state.State`
   **before** calling the helper, so the single write flushes it. No second write; assert
   the counter still increments in the reminder test.

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
    null/empty `ApiKeyHash` (new `[Id(7)]`), so they have no usable key until re-saved
    via a **regenerate**. Acceptable (a couple of personal agents); note it in the ADR /
    doc debt rather than building a backfill.

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

4. **Grain background execution.** `IAgentGrain.EnqueuePromptAsync(string message)`
   (`[Alias]` + `[OneWay]`). Extract `RunAndPersistAsync` per constraint 5; call from
   both it and `ReceiveReminder`; log-and-swallow failures in the background path.
   *Verify:* `AgentGrainTests` — `EnqueuePromptAsync` then poll `GetHistoryAsync` shows
   the user prompt + assistant reply; a separate reminder test still sees `TimesFired`
   increment.

5. **Endpoint.** In `AgentApi.cs`: `record AgentRunRequest(string Prompt)` and `POST
   /api/agent-run`. Logic (flat, early-return): read `X-Api-Key` → 401 if missing/blank;
   `hash = ApiKey.Hash(key)`; `owner = directory(hash).ResolveAsync()` → 401 if null;
   `agent = registry(owner.UserId).GetAsync(owner.AgentId)` → **401** if null (deletion
   race; use 401 not 404 — don't disclose agent existence to an unauthenticated caller,
   keep the auth-failure status uniform, and this backstops the delete ordering in
   constraint 2); `convId = Guid.NewGuid().ToString()`; `await index(owner.UserId).TouchAsync(convId,
   prompt)`; `await agentGrain.BindAgentAsync(...)`; fire `agentGrain.EnqueuePromptAsync(prompt)`
   (OneWay); `return Results.Accepted($"/api/conversations/{convId}/messages", new {
   conversationId = convId })`. *Verify:* endpoint tests (step 7).

6. **CRUD endpoints return the once-key.** Update `POST /api/agents` and `PUT
   /api/agents/{id}` to return `AgentSaveResponse(AgentDefinition Agent, string? ApiKey)`
   (raw key only on create / regenerate). Add `bool RegenerateKey = false` to
   `AgentUpsertRequest` (PUT passes it through). Ensure `GET /api/agents` never emits a
   raw key. *Verify:* API tests (step 7).

7. **API tests** (`AgentApiTests.cs`): create an agent → response carries a non-empty
   `apiKey`; `GET /api/agents` carries **no** raw key; `POST /api/agent-run` with that key
   → **202**, `Location` → messages endpoint, body has `conversationId`, then poll history
   for user prompt + assistant reply, and `/api/conversations/{id}/agent` reports the bound
   persona; missing or unknown `X-Api-Key` → **401** (fail closed); a **regenerated** key
   works and the **old** key → 401.
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

10. **ADR** (`/decide`): `0004-per-agent-api-key-and-easy-auth-carveout.md` — records the
    per-agent hashed-key model, the global key-hash directory grain (the one cross-user
    structure), show-once + regenerate-on-edit, SHA-256-not-bcrypt rationale, fail-closed
    behavior, that existing keyless agents must regenerate, and the relationship to ADR
    0002 (**amends**, not supersedes: the browser surface stays fully Easy-Auth-gated;
    only `/api/agent-run` is key-gated).

11. **Doc debt** (`/sync-docs` + `/gotcha`): a gotcha — "the key directory grain must be
    kept in sync on create/regenerate/delete; a missed deregister leaves a revoked key
    live" — and "an Easy-Auth `excludedPaths` endpoint is publicly reachable and must
    self-enforce auth + fail closed." Update `CLAUDE.md`/frontend notes if they enumerate
    routes or the auth model.

Ordering: 1→2→3 (registry depends on helper + directory); 4 is independent; 5 depends on
2–4; 6 depends on 3; 7 depends on 5–6; 8 depends on 6; 9/10/11 after the design is fixed.

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

(Ingress `excludedPaths` reachability is **not** unit-testable here — verified at deploy.
Stated, not dropped.)

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
