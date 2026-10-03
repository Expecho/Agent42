---
id: 0035
date: 2026-07-30
status: accepted
tags: [agents, conversations, sharing, public-endpoint, easy-auth, excluded-paths, egress, frontend]
amended_by: [0083, 0084]   # 0083 rich rendering (viewer loads lib/richtext.js + highlight.js); 0084 share links serve sandboxed artifacts
---

# Read-only share links for conversations

> **Amended by [ADR 0042](0042-wildcard-easy-auth-exclusions-and-forwarded-proto.md) (2026-08-20) — the
> feature shipped non-functional.** The `excludedPaths` entries below were written as *prefixes*, but the
> Easy Auth matcher treats a bare entry as an **exact** match, so `/shared/`, every viewer asset and both
> public API routes were gated and every recipient hit the Entra login page. 0042 switches to the wildcard
> form and pins the semantics in a test. It also makes `Request.Scheme` honest behind the ingress, so the
> minted `ShareUrl` is `https://` rather than `http://`.

> **Also amended by [ADR 0044](0044-persistent-topbar-action-cluster.md) (2026-08-21) — a second owner
> entry point.** The hover-revealed sidebar row action below is no longer the only way in: a
> `#topbarShareBtn` in the persistent top-right cluster opens the same `#shareLinkDialog`, and the row's
> `↗` text glyph became the same SVG mark that button draws. The row action itself is unchanged, and so
> is the consent property — the topbar button only *opens* the dialog and mints nothing. 0044 records the
> one place that property could have been weakened (a fresh agent chat inheriting the previous
> conversation as a stale share target) and closes it.

> **Also amended by [ADR 0047](0047-native-share-for-read-only-links.md) (2026-08-22) — the link now
> leaves through the OS share sheet.** Pressing **Create link** mints, renders, and then hands the fresh
> URL to `navigator.share`, and a **Share** button replaces **Copy** wherever that API exists — a pure
> feature-detect, so not only on Android. The consent property below is untouched: nothing is minted
> without the Create press, and the auto-open rides that same press. One thing here does change — the
> conversation **title** now leaves the app alongside the URL, where Copy carried the URL alone.

## Context
Every conversation in Agent42 is private to its owner: the whole app sits behind Entra Easy Auth
(ADR 0002), and every read route is user-scoped via `AgentKey(GetUserId(), conversationId)`. There was no
way to let someone **without a login** view a conversation. The user asked to mint a **read-only link** to
any chat (regular or agent chat) that **anyone with the link can open, no sign-in** — images included — and
be able to **revoke** it (no automatic expiry).

Two facts frame the design:

- **There is an exact prior-art pattern for a public, self-enforcing endpoint.** ADR 0022's
  `POST /api/agent-run` is on the one Easy Auth `excludedPaths` carve-out: it authenticates by an opaque
  credential, resolves the owner from a **hash-keyed global directory grain** (`AgentKeyDirectoryGrain`,
  keyed by the credential hash), **never** calls `GetUserId()` (which is `local-dev` on an excluded path),
  and **fails closed**. A share link is the read-only analogue.
- **A growing per-user structure rides shared state with no new container.** `UserPushGrain` / etc. add a
  distinct state name on the shared `agent-state` provider (ADR 0028/0018) — no Bicep, no new Cosmos
  container.

## Decision
Mirror ADR 0022's two-grain split, plus a self-contained public viewer page.

- **`ShareToken`** (copy of `ApiKey`): mints `shr_` + 256 random bits and its lowercase-hex SHA-256 —
  the single place that generates *and* hashes, so the minter and the request-time resolver agree.
- **`ShareLinkDirectoryGrain`** (copy of `AgentKeyDirectoryGrain`): global, **grain key = token hash**,
  state `"share-link-directory"` on `agent-state`; resolves the hash → `ShareLinkTarget(userId, conversationId)`
  or `null`. This is what the public endpoint resolves (O(1), no signed-in user available).
- **`UserShareLinksGrain`**: per-user store (state `"share-links"`), the owner-facing half — list / create /
  revoke + re-display. It keeps the directory in sync **per-operation**.
- **Endpoints** (`AgentApi.cs`, in `MapAgentApi`):
  - Owner (behind Easy Auth, `GetUserId()`): `POST` / `GET` / `DELETE /api/conversations/{id}/share`.
  - Public (on `excludedPaths`, **self-enforcing**, `GetUserId()` **never** used):
    `GET /api/share/{token}` → `{ title, agentName?, turns }` (reuses the `ChatTurn` / `AttachmentRef` DTOs
    — `BlobPath` stays `[JsonIgnore]`), and `GET /api/share/{token}/attachments/{attachmentId}` — a
    token-scoped analogue of the user-scoped image proxy (required because that one is Easy-Auth gated).
    Both resolve `(userId, conversationId)` from the directory and **fail closed → 404** ("not found or
    revoked" — a browser-visitable link, so 404 not the key-header 401).
- **Infra**: entries added to `container-app.bicep` `globalValidation.excludedPaths` for the public data +
  bytes route and the static viewer page. Alongside `/api/agent-run`, these are the only holes in the ADR
  0002 gate. **Corrected by [ADR 0042](0042-wildcard-easy-auth-exclusions-and-forwarded-proto.md):** these
  shipped as the bare entries `/api/share` and `/shared`, which was wrong — `excludedPaths` matches a path
  **exactly** unless the entry ends in `/*`, so every URL a recipient actually loads stayed behind the login
  page and this feature was non-functional in production. The correct list is `/api/share/*`, `/shared` and
  `/shared/*`.
- **Public viewer** (`wwwroot/shared/`): a self-contained page (`index.html` + `viewer.js` + `viewer.css`
  + copies of `marked`/`purify` under `shared/lib/`) that reads the token from the URL **fragment**, calls
  `GET /api/share/{token}`, and renders turns with a trimmed copy of `renderTurns`. Self-contained on
  purpose so the public page loads nothing from the authenticated surface — the Easy Auth hole stays
  confined to the `/shared` and `/api/share` route families (ADR 0042 for the exact entries, and for the
  test that now machine-checks this self-containment claim).
- **Owner UI**: a hover-revealed **↗ Share** action on every sidebar conversation row (`buildConvRow`,
  shared by regular and agent chats) opens `#shareLinkDialog` — create / copy / revoke. Distinct names
  from the ADR 0023 Web Share Target (`#shareDialog`, `initShare`, …), which is inbound and unrelated.

**Two deliberate differences from ADR 0022:**
1. **The raw token is persisted** (in `UserShareLinksGrain`), not just its hash. The owner re-displays and
   copies the link repeatedly, and it is a low-sensitivity, single-conversation, read-only capability the
   owner already fully controls — unlike a one-time-shown bearer API key.
2. **Revoke deregisters the directory FIRST**, then drops the list entry (the *opposite* of the ADR 0022
   delete order). Unsharing does **not** delete the conversation, so there is no existence re-check to
   backstop a missed deregister — deregistering first means a mid-failure leaves the link **dead**, never a
   live revoked link.

## Alternatives considered
- **Keep it behind Easy Auth (allowlisted users only).** Rejected: useless for sharing with anyone outside
  the ~2-user tenant, which is the whole point.
- **Snapshot the conversation at share time.** Rejected: the live view is simpler (the directory points at
  the live grain) and revocation is the control; a frozen copy adds state and a staleness question.
- **Widen `excludedPaths` to `/lib` + `/styles.css` and reuse them in the viewer.** Rejected: copying the
  two vendored libs into `shared/lib/` keeps the auth hole to two tight route families and makes the public
  page provably independent of the authenticated surface. Accepted cost: keep the copies in sync on upgrade.
  (ADR 0042 machine-checks that independence: every same-origin URL the viewer requests is run through the
  measured `excludedPaths` matcher, so pointing it back at `/lib` or `/styles.css` now fails the build.)
- **Expiry timestamps.** Deferred (non-goal): the user asked for revocable-without-expiry; expiry is an
  additive field on `ShareLink` + a check in the endpoint if ever wanted.

## Consequences
- **Headline egress (extends ADR 0013):** a share token exposes that conversation's **full current
  history** — text, images, and anything it contains (the user's street address per ADR 0012,
  document-derived assistant text, web-fetched content) — to **anyone holding the link, outside the Entra
  tenant**. The view is **live** (future turns appear until revoked); **revocation is the only control**.
  *(No longer literal since ADR 0080: a turn's **tool calls** are projected onto `ChatTurn` for the owner
  and stripped from this response. Their arguments and results carry search queries, fetched URLs and MCP
  payloads, and widening a public link to include them was declined. They are the first `ChatTurn` content
  deliberately withheld here; anything added must say which side of that line it falls on.)*
  Minting requires an explicit press, and deleting a conversation also revokes its link.
- The public routes are a new **inbound** surface and, like `/api/agent-run`, must forever self-enforce +
  fail closed; the ingress carve-out is a deploy-time property (not unit-testable — TestServer has no Easy
  Auth). The 256-bit token + hash-keyed lookup means no enumeration.
- No new Cosmos container, no new secret, no new both-hosts DI registration (the grains auto-activate,
  unlike the `IMcpSecretProtector` / `PushConfig` route-matcher services). Every share-link write stays a
  bounded item under the 2 MB cap (ADR 0004).
