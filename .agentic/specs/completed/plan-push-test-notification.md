planned-against: main @ 5807561

# Plan: send a test push notification from Settings

## Overview

Web Push (ADR 0028/0075) only fires on an async reply or when the model calls `send_push_notification`.
That leaves no direct way to check that a device works end to end: subscription stored, VAPID valid,
push service accepting, SW showing the notification, and Android not suppressing it. This change adds a
**"Send test"** button to Settings → General → notifications. It pushes a fixed notification to **this
device's** subscription and shows the push service's verdict inline. Users need it most on an
installed Android PWA, where an OS-level block (notification channel, battery saver) looks exactly like
"push is broken".

## Affected areas (verified against the tree @ 5807561)

| File | Change |
|------|--------|
| `src/Agent42.Backend/Push/PushTestNotifier.cs` | **new**: sends the test push to one stored subscription and returns an outcome |
| `src/Agent42.Backend/Push/PushContracts.cs` | add `PushTestRequest(string Endpoint)` + `PushTestResponse(string Outcome, int? StatusCode)` |
| `src/Agent42.Backend/Push/AgentApi.Push.cs` | add `POST /api/push/test` inside `MapPushEndpoints` (already called from `Api/AgentApi.cs`) |
| `src/Agent42.Backend/Program.cs` | register `PushTestNotifier` inside the existing `if (pushEnabled)` block (~line 418) |
| `src/Agent42.Backend/wwwroot/index.html` | test button + result line in `#pushSection` (~line 559) |
| `src/Agent42.Backend/wwwroot/js/dom.js` | export the new elements (beside `pushSection`, ~line 112) |
| `src/Agent42.Backend/wwwroot/js/push.js` | `sendTestPush()`; show/hide the button in `updatePushControl()` |
| `src/Agent42.Backend/wwwroot/sw.js` | bump `CACHE` (currently `agent42-shell-v61`; bump whatever is current at execution) |
| `tests/Agent42.Tests/FakeWebPushClient.cs` | **new**: move the private `FakeWebPushClient` out of `WebPushNotificationSenderTests.cs` (line ~110) so both test classes share it |
| `tests/Agent42.Tests/PushTestNotifierTests.cs` | **new** |
| `tests/Agent42.Tests/AgentApiFixture.cs` | register a `PushTestNotifier` over a fake client (this fixture already enables `PushConfig`) |
| `tests/Agent42.Tests/AgentApiTests.cs` | API tests beside the existing push block (~line 1059) |
| `tests/Agent42.Tests/PwaAssetsTests.cs` | extend `Frontend_ShipsPushToggle` (~line 471) |
| `.agentic/decisions/` + `CLAUDE.md` feature map | new ADR via `/decide`, amending 0028; add it to the "Push" row |

DevHost and `SharedApiFixture` need **no** change: the endpoint resolves the notifier optionally (see below).

## Context & constraints

- **Optional resolution, not a mandatory registration.** `IWebPushClient` exists only when
  `pushEnabled` (`Program.cs` ~418–440). DevHost and `SharedApiFixture` register `PushConfig(Enabled:false)`
  and no client. So the endpoint resolves `PushTestNotifier` through
  `IServiceProvider.GetService` (the `/api/speech` pattern, `Speech/AgentApi.Speech.cs:31`; CLAUDE.md
  cross-cutting rule 7), and a null result returns **409**, "push is not configured". A required parameter
  would break routing for every host that leaves it out.
- **Why a separate class rather than reusing `IPushNotificationSender`.** `WebPushNotificationSender.SendAsync`
  is deliberately best-effort. It fans out to all devices, **swallows every failure** and returns nothing
  (ADR 0028, "every send failure is swallowed"), because it runs after a persisted turn inside a
  non-reentrant `AgentGrain`. A test that cannot report failure is useless, so `PushTestNotifier` calls
  `IWebPushClient.SendAsync` directly for **one** subscription and turns the result into an outcome.
  It runs in the HTTP request and never in a grain, so the non-reentrancy stall does not apply. It still
  bounds the send with `WebPushNotificationSender.SendBudget`; reuse that constant and don't add a new one.
- **Target this device only, looked up from storage.** The request carries only the `endpoint`. The server
  sends only if that endpoint is **already stored for the calling user** (`IUserPushGrain.GetAllAsync`,
  matched by `Endpoint`). It never accepts keys and dials an arbitrary URL. That adds no blind-SSRF surface
  beyond what ADR 0028 already accepted for stored subscriptions.
- **Self-heal before testing.** The server may have pruned a subscription the browser still holds
  (404/410 prune in `WebPushNotificationSender.SendOneAsync`). So the client first re-POSTs the current
  subscription to `/api/push/subscriptions`, which is an idempotent upsert by endpoint, and then calls
  `/api/push/test`. `not-registered` should then only appear on a race. It stays as an explicit outcome.
- **Outcomes** (`PushTestResponse.Outcome`, HTTP 200 for each):
  `sent` (the push service accepted; that is **not** proof of display), `not-registered`,
  `gone` (404/410, pruned exactly like the sender does), `failed` (with `StatusCode` from
  `PushDeliveryException`), and `timeout` (budget expired). Missing endpoint → 400; push off → 409.
  **Not every failure is a `PushDeliveryException`.** `WebPushClientAdapter.SendAsync` (~34–45) translates
  only `WebPushException`. DNS or connect errors arrive as `HttpRequestException`, and the named client's
  `http.Timeout` (= `SendBudget`, `Program.cs` ~426) can beat the linked budget token and surface as
  `TaskCanceledException`. Mapping:
  - `OperationCanceledException` (including `TaskCanceledException`) while the **caller's** token is not
    cancelled → `timeout`.
  - Any other exception → `failed` with `StatusCode: null`, logged.
  - The notifier never throws into the endpoint, so the endpoint never returns 500. This mirrors the
    sender's catch-all (`WebPushNotificationSender.cs` ~73–76).
- **Payload** uses the SW's existing `{title, body, conversationId}` shape (`sw.js` push handler ~137). It
  needs **no SW logic change**. Title `"Agent 42"`, body a fixed test line, `conversationId: ""`. The click
  handler then focuses the app, and `push.js`'s message listener ignores the empty id. Serialize with the
  web JSON options, as `WebPushNotificationSender` does. The text is fixed app text, so egress matches
  ADR 0028's away-reply push and there is no model-authored text to sanitize (ADR 0075 does not apply).
- **Client diagnostics before the server call:** if `Notification.permission === "denied"`, show "blocked in
  browser settings" and make no request. After `sent`, show a hint: "Sent. If nothing shows within a few
  seconds, check this app's notification settings in Android (and battery optimisation)". The button is
  visible only while this device is subscribed (`updatePushControl`). Disable it while a request is in
  flight, which is enough client-side debounce.
- **Gotchas in play:** cross-cutting rule 3 (bump `CACHE` in `sw.js`); rule 9 (S3776 at 15: keep the
  outcome mapping in `PushTestNotifier`, out of the endpoint lambda); rule 7 (optional resolution, above).
  The named Web Push `HttpClient` already removes its resilience handlers (gotchas ~441). Reuse it through
  `IWebPushClient` and don't create a new client.
- **ADR:** decisions with alternatives here are this device vs all devices, a reporting send path vs reusing
  the swallowing sender, and lookup-by-stored-endpoint vs accepting a full subscription. Record them with
  `/decide` as a new ADR that **amends 0028** (it adds a third push origin: user-initiated, live, fixed text).
  Put a one-line amendment banner on 0028 and add the new number to CLAUDE.md's Push feature-map row.

## Steps

1. **Shared fake.** Move `FakeWebPushClient` from `WebPushNotificationSenderTests.cs` into
   `tests/Agent42.Tests/FakeWebPushClient.cs` (internal). Keep its existing behaviour (`Sent`,
   `ThrowGenericFor`, status throws), and **add** a payload record keyed by endpoint (AC1 asserts
   title/body/conversationId) and an optional delay that honours the token (the timeout case).
   Verify: `dotnet test --filter FullyQualifiedName~WebPushNotificationSenderTests` stays green.
2. **`PushTestNotifier` + contracts.** Add `PushTestRequest`/`PushTestResponse` to `PushContracts.cs`
   and `PushTestNotifier(IGrainFactory, IWebPushClient, ILogger)` with
   `Task<PushTestResponse> SendAsync(string userId, string endpoint, CancellationToken)`.
   Write `PushTestNotifierTests` alongside it (shared cluster, as `WebPushNotificationSenderTests` uses).
   Verify: `dotnet test --filter FullyQualifiedName~PushTestNotifierTests`.
3. **Endpoint + registration.** Add `POST /api/push/test` to `MapPushEndpoints`: 400 on blank endpoint,
   409 when `GetService<PushTestNotifier>()` is null, otherwise `Results.Json(await notifier.SendAsync(http.GetUserId(), …))`.
   Register `builder.Services.AddSingleton<PushTestNotifier>()` inside `Program.cs`'s `if (pushEnabled)`.
   Register a notifier over a `FakeWebPushClient` in `AgentApiFixture`, and expose that fake as a public
   fixture property so `AgentApiTests` can assert on it. Add the API tests. The happy-path test stores
   its subscription under **its own user id** (as `PushSubscription_Post_RejectsIncomplete` does), not the
   shared `LocalDevUser` grain that `PushSubscriptionEndpoints_Crud` writes. The 409 test builds its own
   minimal host, modelled on `PushConfig_Disabled_WhenNoKey` (`AgentApiTests.cs` ~1119–1146:
   `CreateSlimBuilder` + `MapAgentApi()`, no notifier).
   Verify: `dotnet test --filter FullyQualifiedName~AgentApiTests`.
4. **Frontend.** Add a `Send test` button (`id="pushTestBtn"`, hidden by default) and a result line
   (`id="pushTestStatus"`, `aria-live="polite"`) in `#pushSection`; export them from `dom.js`; wire
   `sendTestPush()` in `push.js`: permission check → `(await navigator.serviceWorker.ready).pushManager.getSubscription()`
   (if null, show "not subscribed on this device" and refresh the control) → upsert POST with
   `sub.toJSON().keys` (the same body `togglePush` sends) → test POST → render outcome;
   show/hide the button in `updatePushControl()`. Bump `CACHE` in `sw.js`. Extend `Frontend_ShipsPushToggle`.
   Verify: `dotnet test --filter FullyQualifiedName~PwaAssetsTests`. Then run the DevHost and confirm the
   section stays hidden there (push is off) with no console errors.
5. **Docs.** Run `/decide` (a new ADR amending 0028), and add its amendment banner on 0028 **beside**
   the existing 0075 banner (don't replace it). Update the CLAUDE.md Push row. Extend the gotchas entry
   "`SendAwayReplyPushAsync` belongs in the two async callers" (~2222). It already separates the away-reply
   push from the model-invoked push, so add the third origin: the user-initiated test push. That push is
   live, outside any grain and reports its result, so it must not be "unified" with the swallowing sender.
   Run `/sync-docs`.
6. **Gate run.** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`,
   `dotnet format Agent42.slnx --verify-no-changes`, and `node .agentic/scripts/check-new-code-coverage.mjs`.

Steps 1→2→3 are sequential. Step 4 can run in parallel with 2–3 (it only depends on the agreed route
and response shape). Step 5 comes after 3.

**Manual verification after deploy (real device, not automatable):** on Android Chrome with the
installed PWA, open Settings → enable notifications → Send test, and see the notification arrive, both
with the app foregrounded and backgrounded. Tapping it focuses the app. Then disable notifications for
the app in Android settings, Send test again: it reports `sent` while nothing appears, which is what
the hint text is for.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| 1 | A stored endpoint for the caller receives exactly one push with the fixed title/body and empty conversationId; the outcome is `sent` | `SendAsync_StoredEndpoint_SendsOnceAndReportsSent` | `PushTestNotifierTests.cs` |
| 2 | Only the requested device is pushed, not the user's other subscriptions | `SendAsync_OnlyTargetsRequestedEndpoint` | `PushTestNotifierTests.cs` |
| 3 | An endpoint not stored for this user sends nothing → `not-registered` (another user's endpoint is not reachable) | `SendAsync_UnknownEndpoint_SendsNothing`, `SendAsync_OtherUsersEndpoint_SendsNothing` | `PushTestNotifierTests.cs` |
| 4 | 404/410 → `gone` and the subscription is pruned | `SendAsync_Gone_PrunesAndReportsGone` (Theory 404/410) | `PushTestNotifierTests.cs` |
| 5 | Another push-service status → `failed` with that status; it is not pruned | `SendAsync_OtherStatus_ReportsFailedAndKeeps` | `PushTestNotifierTests.cs` |
| 6 | Exceeding the budget → `timeout`, with no exception thrown | `SendAsync_Slow_ReportsTimeout` | `PushTestNotifierTests.cs` |
| 6a | A `TaskCanceledException` from the client (HttpClient timeout) while the caller's token is live → `timeout` | `SendAsync_ClientTimeout_ReportsTimeout` | `PushTestNotifierTests.cs` |
| 6b | A non-`PushDeliveryException` (e.g. `HttpRequestException`) → `failed` with null status; nothing is pruned or thrown | `SendAsync_GenericError_ReportsFailed` | `PushTestNotifierTests.cs` |
| 7 | `POST /api/push/test` returns 200 JSON with the outcome for the caller's stored endpoint | `PushTest_StoredEndpoint_ReturnsSent` | `AgentApiTests.cs` |
| 8 | Blank endpoint → 400 | `PushTest_BlankEndpoint_Returns400` | `AgentApiTests.cs` |
| 9 | A host with push disabled (no notifier registered) → 409, and routing still works | `PushTest_PushDisabled_Returns409` (own minimal host, modelled on `PushConfig_Disabled_WhenNoKey`) | `AgentApiTests.cs` |
| 10 | Settings ships the test button/result line and `push.js` calls `/api/push/test` (a source-text presence guard only; the permission check and self-heal order are covered by manual verification) | extend `Frontend_ShipsPushToggle` | `PwaAssetsTests.cs` |
| 11 | Real Android PWA shows the test notification | manual (above) | — |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ 5807561)

| Gate | Threshold | Tool |
|------|-----------|------|
| coverage_new_code_pct_min | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| cognitive_complexity_max | 15 | SonarAnalyzer S3776 at build (`dotnet build`) |
| vulnerable_packages | 0 | CI package-freshness job |
| build / tests / format | must pass | `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` |

## Out of scope

- Testing **all** devices at once, or a server-side cooldown or rate limit. The button is per-device and
  user-initiated, and it only reaches the caller's own stored subscription.
- Confirming *display*. Web Push gives no delivery receipt. `sent` means the push service accepted it.
- iOS or desktop-specific tweaks; changes to the SW push or click handlers; notification `tag`/actions.
- Tidying the misplaced section comments in `AgentApi.Push.cs` ("configuration health", "remote MCP servers").

## Review

Critic pass (2026-10-18): no ADR contradictions. All five findings were accepted and folded in above:
non-`PushDeliveryException` / HttpClient-timeout mapping (AC 6a/6b), the 409 test location (minimal host),
the fake's payload record + fixture exposure + an isolated user id, the null-subscription client path,
and the 0028 banner placement + the gotchas entry update. Nothing was rejected.

## Implementation divergences (2026-10-18)

- **Payload shape reused, not duplicated.** `WebPushNotificationSender.PushPayload` and `PayloadJson` went from
  `private` to `internal`, so `PushTestNotifier` serializes exactly what the SW reads.
- **Outcome constants.** The outcome strings live in a `PushTestOutcomes` static class beside the notifier. The
  tests and the endpoint use it instead of string literals.
- **Caller cancellation propagates.** An `OperationCanceledException` raised because the caller's own token was
  cancelled (the client went away) is not mapped to an outcome. Only a budget or `HttpClient` timeout becomes `timeout`.
- **A failed prune still reports `gone`.** `RemoveAsync` after a 404/410 is guarded and logged (a critic finding), so
  the endpoint can't 500 there. The real sender gets the same effect from its outer catch-all.
- **The 409 test shares a helper.** The body of `PushConfig_Disabled_WhenNoKey` was extracted into
  `StartPushDisabledHostAsync`, which it and `PushTest_PushDisabled_Returns409` both use.
- **Three more cache-name pins.** `SearchAssetsTests`, `ResearchAssetsTests` and `ClientToolsAssetsTests` also
  assert the SW `CACHE` value. They were bumped to `agent42-shell-v62` along with `PwaAssetsTests`.
- **Fixture wiring.** `AgentApiFixture` registers the notifier explicitly over its `PushClient` fake rather than
  registering an `IWebPushClient` in the host. The ADR is 0100.
- **The DevHost check ran over HTTP:** `/api/push/config` returned `enabled:false`, `/api/push/test` returned 409, and
  the markup was served. No browser console session was run.
