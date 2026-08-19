---
id: 0032
date: 2026-07-25
status: accepted
tags: [pwa, offline, indexeddb, service-worker, easy-auth, frontend, egress]
---

# Offline read cache for agents and chats in IndexedDB

## Context
The Agent 42 PWA is installable and the service worker caches the static **shell** (`sw.js`,
`agent42-shell-v*`), so the app *opens* offline — but every list comes up **empty**. All data
(`GET /api/agents`, `/api/conversations`, `.../messages`) is fetched fresh into in-memory arrays on
each load, and the loaders swallow network errors silently (`refreshConversations`,
`loadAgents`, `selectConversation`). The user wants their agents and chats **visible offline** in the
installed PWA.

## Decision
Add a **client-side IndexedDB read cache in the frontend** (`src/Agent42.Backend/wwwroot/js/cache.js` since ADR 0041; a single `app.js` when this was written) — the same tier where `agents`/`conversations`
already live as render caches. The server stays the source of truth; the cache is a best-effort mirror.

- **Store.** A dedicated DB `agent42-cache`, one object store `kv`, keyed by `"agents"`,
  `"conversations"`, and `"messages:<id>"`. Promise-wrapped best-effort helpers `cacheGet`/`cachePut`/
  `cacheDelete` modelled on the existing `agent42-share` reader (ADR 0023) — any IndexedDB error
  resolves to a no-op (private mode, quota) and never blocks a load.
- **Write on success.** `refreshConversations`/`loadAgents` `cachePut` their list after a successful
  fetch; `selectConversation` `cachePut`s a conversation's history under `messages:<id>` after it
  renders. `deleteConversation` `cacheDelete`s the history copy.
- **Read on failure / at startup.** A new `hydrateFromCache()` at init paints cached agents +
  conversations *before* the network resolves (length-guarded, so the network fast-path wins). On a
  failed fetch the loaders fall back to the cached list; `selectConversation`'s catch renders a
  previously-opened chat's history from the cache through the same extracted `renderTurns()` the live
  path uses.
- **The SW is untouched apart from a shell version bump** (`-v9` → `-v10`, so returning clients pick
  up the new frontend). The `/api` early-return and guard ordering stay exactly as-is.

## Alternatives considered
- **Cache `/api` GETs in the service worker** (network-first with a cache fallback) — rejected: it
  breaks the ADR 0002 Easy-Auth invariant. The SW deliberately never touches `/api` so a stale
  session's login 302 is never masked and per-user auth-bearing responses are never stale/leaked;
  `ServiceWorker_BypassesApiBeforeCaching` guards that ordering. A client-side cache in the app modules keeps
  the SW invariant intact and the auth redirect visible.
- **Pre-fetch every conversation's messages so all chats are viewable offline** — rejected for v1: a
  fan-out of `/api/.../messages` calls on every load. Instead a chat is viewable offline only if it
  was opened at least once while online (its turns were cached then) — the common case (recent chats)
  with no extra request cost.
- **Encrypt the cached data at rest** — deferred: consistent with the app's existing unencrypted
  `localStorage` UI prefs and the cleartext `agent42-share` blobs; the origin is single-user and
  behind Easy Auth. Recorded as the accepted downside below.

## Consequences
- **New data-at-rest surface.** Conversation text and agent definitions now persist **unencrypted in
  origin IndexedDB**, cleared only by clearing site data — broader than the small `localStorage` UI
  prefs, but the same trust boundary (a signed-in user's own device, ~2 allowlisted users). No new
  egress: nothing leaves the device that didn't already come from the user's own authenticated fetch.
- **Accepted offline degradation.** Image/attachment bytes load through a separate proxy
  (`/api/conversations/{id}/attachments/{aid}`) and won't render offline (broken thumbnails); message
  **text** shows. Sending is impossible offline — the composer already gates on model availability
  (ADR 0019), so no extra work there.
- **No backend or infra change.** The whole feature is frontend JS + a one-line `sw.js` version bump +
  PWA-asset tests. `ServiceWorker_CacheNameIsCurrent` moves to `agent42-shell-v10` in lockstep.
- **Weak automated proof.** New logic is frontend JS (no JS test runner by design), covered by
  source-text heuristics (`Frontend_MirrorsAgentsAndChatsForOffline`) that prove the wiring's *shape* —
  the real proof is a manual offline drive in DevTools (Network → Offline, reload).
