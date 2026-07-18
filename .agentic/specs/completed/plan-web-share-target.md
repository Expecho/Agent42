# Plan — Web Share Target for the Android PWA

`planned-against: main @ 7f3a033`

## Overview

Let an installed Agent 42 PWA appear in Android's system **Share sheet** and receive shared
**text, URLs, images, and documents**. A shared payload lands in a **destination the user picks
per share** — a new regular chat, or a chosen agent (regular or continuous). The shared content is
**prefilled into the composer (text/URL) and attached (images/documents)**, then the user reviews and
presses Send — it is **not** auto-sent (deliberate: sending can egress outside the Entra tenant on a
third-party model — see Constraints).

Why now: the PWA is already installable and, as of the merged tools-icon work, the frontend/attachment
pipelines are stable. The feature is almost entirely **front-of-house** — it *reuses* the existing
per-file upload endpoint and chat pipeline, so it needs **no new backend endpoint and no Bicep change**.

**Platform reality: Android/Chromium only.** iOS PWAs cannot be Web Share *targets* (they can only
share outward). This is not a bug to fix — it is a platform limit to document, not work around.

## Affected areas (verified against the tree @ 7f3a033)

| File | Change |
|------|--------|
| `src/Agent42.Backend/wwwroot/manifest.webmanifest` | Add a `share_target` block (POST + multipart). |
| `src/Agent42.Backend/wwwroot/sw.js` | New `POST /share` fetch branch + IndexedDB stash helper; bump cache `v6`→`v7`. |
| `src/Agent42.Backend/wwwroot/index.html` | New destination-picker `<dialog>` (mirrors the existing `agent-dialog` pattern). |
| `src/Agent42.Backend/wwwroot/app.js` | On load: detect the share hand-off, read the stashed payload, open the picker, then reuse `startNewConversation`/`startAgentChat` + prompt prefill + `handleFiles`. |
| `src/Agent42.Backend/wwwroot/styles.css` | Minimal styles for the picker (reuse `agent-dialog`/`agent-form` where possible). |
| `tests/Agent42.Tests/PwaAssetsTests.cs` | New source-text assertions (repo has no JS test runner by design). |
| `.agentic/decisions/` | New ADR (via `/decide`). |
| `.agentic/gotchas.md` | New gotcha (via `/gotcha`). |
| `CLAUDE.md` | New "Repo-specific notes" bullet. |

**No backend C# change and no `infra/` change are expected.** If execution discovers one is genuinely
needed, that is drift from this plan — stop and flag it.

## Context & constraints (what the executor can't cheaply re-derive)

**The auth pivot — why the SW handles the POST and nothing goes in `excludedPaths`.** Easy Auth is
`unauthenticatedClientAction: 'RedirectToLoginPage'` (`infra/modules/container-app.bicep:252`). A POST
navigation to a protected path with a stale session would 302 to login and **lose the body** on the
redirect. We avoid this entirely: the **service worker intercepts `POST /share` client-side before it
reaches the network**, reads the `FormData`, stashes it in **IndexedDB**, and responds with a **303
redirect to a GET** landing (`/?share=1`). The POST never hits the server, so Easy Auth never sees it.
The subsequent GET is a normal authenticated navigation — if the session is stale it redirects through
login and back, and the payload survives in IndexedDB. **Consequence: no `/share` server route, no
`excludedPaths` carve-out, no Bicep change** — unlike ADR 0022's `/api/agent-run`, which needed all
three because it is a real server endpoint. (Contrast documented at `.agentic/gotchas.md:994-1010`.)

**Reuse, don't rebuild.** The browser already uploads each file to
`POST /api/conversations/{conversationId}/attachments` via `handleFiles()` (`app.js:636` → upload at
`:647`), which branches image vs document server-side (`AgentApi.cs:105-153`), resolving document media
type by **extension** because browser Content-Type is unreliable (`.agentic/gotchas.md:53-69`). The share
flow calls this **same** `handleFiles()` with the shared `File` objects after a destination is chosen.
Images push to `pendingAttachments`, documents to `pendingDocuments`, and go out on the next turn via
`attachmentIds` (`app.js:1169`). **Nothing new is needed server-side** — images → `IAttachmentStore`,
documents → `DocumentIngestionService` + RAG, all already wired.

**Reuse the destination machinery.** `startNewConversation()` (`app.js:897`) mints a fresh UUID and
resets tools; `startAgentChat(agent)` (`app.js:412`) already handles per-chat vs **continuous** agents
(ADR 0016 — continuous derives `agent-${id}` and resumes). The agent list is loaded at init via
`loadAgents()` → `GET /api/agents` (`app.js:345`). The picker reuses these; it does not reimplement
conversation-id logic.

**The service-worker ordering hazard.** The existing `ServiceWorker_BypassesApiBeforeCaching` test
(`PwaAssetsTests.cs:52-71`) asserts `startsWith("/api")` appears **before the first `respondWith`**. The
new share branch introduces a `respondWith`, so it **must be placed after the `/api` early-return**
(and after the existing same-origin guard at `sw.js:25`), e.g.:
```
const url = new URL(req.url);
if (url.origin !== self.location.origin) return;             // keep the existing same-origin guard
if (url.pathname.startsWith("/api")) return;                 // must stay before any respondWith
if (req.method === "POST" && url.pathname === "/share") { event.respondWith(handleShare(req)); return; }
if (req.method !== "GET") return;                            // moved below the share branch
```
Putting the share `respondWith` above the `/api` guard breaks that test and the ADR 0002 invariant;
dropping the origin guard would let a cross-origin `POST /share` be handled. (Critic M2.)

**The SW-must-be-active assumption.** The share only works when the SW is installed and controlling
(true after install on Android). If it isn't, the POST reaches the server and fails ungracefully. This
is the accepted standard behavior for file-capable share targets — document it as a known limit, don't
engineer a server fallback (a server `POST /share` reintroduces the Easy Auth body-loss problem).

**Egress — this is a new inbound vector.** A shared payload flows through the same pipeline that, on a
third-party model, sends content (document text, image SAS URLs, the prompt) **outside the Entra tenant**
(ADR 0013; withholding-covers-history at `.agentic/gotchas.md:383-411`). Sharing is a new *inbound* door
onto that egress, analogous to ADR 0022's headline consequence. **Review-then-send** (no auto-send) keeps
the user's explicit consent on the send action, and the no-model gate (ADR 0019) still applies — a shared
payload into a model-less chat prefills but cannot send (existing `noModelHint`). *Executor: confirm the
composer send is genuinely gated with no model — the guard at `app.js:1672` keys on prompt/attachment
presence, not `hasModel` — before asserting "cannot send" in the ADR (Critic nit).*

**Scope call made with the user (this session).** Payloads: text, URLs, images, documents (all).
Destination: "Both/configurable" — satisfied by a **per-share picker** (New chat / pick an agent). A
**persisted default destination** (a `UserProfile` setting) is **out of scope for v1** to avoid a profile
schema change; the per-share picker is itself the "configurable" surface.

**Governing docs to read before touching each area:** ADR 0002 (Easy Auth), ADR 0004 (attachments),
ADR 0007/0008 (document RAG), ADR 0013 (egress), ADR 0016 (continuous agents), ADR 0022 (multipart reuse
+ `excludedPaths` contrast), ADR 0019 (no-model gate); gotchas at lines 53-69, 383-411, 816-828, 994-1010.

## Steps

1. **Manifest `share_target`** — `manifest.webmanifest`. Add:
   ```json
   "share_target": {
     "action": "/share",
     "method": "POST",
     "enctype": "multipart/form-data",
     "params": {
       "title": "title", "text": "text", "url": "url",
       "files": [{ "name": "file", "accept": ["image/*", "application/pdf", ".pdf",
         ".docx", ".pptx", ".xlsx", ".txt", ".md", "text/plain", "text/markdown"] }]
     }
   }
   ```
   Keep the `accept` set aligned with the composer `#fileInput` (`index.html:136`).
   *Verify:* new manifest test (Step 6) + `dotnet test` for the PwaAssets collection.

2. **Service worker** — `sw.js`. Add a `handleShare(request)` helper: `await request.formData()`, pull
   `title`/`text`/`url` + all `file` parts, write a single record `{ title, text, url, files:[{name,type,blob}] }`
   into an IndexedDB store (e.g. db `agent42-share`, store `pending`, fixed key), then
   `return Response.redirect("/?share=1", 303)`. Insert the `POST /share` branch **after** the `/api`
   early-return and **before** the `if (req.method !== "GET") return;` line (see the ordering hazard
   above). Bump `CACHE` `agent42-shell-v6` → `agent42-shell-v7`.
   *Verify:* SW share-handler + cache-name tests (Step 6).

3. **Destination picker dialog** — `index.html`. Add a `<dialog class="agent-dialog" id="shareDialog">`
   mirroring the existing dialog/form pattern (e.g. `#toolsDialog` at `index.html:301`): a title, a
   "New chat" default action, and a `<ul id="shareAgentList">` populated from the loaded agents, plus a
   Cancel. No tabs.
   *Verify:* index source-text test (Step 6).

4. **Share flow wiring** — `app.js`. At the end of init (near `app.js:1829-1837`), if
   `new URLSearchParams(location.search).has("share")`: read+delete the IndexedDB record, rebuild `File`
   objects from the stored blobs, strip the `?share=1` param from the URL (`history.replaceState`), and
   open `#shareDialog`. **`await loadAgents()` (or re-render `#shareAgentList` from an agents-loaded hook)
   before opening the picker** — `loadAgents()` (`app.js:1831`) is fire-and-forget, so an eager share-init
   can open the picker before the agent list resolves and render it empty (Critic M1). On a destination
   choice: call `startNewConversation()` or `startAgentChat(agent)`,
   set `promptInput.value` from `text`/`url`/`title` (URL appended if both), trigger the existing textarea
   autosize, and call `handleFiles(sharedFiles)` to run the shared files through the normal upload path. Do
   **not** auto-send. Keep `handleShare`/the init branch under cognitive-complexity 15 (extract helpers).
   *Verify:* app.js source-text test (Step 6); manual DevHost drive of a simulated `?share=1` + seeded
   IndexedDB record (behavioral, not asserted — matches how theme/upload behavior is verified).

5. **Picker styles** — `styles.css`. Reuse `agent-dialog`/`agent-form`; add only what the agent-list rows
   need (mirror the conversation/agent list styling already present). *Verify:* covered by the index/app
   tests + visual DevHost check.

6. **Tests** — `tests/Agent42.Tests/PwaAssetsTests.cs`, following the existing source-text heuristic style:
   - **Manifest**: parse `/manifest.webmanifest`, assert `share_target.action == "/share"`,
     `method == "POST"`, `enctype == "multipart/form-data"`, and that `params.files[0].accept` contains
     `"image/*"` and `"application/pdf"`.
   - **SW share handler**: `/sw.js` contains `"/share"` handling and that `startsWith("/api")` still
     precedes the **first** `respondWith` (extend/repeat the ADR-0002 position assertion so the new branch
     can't regress it).
   - **SW cache bump**: update `ServiceWorker_CacheNameIsCurrent` expectation to `agent42-shell-v7`.
   - **Index**: `/` contains `id="shareDialog"` and `id="shareAgentList"`.
   - **App**: `/app.js` contains the share hand-off read (e.g. `"share"` param check) and reuse of
     `handleFiles` in the share path (assert co-occurrence, mutation-observable — avoid vacuous
     whole-file Contains).
   *Verify:* `dotnet test Agent42.slnx` (the `StaticAssets` collection) green.

7. **Docs (same MR)** —
   - `/decide`: ADR "Web Share Target for the Android PWA" — records the SW-stash-across-Easy-Auth design,
     the no-backend/no-Bicep consequence and its contrast with ADR 0022, review-then-send, the Android-only
     limit, and the egress inbound-vector consequence (references ADR 0002/0013/0022). Add one sentence
     naming sharing a large image/document into a **continuous** agent as a new low-friction path to the
     ADR 0016 unbounded-grain 2 MB brick (`.agentic/gotchas.md:124-144`) — Critic M3.
   - `/gotcha`: the SW ordering hazard (share `respondWith` after the `/api` guard, before the non-GET
     early-return) + the SW-must-be-active assumption, with evidence paths.
   - `CLAUDE.md`: a new "Repo-specific notes" bullet summarizing the feature and its ADR.
   *Verify:* `/audit-docs` clean; doc-path integrity check in CI.

Steps 1–5 are one tightly-coupled frontend surface — implement in order (2 defines the redirect/stash
contract that 4 consumes; 3 defines the ids 4 wires). Step 6 lands alongside each. Not parallelizable
across subagents (single-checkout frontend files).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Proving test | Test file |
|---|----------------------|--------------|-----------|
| AC1 | Manifest declares a POST/multipart `share_target` at `/share` accepting text/url/files incl. images + PDFs | `Manifest_DeclaresShareTarget` (new) | `PwaAssetsTests.cs` |
| AC2 | SW intercepts `POST /share` and the `/api` bypass still precedes any `respondWith` | `ServiceWorker_HandlesShareTarget` (new) + existing `ServiceWorker_BypassesApiBeforeCaching` | `PwaAssetsTests.cs` |
| AC3 | SW cache version bumped so returning users pick up the new shell | `ServiceWorker_CacheNameIsCurrent` (updated to `v7`) | `PwaAssetsTests.cs` |
| AC4 | Destination picker dialog exists in the shell | `Frontend_ShipsShareDestinationPicker` (new) | `PwaAssetsTests.cs` |
| AC5 | App reads the share hand-off and routes shared files through the existing upload path | `Frontend_WiresShareHandoffToHandleFiles` (new) — **structural/weak check only** (Critic): `handleFiles` pervades `app.js`, so this proves the share-init strings *exist*, not that they are wired. Real proof is AC6. | `PwaAssetsTests.cs` |
| AC6 | Behavioral: sharing text/url/image/doc lands in the chosen destination, prefilled/attached, not auto-sent (**this is AC5's real proof**) | Manual DevHost drive (documented, unasserted — no JS runner by design) | — |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ 7f3a033)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80` — note: new logic is **frontend JS**, covered by source-text
  heuristics (repo has no JS test runner by design); expected **new C# is ~zero**, so coverlet has little
  new C# to measure. Call this out honestly in the MR rather than claiming a JS coverage number.
- `cognitive_complexity_max: 15` — keep `handleShare` (SW) and the app.js share init/branch under it;
  extract helpers.
- `vulnerable_packages: 0` — no new dependencies.
- Branch: a `feature/*` branch + MR (never commit to `main` — hard rule 1).
- CI hard blocks: build, tests, `dotnet format`, vulnerable packages, doc-path integrity.

## Out of scope

- **iOS** share-target support (platform-impossible).
- A **persisted default destination** preference (`UserProfile` schema change) — per-share picker only in v1.
- **Auto-send** of a shared payload — review-then-send only (egress consent).
- Any **new backend endpoint**, `/share` server route, or `excludedPaths`/Bicep change — the SW-stash
  design makes them unnecessary; needing one is drift, not a task.
- A **server fallback** for when the SW isn't active — accepted degradation, documented as a limit.
- Sharing **from** Agent 42 outward (Web Share API) — this plan is inbound only.

## Review

A fresh-context `critic` subagent reviewed the plan against the ADRs, gotchas, and real code (all cited
`file:line` claims verified as accurate; `planned-against: 7f3a033` confirmed as HEAD). **Verdict: sound
design, no Critical findings, no ADR contradictions** (0002/0004/0007-8/0013/0016/0019/0022 all checked).
The critic independently confirmed the core mechanism works — the SW-synthesized POST never touches the
network so Easy Auth never sees it, and IndexedDB is origin-scoped so the payload survives the GET login
round-trip — and that declining an `excludedPaths` carve-out is what keeps it clear of the self-enforce
gotcha. It also checked for a continuous-agent attachment race and found none (`selectConversation` clears
synchronously before its first `await`).

Three Minor findings + one caveat, **all accepted and folded in**:
- **M1 (agent-list init race)** → Step 4 now requires `await loadAgents()` before opening the picker.
- **M2 (dropped same-origin guard in the snippet)** → Constraints ordering snippet now keeps the
  `sw.js:25` origin guard above the share branch.
- **M3 (undocumented continuous-agent brick acceleration)** → Step 7 ADR/gotcha now names sharing a large
  file into a continuous agent as a new path to the 2 MB cap.
- **Test-strength caveat (AC5 source-text check is structural/weak)** → AC table now labels AC5 as a weak
  structural check and marks AC6 (manual DevHost drive) as its real proof.
- Two nits accepted: manifest `accept` now includes literal `.pdf`; Step's no-model note flags the executor
  to confirm the composer send is genuinely gated (not just the hint shown).

No findings were rejected.

## Implementation notes (divergence from plan)

Executed on `feature/web-share-target` from `main @ 7f3a033` (no drift — `planned-against` == HEAD).
The implementation followed the plan with **no material divergence**. Details worth recording:

- **All 7 steps landed as specified.** Manifest `share_target`, SW `handleShare` + IndexedDB stash
  (cache `v6`→`v7`), `#shareDialog`/`#shareAgentList` picker, `app.js` share wiring (`initShare` →
  `readSharePayload` → `renderShareAgents`/`applyShare`), `.share-*` styles, and the PwaAssets tests.
- **SW ordering hazard honored exactly.** The fetch handler was reordered to: same-origin guard →
  `/api` guard → `POST /share` branch (first `respondWith`) → non-GET early-return. The two position
  tests (`ServiceWorker_BypassesApiBeforeCaching`, new `ServiceWorker_HandlesShareTarget`) both pass.
- **Critic M1/M2/M3 folded in as planned:** `await loadAgents()` before opening the picker; the
  same-origin guard kept above the share branch; the ADR + gotcha + CLAUDE.md name the continuous-agent
  2 MB brick acceleration.
- **No-model gate confirmed (Critic nit).** Verified `applyModelAvailability` sets
  `sendBtn.disabled = !hasModel` **and** `input.disabled = !hasModel` (`app.js:204-210`), so a share into
  a model-less chat prefills but genuinely cannot send. The ADR states this as confirmed, not assumed.
- **Prefill shape decision (minor, within plan latitude):** `sharePrefillText` joins a shared note and
  URL with a newline when both are present, else falls back to the title. The plan said "URL appended if
  both"; newline-join is the concrete realization.
- **Constants duplicated across scopes by necessity:** `SHARE_DB`/`SHARE_STORE`/`SHARE_KEY` exist in both
  `sw.js` and `app.js` — separate script scopes that share an IndexedDB store, not code. 3 lines; not a
  duplication-gate concern.
- **No backend/infra change** was needed — the SW-stash design held, as the plan predicted (no drift).
- **AC6 (behavioral) remains a manual DevHost drive.** No JS test runner by design and no headless-browser
  automation in this environment; the source-text tests + `node --check` on both JS files are the
  automated floor. To drive manually: run DevHost, open DevTools → Application → IndexedDB, seed
  `agent42-share`/`pending` key `payload` with `{title,text,url,files:[]}`, navigate to `/?share=1`, and
  confirm the picker opens, a destination prefills the composer, and nothing auto-sends.
- **Validation evidence:** full suite `365/365` pass; `dotnet format --verify-no-changes` clean;
  `node --check app.js sw.js` OK; PwaAssets collection `19/19` pass.
