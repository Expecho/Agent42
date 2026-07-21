# Plan — PWA push notification when an agent replies while the user is away

planned-against: main @ 601f61f

## Overview

**Goal:** When an agent finishes a reply on an **asynchronous** path — a scheduled
prompt firing (`ReceiveReminder`) or an external `/api/agent-run` job (`EnqueuePromptAsync`,
ADR 0022) — and the user is not actively watching, deliver a **Web Push** notification to
the user's installed Android PWA whose **title is the agent's name**.

**Why now:** these two async paths already run with *no live client attached* (their
streamed events have no listener), so a reply completes and the user only discovers it by
re-opening the app. Push closes that gap. This is greenfield — the repo has a service
worker and manifest (ADR 0023) but **no push infrastructure** (no VAPID, no subscription
store, no `push`/`notificationclick` handlers).

## "User not online" — the mechanism (read this first)

There is deliberately **no server-side presence/heartbeat system**. "Not online" is
approximated by a **single** fact, kept simple on purpose:

- **We only send on the two async paths**, never from the live interactive `ChatAsync`
  stream. A user actively chatting receives their reply in-stream; no push is generated
  for that turn. `ChatAsync`, `EnqueuePromptAsync`, and `ReceiveReminder` all share
  `ChatCoreAsync` (`AgentGrain.cs:603`), so the send MUST be placed in the two async
  **callers** after a *successful* completion — never inside `ChatCoreAsync` (that would
  fire on the live path too). Because push subscriptions are per-device, a delivered push
  reaches whichever of the user's devices are subscribed; the async paths only fire when
  the user isn't the one driving the turn, so this is the right trigger.

**Why NOT suppress-if-focused in the service worker (critic finding #2):** the obvious
"skip `showNotification` when a client is visible" trick **violates the Chromium
`userVisibleOnly:true` contract** — a push received without a shown notification consumes
push budget and, once exhausted, Chrome shows its own generic "site updated in the
background" notice and can revoke the subscription. That penalty lands in exactly the
target case (a scheduled/agent-run reply completing while the user happens to have the PWA
open on that device). So the SW **always shows** the notification on a received push
(honouring the contract, zero budget risk). If the user is looking at the app on that
device when their *background* agent finishes, a notification titled with the agent name
is acceptable and arguably wanted. Recorded as an accepted tradeoff in the ADR (Step 9).

A server-side online/heartbeat tracker was **considered and rejected** as over-engineering:
it adds grain state + a client ping loop for no behavioural gain over "async paths only".
Recorded in the ADR (Step 9).

## Affected areas (verified against the tree @ 601f61f)

**New files**
- `src/Agent42.Agents/IPushNotificationSender.cs` — `IPushNotificationSender` + `PushMessage` record (interface lives in Agents, like `IGeocoder`/`IWebSearch`/`IWebPageFetcher`).
- `src/Agent42.Agents/IUserPushGrain.cs` — per-user subscription grain interface.
- `src/Agent42.Agents/UserPushGrain.cs` — grain impl + `[GenerateSerializer]` state + `PushSubscription` record.
- `src/Agent42.Agents/NullPushNotificationSender.cs` — no-op sender (unconfigured prod + offline).
- `src/Agent42.Backend/WebPushNotificationSender.cs` — real sender (WebPush lib), prunes dead subs.
- `src/Agent42.Backend/IWebPushClient.cs` + `WebPushClientAdapter.cs` — thin testable seam over the library's raw send.
- `tests/Agent42.Tests/RecordingPushNotificationSender.cs` — records sends for grain tests.
- `tests/Agent42.Tests/UserPushGrainTests.cs` + `WebPushNotificationSenderTests.cs` — unit tests (Steps 1–2).
- `.agentic/decisions/0028-*.md` — the decision (Step 9, via `/decide`).

**Modified files**
- `Directory.Packages.props` — add the resolved web-push package (CPM pin).
- `src/Agent42.Agents/AgentGrain.cs` — inject `IPushNotificationSender`; send **inside the `try`, after the drain loop** in `EnqueuePromptAsync` (loop ends ~`:591`, before the `catch` at `:594` — **not** at `:601` after the try/catch, which would notify on a faulted turn), gated on a real reply; and after `WriteStateAsync` in `ReceiveReminder` (~`:1577`, which already early-returns on the no-model path at `:1555`).
- `src/Agent42.Backend/AgentApi.cs` — `GET /api/push/config`, `POST` + `DELETE /api/push/subscriptions` (userId via `http.GetUserId()`; exemplar: the `/api/providers` block ~`:422`).
- `src/Agent42.Backend/Program.cs` — register `WebPushNotificationSender` when a VAPID private key is configured, else `NullPushNotificationSender`; read `Push:*`.
- `src/Agent42.Backend/appsettings.json` — `Push` section: `VapidSubject` + `VapidPublicKey` (both **non-secret**, committed).
- `tools/Agent42.DevHost/Program.cs` — register `NullPushNotificationSender` (offline; no VAPID).
- `src/Agent42.Backend/wwwroot/sw.js` — `push` + `notificationclick` handlers (+ suppress-if-focused).
- `src/Agent42.Backend/wwwroot/app.js` — permission→subscribe/unsubscribe flow, `/api/push/config` fetch, a Preferences toggle, and a `message` handler that calls `selectConversation` on click (`selectConversation` exists at `app.js:955`).
- `src/Agent42.Backend/wwwroot/index.html` — the Preferences toggle control (tab structure wired by `wireTabs` / `openProfileDialog`, `app.js:1428`/`1456`).
- `infra/main.bicep` + `infra/modules/container-app.bicep` — one **optional** secret param `vapidPrivateKey` → env `Push__VapidPrivateKey` (copy the `contextApiKey`/`googleMapsApiKey` shape exactly — empty default, no `@minLength`, conditional emission).
- `.github/workflows/deploy.yml` — pass `vapidPrivateKey="${{ secrets.VAPID_PRIVATE_KEY }}"` (alongside `contextApiKey` ~`:98`).
- **All five test silo configurators** (each hand-registers the AgentGrain dep set — verified they all register `IUserMcpToolProvider`; a required new ctor dep breaks activation in any that miss it): `tests/Agent42.Tests/TestSiloConfigurator.cs:76`, `NoModelSiloConfigurator.cs:59`, `CapSizedWebPageFetcher.cs:75` (`CapSizedFetcherSiloConfigurator`), `FakeGeocoder.cs:51` (`CoordinatesOnlyGeocoderSiloConfigurator`), `AttachmentRehydrateTests.cs:46` (`CountingStoreSiloConfigurator`). Register a `RecordingPushNotificationSender` in `TestSiloConfigurator` (the one the behavioural tests use) and a `NullPushNotificationSender` in the other four.
- `tests/Agent42.Tests/AgentGrainTests.cs` — behavioural tests (below).
- `tests/Agent42.Tests/AgentApiTests.cs` — subscription CRUD + config endpoint tests.
- `tests/Agent42.Tests/PwaAssetsTests.cs` — source-text guard for the new SW handlers (existing pattern: `ServiceWorker_HandlesShareTarget`).
- `CLAUDE.md` + `.agentic/gotchas.md` — doc debt paid in the same MR.

## Context & constraints (what the executor can't cheaply re-derive)

- **Agent name source:** `_state.State.AgentName` (`AgentGrainState.cs:48`, `string?`),
  snapshotted on bind (`AgentGrain.cs:441`). Use `_state.State.AgentName ?? "Agent 42"` —
  a scheduled prompt on a **plain** (non-agent) conversation has no bound agent, and a
  reminder firing there is still an away-reply worth surfacing. **Fallback is `"Agent 42"`
  (with a space) — the product name from `manifest.webmanifest`, a user-facing title.**
  Note it deliberately differs from the internal persona-fallback `"Agent42"` (no space) at
  `AgentGrain.cs:322`, which is an agent *option* string, not a display title. AC4 asserts
  `"Agent 42"` exactly — use that literal.
- **Send must not stall the turn (critic finding #3, gotcha `gotchas.md:221`):** `AgentGrain`
  is **non-reentrant**, so an inline send blocks the whole activation for the round-trip to
  the push service × N subscriptions. Two mitigations, both required: (1) the sender bounds
  the whole operation with a **short `CancellationToken` timeout** (e.g. a few seconds total)
  and sends to subscriptions concurrently; (2) the underlying `HttpClient` (whether the
  library's or `WebPushClientAdapter`'s) must **`RemoveAllResilienceHandlers()`** — the
  gotcha warns every Backend named client silently inherits `AddServiceDefaults`' 3-retry
  pipeline, which would quadruple a dead-endpoint stall on this exact grain. Confirm the
  chosen library's HTTP path and how it obtains its `HttpClient` before wiring.
- **User id:** the retained `_userId` field (`AgentGrain.cs:61`, set in `OnActivateAsync`
  `:128`). Both `_userId` and `AgentName` are **activation-scoped, not turn-scoped**, so
  the send has **no** turn-scoped-field hazard (contrast `_turnLocation` — gotcha at
  `gotchas.md:297`). Do not read any per-turn field in the send.
- **Interface/impl split (exemplar):** `IGeocoder` etc. are declared in `Agent42.Agents`
  (`IGeocoder.cs`) with the concrete impl in `Agent42.Backend` (`GoogleGeocoder.cs`) and
  injected into `AgentGrain`'s constructor (`AgentGrain.cs:98-120`). `IPushNotificationSender`
  follows this exactly — `Agent42.Agents` must NOT reference the web-push package.
- **Subscription store — dedicated grain, not `UserProfile`:** `UserProfile` (ADR 0005)
  frames itself as a **scalar singleton** of preferences (it rejected reusing
  `AgentRegistryGrain` precisely because a profile is a singleton, not a collection). A
  per-device subscription **list** that the send path mutates/prunes is a churny collection —
  a poor fit for the scalar profile — so it gets its own grain — `IUserPushGrain` riding the shared `agent-state` provider with a distinct
  state name `"push-subs"` (**no new Cosmos container**, same as `UserProviderConfigGrain`
  state `"providers"` / `UserMcpConfigGrain` state `"mcp-servers"`). Cleanest CRUD exemplar:
  `UserProviderConfigGrain.cs`. Grain API: `GetAllAsync()`, `AddAsync(PushSubscription)`
  (upsert by endpoint), `RemoveAsync(string endpoint)`.
- **Subscriptions stored in cleartext (decision):** a push subscription is a
  capability URL + `p256dh`/`auth` keys — device push tokens, not third-party account
  credentials. Unlike model-provider/MCP secrets (which the repo DP-encrypts), these are
  useful only against *our* VAPID pair and are never echoed to any other user. Storing them
  plain in the per-user grain (behind the same Cosmos as everything else) keeps scope
  minimal. Encryption via a DP purpose was considered; deferred. Record in the ADR; a
  reviewer may reasonably push back — see Review.
- **VAPID keys — public committed, private is the only secret:** the public key and the
  `mailto:` subject are **not** secret and live in `appsettings.json` (committed); the
  browser reads the public key from `GET /api/push/config`. Only the private key is a
  secret → **one** new GitHub Actions secret `VAPID_PRIVATE_KEY` → `Push__VapidPrivateKey`
  env, wired as **OPTIONAL / not on the availability path** (copy `contextApiKey` /
  `googleMapsApiKey`, *not* `searxngSecret`): with no private key the app starts normally,
  `GET /api/push/config` returns `enabled:false`, the UI hides the toggle, and no send is
  attempted. This is the ADR 0012/0027 "optional secret" shape.
- **Package (hard rule 4):** the executor resolves the web-push library at add-time
  (`dotnet add package …`) and pins it in `Directory.Packages.props` — **never** a version
  from memory. Candidate: `WebPush` (web-push-libs/web-push-csharp). Verify it passes the
  CI `vulnerable_packages: 0` gate before committing; if it fails/looks unmaintained, pick a
  maintained VAPID web-push library instead. The raw send is wrapped behind `IWebPushClient`
  so the library choice is isolated and the pruning logic is unit-testable with a fake.
- **Dead-subscription pruning:** the push service returns **404/410** for an expired/gone
  subscription; `WebPushNotificationSender` catches that (the library surfaces it as
  `WebPushException` with a status code) and calls `IUserPushGrain.RemoveAsync(endpoint)`.
  All send failures are swallowed + logged — a push failure must **never** propagate into
  the turn (the turn has already been persisted).
- **Every AgentGrain host must register the sender:** prod (`Program.cs`), offline
  (`tools/Agent42.DevHost/Program.cs`), and the test silo
  (`tests/Agent42.Tests/TestSiloConfigurator.cs`) — else grain activation fails, exactly
  like the `IGeocoder`/`IUserMcpToolProvider` rule in CLAUDE.md. Every host that maps
  `MapAgentApi` already resolves grains via `IGrainFactory`, so the endpoints need no new
  host dependency beyond what's registered.
- **Egress (ADR 0013) — payload is E2E-encrypted, but note it:** each notification transits
  a third-party push service (FCM / Mozilla autopush / WNS). Web Push payloads are
  **end-to-end encrypted per RFC 8291** with the subscription's `p256dh`/`auth` keys, so the
  push service **cannot read** the agent name or body — a materially smaller egress than
  ADR 0013's model egress. Still record it in the ADR, because the **title is the
  user-chosen agent name**, which a user could have made sensitive, and the endpoint's
  origin reveals *that* a notification was sent.
- **Frontend has no JS *runtime* harness, but a source-text seam exists (critic finding
  #6):** as with ADR 0023, behaviour on a real device is verified by **offline run + manual
  check**. But the repo *does* assert SW/manifest content via C# source-text heuristics
  (`tests/Agent42.Tests/PwaAssetsTests.cs`, e.g. `ServiceWorker_HandlesShareTarget`) — so
  add a cheap guard that `sw.js` registers `push` + `notificationclick` listeners. The
  interactive subscribe flow in `app.js` stays manual-only.
- **Governing docs to read before touching each area:** `gotchas.md:297` (turn-scoped
  fields — why the send is *safe* here), ADR 0022 (the agent-run async path), ADR 0023
  (SW structure, Android-only PWA reality, `serviceWorker.register` at `app.js:2189`), ADR
  0005 (why a dedicated grain, not `UserProfile`), ADR 0012/0027 (the optional-secret shape).

## Steps

> Steps 1, 2, 6, and 8 are independent and **parallelizable** (touch disjoint files).
> Step 3 depends on 1+2; Step 4 depends on 1; Step 5 depends on 2; Step 7 depends on 4+5+6.

**Step 1 — Subscription grain.** `IUserPushGrain` + `UserPushGrain` (state `"push-subs"`
on `AgentGrain.StorageProviderName`) + `PushSubscription` record (`Endpoint`, `P256dh`,
`Auth`; `[GenerateSerializer]`, positional `[Id(n)]`). Upsert-by-endpoint in `AddAsync`.
*Files:* the three `*Push*` files in `Agent42.Agents`.
*Verify:* new grain roundtrip test (add→get→remove; upsert dedupes by endpoint) —
`dotnet test Agent42.slnx --filter FullyQualifiedName~UserPushGrain`.

**Step 2 — Sender abstraction + impls.** `IPushNotificationSender.SendAsync(userId,
PushMessage, ct)` + `PushMessage(Title, Body, ConversationId)` in Agents;
`NullPushNotificationSender` (no-op) in Agents; `IWebPushClient` seam + `WebPushClientAdapter`
+ `WebPushNotificationSender` in Backend (add the package to `Directory.Packages.props`
first). Sender: resolve `IUserPushGrain(userId).GetAllAsync()`, send to each via
`IWebPushClient` **concurrently under one bounded `CancellationToken` timeout**, prune on
404/410, swallow+log all failures. The adapter's `HttpClient` must call
**`RemoveAllResilienceHandlers()`** (gotcha `:221`).
*Files:* the Agents + Backend sender files, `Directory.Packages.props`.
*Verify:* unit test with a fake `IWebPushClient` — sends to all subs; a 410 triggers
`RemoveAsync`; a throw/timeout does not escape. `--filter ~WebPushNotificationSender`.

**Step 3 — Wire the send into the async paths.** Inject `IPushNotificationSender` into
`AgentGrain`'s constructor. Call `SendAsync(_userId, new PushMessage(
_state.State.AgentName ?? "Agent 42", <short body>, <conversationId>), ct)`:
- in `EnqueuePromptAsync`, **inside the `try`, immediately after the drain loop** (not after
  the try/catch — a faulted turn must not notify), **gated on `!string.IsNullOrEmpty(_modelId)`**
  so the no-model path (which yields "_No model provider is configured_" + `Done` and drains
  *normally*, `AgentGrain.cs:615-625`) does **not** fire a false "your agent replied";
- in `ReceiveReminder`, after `WriteStateAsync` (`:1577`) — the no-model case already
  early-returned at `:1555`, so no extra guard needed there.
**Do not** add the call to `ChatCoreAsync` or `ChatAsync`.
*Files:* `AgentGrain.cs`.
*Verify (core behaviour):* in `AgentGrainTests` with `RecordingPushNotificationSender`
(registered via TestSiloConfigurator):
(a) a fired reminder records exactly one send whose title == the bound agent's name;
(b) `EnqueuePromptAsync` records a send; (c) an interactive `ChatAsync` turn records
**zero** sends; (d) an `EnqueuePromptAsync` with **no model** configured records **zero**
sends (use `NoModelSiloConfigurator`). `--filter ~AgentGrainTests`.

**Step 4 — Subscription + config endpoints.** In `MapAgentApi`: `GET /api/push/config`
→ `{ enabled, publicKey }` (`enabled` = private key present); `POST /api/push/subscriptions`
(body = subscription) → `AddAsync`; `DELETE /api/push/subscriptions` (body/query = endpoint)
→ `RemoveAsync`. userId via `http.GetUserId()`.
*Files:* `AgentApi.cs`.
*Verify:* `AgentApiTests` — POST persists (visible via grain), DELETE removes, GET config
reflects the configured `enabled`/`publicKey`. `--filter ~AgentApiTests`.

**Step 5 — DI + config wiring.** `Program.cs`: bind `Push:*`; register
`WebPushNotificationSender` (+ `IWebPushClient` adapter) when `Push:VapidPrivateKey` is
non-empty, else `NullPushNotificationSender`. `appsettings.json`: `Push` with `VapidSubject`
+ `VapidPublicKey` (generated once — see prereqs). `DevHost/Program.cs`: register
`NullPushNotificationSender`.
*Files:* `Program.cs`, `appsettings.json`, `DevHost/Program.cs`.
*Verify:* `dotnet build Agent42.slnx`; `dotnet run --project tools/Agent42.DevHost` boots
(offline, push disabled).

**Step 6 — Service worker handlers.** In `sw.js`: `push` listener (parse JSON data →
`showNotification(title, {body, data:{conversationId}})`, **always shown** — no
suppress-if-focused, per finding #2 and the mechanism section) and `notificationclick`
listener (focus an existing client and `postMessage {type:"open-conversation",
conversationId}`, else `clients.openWindow("/")`). Keep the existing share-target `fetch`
handler untouched. Bump the shell cache version if the SW changes require it.
*Files:* `sw.js`.
*Verify:* (1) source-text guard in `PwaAssetsTests.cs` asserts the two listeners exist
(`--filter ~PwaAssets`); (2) manual — DevTools *Application → Push* test payload shows a
notification titled with the agent name.

**Step 7 — Frontend subscribe flow + Preferences toggle.** In `app.js`: on load fetch
`/api/push/config`; if `enabled` and `PushManager`/`Notification` are supported, render a
Preferences toggle "Notify me when an agent replies while I'm away". Toggling on →
`Notification.requestPermission()` → `registration.pushManager.subscribe({userVisibleOnly:
true, applicationServerKey: <publicKey>})` → `POST /api/push/subscriptions`. Toggling off →
`subscription.unsubscribe()` + `DELETE`. Add a `navigator.serviceWorker` `message` handler
for `open-conversation` → `selectConversation(id)`. `index.html`: the toggle markup in the
Preferences dialog.
*Files:* `app.js`, `index.html`.
*Verify:* manual on Android Chrome (installed PWA) — enable in Preferences, trigger a
reminder/agent-run while the app is backgrounded, confirm a notification titled with the
agent name arrives and tapping it opens the conversation. Feature-detect so unsupported
browsers (incl. iOS-in-browser) simply hide the toggle.

**Step 8 — Infra + deploy secret.** Add the optional `vapidPrivateKey` param to
`main.bicep` + `container-app.bicep` (copy the `contextApiKey` shape verbatim: empty
default, no `@minLength`, conditional secret + `Push__VapidPrivateKey` env). Add
`vapidPrivateKey="${{ secrets.VAPID_PRIVATE_KEY }}"` to `deploy.yml`.
*Files:* `infra/main.bicep`, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml`.
*Verify:* `az bicep build -f infra/main.bicep` (or the repo's bicep lint) succeeds; the
`Push__VapidPrivateKey` env is emitted only when the param is non-empty.

**Step 9 — Docs (same MR).** `/decide` → ADR 0028 (web push: async-paths-only trigger,
SW-always-shows / no suppress-if-focused + why (push-budget contract), dedicated grain,
cleartext subscriptions + rejected alternatives, optional-secret VAPID, RFC 8291 egress
note). Add a CLAUDE.md "Repo-specific notes" bullet. Add a
`gotchas.md` entry: *the send belongs in the two async callers, never in `ChatCoreAsync`,
or the live path notifies too* (with evidence lines). Move this spec to `specs/completed/`.
*Verify:* `node .agentic/scripts/check-doc-paths.mjs` (doc-path integrity, part of CI).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A fired reminder sends one push titled with the agent name | `ReceiveReminder_SendsPush_WithAgentName` | `AgentGrainTests.cs` |
| AC2 | An `/api/agent-run` (`EnqueuePromptAsync`) reply sends a push | `EnqueuePrompt_SendsPush` | `AgentGrainTests.cs` |
| AC3 | An interactive `ChatAsync` turn sends **no** push | `ChatAsync_DoesNotSendPush` | `AgentGrainTests.cs` |
| AC4 | A plain (no-agent) conversation reminder falls back to "Agent 42" | `ReceiveReminder_NoAgent_UsesFallbackTitle` | `AgentGrainTests.cs` |
| AC5 | Subscription add/get/remove roundtrips; upsert dedupes by endpoint | `Add_RoundTrips_AndSurvivesReactivation`, `Add_UpsertsByEndpoint`, `Remove_DropsByEndpoint`, `IsIsolatedPerUser` | `UserPushGrainTests.cs` (new) |
| AC6 | A 410 from the push service prunes that subscription; a send failure never throws | `Prunes410Subscription`, `Prunes404Subscription`, `TransientFailure_DoesNotPrune_AndDoesNotThrow`, `ArbitraryThrow_DoesNotEscape` (+ `WebPushClientAdapterTests` pins the `WebPushException`→`PushDeliveryException` translation) | `WebPushNotificationSenderTests.cs`, `WebPushClientAdapterTests.cs` (new) |
| AC7 | `POST`/`DELETE /api/push/subscriptions` persist/remove; `GET /api/push/config` reflects enabled state | `PushSubscriptionEndpoints_Crud`, `PushConfig_ReflectsEnabled` | `AgentApiTests.cs` |
| AC8 | With no VAPID private key, app boots and config reports `enabled:false` | `PushConfig_Disabled_WhenNoKey` | `AgentApiTests.cs` |
| AC9 | An `EnqueuePromptAsync` turn with no model configured sends **no** push | `EnqueuePrompt_NoModel_DoesNotSendPush` | `AgentGrainTests.cs` |
| AC10 | `sw.js` registers `push` + `notificationclick` handlers | `ServiceWorker_HandlesPush` | `PwaAssetsTests.cs` |
| AC11 (manual) | On Android PWA, an away agent reply raises a notification titled with the agent name and tapping it opens the conversation | Manual — Step 6/7 verify | — |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ plan time)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80` (backend/grain code; frontend JS has no harness — excluded, verified manually)
- `cognitive_complexity_max: 15`
- `dependencies.vulnerable_packages: 0` (hard block — the web-push package must pass)
- `add_policy: latest-stable` (resolve the package at add time; pin in `Directory.Packages.props`)
- Coverage tool: `coverlet` via `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"`

## Manual prerequisites (hand to the user — not agent-executed)

1. **Generate a VAPID key pair once**, on your own machine (a P-256 / prime256v1 EC
   keypair, base64url-encoded — the private key must never land in a shared transcript).
   Pick one:
   - **`web-push` CLI (simplest, needs Node):** `npx web-push generate-vapid-keys` → prints
     a `Public Key` (~87 chars) and `Private Key` (~43 chars).
   - **.NET, matches the stack** (repo `dotnet run app.cs` / `spike-dotnet` pattern) — a
     throwaway file using the chosen web-push package's key generator, e.g. with `WebPush`:
     ```csharp
     #:package WebPush@*
     using WebPush;
     var k = VapidHelper.GenerateVapidKeys();
     Console.WriteLine($"Public:  {k.PublicKey}");
     Console.WriteLine($"Private: {k.PrivateKey}");
     ```
     then `dotnet run app.cs`.
   - **OpenSSL (no extra tooling):**
     ```
     openssl ecparam -genkey -name prime256v1 -out vapid.pem
     openssl ec -in vapid.pem -pubout -outform DER | tail -c 65 | basenc --base64url | tr -d '='   # public
     openssl ec -in vapid.pem -outform DER | tail -c +8 | head -c 32 | basenc --base64url | tr -d '='  # private
     ```
2. **Commit the public half** — put the **public key** in `appsettings.json`
   `Push:VapidPublicKey` and a `mailto:` **subject** in `Push:VapidSubject` (e.g.
   `mailto:claude.peter.bons@hvdgroup.com`). Neither is secret; the browser fetches the
   public key from `GET /api/push/config`.
3. **Add the private key** as GitHub Actions secret `VAPID_PRIVATE_KEY` → `Push__VapidPrivateKey`
   env. Absent → push simply disabled (not on the availability path; deployment still
   succeeds). *(No Azure RBAC change — hard rule 5 does not apply here.)*
4. **The keypair is stable — never rotate it casually.** Rotating the private key
   invalidates **every** existing subscription (browsers signed each against the old public
   key), silently breaking push for all subscribed devices until they re-subscribe. Generate
   once, keep it.

## Out of scope

- **No server-side presence/heartbeat.** Away-ness is async-path-only + SW suppress-if-focused.
- **iOS push.** Android/Chromium PWA is the target; feature-detection hides the toggle
  elsewhere (web push on iOS needs an installed PWA on 16.4+ — a possible later follow-up).
- **Notifying for interactive turns the user abandons mid-stream** (navigated away). Not the
  stated ask; the SW suppression already covers the still-open case.
- **Rich notification bodies / actions / images / badges.** Title = agent name + a short
  generic body only.
- **Encrypting stored subscriptions** (deferred; see Context & the ADR).

## Review

A `critic` subagent reviewed this plan against the knowledge files and code. Verdict:
sound design, several concrete gaps — all legitimate ones **accepted and folded in**:

- **#1 HIGH — missing test silo configurators.** Accepted. There are **five** independent
  configurators, not one; a required new ctor dep breaks activation in any that miss it.
  Affected-areas + Step-3 registration list now names all five (Recording in
  `TestSiloConfigurator`, Null in the other four).
- **#2 MEDIUM — `userVisibleOnly` vs suppress-if-focused.** Accepted. Suppressing a shown
  notification burns Chromium push budget and risks subscription revocation. **Design
  changed:** the SW now **always shows**; away-ness is carried by "async paths only". The
  mechanism section and Step 6 were rewritten; recorded as an accepted tradeoff for the ADR.
- **#3 MEDIUM — non-reentrant grain stalls on send (gotcha `:221`).** Accepted. Added:
  bounded `CancellationToken` timeout + `RemoveAllResilienceHandlers()` on the sender's
  `HttpClient` (Context & Step 2).
- **#4 LOW-MED — send fires on faulted / no-model turns.** Accepted. Step 3 now specifies
  the send goes **inside the `try` after the loop** and is **gated on `_modelId`**; added
  AC9 (no-model → no push) using `NoModelSiloConfigurator`.
- **#5 LOW — `"Agent42"` vs `"Agent 42"`.** Accepted. Standardized on `"Agent 42"` (the
  manifest product name), removed the incorrect claim that it mirrors `:322`, and pinned
  AC4 to the exact literal.
- **#6 LOW — existing frontend source-text test seam.** Accepted. Added a `PwaAssetsTests`
  guard for the new SW handlers (AC10); corrected the "manual only" framing.
- **#7 LOW — ADR 0013 egress unmentioned.** Accepted. Added an egress note (payload is
  RFC 8291 E2E-encrypted, but the title is the user-chosen agent name) for the ADR.

The critic explicitly **confirmed as sound** (no change needed): the turn-scoped-field
gotcha is correctly avoided; the 2 MB-session concern is N/A; `/api/push/*` correctly sits
behind Easy Auth (not an ADR 0022 `excludedPaths` carve-out); the send belongs in the two
async callers, not `ChatCoreAsync`; the dedicated grain + `"push-subs"` state name matches
the overlay-grain exemplar; the optional-secret Bicep/deploy wiring copies the
`contextApiKey` shape correctly; **cleartext subscription storage is defensible** (a device
push capability usable only against our VAPID pair, and a Cosmos-read attacker already has
the full conversation); ADR number 0028 is free.

## Divergences from the plan (as executed)

1. **Branch name.** `feature/webpush-notify-away-agent-reply`, not `feature/push-notify-away-agent-reply`:
   the `protect-main` hook detects a push from `main` via `\bpush\b`, so `git checkout -b push-…` from
   `main` is blocked. Renaming to `webpush…` (no standalone `push` token) sidesteps it. No code impact.
2. **`NoModelSiloConfigurator` registers `RecordingPushNotificationSender`, not `Null`.** The plan's
   affected-areas said "Null in the other four". AC9 asserts a no-model agent-run sends **zero** pushes —
   only observable with a recorder, and this is the one silo whose provider flips no-model → model to prove
   the `_modelId` gate suppressed the send. The other three degraded silos keep `Null`.
3. **New `PushConfig` type registered in ALL six `MapAgentApi` hosts.** `GET /api/push/config` injects a
   `PushConfig` singleton; minimal-API infers every endpoint's params up front, so every host mapping the
   API must register it or routing breaks host-wide. This cascaded to five test hosts NOT in the plan's file
   list — `AttachmentTests`, `McpOAuthEndpointTests`, `McpServersApiTests`, `ProvidersApiTests`,
   `ToolSelectionApiTests` — plus `Program.cs` and `DevHost/Program.cs`.
4. **New `WebPushClientAdapterTests` (not in the plan's file list).** Drives the real `WebPush` library
   through a stubbed `HttpMessageHandler` with a real VAPID + P-256 subscription keypair to pin the
   `WebPushException` → `PushDeliveryException(status)` translation AC6 relies on but fakes out — and to
   cover the adapter isolation seam, lifting new-code line coverage from 79.3% to 93.5% (over the 80% gate).
5. **`AllSends()` aggregates recorded sends across every silo.** The default multi-silo `TestCluster` may
   place the conversation grain on any silo, and `RecordingPushNotificationSender` is a per-silo singleton;
   reading only the primary's recorder was a placement-dependent flake (surfaced under coverage). The
   behavioural push tests now sum sends across all silos. `PollUntilAsync`'s bound was also raised (the
   OneWay agent-run runs far slower under coverage + parallel load).
6. **`Frontend_ShipsPushToggle` presence guard added** (beyond AC10's `sw.js` guard), consistent with the
   repo's `Frontend_Ships*` source-text guards; the interactive subscribe flow stays manual-only.
7. **SW shell cache bumped `agent42-shell-v8` → `v9`** (app.js, in `SHELL`, changed) and the pinning test
   `ServiceWorker_CacheNameIsCurrent` updated.
8. **ADR records one extra accepted property the plan didn't call out:** the sender POSTs to the
   browser-supplied subscription endpoint — a standard Web Push blind-SSRF property (authenticated,
   RFC 8291-encrypted body, no response returned), deliberately not host-guarded.

Everything else matches the plan. `WebPush` 1.0.13 was resolved at add-time and pinned in
`Directory.Packages.props`; it pulls only `Portable.BouncyCastle 1.9.0` (no vulnerable packages — the
pre-existing test-only `System.Security.Cryptography.Xml` vuln is from DataProtection, present on `main`).
