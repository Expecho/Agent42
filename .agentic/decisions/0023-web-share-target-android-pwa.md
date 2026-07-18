---
id: 0023
date: 2026-07-18
status: accepted
tags: [pwa, web-share-target, service-worker, easy-auth, attachments, egress, android, frontend]
---

# Web Share Target for the Android PWA

## Context
The Agent 42 PWA is installable, but an installed app could only *receive* content by the user
opening it and pasting/attaching. The user wanted Agent 42 to appear in Android's system **Share
sheet** so text, a URL, an image or a document shared from any app lands in a chat — into a
destination picked per share (a new chat, or a stored agent). The frontend and attachment pipelines
(ADR 0004 images, ADR 0007/0008 documents) are stable, so this is almost entirely front-of-house.

**Platform reality:** Web Share *Target* is Android/Chromium-only. iOS PWAs can share *outward* but
cannot register as a share target — a platform limit to document, not engineer around.

## Decision
Declare a `share_target` in the manifest and handle the incoming POST **entirely in the service
worker**, then reuse the existing chat/upload machinery.

- **Manifest.** A `share_target` with `action: "/share"`, `method: "POST"`,
  `enctype: "multipart/form-data"`, and a `files` param whose `accept` mirrors the composer
  `#fileInput` (images, PDF/Office/text).
- **The auth pivot — SW-stash, no server route.** Easy Auth is `RedirectToLoginPage` (ADR 0002); a
  POST navigation to a protected path with a stale session would 302 to login and **lose the body**.
  We avoid this entirely: the service worker **intercepts `POST /share` client-side before it reaches
  the network**, reads the `FormData`, stashes `{title,text,url,files:[{name,type,blob}]}` in
  **IndexedDB** (`agent42-share`/`pending`, fixed key), and answers with a **303 redirect to a GET**
  landing (`/?share=1`). The POST never hits the server, so Easy Auth never sees it; the subsequent
  GET is a normal authenticated navigation, and if the session is stale it round-trips through login
  while the payload survives in origin-scoped IndexedDB.
- **Ordering invariant.** The share `respondWith` sits **after** the `/api` early-return (the ADR 0002
  never-intercept-`/api` invariant, guarded by `ServiceWorker_BypassesApiBeforeCaching`) and **after**
  the same-origin guard, but **before** the non-GET early-return (a share is a POST). The non-GET
  return moved below the share branch.
- **Per-share picker, review-then-send.** On the `/?share=1` load, `app.js` reads+clears the stash,
  rebuilds `File` objects, strips `?share=1` (`history.replaceState`), `await`s `loadAgents()` (so the
  list is populated before the picker opens), and shows a destination `<dialog>` (New chat / an agent).
  The choice calls the existing `startNewConversation()` / `startAgentChat(agent)` (which already
  handles continuous agents, ADR 0016), prefills the composer from `text`/`url`/`title`, and runs the
  shared files through the existing `handleFiles()` upload path (images → attachments, documents →
  RAG). It is **never auto-sent** — the user reviews and presses Send.

## Alternatives considered
- **A server `POST /share` route + `excludedPaths` carve-out** (like ADR 0022's `/api/agent-run`) —
  rejected: it reintroduces the Easy Auth body-loss problem (a stale session drops the POST body on the
  login redirect), and it needs a Bicep change and a self-enforcing public endpoint. The SW-stash design
  needs **no backend endpoint, no `excludedPaths`, no Bicep** — the deliberate contrast with ADR 0022,
  which needed all three *because* it is a real server endpoint.
- **A persisted default destination** (a `UserProfile` setting) — deferred: out of scope for v1 to
  avoid a profile schema change; the per-share picker is itself the "configurable" surface.
- **Auto-send the shared payload** — rejected: sending can egress outside the Entra tenant on a
  third-party model (ADR 0013). Review-then-send keeps the user's explicit consent on the send action.
- **A server fallback for when the SW isn't active** — rejected: any server `POST /share` is the
  rejected route above. SW-inactive share is accepted degradation, documented as a limit.

## Consequences
- **No backend or infra change.** No C# route, no `excludedPaths`, no Bicep — unlike ADR 0022. The
  whole feature is manifest + `sw.js` + `index.html`/`app.js`/`styles.css` + PWA-asset tests. The SW
  shell cache bumps `agent42-shell-v6` → `-v7` so returning users pick up the new shell.
- **Headline egress (extends ADR 0013), a new inbound vector.** A shared payload — a note, a URL, an
  image, a document — flows into the same pipeline that, on a third-party model, sends content outside
  the Entra tenant. Sharing is a new *inbound* door onto that egress (analogous to ADR 0022). Review-
  then-send keeps consent on Send, and the no-model gate (ADR 0019) still applies: a share into a
  model-less chat prefills but **cannot send** — `applyModelAvailability` disables the send button and
  the input on `hasModel === false`, verified before this claim.
- **Continuous-agent brick acceleration.** Sharing a **large image or document into a continuous agent**
  (ADR 0016) is a new low-friction path to that agent's single unbounded grain, hastening the 2 MB
  session cap (ADR 0004) that bricks the conversation — the escape hatch stays
  `DELETE /api/conversations/agent-{agentId}`.
- **Android-only.** iOS gets nothing here (platform limit), and the share only works when the SW is
  installed and controlling; otherwise the POST reaches the server and fails — accepted degradation
  (see gotcha). Both are documented, not worked around.
- **Weak automated proof.** New logic is frontend JS (no JS test runner by design), covered by
  source-text heuristics that prove the contract's *shape* — the real proof is a manual DevHost drive
  of a simulated `/?share=1` + seeded IndexedDB record.
