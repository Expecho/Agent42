---
id: 0100
date: 2026-10-18
status: accepted
tags: [push-notifications, pwa, web-push, settings, egress, ssrf, no-migration]
supersedes: []
amends: [0028]
---

# A user-initiated test push to this device, with a reported verdict

## Context
Web Push (ADR 0028/0075) only fires on an async reply or when the model calls `send_push_notification`. Neither
gives a user a direct way to check that a device works end to end. On an installed Android PWA an OS-level block
(the app's notification channel, battery optimisation) looks exactly like "push is broken", and the existing sender
cannot tell the two apart: it swallows every failure by design.

## Decision
Settings → General → notifications gets a **Send test** button, visible only while this device is subscribed.
It calls `POST /api/push/test` with `{endpoint}` and shows the verdict inline. This is a **third push origin**:
user-initiated, live, fixed text.

- **This device only.** The server pushes only when the endpoint is already stored for the caller
  (`IUserPushGrain.GetAllAsync`, matched by `Endpoint`). It never accepts keys and never dials a URL the caller
  supplied in the request.
- **A reporting send path, `PushTestNotifier`, separate from `IPushNotificationSender`.** It calls
  `IWebPushClient.SendAsync` for one subscription inside the HTTP request (never in a grain), bounded by
  `WebPushNotificationSender.SendBudget`, and maps the result to an outcome: `sent`, `not-registered`, `gone`
  (404/410, pruned exactly like the sender), `failed` (with the push service's status if any) or `timeout`
  (the budget, or the named `HttpClient`'s own `Timeout` while the caller is still connected). It never throws for
  a send failure. Blank endpoint → 400.
- **Resolved optionally.** The notifier exists only when push is configured. The endpoint resolves it through
  `IServiceProvider.GetService` (the `/api/speech` pattern) and answers 409 when it is absent, so DevHost and test
  hosts need no registration.
- **The client self-heals first.** It re-POSTs the current subscription to `/api/push/subscriptions` (an idempotent
  upsert) before the test, so a server-side prune doesn't fail it. `Notification.permission === "denied"` is
  reported locally with no request. After `sent` the hint points at the Android notification and battery
  settings, because `sent` means the push service accepted it, not that anything was displayed.
- **Payload** is the SW's existing `{title, body, conversationId}` shape with fixed text and an empty
  `conversationId`, so the SW needs no change. Egress matches ADR 0028's away-reply push: fixed app text, no
  conversation content, no model-authored text (ADR 0075's sanitising concern does not apply).

## Alternatives considered
- **Test every device of the user at once**: the person pressing the button is holding one device, and a fan-out
  verdict ("2 of 3 sent") can't say which device is broken. Out of scope; the button is per device.
- **Reuse `IPushNotificationSender`**: it fans out and swallows every failure, because it runs after a persisted
  turn inside a non-reentrant `AgentGrain` (ADR 0028). Making it report would change the contract of a path that
  must never fault a turn. A test that cannot report failure is useless, so the test gets its own small path.
- **Accept the full subscription (endpoint + keys) in the request and push to it**: this would let a caller make
  the server POST to any URL, which widens ADR 0028's accepted blind-SSRF property from "stored subscriptions" to
  "any request". Looking the endpoint up in the caller's own storage adds no new surface.

## Consequences
- A user can tell "the server can't reach this device" (`gone`, `failed`, `timeout`) from "the push service
  accepted it but the OS hid it" (`sent` with nothing on screen).
- There are now three push origins with different rules: the away-reply push (async callers only, swallowing), the
  model-invoked push (any path, ADR 0075) and this test push (HTTP request, reporting). They must not be merged.
- No server-side rate limit: the button is user-initiated, debounced by disabling it while in flight, and only
  reaches the caller's own stored subscription.
- No delivery receipt exists in Web Push, so display on a real device stays a manual check.
