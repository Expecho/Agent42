---
id: 0028
date: 2026-07-21
status: accepted
tags: [push-notifications, pwa, service-worker, web-push, vapid, egress, security, ssrf]
amended_by: [0075, 0100, 0111]
---

> **Amended by [ADR 0075](0075-model-invoked-push-notification.md) (2026-09-16).** Two things change, and
> the rest of this ADR stands. **(1)** "Never push from `ChatCoreAsync`" is **narrowed, not repealed**: it
> governs `SendAwayReplyPushAsync` specifically — including both of its placement traps, which are
> untouched — while a *model-invoked* notification (`send_push_notification`) may fire from any path,
> the live turn included. **(2)** The away-reply push is now **suppressed when that tool already pushed
> during the turn**, so one reply never stacks two entries in a shade that sets no `tag`. The Decision
> sentence below, "A user actively chatting receives their reply in-stream; no push is generated for that
> turn", is the one line this makes false. The **Egress** section's reasoning also needs re-reading: the
> RFC 8291 encryption is unchanged, but the payload is no longer just a user-chosen agent name — title and
> body are now model-authored, conversation-derived text.

> **Amended by [ADR 0100](0100-push-test-notification.md) (2026-10-18).** A third push origin: the user-initiated
> **Send test** in Settings (`POST /api/push/test`). It pushes fixed text to one subscription already stored for
> the caller and **reports** the result through its own `PushTestNotifier`. The swallowing sender below is
> unchanged, and the blind-SSRF property stays limited to stored subscriptions.

> **Amended by [ADR 0111](0111-chat-gateway-for-chat-apps.md) (2026-10-21, phase 1c): an away reply can also go to a chat.**
> Next to `SendAwayReplyPushAsync`, at its same two sites and never in `ChatCoreAsync`, the grain hands the reply's text to
> `IChatDeliverySender`: to the chat the conversation came from, else to the owner's home chat when notifications are on.
> Web Push itself is unchanged.

# Web Push notification when an agent replies while the user is away

## Context
Two reply paths run with **no live client attached**: a scheduled prompt firing (`ReceiveReminder`) and
an external agent-run job (`EnqueuePromptAsync`, ADR 0022). Their streamed events have no listener, so a
reply completes and the user only discovers it by re-opening the app. The repo already ships a service
worker and manifest (ADR 0023) but no push infrastructure — no VAPID keys, no subscription store, no
`push`/`notificationclick` handlers. This is greenfield.

## Decision
Deliver a **Web Push** notification, whose **title is the agent's name**, to the user's installed
Android/Chromium PWA when an agent finishes a reply on one of the two **asynchronous** paths and no live
client is driving the turn.

- **"Not online" is approximated by a single fact: we only send on the two async paths.** `ChatAsync`,
  `EnqueuePromptAsync` and `ReceiveReminder` all share `ChatCoreAsync`, so the send is placed in the two
  async **callers** after a successful completion — **never** inside `ChatCoreAsync` (that would fire on
  the live interactive stream too, where the user is already watching). A user actively chatting receives
  their reply in-stream; no push is generated for that turn.
- **Subscriptions live in a dedicated per-user grain** (`IUserPushGrain`, state `"push-subs"` on the
  shared `agent-state` provider — no new Cosmos container), not in `UserProfile` (ADR 0005 keeps that a
  scalar singleton; a per-device subscription list that the send path mutates/prunes is a churny
  collection). Same overlay-grain shape as `UserProviderConfigGrain` / `UserMcpConfigGrain`.
- **The interface/impl split follows `IGeocoder`**: `IPushNotificationSender` + `PushMessage` +
  `NullPushNotificationSender` live in `Agent42.Agents`; the real `WebPushNotificationSender` (over the
  `WebPush` library, isolated behind an `IWebPushClient` seam) lives in `Agent42.Backend`. Every host that
  activates `AgentGrain` must register a sender, and every host that maps `MapAgentApi` must register the
  `PushConfig` singleton.
- **The VAPID private key is the only secret and is optional** — the `contextApiKey`/`googleMapsApiKey`
  shape (ADR 0012/0027), not `searxngSecret`: with no key the app boots, `GET /api/push/config` returns
  `enabled:false`, the UI hides the toggle, and the no-op sender does nothing. The public key + subject
  are non-secret and committed in `appsettings.json`.

### "Not online" — a server-side presence/heartbeat tracker was rejected
It would add grain state plus a client ping loop for **no behavioural gain** over "async paths only": the
async paths already fire exactly when the user is not the one driving the turn. Because push subscriptions
are per-device, a delivered push reaches whichever devices are subscribed — which is the right target.

### The service worker ALWAYS shows the notification — no suppress-if-focused
The obvious "skip `showNotification` when a client is visible" trick **violates Chromium's
`userVisibleOnly:true` contract**: a push received without a shown notification consumes push budget and,
once exhausted, Chrome shows its own generic "site updated in the background" notice and can **revoke the
subscription**. That penalty lands in exactly the target case (a background reply completing while the PWA
happens to be open on that device). So the SW always shows on a received push (honouring the contract,
zero budget risk); away-ness is carried entirely by "the server only pushes on the two async paths". If
the user is looking at that device when their background agent finishes, a notification titled with the
agent name is acceptable and arguably wanted. Accepted tradeoff.

## Alternatives considered
- **Server-side online/heartbeat presence tracker** — rejected as over-engineering (above).
- **Suppress-if-focused in the SW** — rejected: burns `userVisibleOnly` push budget, risks revocation
  (above).
- **Store subscriptions in `UserProfile`** — rejected: a per-device list the send path prunes is a
  collection, a poor fit for the scalar preferences singleton (ADR 0005).
- **Encrypt stored subscriptions** (a DP purpose, like provider/MCP secrets) — **deferred**. A push
  subscription is a device push capability usable only against **our** VAPID pair, not a third-party
  account credential, and is never echoed to another user; a Cosmos-read attacker already holds the full
  conversation. Stored in cleartext to keep scope minimal. A reviewer may reasonably push back — recorded
  honestly rather than asserted as a non-risk.

## Consequences

### The send belongs in the two async callers, never in `ChatCoreAsync`
This is the load-bearing invariant and the easiest to break by "refactoring the duplicated send into the
shared core". `ChatCoreAsync` is the live path's body too — a send there would notify a user who is
actively watching their reply stream. The send sits **inside `EnqueuePromptAsync`'s `try`, after the drain
loop** (a faulted turn caught below must not notify) and is **gated on `_modelId`** (the no-model path
drains normally after emitting the "configure a provider" notice — it must not fire a false "your agent
replied"); and in `ReceiveReminder` **after `WriteStateAsync`** (the no-model case already early-returned,
so a push only ever follows a durably-persisted reply). Recorded as a gotcha with evidence.

### Egress: the payload is RFC 8291 end-to-end encrypted, but note it anyway
Each notification transits a third-party push service (FCM / Mozilla autopush / WNS). Web Push payloads
are **end-to-end encrypted per RFC 8291** with the subscription's `p256dh`/`auth` keys, so the push
service **cannot read** the agent name or body — a materially smaller egress than ADR 0013's model egress.
It is still recorded, because the title is the **user-chosen agent name** (a user could have made it
sensitive) and the endpoint's origin reveals *that* a notification was sent.

### The sender POSTs to the browser-supplied subscription endpoint — an accepted blind-SSRF property
An authenticated user's stored subscription endpoint is a URL our container POSTs to. This is the
**standard Web Push architecture** — every web-push server dials browser-supplied endpoints — and the
residual is narrow: authenticated users only (behind Easy Auth), the request body is RFC 8291-encrypted
noise, and the **response is never returned to the caller** (the sender swallows and logs all failures),
so it is a *blind* POST with no exfiltration channel. Unlike the geocoder (a fixed host) or a fetched URL
(ADR 0027, moved off our container), a host allowlist here would be a fragile, breakage-prone list of push
services. Accepted rather than guarded, and recorded so it is not rediscovered as a surprise.

### The non-reentrant grain must not stall on the send
`AgentGrain` is non-reentrant, so an inline send blocks the whole activation for the round-trip to the
push service × N subscriptions. Two mitigations, both required: the sender bounds the entire fan-out with
a short `CancellationToken` timeout (`WebPushNotificationSender.SendBudget`) and sends concurrently; and
the adapter's `HttpClient` calls **`RemoveAllResilienceHandlers()`** — every Backend named client silently
inherits `AddServiceDefaults`' 3-retry pipeline, which would quadruple a dead-endpoint stall on this exact
grain (the same trap as the web/geocoding clients).

### Dead subscriptions are pruned; every send failure is swallowed
The push service returns **404/410** for a gone subscription; `WebPushNotificationSender` translates the
library's `WebPushException` into a seam-local `PushDeliveryException` carrying the status, and prunes on
404/410 via `IUserPushGrain.RemoveAsync`. Every other failure is logged and swallowed — a push failure
must never propagate into a turn that has already been persisted.

### `WebPushClientAdapter` is the isolation seam; `IWebPushClient` is the tested boundary
The `WebPush` package appears in exactly one file (the adapter). The sender's fan-out/prune logic is unit-
tested against a fake `IWebPushClient`; the adapter's only real logic — the `WebPushException` → status
translation the prune rests on — is pinned by a separate test that drives the real library through a
stubbed `HttpMessageHandler` with a real VAPID + P-256 subscription keypair.

### Frontend is verified by offline run + manual device check, plus a source-text seam
As with ADR 0023, real-device behaviour (subscribe, background reply, tap-to-open) is verified manually on
Android Chrome. The repo's C# source-text guards (`PwaAssetsTests`) assert the SW registers the `push` +
`notificationclick` listeners and that the `src/Agent42.Backend/wwwroot/js/push.js` subscribe wiring is present; the interactive flow
itself stays manual-only.

### Out of scope
No server-side presence/heartbeat; no iOS push (Android/Chromium PWA is the target, feature-detection
hides the toggle elsewhere); no notifying for interactive turns the user abandons mid-stream; no rich
bodies/actions/images; no encrypting stored subscriptions (deferred, above).
