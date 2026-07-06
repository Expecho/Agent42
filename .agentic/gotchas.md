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
| Orleans grain (state + reminders + agent tools) | `src/Agent42.Agents/AgentGrain.cs`                | 2026-07-06 |
| Minimal-API endpoint group                      | `src/Agent42.Backend/AgentApi.cs`                 | 2026-07-06 |
| Grain test (Orleans TestCluster + fake client)  | `tests/Agent42.Tests/AgentGrainTests.cs`          | 2026-07-06 |
| Bicep module                                    | `infra/modules/cosmos.bicep`                      | 2026-07-06 |
| Frontend logic (vanilla JS, no framework)       | `src/Agent42.Backend/wwwroot/app.js`              | 2026-07-06 |
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
- **Evidence:** `src/Agent42.Backend/AgentApi.cs` (`/model` endpoint), `src/Agent42.Backend/wwwroot/app.js` (`selectConversation`), `tests/Agent42.Tests/AgentApiTests.cs`
- **Verified:** 2026-07-06

### Agent Framework 1.13 session API differs from older samples

- **Claim:** it's `AgentSession` with `CreateSessionAsync` / `SerializeSessionAsync` / `DeserializeSessionAsync` — the `AgentThread` / `GetNewThread()` API from pre-1.13 samples (and model training data) no longer exists. Copy `AgentGrain.cs`, not blog posts.
- **Evidence:** `src/Agent42.Agents/AgentGrain.cs`
- **Verified:** 2026-07-06
