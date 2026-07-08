# Gotchas & exemplars

The ONLY prose knowledge file in this repo. Two kinds of entries, both cheap to
verify and dated. Add entries via `/gotcha`; re-verify via `/audit-docs`.
Rule of thumb: if the code can express it (types, lint rule, test), it does NOT
belong here. Only constraints invisible in the code itself.

**Size guardrail:** this file describes the *present*, not history (history lives in
`.agentic/decisions/`), so it should hover around equilibrium — roughly ≤40 entries.
Beyond that, do NOT split first: audit for entries that should be lint rules, ADRs,
or deletions (a bloated gotchas file almost always means the admission test lapsed).
Only if 40+ entries survive that audit, split by area into `.agentic/gotchas/<area>.md`
and turn this file into a one-line-per-area index.

Format — every entry MUST have `evidence:` (a repo path) and `verified:` (date):

## Exemplars (canonical shapes — copy these, don't invent)

| Shape                                          | Exemplar                                          | Verified   |
|------------------------------------------------|---------------------------------------------------|------------|
| Orleans grain (state + reminders + agent tools) | `src/Agent42.Agents/AgentGrain.cs`                | 2026-07-07 |
| Per-user registry grain (list-in-state)         | `src/Agent42.Agents/AgentRegistryGrain.cs`        | 2026-07-07 |
| Per-user singleton-preference grain (get/save)  | `src/Agent42.Agents/UserProfileGrain.cs`          | 2026-07-08 |
| Minimal-API endpoint group                      | `src/Agent42.Backend/AgentApi.cs`                 | 2026-07-07 |
| Grain test (Orleans TestCluster + fake client)  | `tests/Agent42.Tests/AgentGrainTests.cs`          | 2026-07-07 |
| Bicep module                                    | `infra/modules/cosmos.bicep`                      | 2026-07-06 |
| Frontend logic (vanilla JS, no framework)       | `src/Agent42.Backend/wwwroot/app.js`              | 2026-07-07 |
| Storage abstraction (interface + prod + in-mem) | `src/Agent42.Agents/IAttachmentStore.cs`, `src/Agent42.Agents/InMemoryAttachmentStore.cs`, `src/Agent42.Backend/BlobAttachmentStore.cs` | 2026-07-08 |
| PWA service worker (root scope, auth-safe)      | `src/Agent42.Backend/wwwroot/sw.js`               | 2026-07-06 |
| Runtime agent skill (progressive disclosure)    | `src/Agent42.Agents/Skills/scheduledprompt/SKILL.md` | 2026-07-06 |

## Constraints

### Cosmos containers must be pre-declared in Bicep

- **Claim:** the Cosmos account has `disableLocalAuth: true` and AAD data-plane RBAC cannot create databases/containers — only ARM can. Any new container must be added to `infra/modules/cosmos.bicep`; Orleans providers run with `IsResourceCreationEnabled = false`, so runtime auto-create silently isn't there to save you.
- **Evidence:** `infra/modules/cosmos.bicep`, `src/Agent42.Backend/Program.cs`
- **Verified:** 2026-07-06

### Cosmos account region is pinned to northeurope and immutable

- **Claim:** West Europe rejected new Cosmos accounts at creation time; the account was created in North Europe and a Cosmos account's region can't change after creation. The `cosmosLocation="northeurope"` parameter in the deploy workflow must keep matching where the account actually lives.
- **Evidence:** `.github/workflows/deploy.yml`
- **Verified:** 2026-07-06

### Custom domain must stay declared in Bicep

- **Claim:** an undeclared custom domain gets silently unbound on the next deploy. `customDomainName`/`customDomainCertificateName` must stay set, and the certificate name must match the auto-generated managed certificate — if the cert is ever recreated, update the name or the deployment unbinds the domain again.
- **Evidence:** `infra/modules/container-app.bicep`
- **Verified:** 2026-07-06

### OpenAI role grant is a CLI step in the deploy workflow, not Bicep

- **Claim:** the AI Foundry account lives in RG `agent42`, where the deploy SP has only `Microsoft.Authorization/roleAssignments/write` scoped to that one account — not `Microsoft.Resources/deployments/write`. A cross-RG Bicep module therefore fails; the grant is an idempotent `az rest` PUT in the deploy workflow. Don't "clean it up" into Bicep.
- **Evidence:** `.github/workflows/deploy.yml` (Grant Cognitive Services OpenAI User step + comment)
- **Verified:** 2026-07-06

### Deploy service principal uses a client secret, not OIDC

- **Claim:** `sp-agent42-deploy` authenticates with a client secret, so `azure/login` needs the full credentials JSON (`clientId`+`clientSecret`+…), not the OIDC client-id-only form.
- **Evidence:** `.github/workflows/deploy.yml`
- **Verified:** 2026-07-06

### Minimal-API handlers returning a bare string serve text/plain, not JSON

- **Claim:** an endpoint whose handler returns `string` (e.g. `GetSelectedModelAsync()`'s
  `Task<string>`) is written as `Content-Type: text/plain` with the raw value (`gpt-4o`),
  NOT JSON (`"gpt-4o"`). The frontend calls `response.json()` on every endpoint, which
  then throws `SyntaxError` — and because that call sat before the history render in one
  shared `try`, it silently hid the whole conversation. Wrap string results in
  `Results.Json(...)` so the API is uniformly JSON. Keep UI side-effects that can throw
  (model-picker restore) after the primary render.
- **Corollary — `Results.Json(null)` writes no body and no content-type.** Returning a
  nullable value (e.g. `Task<AgentBinding?>` from the `/agent` endpoint) through
  `Results.Json` sends an empty 200 with *no* `Content-Type` when the value is null, which
  re-trips the same `response.json()` failure. Serialize explicitly for nullable payloads:
  `Results.Text(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), "application/json")`
  so an absent value comes back as a real JSON `null`.
- **Evidence:** `src/Agent42.Backend/AgentApi.cs` (`/model` and `/agent` endpoints), `src/Agent42.Backend/wwwroot/app.js` (`selectConversation`), `tests/Agent42.Tests/AgentApiTests.cs`
- **Verified:** 2026-07-07

### Service worker must stay network-first for navigations and bypass /api

- **Claim:** the whole app is behind Entra Easy Auth at the ingress (ADR 0002), so an
  expired session turns any navigation into a 302 redirect to the Entra login. The PWA
  service worker (`src/Agent42.Backend/wwwroot/sw.js`) is therefore **network-first for `req.mode ===
  "navigate"`** (cached `index.html` is an offline-only fallback) and **returns early for
  any `/api` path before touching the cache**. "Optimizing" the SW to cache-first
  navigations would serve a stale shell over the login redirect (auth silently breaks);
  caching `/api` would stale/leak per-user, auth-bearing, streaming chat responses. The
  `PwaAssetsTests.ServiceWorker_BypassesApiBeforeCaching` test guards the `/api` ordering,
  but the navigation strategy is only enforced here — keep it network-first.
- **Evidence:** `src/Agent42.Backend/wwwroot/sw.js`, `.agentic/decisions/0002-entra-easy-auth-gate.md`
- **Verified:** 2026-07-06

### A shared static instance must never be a grain-state default (Orleans mutates it)

- **Claim:** giving Orleans grain state a default that is a *cached static* instance
  (`public static T Empty { get; } = new(...)` used as `State.Prop = T.Empty`) leaks across
  grains: Orleans' generated copier/serializer writes *into* the state object it is handed, so
  every grain that started from the shared instance ends up seeing another grain's value. This
  is invisible in the code (the record is immutable-looking) and hides in single-test runs — it
  only surfaces when two grains with *different* keys are exercised in one process and one reads
  another's data. Make the default a **factory** (`public static T Empty => new(...)`, a fresh
  instance per access) or initialize the field with `new(...)` directly — never a `{ get; } =`
  singleton. Same discipline as the list defaults (`= []`) elsewhere.
- **Evidence:** `src/Agent42.Agents/UserProfile.cs` (`Empty` is `=> new("")`), `src/Agent42.Agents/UserProfileGrain.cs` (`UserProfileState.Profile`), `tests/Agent42.Tests/UserProfileGrainTests.cs` (`UserProfile_Empty_IsAFreshInstance_NotASharedSingleton` deterministically guards the factory property; `UserProfile_IsIsolatedPerUser` originally surfaced the leak in-cluster)
- **Verified:** 2026-07-08

### Agent Framework 1.13 session API differs from older samples

- **Claim:** it's `AgentSession` with `CreateSessionAsync` / `SerializeSessionAsync` / `DeserializeSessionAsync` — the `AgentThread` / `GetNewThread()` API from pre-1.13 samples (and model training data) no longer exists. Copy `AgentGrain.cs`, not blog posts.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs`
- **Verified:** 2026-07-06

### Testing that a bound persona reaches the model needs the fake to echo its instructions

- **Claim:** a `ChatClientAgent` built with `ChatClientAgentOptions.ChatOptions.Instructions`
  delivers the system prompt to the *terminal* `IChatClient`, but a canned fake that only
  returns a fixed reply can't prove it arrived — a storage round-trip
  (`GetAgentBindingAsync`) proves the prompt was *saved*, not *delivered*. So `FakeChatClient`
  deliberately echoes what it receives: it prepends a `[sys:…]` marker built from the
  instructions it sees (checking `options?.Instructions` first, then any system-role
  `ChatMessage`, so the echo is robust to however AF carries them), letting a test assert the
  persona reached the model. Two traps when writing that assertion: the `AgentSkillsProvider`
  *appends* its skill catalog to the instructions, so assert the prompt is *contained*, not an
  exact match; and every chat now carries the default persona as instructions, so assert on a
  *distinctive* bound prompt, not merely on non-empty instructions.
- **Evidence:** `tests/Agent42.Tests/FakeChatClient.cs` (`InstructionsEcho`), `tests/Agent42.Tests/AgentGrainTests.cs` (`AgentChat_AppliesBoundSystemPrompt`)
- **Verified:** 2026-07-07

### Image attachment bytes must stay out of the session, and prior-turn image URLs get re-fetched

- **Claim:** the serialized `AgentSession` is written to one `agent-state` Cosmos item every
  turn, and Cosmos caps an item at **2 MB** — so attachment bytes can never be inlined
  (`DataContent`), or the first real photo blows the limit (ADR 0004). Images live in blob
  storage and reach the model as a **`UriContent` user-delegation SAS URL** (a short string).
  The non-obvious trap: a Step 1 spike proved Azure OpenAI **re-fetches a prior turn's image
  URL on every later turn**, so a short-lived SAS baked into a reopened conversation would 400.
  `AgentGrain` therefore **rehydrates before every model call** (`RehydrateAttachmentUrlsAsync`,
  from both `ChatAsync` and `ReceiveReminder`) — refreshing the SAS on every attachment
  `UriContent` in the session. Not just on activation: a warm grain serves many turns without
  reactivating, so an activation-only refresh would let a short-lived SAS lapse mid-conversation
  and 400 on the next turn. It also persists per-turn refs in `AgentGrainState.TurnAttachments`
  (one entry per user turn, in order,
  including an empty entry on the reminder path) so history joins turns to images by index.
  Don't "simplify" back to `DataContent`, and don't drop the rehydrate step. This is a prod-blob
  property the offline harness can't test (the in-memory store returns non-expiring URLs).
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs` (`RehydrateAttachmentUrlsAsync`, `ChatAsync`, `AlignTurnAttachmentsToHistory`), `src/Agent42.Backend/BlobAttachmentStore.cs`, `.agentic/decisions/0004-attachments-in-blob.md`
- **Verified:** 2026-07-08

### Attachments Storage RBAC is in Bicep — but only because the deploy SP was elevated once

- **Claim:** the **Storage Blob Data Contributor** grant to the container app identity
  (`infra/modules/storage-role-assignment.bicep`) is classic `Microsoft.Authorization/roleAssignments`,
  which a plain Contributor cannot create. It works in Bicep only because the deploy SP
  (`sp-agent42-deploy`) was granted **Role Based Access Control Administrator** on `rg-agent42` — a
  one-time manual elevation done 2026-07-08 (`az role assignment create … --role "Role Based Access
  Control Administrator" --scope subscriptions/<sub>/resourceGroups/rg-agent42`). If that elevation is
  removed or the SP recreated, this module's deploy fails with an authorization error — fall back to a
  manual `az role assignment create` of Storage Blob Data Contributor. This is the key difference from
  the **OpenAI grant, which stays manual** (its account is in a *different* RG where a cross-RG Bicep
  role module fails). The role also authorizes `generateUserDelegationKey`, so no account key is needed
  (shared-key auth is disabled on the account). The agent never runs role-assignment writes itself; the
  one-time elevation was run by the user.
- **Gotcha on Windows/Git Bash:** an `az … --scope "/subscriptions/…"` argument starting with `/` is
  mangled by MSYS path conversion into a Windows path → ARM returns `MissingSubscription`. Drop the
  leading `/` (`--scope "subscriptions/…"`) or prefix the command with `MSYS_NO_PATHCONV=1`.
- **Evidence:** `infra/modules/storage-role-assignment.bicep`, `infra/main.bicep` (`storageRoleAssignment` module), `infra/modules/storage.bicep`
- **Verified:** 2026-07-08
