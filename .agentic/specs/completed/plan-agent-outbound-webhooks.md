# Plan — Outbound webhooks for stored agents

planned-against: main @ 2c1dbf3 (re-based 2026-10-10 onto main @ 5f36418, see "Amendment: drift since 2c1dbf3")

## Overview

**Goal:** a stored agent can have one outbound webhook. Whenever a turn of a conversation bound to that agent completes and is durably persisted — from the live UI, `/api/agent-run`, `ask_agent` over MCP, a fired reminder, or a future email/Slack trigger — the app POSTs a signed JSON event (agent, conversation id, trigger, prompt, full reply text, truncated at a cap) to the owner's URL. The owner configures it in a new **Webhook** tab of the agent dialog, which shows the signing secret once, has a "Send test" button and lists recent deliveries with their status.

**Why now:** the user asked for it. Inbound automation exists (ADR 0022 `/api/agent-run`, ADR 0070 MCP), but a finished run only reaches the owner's phone (ADR 0028 push), never another system. This sends conversation content to a third party, so it is an **ADR 0013 egress extension** (CLAUDE.md cross-cutting rule 13) and needs a new ADR.

## Affected areas

Verified against the tree at `2c1dbf3`.

| Area | Files |
|---|---|
| New Agents feature folder (flat namespace `Agent42.Agents`, ADR 0039) | `src/Agent42.Agents/Webhooks/` (new): `IAgentWebhookGrain.cs`, `AgentWebhookGrain.cs`, `AgentWebhookState.cs`, `AgentWebhookEvent.cs`, `IAgentWebhookSender.cs`, `NullAgentWebhookSender.cs`, `WebhookLimits.cs`, `AgentGrain.Webhooks.cs` (partial) |
| Turn completion sites | `src/Agent42.Agents/Conversations/AgentGrain.cs` (`ChatTurnAsync` after `PersistTurnUnlessEphemeralAsync`, which gains a `bool` return; `RunReminderAsync` after its `WriteStateAsync`), `src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs` (inside the existing `if (!_ephemeral)` block) |
| Agent delete | `src/Agent42.Agents/StoredAgents/AgentRegistryGrain.cs` (`DeleteAsync`) |
| New Backend feature folder | `src/Agent42.Backend/Webhooks/` (new): `HttpAgentWebhookSender.cs`, `WebhookSigner.cs`, `WebhookSecretProtector.cs` (+ `IWebhookSecretProtector`), `WebhookRegistration.cs`, `AgentApi.Webhooks.cs`, `WebhookContracts.cs` |
| Composition | `src/Agent42.Backend/Program.cs` (one `AddAgentWebhooks(...)` call), `src/Agent42.Backend/Api/AgentApi.cs` (`MapAgentApi` → `MapWebhookEndpoints(app)`), `tools/Agent42.DevHost/Program.cs` |
| Reused SSRF guard (read-only reuse) | `src/Agent42.Backend/Web/GuardedDocumentFetcher.cs` (`CreateHandler`), `src/Agent42.Backend/Web/PrivateAddressGuard.cs` (`IsPubliclyRoutable`) |
| Frontend | `src/Agent42.Backend/wwwroot/index.html` (4th agent-dialog tab beside `tabAgentGeneral`/`tabAgentTools`/`tabAgentApi`), `wwwroot/js/dom.js` (`agentTabs` + new element exports), `wwwroot/js/agents.js` (`openAgentDialog`, webhook panel), new `wwwroot/js/webhooks.js` (imported from `agents.js`, hence reachable from `main.js`), `wwwroot/sw.js` (`CACHE` bump, currently `agent42-shell-v52`; add `/js/webhooks.js` to `SHELL`, which lists every module) |
| Silo hosts (rule 7) | `tests/Agent42.Tests/TestSiloConfigurator.cs`, `NoModelSiloConfigurator.cs`, `AttachmentRehydrateTests.cs`, `CapSizedWebPageFetcher.cs`, `FakeGeocoder.cs`, `LiveModelChatTests.cs`, `WebDocumentProbeSilo.cs` (each registers an `IPushNotificationSender` today; each needs an `IAgentWebhookSender` beside it), `tests/Agent42.Tests/SharedApiFixture.cs` |
| Tests (new) | `tests/Agent42.Tests/AgentWebhookGrainTests.cs`, `AgentWebhookTurnTests.cs`, `WebhookSignerTests.cs`, `HttpAgentWebhookSenderTests.cs`, `WebhookApiTests.cs`, `RecordingAgentWebhookSender.cs` |
| Tests (extended) | `GrainStorageSerializerTests.cs`, `AgentRegistryGrainTests.cs`, and **every test that pins the `sw.js` `CACHE` string** — find them with `grep -rn "agent42-shell-v" tests` before bumping (at plan time: `ClientToolsAssetsTests.cs`, `PwaAssetsTests.cs`, `SearchAssetsTests.cs`, `ResearchAssetsTests.cs`) |
| Docs | new ADR in `.agentic/decisions/`, `.agentic/gotchas.md` (two entries), `CLAUDE.md` feature map row "Stored agents" |

**Not affected:** `infra/`. All grain state lives in the one Cosmos container `agent-state` (`Program.cs:457`), so a new `[PersistentState]` name needs no Bicep change; no new `excludedPaths` entry, because every new endpoint sits behind Easy Auth.

## Context & constraints

### Decisions made with the user (not to be revisited)
- **Trigger set:** every completed turn of a conversation bound to a stored agent, whatever the entry point (UI, agent-run, MCP `ask_agent`, reminder, future drains of `ChatCoreAsync`).
- **Content:** full reply text plus metadata, HMAC-signed.
- **Egress:** recorded as an ADR 0013 extension in a new ADR (rule 13).

### Decisions made in this plan (record in the ADR with alternatives)

1. **Fire only after a durable persist; "persisted" is the gate.** `PersistTurnUnlessEphemeralAsync` (`AgentGrain.cs:1217`) returns `bool` (false on its ephemeral early return); the chat site fires only on `true`. This puts the ephemeral exclusion (ADR 0034) and "only durably recorded replies" in one place — the same placement argument ADR 0028 makes for push. The site must **not** re-read `_ephemeral`: that field is turn-scoped and reassigned by `EnqueuePromptAsync` during this iterator's yields (gotcha "A grain's turn-scoped field lives for the ACTIVATION"). The persist method itself reads `_ephemeral` after the yields; the `bool` return inherits exactly that read, so the webhook gate is precisely as safe as the write it reports on — never weaker. State this in the ADR. Consequences:
   - Failed turns (an exception out of `StreamModelReplyAsync`) never reach the persist → no fire.
   - The no-model path, the copilot refusal and the stopped-with-unreachable-history branch all `yield break` before the persist → no fire.
   - Stopped turns (ADR 0078) **do** persist → fire with `stopped: true` and the partial reply.
   - Failure events are out of scope.
2. **Configuration is live, not snapshotted.** The webhook is delivery routing, not persona — the same family as `Continuous` (ADR 0016) and `McpExposed` (ADR 0070), which `AgentDefinition`'s remarks already say are not snapshotted into `AgentBinding`. The conversation never stores the URL; it fires to whatever the agent's webhook grain holds at delivery time. Editing or removing the webhook therefore affects existing conversations immediately, which is the point.
3. **Config lives in its own grain, not on `AgentDefinition`.** `IAgentWebhookGrain`, keyed `{userId}/{agentId}`, `[PersistentState("agent-webhook", AgentGrain.StorageProviderName)]`. Why not on `AgentDefinition`: `GET /api/agents` serializes `AgentDefinition` verbatim (`AgentApi.StoredAgents.cs:20`, and `AgentContracts.cs` says so), so an encrypted secret there would be echoed to the browser — the provider/MCP secret pattern forbids that (gotcha "A user provider's API key is encrypted, never echoed"). The grain also gives the delivery log a home and keeps the sequential delivery queue off the conversation grain.
4. **Fire-and-forget off the conversation grain.** `AgentGrain` calls `IAgentWebhookGrain.DeliverAsync(AgentWebhookEvent)`, marked **`[OneWay]`** (precedent: `IAgentGrain.EnqueuePromptAsync`, `IAgentGrain.cs:22`). The conversation grain is non-reentrant, so an awaited HTTP call would stall the turn (why push needs `SendBudget` — gotcha "`SendAwayReplyPushAsync` belongs in the two async callers"). Wrap the OneWay call in `try/catch`, log, never fault the turn. Call only when `_state.State.AgentId` is non-empty, so unbound chats never activate the webhook grain; the webhook grain no-ops when no URL is configured. The webhook grain's **config and read methods** — `GetAsync`, `SaveAsync`, `ClearAsync` — are `[AlwaysInterleave]` (precedent `IAgentGrain.cs:67`). A delivery can take ~40 s (3 × 10 s attempts + 2 s + 8 s delays), past Orleans' default 30 s call timeout, so anything queued behind it — the dialog, Save, and `AgentRegistryGrain.DeleteAsync`'s awaited clear — would hang or time out. They only touch in-memory state plus one `WriteStateAsync`, and `DeliverAsync` re-reads the URL/secret **after** each attempt's await before logging, so an interleaved clear wins: a delivery that finds the config cleared drops its log write. `SendTestAsync` is **not** interleaved (it is itself a delivery) and the button shows a spinner with a client-side 45 s timeout. **Bounded backlog:** the grain keeps a pending count; `DeliverAsync` arriving while ≥ `WebhookLimits.MaxPendingDeliveries` (10) are queued is dropped and logged as `dropped-backlog` instead of queuing unboundedly.
5. **Signing follows the Standard Webhooks scheme.** Headers `webhook-id` (delivery GUID), `webhook-timestamp` (unix seconds), `webhook-signature: v1,<base64(HMAC-SHA256(secret, "{id}.{timestamp}.{body}"))>`. Secret format `whsec_<base64 of 32 random bytes>`. Why: receivers get off-the-shelf verifiers, and id + timestamp give replay protection for free. A home-grown header was rejected because every receiver would have to hand-roll verification.
6. **Secret storage.** Generated server-side and returned once in the PUT response (mirrors the ADR 0022 key that is shown once). The webhook grain stores **only the ciphertext**, encrypted in the endpoint by a new `WebhookSecretProtector` with its own Data Protection purpose (e.g. `"Agent42.AgentWebhooks.Secret.v1"`) — a sibling of `McpSecretProtector`, not a reuse of `ProviderSecretProtector` (`McpSecretProtector.cs:12` is the "distinct on purpose" precedent). It cannot be hashed like the ADR 0022 key because HMAC needs the raw secret on every send. `HttpAgentWebhookSender` (Backend) unprotects at send time, so the grain never sees plaintext. `Unprotect` returning null (rotated key ring) → the delivery is recorded as failed with error `secret-unavailable`; nothing is sent unsigned.
7. **SSRF: the webhook URL IS guarded, unlike a provider endpoint — the ADR must say why.** The provider-endpoint gotcha ("deliberately NOT SSRF-guarded") rests on the endpoint being the owner's own config. The webhook URL is owner config too, but gets a guard because: (a) the owner-facing **Send test** button and the delivery log turn every send into a status-code **oracle** for probing the container's network, which holds the managed identity Azure IMDS mints tokens for (gotcha "There are TWO web fetch paths…"); (b) ADR 0070 and ADR 0022 key holders can trigger turns, and therefore sends, with no browser session; (c) unlike Ollama on `localhost`, there is no legitimate private-address use case. Controls:
   - Save time (UX error only): absolute `https` URL, no userinfo, no IP-literal host that `PrivateAddressGuard.IsPubliclyRoutable` rejects.
   - Connect time (the actual control): the primary handler is `GuardedDocumentFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable)` with **`AllowAutoRedirect = false`** on the returned `SocketsHttpHandler` (a redirect on a signed POST is meaningless, and following it re-opens the URL-string bypass).
   - The log stores **status code and an error class only, never the response body**.
   - As in ADR 0066, a guard that is built but not wired ships green, so a **source-text test pins the wiring** in the registration file (precedent `WebDocumentToolTests.ProgramCs_WiresTheGuardIntoTheDocumentClientsPrimaryHandler`).
8. **HttpClient and retry (rule 8).** Named client `HttpAgentWebhookSender.HttpClientName` with `.RemoveAllResilienceHandlers()` and a 10 s `Timeout`. Retry is the sender's own bounded loop: **3 attempts**, delays **2 s then 8 s**, only on `HttpRequestException` (not when its inner cause is `BlockedAddressException`), timeout, 5xx and 429; any other 4xx is final. **No durable retry queue** — an event in flight when the silo restarts is lost; accepted in the ADR, since the reply is durably in the conversation anyway. The sender **never throws** (the `IPushNotificationSender` contract) and returns `WebhookDeliveryResult(StatusCode?, Error?, Attempts, Duration)`.
9. **Payload shape** (`type: "agent.turn.completed"`; the test button sends `type: "webhook.test"` with no conversation content):
   ```json
   { "type": "agent.turn.completed", "id": "<delivery guid>", "timestamp": "<ISO-8601>",
     "agent": { "id": "...", "name": "..." }, "conversationId": "...",
     "trigger": "interactive|background|reminder", "reminderName": null,
     "prompt": "...", "promptTruncated": false,
     "reply": "...", "replyTruncated": false,
     "stopped": false, "imageCount": 0 }
   ```
   - `interactive` comes from the `interactive` local captured at the top of `ChatTurnAsync` (`AgentGrain.cs:665`) — never re-read `_interactiveTurn`. `background` covers agent-run, `ask_agent` and future email/Slack drains. `reminder` is set in `RunReminderAsync`.
   - Caps `WebhookLimits.MaxReplyChars = 32_000`, `MaxPromptChars = 4_000`, applied in the grain **before** the OneWay call, so an oversized event never crosses the grain boundary.
   - Image turns: `reply` empty, `imageCount` = number of images. The payload never carries attachment or SAS URLs (credential-bearing, ADR 0004/0013).
   - No deep link: the app has no conversation-URL route (`sw.js` opens `/` and posts `open-conversation`), so only `conversationId`.
10. **Delivery log:** the last **20** `WebhookDeliveryRecord`s (at, delivery id, type, trigger, conversationId, statusCode?, error?, attempts, durationMs), persisted in the webhook grain after each delivery. A mutable `List<T>` in the state class — **never assign a collection expression into an `IReadOnlyList<T>` grain-state member** (gotcha "A collection expression into `IReadOnlyList<T>`…"). The state is a **class with defaults**, not a positional record (gotcha "A record parameter a Cosmos document lacks binds to `null`").
11. **Delete order:** `AgentRegistryGrain.DeleteAsync` clears the webhook grain (`ClearAsync`) **before** removing the agent from the registry. Conversations outlive their agent (ADR 0003 persona snapshot) and keep calling `DeliverAsync` with the old agent id; if the clear ran second, a mid-failure would leave a live webhook firing for orphaned chats. Clearing first fails safe (worst case: the agent survives without a webhook). **Note this is the opposite order from the API-key directory** (`AgentRegistryGrain.cs:163-171` removes from the registry first and deregisters second, gotcha "The API-key directory must be kept in sync per-operation"). The difference is deliberate and the ADR must say why: a stale key-directory entry is harmless because the registry lookup then fails closed, whereas a stale webhook keeps **sending content** for orphaned conversations, which do not consult the registry. The safe-failure direction is therefore reversed.

### Facts the executor must re-check, not assume
- **Reminder reply text:** `RunReminderAsync` currently discards the `RunAsync` result (`AgentGrain.cs` ~1606). Capture it and take the **last assistant message's** text, not `response.Text`, which may concatenate research-loop iterations (ADR 0082: `LoopAgent` persists each iteration). Verify with the existing research test fakes before choosing.
- **S3776 at 15 (rule 9):** `ChatTurnAsync` is already long. Add the webhook as **one unconditional call** to a helper in `AgentGrain.Webhooks.cs` that takes the persisted flag; keep all branching inside the helper.
- **Registration:** `Program.cs` must not grow inline webhook wiring. Put the named client, sender and protector in `WebhookRegistration.AddAgentWebhooks(...)`, following the `HindsightRegistration` shape (rule 9; `Program.cs:370`).
- **Optional endpoint dependency (rule 7):** endpoints resolve `IWebhookSecretProtector` and `IAgentWebhookSender` via `IServiceProvider.GetService` (the `/api/speech` pattern) and answer **503** when either is missing. That leaves the ~8 hand-built API test hosts that register protectors explicitly (e.g. `AgentApiTests.cs:1145`, `SpeechApiTests.cs:40`) untouched. The **grain** dependency `IAgentWebhookSender` is constructor-injected, so it **is** registered in every silo host listed under Affected areas.

### Governing ADRs, gotchas and exemplars
- **ADRs:** 0013 (egress — the new ADR extends it; its scalar `amended_by: 0084` becomes `[0084, NNNN]`), 0022 (key shown once, agent-run), 0028/0075 (push placement; the webhook is a third, separate rule), 0016/0070 (routing, not persona), 0034 (ephemeral), 0066 (guard wiring + source-text test), 0078 (stopped turns persist by hand), 0082 (research reply), 0085 (copilot turns run through the same `StreamModelReplyAsync` path, so they are covered with no extra site), 0039 (flat namespace), 0050/0051 (complexity).
- **Gotchas:** "A named HttpClient silently inherits AddServiceDefaults' retry pipeline", "There are TWO web fetch paths…", "A user provider overlay is … deliberately NOT SSRF-guarded" (the contrast the new ADR must explain), "A user provider's API key is encrypted, never echoed", "A grain's turn-scoped field lives for the ACTIVATION", "Ephemeral chat: `_ephemeral` is the same turn-scoped trap", "`SendAwayReplyPushAsync` belongs in the two async callers", "The background (agent-run) run must drain `ChatCoreAsync`", "Every MR that touches `wwwroot/js/` must bump `CACHE`", "A frontend module unreachable from `main.js` is invisible…", "A collection expression into `IReadOnlyList<T>`…", "Cosmos grain storage is JSON with `$type`".
- **Exemplars:** per-user registry grain `StoredAgents/AgentRegistryGrain.cs`; keyed third-party HTTP client behind an Agents interface `Backend/Location/GoogleGeocoder.cs`; connect-time guarded client `Backend/Web/GuardedDocumentFetcher.cs`; minimal-API group `Backend/Users/AgentApi.Users.cs`; grain test `SharedClusterTests.cs` + `UserProfileGrainTests.cs`; API test `SharedApiFixture.cs` + `McpServersApiTests.cs`; frontend module `wwwroot/js/conversations.js`.

## Steps

1. **Record the decision (`/decide`).** New ADR *"Outbound webhooks for stored agents (an ADR 0013 egress extension)"* covering decisions 1–11. Take the next free number at execution time (sibling plans may land ADRs first). Alternatives: fields on `AgentDefinition` (rejected: secret echo); snapshot into the binding (rejected: it is routing); awaited send on `AgentGrain` (rejected: non-reentrant stall); the AddServiceDefaults retry (rejected: rule 8); a durable outbox (deferred); leaving the URL unguarded like provider endpoints (rejected: the oracle); a custom signature header (rejected: Standard Webhooks). Convert ADR 0013's scalar `amended_by: 0084` to the list form `[0084, NNNN]` (as ADR 0035 does) and add a body amendment note, as other amended ADRs have.
   *Verify:* `dotnet test --filter FullyQualifiedName~QualityGateTests`, and the doc-path integrity check (`DocPathCheckerTests`).

2. **Agents: contracts, state, grain.** Create the `src/Agent42.Agents/Webhooks/` files: `AgentWebhookEvent` (`[GenerateSerializer]`, `[Alias("Agent42.Agents.AgentWebhookEvent")]`), `AgentWebhookState`, `WebhookDeliveryRecord`, `WebhookLimits` (caps + `Truncate`), `IAgentWebhookSender` + `WebhookDeliveryResult` + `AgentWebhookTarget(Url, SecretCiphertext)`, `NullAgentWebhookSender`, `IAgentWebhookGrain`, `AgentWebhookGrain`.
   - Grain methods: `GetAsync` (`[AlwaysInterleave]`; URL, `HasSecret`, log — never the ciphertext); `SaveAsync(url, secretCiphertext?)` (null ciphertext keeps the stored one); `ClearAsync`; `DeliverAsync(evt)` (`[OneWay]`; no-op without a URL, else send, append to log, trim to 20, write); `SendTestAsync()` (not OneWay; returns the result and logs it).
   - Tests `AgentWebhookGrainTests`: save/get round-trip never returns the ciphertext; deliver with no URL is a no-op; log capped at 20; clear removes everything; `GetAsync`, `SaveAsync` and `ClearAsync` all answer while a delivery is in flight (blocking fake sender), and a delivery finishing after an interleaved clear writes no log entry; deliveries beyond `MaxPendingDeliveries` are recorded as `dropped-backlog`; a test send carries no conversation content. Extend `GrainStorageSerializerTests` with an `AgentWebhookState` holding ≥2 log entries, plus a document missing every field (Cosmos JSON path, rule 2).
   - Register in every silo host: a new `RecordingAgentWebhookSender` (shape of `RecordingPushNotificationSender.cs`) in `TestSiloConfigurator` and `NoModelSiloConfigurator`; `NullAgentWebhookSender` in the other five test silos and in `tools/Agent42.DevHost/Program.cs`.
   *Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentWebhookGrainTests|FullyQualifiedName~GrainStorageSerializerTests"`.

3. **AgentGrain: fire at the completion sites.** *Depends on 2.*
   - `PersistTurnUnlessEphemeralAsync` returns `bool`.
   - New partial `AgentGrain.Webhooks.cs`: `NotifyAgentWebhookAsync(bool persisted, string prompt, string reply, string trigger, bool stopped, int imageCount, string? reminderName)` — returns early when not persisted or `_state.State.AgentId` is empty; truncates via `WebhookLimits`; makes the OneWay call inside try/catch.
   - Call sites: (a) `ChatTurnAsync` right after the persist, with `message`, `reply.ToString()`, `interactive ? "interactive" : "background"`, `outcome.Stopped`; (b) `GenerateImageTurnAsync` inside its `if (!_ephemeral)` block after the write; (c) `RunReminderAsync` **immediately after its `WriteStateAsync` / `IndexNewTurnsForSearchAsync` and before the `if (_pushesThisTurn > 0) { return; }` early return** (~line 1664), with `"reminder"`, `info.Prompt` and the captured reply (see "Facts to re-check"). A call placed after the push block would be skipped whenever the model pushed during the turn (ADR 0075). Update the class-level comment in `AgentGrain.cs` that lists each run site's side effects.
   - `AgentRegistryGrain.DeleteAsync` calls `IAgentWebhookGrain.ClearAsync()` **before** removing the agent.
   - Tests `AgentWebhookTurnTests` (use `RecordingAgentWebhookSender` and **poll** for the OneWay result — agent-run tests gotcha): bound interactive turn fires once with trigger and reply; unbound chat never fires; ephemeral bound turn never fires and the next normal turn on the same activation does; no-model turn never fires; faulting turn never fires; stopped turn fires with `stopped: true`; `EnqueuePromptAsync` fires `background`; fired reminder fires `reminder` with its prompt **and reply text**; a research-kind reminder with several loop iterations delivers only the last assistant message's text; a reminder turn whose model pushed (ADR 0075) still fires; oversize reply truncated with the flag; image turn fires `imageCount: 1` with no URL anywhere in the serialized event; a deleted agent's orphaned conversation no longer fires. Extend `AgentRegistryGrainTests`: `AgentDelete_ClearsWebhook`, and `AgentDelete_WhenWebhookClearFails_AgentSurvives` (a webhook grain whose clear throws — proves the clear precedes the registry write).
   *Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentWebhookTurnTests|FullyQualifiedName~AgentRegistryGrainTests|FullyQualifiedName~StopGenerationTests|FullyQualifiedName~AgentGrainTests"`, then `dotnet build Agent42.slnx` (S3776).

4. **Backend: signer, protector, guarded sender, registration.** *Depends on 2; parallel with 3 only in a worktree.*
   - `WebhookSigner`: static helper — secret generation and Standard Webhooks signing.
   - `WebhookSecretProtector`: shape of `McpSecretProtector`; `Unprotect` swallows `CryptographicException` and returns null.
   - `HttpAgentWebhookSender`: serialize, sign, POST with decision 8's bounded retry; never throws.
   - `WebhookRegistration.AddAgentWebhooks(IServiceCollection)`: the named client with 10 s timeout; `.ConfigurePrimaryHttpMessageHandler(() => { var h = GuardedDocumentFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable); h.AllowAutoRedirect = false; return h; })`; `.RemoveAllResilienceHandlers()`; the protector; the sender. Call it from `Program.cs`.
   - Tests `WebhookSignerTests`: a fixed vector (known secret/id/timestamp/body → known signature, computed independently in the test with `HMACSHA256`); secret format/length. `HttpAgentWebhookSenderTests`: 503-then-200 → 2 attempts and success; 400 → one attempt; three 5xx → failed result, no throw; null-unprotect secret → no HTTP call; headers present and verifiable; **a real `CreateHandler(IsPubliclyRoutable)` client against a loopback listener is refused and not retried**; a 302 is not followed; **a source-text test** that `WebhookRegistration.cs` contains `PrivateAddressGuard.IsPubliclyRoutable`, `AllowAutoRedirect = false` and `RemoveAllResilienceHandlers`, and that `Program.cs` calls `AddAgentWebhooks`.
   *Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~WebhookSignerTests|FullyQualifiedName~HttpAgentWebhookSenderTests"`.

5. **Backend: endpoints.** *Depends on 2 and 4.* `AgentApi.Webhooks.cs`, wired from `MapAgentApi` (a partial `MapAgentApi` never calls silently deletes its endpoints, rule 7). All authenticated, `userId` from `GetUserId()`, and each checks the agent exists in the caller's registry (404 otherwise).
   - `GET /api/agents/{id}/webhook` → `{ url, hasSecret, deliveries[] }`.
   - `PUT /api/agents/{id}/webhook` with `{ url, rotateSecret }` → validates the URL (400 unless absolute `https`, no userinfo, not a private IP literal); mints the secret on first save or on `rotateSecret`; encrypts; saves; returns `{ url, hasSecret, secret? }` — the secret **once**, only when minted.
   - `DELETE /api/agents/{id}/webhook`.
   - `POST /api/agents/{id}/webhook/test` → `{ statusCode, error, attempts, durationMs }`.
   - Missing optional services → 503. Register the protector (`EphemeralDataProtectionProvider`) and a recording sender in `SharedApiFixture.cs`.
   - Tests `WebhookApiTests`: minting PUT returns the secret once and a later GET never contains `secret` or the ciphertext; a plain PUT keeps the secret; `rotateSecret` returns a new one; `http://`, `https://user:pw@…`, `https://127.0.0.1/` and `https://169.254.169.254/` → 400; another user's agent id → 404; the test endpoint returns the recorded result; DELETE clears. `EasyAuthExclusionTests` unchanged and green (nothing became public).
   *Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~WebhookApiTests|FullyQualifiedName~EasyAuthExclusionTests"`.

6. **Frontend: the Webhook tab.** *Depends on 5.*
   - `index.html`: 4th tab + panel in the agent dialog (URL input, Save, Rotate secret, one-time secret reveal with copy — the `agentKeyReveal` shape — Send test, Remove, recent-deliveries list). Gate it on a **saved** agent, as `agentKeySection` is.
   - `webhooks.js`: feature module (`conversations.js` shape), imported by `agents.js`; `openAgentDialog` loads the webhook status. Delivery rows rendered as text nodes only, never `innerHTML` with server strings. The panel copy says plainly that every turn's prompt and reply are sent to this URL.
   - `dom.js`: extend the `agentTabs` `wireTabs([...])`. `saveAgent` calls `agentTabs.select(2)` assuming the API tab is index 2 — keep the new tab **last**.
   - `sw.js`: bump `CACHE`; add `/js/webhooks.js` to `SHELL` (it lists every module, including `/js/agents.js`; SHELL is all-or-nothing, a missing entry breaks offline load). Before bumping, `grep -rn "agent42-shell-v" tests` and move **every** pin found (see Affected areas).
   - Tests: a new `tests/Agent42.Tests/AgentWebhookAssetsTests.cs` (shape of `ClientToolsAssetsTests`): `webhooks.js` imported by `agents.js`; delivery rows use `textContent`, never `innerHTML`; the webhook tab is the last tab; `SHELL` lists `/js/webhooks.js`.
   *Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentWebhookAssetsTests|FullyQualifiedName~PwaAssetsTests|FullyQualifiedName~ClientToolsAssetsTests|FullyQualifiedName~SearchAssetsTests|FullyQualifiedName~ResearchAssetsTests"`. Then DevHost (`dotnet run --project tools/Agent42.DevHost`): the tab renders, saves, shows the secret once, and Send test returns the Null sender's result.

7. **Docs (`/sync-docs`, `/gotcha`).**
   - CLAUDE.md: add the ADR number to the "Stored agents" feature-map row.
   - Two gotchas: *"The webhook fires on the PERSISTED flag, never on `_ephemeral`, and at three sites — `ReceiveReminder` and the image turn don't go through the chat persist."* and *"The webhook URL is SSRF-guarded although it is owner config — the provider-endpoint exemption does not transfer, and redirects are off."*
   - Move this spec to `specs/completed/`.
   *Verify:* full suite and lint (below).

**Parallelism:** step 1 first. Step 4 may run parallel with step 3 only in a worktree (write-capable subagents). Steps 5, 6, 7 in order.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | A completed turn of a bound conversation POSTs once with agent, conversation, trigger, prompt and reply | `InteractiveTurn_BoundAgent_DeliversOnce` | `AgentWebhookTurnTests.cs` |
| 2 | Agent-run and `ask_agent` turns fire with `trigger: background` | `EnqueuePrompt_DeliversBackgroundTrigger`, `NonInteractiveChat_DeliversBackgroundTrigger` | `AgentWebhookTurnTests.cs` |
| 3 | A fired reminder fires with `trigger: reminder`, its prompt and the last assistant message's text — also when the model pushed during the turn, and for a multi-iteration research reminder | `ReceiveReminder_DeliversReminderTrigger`, `ReceiveReminder_AfterPush_StillDelivers`, `ResearchReminder_DeliversLastAssistantText` | `AgentWebhookTurnTests.cs` |
| 4 | An ephemeral turn never fires; the next normal turn on the same activation does | `EphemeralTurn_NeverDelivers_NextTurnDoes` | `AgentWebhookTurnTests.cs` |
| 5 | Unbound chats, no-model turns and faulted turns never fire | `UnboundChat_NeverDelivers`, `NoModelTurn_NeverDelivers`, `FaultedTurn_NeverDelivers` | `AgentWebhookTurnTests.cs` |
| 6 | A stopped turn fires with `stopped: true` and the partial reply | `StoppedTurn_DeliversWithStoppedFlag` | `AgentWebhookTurnTests.cs` |
| 7 | Prompt and reply are truncated at the `WebhookLimits` caps with the flags set | `OversizeReply_IsTruncatedWithFlag` | `AgentWebhookTurnTests.cs` |
| 8 | An image turn carries `imageCount` and no attachment/SAS URL | `ImageTurn_DeliversCountWithoutUrls` | `AgentWebhookTurnTests.cs` |
| 9 | Deleting the agent stops its orphaned conversations firing; the clear precedes the registry write | `AgentDelete_ClearsWebhook`, `AgentDelete_WhenWebhookClearFails_AgentSurvives`, `OrphanedConversation_AfterAgentDelete_NeverDelivers` | `AgentRegistryGrainTests.cs`, `AgentWebhookTurnTests.cs` |
| 10 | The body is signed with Standard Webhooks headers; the signature verifies against an independent HMAC | `Sign_MatchesIndependentHmac`, `Send_SetsVerifiableSignatureHeaders` | `WebhookSignerTests.cs`, `HttpAgentWebhookSenderTests.cs` |
| 11 | Retry is bounded: 5xx/429/timeouts up to 3 attempts, other 4xx not retried; the sender never throws | `Send_RetriesTransientThenSucceeds`, `Send_DoesNotRetryClientError`, `Send_ExhaustedRetries_ReturnsFailureWithoutThrowing` | `HttpAgentWebhookSenderTests.cs` |
| 12 | Loopback/private destinations are refused at connect time, redirects not followed, wiring pinned | `Send_LoopbackDestination_IsBlockedAndNotRetried`, `Send_DoesNotFollowRedirect`, `Registration_WiresGuardNoRedirectAndNoResilience` | `HttpAgentWebhookSenderTests.cs` |
| 13 | A secret the key ring can't decrypt sends nothing and records `secret-unavailable` | `Send_UndecryptableSecret_SendsNothing` | `HttpAgentWebhookSenderTests.cs` |
| 14 | The secret is shown once, never returned by GET, rotatable; the grain stores ciphertext only | `Put_MintsSecretOnce_GetNeverReturnsIt`, `Put_RotateSecret_ReturnsNewSecret`, `GetAsync_NeverReturnsCiphertext` | `WebhookApiTests.cs`, `AgentWebhookGrainTests.cs` |
| 15 | Non-https, userinfo and private-IP-literal URLs → 400; another user's agent → 404 | `Put_RejectsInvalidUrls`, `Put_OtherUsersAgent_Returns404` | `WebhookApiTests.cs` |
| 16 | The delivery log keeps the last 20; get/save/clear answer during an in-flight delivery; the backlog is bounded; no response body is stored | `Deliver_LogCappedAt20`, `ConfigCalls_AnswerDuringSlowDelivery`, `ClearDuringDelivery_DropsTheLogWrite`, `Deliver_BeyondBacklog_IsDroppedAndLogged`, `DeliveryRecord_HasNoResponseBody` | `AgentWebhookGrainTests.cs` |
| 17 | The webhook state round-trips on the Cosmos JSON path, including a document missing fields | `AgentWebhookState_RoundTripsThroughCosmosJson` | `GrainStorageSerializerTests.cs` |
| 18 | The test endpoint sends a content-free `webhook.test` and returns the result | `PostTest_ReturnsDeliveryResult`, `SendTest_CarriesNoConversationContent` | `WebhookApiTests.cs`, `AgentWebhookGrainTests.cs` |
| 19 | No new public path | `EasyAuthExclusionTests` (unchanged, green) | `EasyAuthExclusionTests.cs` |
| 20 | `webhooks.js` reachable from `main.js` via `agents.js`; server strings via `textContent`; tab last; `CACHE` bumped with every pin moved | `AgentWebhookAssetsTests` + existing `CACHE` pins | `AgentWebhookAssetsTests.cs`, `PwaAssetsTests.cs`, `ClientToolsAssetsTests.cs`, `SearchAssetsTests.cs`, `ResearchAssetsTests.cs` |

## Quality gate checklist

Thresholds copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` over `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` |
| `cognitive_complexity_max` | **15** (S3776, build error) | `dotnet build Agent42.slnx` |
| `dependencies.vulnerable_packages` | **0** | CI package-freshness job |
| `dependencies.add_policy` | latest-stable | n/a — no new package (HMAC, Data Protection, `SocketsHttpHandler` are in-box) |

Also before reporting: `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`.

## Out of scope

- Failure, error or "turn started" events — only completed, persisted turns fire.
- A durable outbox, retry after restart, redelivery from the log.
- More than one webhook per agent, per-trigger filters, webhooks for unbound (plain) chats.
- Custom headers or auth schemes (bearer, mTLS) beyond the HMAC signature.
- Attachments, image bytes or SAS URLs in the payload; tool-call details and reasoning.
- A conversation deep-link route — the payload carries `conversationId` only.
- Opting out of the IMDS/private-address guard for local development — DevHost uses the Null sender.

## Amendment: drift since 2c1dbf3 (2026-10-10, approved by the user before execution)

`main` moved to `5f36418` before execution: ADR 0091 (export, #181), ADR 0092 (edit/regenerate/fork, #182) and ADR
0093 (location as a client tool, #179). 0093 only removed `_turnLocation` and the `location` parameter of
`ChatAsync`: every hook point above still exists under its name. 0092 added paths the plan never saw.

12. **Edit and regenerate still fire, marked `"replacesLastTurn": true`.** They are two grain calls —
    `TruncateLastTurnAsync`, then an ordinary `ChatAsync` — so a regenerated reply is a completed, persisted turn
    and fires like any other. The receiver must be able to tell it supersedes the previous event of that
    conversation, so the event gains `replacesLastTurn`. It travels as a **`ChatAsync` parameter**
    (`replacesLastTurn`, trailing, default false) threaded through `ChatCoreAsync` → `ChatTurnAsync` /
    `GenerateImageTurnAsync` as a local, **never a field set by the truncate**: the truncate and the chat are separate
    calls, so an `EnqueuePromptAsync` or reminder running between them would consume a turn-scoped field (the
    ACTIVATION trap). The endpoint passes `request.ReplaceLastTurn`. The truncate itself never fires (it is not a turn).
    Alternative rejected: suppress the event (the receiver would keep a reply the user replaced); a separate
    `agent.turn.replaced` event (two events for one action, and the truncate has no reply to send).
13. **A fork's turns fire under the fork's conversation id.** `InitializeFromForkAsync` copies `AgentId`, so a fork of
    a bound conversation is bound and its later turns deliver normally — no code. The fork's initialization is not a
    turn and does not fire.
14. **Refreshed numbers:** the ADR is **0094**; `CACHE` is `agent42-shell-v55` (bump to v56, re-grep the pins);
    `ChatAsync` is `(message, model, attachments, ephemeral, interactive, clientTools)` — no location.

Added acceptance criteria:

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 21 | A regenerate (truncate + chat with `replacesLastTurn`) delivers once with `replacesLastTurn: true`; the truncate alone delivers nothing; a plain turn carries `false` | `RegeneratedTurn_DeliversWithReplacesFlag`, `Truncate_AloneNeverDelivers` | `AgentWebhookTurnTests.cs` |
| 22 | The chat endpoint passes `ReplaceLastTurn` through to the grain | `Chat_ReplaceLastTurn_PassesFlagToGrain` (source-text or API) | `WebhookApiTests.cs` |
| 23 | A turn on a fork of a bound conversation delivers with the fork's conversation id | `ForkedConversation_TurnDeliversWithForkId` | `AgentWebhookTurnTests.cs` |

## Implementation: where it diverged from the plan

Executed 2026-10-10 on `feature/agent-outbound-webhooks` from main @ `5f36418`.

- **The webhook grain is `[Reentrant]` as a whole, `SendTestAsync` included** (decision 4 said config methods
  `[AlwaysInterleave]` and the test send not interleaved). A non-reentrant grain cannot count its backlog: calls still
  waiting in its mailbox are invisible to grain code, so `MaxPendingDeliveries` would be unenforceable. Deliveries
  therefore interleave, and the grain serializes what must not overlap itself: a send gate (one delivery at a time)
  and a write gate (so concurrent `WriteStateAsync` calls cannot race the ETag). "Not interleaved" would buy nothing
  for the test send once deliveries interleave, so it bypasses the send gate instead: the owner waiting on the button
  is never queued behind a backlog.
- **Delivery order is not promised.** Orleans does not order messages, and `SemaphoreSlim` is not FIFO, so the log is
  in completion order. Found by `Deliver_LogCappedAt20`, which now asserts the cap and newest-first by `At` only.
- **The research reminder's reply text: the plan's check did not discriminate.** With the test fake, a research
  reminder's `response.Text` equals the last assistant message, so a mutation to `response.Text` left
  `ResearchReminder_DeliversLastAssistantText` green. `LastAssistantText` is kept as the plan's safer choice (it is
  also right for a tool-calling turn whose narration is a separate assistant message), but no test proves it beats
  `response.Text`. The persisted-gate mutation (dropping `!persisted`) was caught.
- **Agent-delete failure test** uses a test-only incoming grain call filter (`FailingWebhookClearFilter`, registered
  in `TestSiloConfigurator`) to make `ClearAsync` throw for a marked key — the plan named the test but not the means.
- **The plan's "class-level comment in `AgentGrain.cs` that lists each run site's side effects" does not exist**, so
  there was nothing to update; the three fire sites carry "fire site n of 3" comments and the gotcha lists them.
- **DevHost also registers `WebhookSecretProtector`** (the plan named only the Null sender): without it the PUT
  answered 503 on DevHost.
- **`webhooks.js` is wired from `main.js`** (`wireWebhooks()`, ADR 0041's composition root) and opened from
  `agents.js` (`openWebhookPanel`), with a row in `ProjectLayoutTests.FrontEndWiredModules`. `Webhooks` joins both
  projects' pinned feature-folder sets and ADR 0039/0040's lists.
- **`MapWebhookEndpoints` uses named handlers**: four inline lambdas scored 19 on S3776.
- **AC22 is a source-text test** (`Chat_ReplaceLastTurn_PassesFlagToGrain`); the grain half is AC21's test.
- **Manual DevHost check:** the four endpoints were exercised with curl (secret once, plain PUT keeps it, test 200,
  log, delete 204). The tab's rendering was **not** checked in a browser — the browser extension was not connected.

## Review

Critic pass 2026-10-10 (fresh-context `critic` agent). No blockers; code claims verified (persist method and call site, `interactive` local, reminder `RunAsync` discard, `CreateHandler` returns `SocketsHttpHandler`, silo-host list complete, four `CACHE` pins, the three fire sites are the only run paths — research and Copilot flow through them).

| Finding | Severity | Handling |
|---|---|---|
| A. `ClearAsync`/`SaveAsync` queue behind a ~40 s delivery, past the 30 s call timeout; unbounded OneWay backlog | Medium | **Fixed** — config methods `[AlwaysInterleave]` with re-read-after-await; `MaxPendingDeliveries` backlog cap; tests added |
| B. Cited delete-order precedent does the opposite | Medium | **Fixed** — the reversal and its reason are stated; ordering test `AgentDelete_WhenWebhookClearFails_AgentSurvives` |
| C. Reminder fire site must precede the push early-return | Medium | **Fixed** — placement pinned; `ReceiveReminder_AfterPush_StillDelivers` |
| D. `SHELL` wording hedged | Minor | **Fixed** — unconditional |
| E. `amended_by` is a scalar | Minor | **Fixed** — convert to list + body note |
| F. Reminder reply text unasserted | Minor | **Fixed** — reply asserted; multi-iteration research reminder case |
| G. `_ephemeral` reasoning mismatch | Minor | **Fixed** — the gate inherits the persist's own read; ADR to state it |

Diff critic pass 2026-10-10 (execution): no blockers. Applied: `UseProxy = false` on the guarded handler (a proxy
would make `ConnectCallback` vet the proxy's address, not the target's) and pinned it in the source-text test;
`ClearAsync` writes nothing for a webhook that was never configured (every agent delete calls it); the log is mutated
inside the write gate. Accepted as-is: a failed `WriteStateAsync` in Save/Clear leaves memory ahead of storage until
reactivation (the repo-wide pattern); a DELETE racing a PUT can leave a URL without a secret, which sends nothing and
self-heals on the next PUT (it mints one because `HasSecret` is false).
