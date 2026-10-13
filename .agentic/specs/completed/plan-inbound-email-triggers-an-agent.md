# Plan — Inbound email triggers a stored agent

planned-against: main @ 270c94b (refreshed 2026-10-10; first planned against 941127f, see **Refresh notes** at the end)

> **Status: implemented (2026-10-10), ADR 0095.** Where the build departs from the plan below, the
> **Implementation divergences** section at the end wins. In particular, the AC table's status codes and
> several test names changed.

## Overview

**Goal:** send (or auto-forward) an email to a per-agent address and have that agent run a
turn on it — attachments included — with no browser session, no login, and no reply.
The result appears as a listed conversation and fires the existing ADR 0028 away-reply push.

**Why now:** the user wants email as an inbound trigger. ADR 0022 already built this exact
door for HTTP (`POST /api/agent-run`: key → directory grain → owner, `excludedPaths`,
multipart, 202, background drain). This is a **second instance of that shape** with a
different credential carrier, not a new architecture.

**Shape:**

```
sender ──SMTP──► Mailgun Route ──multipart POST──► /api/email-in   (excludedPaths)
                 (MX on in.agent42.nl,                   │ 1. verify Mailgun HMAC (fail closed)
                  HMAC-signs the webhook)                │ 2. parse `recipient` local part → token
                                                         │ 3. lowercase → hash → directory grain
                                                         ▼    → (userId, agentId), else 401
                                            TouchAsync (list the conversation)
                                            BindAgentAsync (ADR 0003 persona snapshot)
                                            StoreAgentRunFilesAsync (images + RAG docs)
                                            RegisterDocumentAsync per document
                                                         │
                                            EnqueuePromptAsync ──► 202, background turn
                                                         │
                                            drains ChatCoreAsync → SendAwayReplyPushAsync
```

## Decisions taken in the planning interview

These were the user's explicit calls. Do not relitigate them; if one must change, that is a
new planning round.

- **D1 — Inbound only. Reply-by-email is OUT of scope.** The answer lands in the UI plus the
  ADR 0028 push. This removes an `IEmailReplySender` port, a send credential, `In-Reply-To`
  threading, SPF/DKIM records for outbound, and the open-relay analysis. If it is ever added,
  it must reply **only** to the owner's Easy-Auth-verified address, never to the inbound
  `From` — replying to the sender turns the app into an attacker-steered mail relay.
- **D2 — Mailgun Routes** is the provider. Chosen over SendGrid Inbound Parse because Mailgun
  HMAC-signs its webhooks, giving the endpoint a cryptographic check *independent of* the
  address token. Over Cloudflare Email Workers because that puts feature code in another repo.
  Over ACS because inbound receive is the least-proven path.
- **D3 — The token in the address is the only credential.** No `From` allowlist. Rationale:
  an automatic forwarding rule preserves the *original* sender's `From` and fails SPF, so a
  `From` allowlist would reject exactly the hands-free case this feature is for.
- **D4 — One address per stored agent.** Resolves to `(userId, agentId)`, binds that agent's
  persona/model/tool snapshot, and each email starts a **new** conversation. No continuous
  mode: mail volume would reach ADR 0016's 2 MB grain brick faster than a human types.

## Affected areas

Verified against the tree at `270c94b`.

**New — Agents (`src/Agent42.Agents/Email/`, flat `Agent42.Agents` namespace — ADR 0039):**
- `EmailAddress.cs` — token generate/hash (the one place that does both, `ApiKey`'s rule)
- `IEmailAddressDirectoryGrain.cs`, `EmailAddressDirectoryGrain.cs`
- `EmailLimits.cs` — prompt-length cap

**New — Backend (`src/Agent42.Backend/Email/`, flat `Agent42.Backend` namespace — ADR 0040):**
- `AgentApi.Email.cs` (the **23rd** endpoint partial; 22 exist besides `Api/AgentApi.cs`), `EmailContracts.cs`
- `MailgunWebhook.cs` — signature verification + payload projection

**Modified — source:**
- `src/Agent42.Agents/StoredAgents/AgentDefinition.cs` — trailing `[Id(12)] string EmailToken = ""` (`[Id(10)] McpExposed` and `[Id(11)] Kind` are taken)
- `src/Agent42.Agents/StoredAgents/AgentRegistryGrain.cs` — per-operation email-directory sync
  in all three operations (`InsertAsync`, `SaveAsync` regenerate, `DeleteAsync`)
- `src/Agent42.Backend/StoredAgents/AgentContracts.cs` — `RegenerateEmail` on
  `AgentUpsertRequest` (trailing default), address on `AgentSaveResponse`
- `src/Agent42.Backend/Api/AgentApi.cs` — register `MapEmailEndpoints`
- `src/Agent42.Backend/Program.cs` — read `Email:MailgunSigningKey`
- `src/Agent42.Backend/Configuration/ConfigHealth.cs` — one presence row
- `src/Agent42.Backend/appsettings.json` — `Email` section (comment only; the key is a secret)
- `src/Agent42.Backend/wwwroot/`: `index.html` (`#panelAgentApi`), `js/dom.js`, `js/agents.js`,
  `sw.js` (**CACHE `agent42-shell-v56` → `-v57`**)
- `tools/Agent42.DevHost/Program.cs` — a dev signing key so the leg is exercisable offline
- `infra/main.bicep`, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml`

**Modified — tests:** `ProjectLayoutTests` (both folder lists + `AgentApiParts`),
`EasyAuthExclusionTests`, `AgentRegistryGrainTests`, `AgentApiTests`,
`AgentEditorAssetsTests`, `GrainStorageSerializerTests`, `SerializationCompatibilityTests`.
**New tests:** `EmailAddressTests`, `EmailAddressDirectoryGrainTests`, `MailgunWebhookTests`.

## Context & constraints

Judgment the executor cannot cheaply re-derive. Code facts are deliberately omitted — read
the files.

### C1 — Do NOT reuse `IAgentKeyDirectoryGrain`. This is the security crux.

It is tempting: that grain is already keyed by a SHA-256 hash and already returns
`(userId, agentId)`, so registering the email token's hash into it would need no new grain
type at all. **It is wrong.** Both directories are consulted by hash alone, so a token
registered in the shared directory would authenticate as an `X-Api-Key` against
`/api/agent-run` — and an email address is *vastly* more exposed than an API key (it sits in
mail headers, forwarding rules, provider logs and anyone's autocomplete). One leaked address
would become a live agent-run key, **and** a live `/api/agent-mcp` key, since ADR 0070's
`AgentMcpKeyGate` resolves through the same directory. A **separate grain type** keeps the two credential
namespaces disjoint. `EmailAddressDirectoryGrain` is a near-copy of
`src/Agent42.Agents/StoredAgents/AgentKeyDirectoryGrain.cs`; copy its shape, not its type.

### C2 — The token alphabet MUST be case-insensitive. This would otherwise ship broken.

`ApiKey.Generate()` emits **base64url**, which is case-sensitive. A local part is not
reliably case-preserving: mail servers and Mailgun routinely normalise it, and a human
retyping an address will not preserve case either. A base64url token in an address therefore
hashes to a different digest on arrival and **every inbound message 401s**, with the address
looking perfectly correct in the UI.

Use **lowercase base32** (RFC 4648 alphabet, no padding) over 20 random bytes = 160 bits =
32 characters, plus a short literal prefix. Well inside the 64-character local-part limit, and
`ToLowerInvariant()` on the parsed local part before hashing makes the whole path
case-insensitive end to end. AC5 pins this with an uppercased address.

### C3 — Store the token RAW. This inverts ADR 0022 and follows ADR 0035.

ADR 0022 stores only the hash because an API key is pasted once into a config. An email
address must be **re-readable**: the user needs to see it again to set up a forwarding rule,
check it, or paste it elsewhere. ADR 0035 hit the identical tension with share tokens and
stored the raw token for exactly this reason. Do the same: `AgentDefinition [Id(12)]` holds
the raw token, the directory is keyed by `EmailAddress.Hash(token)` computed at
register/deregister time. Consequence to state in the ADR: a credential now sits in cleartext
grain state (as `ShareToken` already does), and **rotation is the only revocation control**.

### C4 — Per-operation directory sync, with the regenerate inversion.

`AgentRegistryGrain` now owns **two** hashes and must pair every change to each with a
directory update, in the per-operation order the ADR 0022 gotcha specifies: create writes the
registry then registers; delete removes then deregisters; **regenerate deregisters the OLD
hash first**. The email directory follows the same three orders independently. Since ADR 0094, `DeleteAsync`
first clears the agent's outbound webhook (`IAgentWebhookGrain.ClearAsync`) **before** the
registry write. Keep that first; the email deregister goes after the registry write, next to the
API-key deregister. A missed
deregister leaves a revoked address live — and unlike an API key, a live revoked address is
one anybody who ever saw it can drive. Read
`.agentic/gotchas.md` → "The API-key directory must be kept in sync per-operation".

### C5 — `FixedTimeEquals` IS required here, unlike in `ApiKey`.

`ApiKey`'s doc comment explains that no `FixedTimeEquals` is needed because authentication is
a hash-keyed *grain lookup*, not a secret comparison. The Mailgun signature check is the
opposite: it compares a computed HMAC against an attacker-supplied one, which is a classic
timing channel. Use `CryptographicOperations.FixedTimeEquals`. Do not "harmonise" the two —
write the contrast into the code comment so the next reader does not.

### C6 — Blank signing key means 503, never "skip verification".

The secret takes the **optional** shape (`GOOGLE_MAPS_API_KEY`/`CONTEXT_API_KEY`, not
`SEARXNG_SECRET`): no `@minLength`, empty default, conditional env emit, app boots fine. But
the failure mode differs from geocoding's graceful degradation — with no key the endpoint must
**503 and reject**, because the alternative (accepting unsigned webhooks) turns the optional
secret into an optional *security control*. Follow the plumbing of `contextApiKey` through
`.github/workflows/deploy.yml` → `infra/main.bicep` → `infra/modules/container-app.bicep` →
`appsettings.json` → `ConfigHealth`.

### C7 — `excludedPaths` gets ONE exact entry, and the endpoint is the only auth boundary.

Add `'/api/email-in'` — **exact, no `/*`**: the route takes no further segments, and ADR 0042
measured that a wildcard's prefix keeps its trailing slash, so a bare entry is both correct and
narrower. That makes the path publicly reachable, so the handler self-enforces and fails
closed: bad signature, unknown token, or a deleted agent all return a **uniform 401**, and the
userId comes from the directory resolution — **never `GetUserId()`**, which returns `local-dev`
where there is no Easy Auth principal. Read `.agentic/gotchas.md` → "An Easy-Auth
`excludedPaths` endpoint is publicly reachable".

### C8 — `[Id(12)]` is the next free slot, and the phone-call plan claims the same one.

`AgentDefinition` now ends at `[Id(11)] string? Kind` (ADR 0082); `[Id(10)]` is `McpExposed`
(ADR 0070). The next free slot is `[Id(12)]`. `.agentic/specs/plan-phone-call-your-agent.md`
(untracked, unexecuted) still says `[Id(10)]` and is equally stale. Orleans numbering is
positional and renumbering breaks persisted state, so **whichever ships second must take
`[Id(13)]`** and update its own spec. Re-read `AgentDefinition.cs` before writing the field.

### C9 — The prompt body: `body-plain`, not `stripped-text`.

Mailgun offers both. `stripped-text` removes quoted reply/forward content — which is precisely
the payload when a user forwards a thread to their agent. Use `body-plain`, prefixed by
`subject`. If `body-plain` is absent (an HTML-only message), fall back to `stripped-text`, and
if that is also absent reject with 400 rather than pushing HTML soup into the prompt —
`AngleSharp` is deliberately **not** in this repo (ADR 0066) and must not be added for this.
Cap the assembled prompt via `EmailLimits`; a tool return value and a prompt both count against
ADR 0004's 2 MB session.

### C10 — Cognitive complexity will be the friction point.

`RunAgentAsync` is already decomposed into `ResolveAgentRunCallerAsync` /
`ValidateAgentRunFiles` / `StoreAgentRunFilesAsync` for exactly this reason. The email handler
does strictly more (signature, parse, lowercase, resolve, project) and **will** exceed S3776's
threshold of 15 if written inline. Decompose from the start; reuse the two file helpers
verbatim rather than copying them. `ValidateAgentRunFiles` now takes a `copilot` flag (ADR 0085:
a copilot agent has no retrieval and refuses documents). Pass
`AgentKinds.IsCopilot(definition.Kind)` exactly as `RunAgentAsync` does. Research (ADR 0082) and
copilot agents are per-chat only, which already matches D4.

### C11 — Security position, which is the substance of the ADR.

This opens a **permanently exposed inbound door**. Unlike ADR 0022's 256-bit header key, an
address ends up in headers, logs and contact lists. Whoever holds it can push untrusted
instruction text and documents into an agent — text that composes with **ADR 0056 memory (no
delete path, so an injection retained once is re-injected into every future conversation of
that user)**, ADR 0011/0027's exfiltration channel, and ADR 0067's seeded fetch allowlist. It
is also a new inbound vector for ADR 0013's egress. Since ADR 0094, **an email-triggered
turn on an agent with an outbound webhook POSTs the reply to that URL**. That is a complete
untrusted-in, automatic-out path with no human in the loop. It is the owner's own config, but the
ADR must name the composition.

Say this plainly in the ADR, and record the two mitigations that exist: rotation, and pointing
email at an agent whose ADR 0021 `EnabledTools` snapshot excludes `memory` and the fetch tools.
Do **not** claim the Mailgun HMAC mitigates it — that authenticates the *transport*, not the
sender.

### C12 — DNS/MX is a manual operator prerequisite, outside this repo.

`in.agent42.nl` MX → Mailgun, a Route matching that domain forwarding to
`https://chat.agent42.nl/api/email-in`, and a `MAILGUN_SIGNING_KEY` GitHub secret. Use a
**subdomain**: the apex may carry real mail. Note in the ADR that `infra/main.bicep` (`customDomainName`) only
binds the ACA custom domain — DNS lives at the registrar.

## Spike findings (Step 1, 2026-10-10)

Measured against Mailgun's current docs
([Route actions](https://documentation.mailgun.com/docs/mailgun/user-manual/receive-forward-store/receive-http),
[Securing webhooks](https://documentation.mailgun.com/docs/mailgun/user-manual/webhooks/securing-webhooks),
[Webhook retries](https://documentation.mailgun.com/docs/mailgun/user-manual/webhooks/webhook-retries)).
No sandbox account was available, so these are not checked against a real delivery. Step 11 drives
the same shape locally.

- **Fields (form POST to a plain URL):** `signature`, `timestamp`, `token`, `subject`, `sender`,
  `from`, `recipient`, `message-headers`, `body-plain`, `body-html`; best-effort `stripped-text`,
  `stripped-signature`, `stripped-html` (absent when parsing fails). With attachments the POST is
  `multipart/form-data` and carries `attachment-count`, `attachment-1..N`, `content-id-map`.
  The forward URL must **not** end in `mime`/`raw-mime` (sends `body-mime` instead of
  `body-plain`) or `json` (drops `signature`/`timestamp`/`token` from the form). `/api/email-in`
  is safe on both counts.
- **`recipient`** is the SMTP envelope `RCPT TO`, not the `To` header. That is the right field: a
  forwarding rule rewrites the envelope to our address but keeps the original `To`. The docs don't
  say whether it can carry several addresses, so the handler splits on `,` and tries each one.
- **`body-plain`** is documented as always present for plain URLs. C9's fallback chain stays as
  defence in depth.
- **Signature:** HMAC-SHA256, keyed by the account's **HTTP webhook signing key** (not the API key),
  over `timestamp + token` concatenated with no separator, hex digest, compared to `signature`.
  **The HMAC does not cover the body or `recipient`.** A captured `(timestamp, token, signature)`
  triple can be replayed with any body for as long as the timestamp check accepts it. The docs
  recommend caching seen tokens, so the handler does that (see divergence D-S2).
- **Retries:** only **200** is documented as success. **406** is a rejection that is never retried.
  Any other status is retried at 10m/15m/30m/1h/2h/4h (the retries page says 5m/10m/15m/1h/2h/4h)
  for **8 hours** in total. The docs don't say whether a retry is re-signed with a fresh timestamp.

**Consequences for the steps below (they override the AC table's status codes):**

- **D-S1 — Accept with `200`, never `202`.** A 202 is not documented as success. If Mailgun retried
  it, every email would run its turn up to seven times.
- **D-S2 — Replay window is 8h15m in the past and 5 minutes in the future, plus a seen-token cache.**
  The window covers the whole retry span in case retries are not re-signed: a 5-minute window would
  turn an hour of 503 (signing key not yet set) into permanently lost mail. A long window alone
  would leave a captured triple replayable with any body, so an in-process seen-token cache for
  the same window rejects reuse. It is only sound under rule 12's single replica. It is cleared on
  restart, which leaves the address token as the remaining gate.
- **D-S3 — Status codes:** 503 when no signing key is configured (retried, so mail arrives once
  the operator sets it). 401 for a missing/bad/stale/replayed signature: genuine Mailgun traffic
  never sends one except during a key rotation, where a retry *can* succeed. **406** for every
  rejection of a correctly signed message: unknown address, deleted agent (indistinguishable from
  each other), bad or oversize attachment, empty or HTML-only body. A retry cannot fix any of these.
  Answering 406 instead of 401 for an unknown address leaks nothing, because only a holder of the
  signing key ever gets past the signature check.

## Steps

**Step 1 — Spike: pin Mailgun's actual inbound contract.** No repo change. Confirm against
Mailgun's current docs (and, if a sandbox account is available, a real delivery): the exact
multipart field names (`recipient`, `sender`, `subject`, `body-plain`, `stripped-text`,
`attachment-count`, `attachment-N`, `timestamp`, `token`, `signature`), and the signature
algorithm (HMAC-SHA256 over `timestamp + token`, hex-encoded, keyed by the signing key).
Also pin Mailgun's **retry semantics** for a Route's forward action. My unverified recollection
is that any non-2xx except `406 Not Acceptable` is retried for hours. If that holds, every
deliberate rejection (bad file AC7, prompt too long, HTML-only per C9) must answer `406`, not
`400`, or each one becomes a retry storm. 401 for an unknown token is the open call: a retry
cannot succeed either way. Record the measured names and status semantics in this spec under a
**Spike findings** heading before Step 5, and adjust the AC table's status codes to match.
*Everything downstream assumes these names; they are currently unverified.*
**Verify:** findings written into this file.

**Step 2 — `EmailAddress` token helper.** New `src/Agent42.Agents/Email/EmailAddress.cs`:
`Generate()` → `(string Token, string Hash)` and `Hash(string)`, lowercase base32 per C2, one
place doing both per `ApiKey`'s rule, with a comment stating why the alphabet is not base64url
and why this is not `ApiKey`. New `EmailLimits.cs`.
**Verify:** new `EmailAddressTests` — generate/hash agree, tokens unique, hash stable,
`Hash(token.ToUpperInvariant().ToLowerInvariant()) == Hash(token)`, and the token matches
`^[a-z2-7]+$` after its prefix. *Parallelizable with Step 3.*

**Step 3 — Directory grain.** `IEmailAddressDirectoryGrain` + `EmailAddressDirectoryGrain`,
copying `AgentKeyDirectoryGrain`'s shape (grain key = hash, `agent-state` provider, own state
name `"email-address-directory"`), with a comment carrying C1's reason.
**Verify:** new `EmailAddressDirectoryGrainTests` — register/resolve/deregister round-trip and
survival across deactivation, mirroring `AgentKeyDirectoryGrainTests`.
*Parallelizable with Step 2.*

**Step 4 — Persist the token and sync the directory.** `AgentDefinition [Id(12)] EmailToken`
(after checking C8), `AgentRegistryGrain` minting on insert and syncing the email directory in
all three per-operation orders, `AgentUpsertRequest.RegenerateEmail` + the address on
`AgentSaveResponse`.
**Verify:** `AgentRegistryGrainTests` — create mints an address that resolves; regenerate
invalidates the old address and the new one resolves; delete deregisters **both** directories.
Plus the serialization goldens (Step 10).

**Step 5 — Mailgun signature verification.** `src/Agent42.Backend/Email/MailgunWebhook.cs`:
`FixedTimeEquals` per C5, a replay window on `timestamp` (reject beyond a few minutes), and a
payload projection to a small record.
**Verify:** new `MailgunWebhookTests` — a known-good vector verifies, a tampered signature
fails, a stale timestamp fails, a blank signing key fails. *Parallelizable with Steps 2–4.*

**Step 6 — The endpoint.** `src/Agent42.Backend/Email/AgentApi.Email.cs` with
`MapEmailEndpoints`, registered from `src/Agent42.Backend/Api/AgentApi.cs`. Decomposed per
C10, reusing `ValidateAgentRunFiles`/`StoreAgentRunFilesAsync`. Add `"Email"` to **both**
`ProjectLayoutTests.ExpectedFolders()` lists and the entry to `AgentApiParts()`.
**Verify:** `ProjectLayoutTests` green; `AgentApiTests` rows from the AC table below.

**Step 7 — Config plumbing.** `Program.cs` reads `Email:MailgunSigningKey`; `appsettings.json`
comment; one `ConfigHealth` presence row (present = "inbound email enabled", missing = warning
naming the 503). No live probe — there is nothing to probe without a delivery.
**Verify:** existing config-health tests plus a row assertion.

**Step 8 — Infra.** `'/api/email-in'` into `container-app.bicep`'s `excludedPaths`;
`@secure() param mailgunSigningKey string = ''` in `main.bicep` threaded to the module with a
conditional secret + env emit; `deploy.yml` passes it.
**Verify:** `EasyAuthExclusionTests` — new rows asserting `/api/email-in` **is** excluded and
`/api/email-inzzz` is **not**; the existing not-excluded theory stays green.

**Step 9 — Frontend.** An email section in `#panelAgentApi` mirroring the API-key block
(`#agentEmailSection`, `#agentEmailValue`, `#agentEmailCopyBtn`, `#agentRegenerateEmailBtn`),
`dom.js` exports, `agents.js` wiring off `pendingRegenerate`'s pattern, **`sw.js` CACHE
`agent42-shell-v56` → `-v57`** (re-read it first; any MR merged before this one moves it). The address is shown **persistently** (C3), not once — do not
copy `#agentKeyReveal`'s hidden-until-minted behaviour for it.
**Verify:** `AgentEditorAssetsTests` new assertions pinning the ids and the regenerate payload;
`PwaAssetsTests` for the CACHE bump. *Parallelizable with Steps 5–8 (disjoint files), but see
the worktree rule in CLAUDE.md before running two writers at once.*

**Step 10 — Serialization goldens.** Add `EmailAddressDirectoryState` to
`GrainStorageSerializerTests.PersistedTypeNames()` with a committed golden document, a
backward-compat test proving a pre-`[Id(12)]` `AgentDefinition` binds `EmailToken` to `""`, and
the binary golden in `SerializationCompatibilityTests`. **Never regenerate a golden at test
time.**
**Verify:** both suites green.

**Step 11 — Exercise it offline.** Give DevHost a dev signing key, then drive
`POST /api/email-in` with `curl` using a locally-computed signature: assert 202, the
conversation appears, an attachment rides through, an uppercased address still resolves, and a
bad signature 401s. Record the measured values for the ADR.
**Verify:** the curl transcript, pasted into the MR.

**Step 12 — Docs.** `/decide` for the ADR (next free number, **0095** at `270c94b`), carrying C1, C2, C3, C5, C11 and D1–D4 and
the manual DNS/secret prerequisites; `/gotcha` for **two** entries — the case-insensitive
alphabet (C2) and the two-directories rule (C1) — and extend the existing per-operation-sync
gotcha to name the email directory. Add the CLAUDE.md repo-note bullet.
**Verify:** `check-doc-paths.mjs` green.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A signed webhook with a known address 202s and runs a turn into a new listed conversation | `EmailIn_ValidSignedMessage_RunsTurn` (polls) | `AgentApiTests.cs` |
| AC2 | An invalid/absent Mailgun signature is rejected 401 | `EmailIn_BadSignature_Returns401` | `AgentApiTests.cs` |
| AC3 | An unknown token is rejected 401, indistinguishable from AC2 | `EmailIn_UnknownAddress_Returns401` | `AgentApiTests.cs` |
| AC4 | A deleted agent's address fails closed 401, not 404 | `EmailIn_DeletedAgent_Returns401` | `AgentApiTests.cs` |
| AC5 | An address whose local part arrives uppercased still resolves | `EmailIn_UppercasedAddress_StillResolves` | `AgentApiTests.cs` |
| AC6 | An attached image rides onto the turn; a document is ingested for RAG | `EmailIn_WithImage_RecordsAttachment`, `EmailIn_WithDocument_Ingests` | `AgentApiTests.cs` |
| AC7 | A batch with one bad file is rejected whole (400) before any conversation exists | `EmailIn_BadFile_RejectsWholeBatch` | `AgentApiTests.cs` |
| AC8 | With no signing key configured the endpoint 503s and never accepts | `EmailIn_NoSigningKey_Returns503` | `AgentApiTests.cs` |
| AC9 | Create mints an address that resolves to `(userId, agentId)` | `AgentCreate_MintsResolvableEmailAddress` | `AgentRegistryGrainTests.cs` |
| AC10 | Regenerate kills the old address and the new one resolves | `EmailAddress_Regenerate_InvalidatesOld` | `AgentRegistryGrainTests.cs` |
| AC11 | Delete deregisters **both** the API-key and email directories | `AgentDelete_DeregistersBothDirectories` | `AgentRegistryGrainTests.cs` |
| AC12 | The email token hash never resolves as an agent-run API key (C1) | `EmailToken_DoesNotAuthenticateAgentRun` | `AgentApiTests.cs` |
| AC13 | Token/hash agree, are unique and stable, and are case-insensitive (C2) | `EmailAddressTests` (4 facts) | `EmailAddressTests.cs` |
| AC14 | Signature verification: good/tampered/stale/no-key | `MailgunWebhookTests` (4 facts) | `MailgunWebhookTests.cs` |
| AC15 | `/api/email-in` is excluded; a name stem is not | `Exclusions_CoverInboundEmail`, `WildcardEntry_DoesNotMatchANameStem` | `EasyAuthExclusionTests.cs` |
| AC16 | The new folders and the 23rd partial are registered from the composition root | `ProjectLayoutTests` theories | `ProjectLayoutTests.cs` |
| AC17 | A pre-`[Id(12)]` agent document binds `EmailToken` to `""` | `AgentDefinition_PreEmailToken_BindsEmpty` | `GrainStorageSerializerTests.cs` |
| AC18 | The agent editor exposes the address + regenerate control | `AgentEditor_HasEmailAddressAndRegenerate` | `AgentEditorAssetsTests.cs` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `.agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time (`error`) |
| `vulnerable_packages` | **0** | CI package-freshness job |
| Branch policy | never commit to `main` | `.claude/hooks/protect-main.mjs`, `lefthook.yml` |

No new package is expected (HMAC is `System.Security.Cryptography`). If one proves necessary,
resolve the version from the feed — never from memory — and pin it in
`Directory.Packages.props`.

## Out of scope

- **Reply by email** (D1) — no send credential, no `IEmailReplySender`, no threading.
- **A `From` allowlist** (D3), and any use of `From` for authorization.
- **Continuous/threaded email conversations** (D4).
- **Running SMTP, POP or IMAP.** The ACA app has one HTTP ingress; Mailgun owns MX.
- **HTML email rendering** — `AngleSharp` stays out of the repo (C9).
- **A second address for plain (non-agent) chats.**
- **Backfilling addresses for existing agents** — trailing default `""`, minted on next
  create or regenerate, exactly as ADR 0022 handled keys.

## Refresh notes (2026-10-10, 941127f → 270c94b)

45 commits landed after the first draft. These are the changes to this spec, each checked
against the code:

- `AgentDefinition` grew `[Id(10)] McpExposed` (ADR 0070) and `[Id(11)] Kind` (ADR 0082), so
  the email token moves to **`[Id(12)]`** (C3, C8, Step 4, Step 10, AC17).
- The ADR number is **0095**; 0068 is already the stored-agent retention ADR.
- `sw.js` CACHE is `agent42-shell-v56`; the bump is to **v57**.
- 22 `AgentApi.*` partials exist, so email is the **23rd**.
- ADR 0070's `AgentMcpKeyGate` also resolves through `IAgentKeyDirectoryGrain`, which strengthens C1.
- ADR 0094's `DeleteAsync` clears the webhook first (C4), and an email-triggered turn fires the
  agent's outbound webhook (C11).
- ADR 0085: `ValidateAgentRunFiles` now takes a `copilot` flag (C10).
- New in Step 1: pin Mailgun's retry-on-non-2xx semantics before fixing the rejection status codes.

## Implementation divergences (2026-10-10)

What shipped differs from the steps above in these ways. Each is recorded in ADR 0095.

- **Status codes (Spike findings D-S1..D-S3).**
  - Accepted is **200**, not 202.
  - Every rejection of a correctly signed message is **406**, not 400/401. That covers unknown and deleted addresses,
    nothing readable, a document that fails ingestion, and an oversize body.
  - 401 is only for a bad, stale or replayed signature; 503 is for no signing key.
  - So AC2–AC4 and AC7 assert different codes than the table says.
- **Replay protection added.** The HMAC does not cover the body, so `MailgunReplayGuard` (an in-process seen-token
  cache) refuses token reuse, with an 8h15m timestamp window (`MailgunWebhook.MaxAge`). It is new in Step 5 and
  tested in `MailgunWebhookTests` and `EmailIn_ReplayedToken_Returns401`.
- **AC7 inverted: skip-and-note.** An unusable attachment is skipped and named in the prompt, not a whole-batch
  rejection, because the sender gets no reply and a rejected email would vanish. The per-file rule moved into
  `AgentRunFileError`, shared with `/api/agent-run` (whose behaviour is unchanged). The test is
  `EmailIn_UnsupportedFile_IsSkippedAndNoted`.
- **The HTML-only body is noted, not rejected** (C9 said reject with 400). The subject still makes a usable prompt.
  The email is refused only when there is no subject, no plain-text body and no usable file.
- **`EmailToken` is `string? = null`, not `string = ""`.** This repeats `Kind`'s reasoning: the Cosmos JSON path binds
  a missing key to null. AC17 is `AgentDefinition_PreEmailToken_BindsNull`.
- **A shared run starter.** `StartAgentRunAsync` (list, bind, register docs, enqueue) was extracted from
  `RunAgentAsync`. `StoreAgentRunFilesAsync` now returns an error string, so each door picks its own status.
- **`GET /api/email/config`** (enabled + domain, never the key) was added so the editor can show the address. The
  plan put the address on `AgentSaveResponse`, which a later GET could not carry. There is a new
  `Email:InboundDomain` setting (`in.agent42.nl`, committed, not secret).
- **A request size cap (critic finding).** The body is read before authentication, so `/api/email-in` is capped at
  30 MB (`EmailLimits.MaxRequestBytes`) via a `Content-Length` pre-check (406) and route-level Kestrel and form limits.
- **Endpoint tests live in `EmailApiTests.cs`** (own `EmailApiFixture` with a known key) and `EmailApiNoKeyTests`, not
  in `AgentApiTests.cs`. Several AC tests were merged or renamed: unknown and deleted share one test, and image and
  document share one.
- **Recipient parsing** handles a comma list (up to `EmailLimits.MaxRecipients`), a display name, and `+tag`
  sub-addressing.
- **The prompt** carries `From` and `Subject` lines (newlines flattened so a subject cannot forge a header line) and
  never the recipient address. It is capped at 32,000 characters with a marker.
- **Step 11 used DevHost** with signing key `dev-mailgun-signing-key` and domain `in.localhost`. The run checked a
  valid send with an image (200, listed, attachment on the turn), an uppercased address (200), a bad signature
  (401), a replay (401), an unknown address (406) and the email token as `X-Api-Key` (401). No real Mailgun delivery
  was made.
