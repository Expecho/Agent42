# Plan — Fix two upload-awareness failure modes

`planned-against: main @ a809504`

## Overview

Two frontend defects let an upload silently fail to take effect in the conversation the
user is looking at — both surfaced during the document-RAG diagnosis that produced the
empty-document warning chip (PR #26). Fix them so the user always gets truthful feedback
about whether an uploaded image/document actually landed:

1. **Silently-swallowed non-OK uploads** — `handleFiles` does `if (!res.ok) continue;`, so a
   rejected upload (unsupported type, oversize, or a 500 from a failed embedding call) shows
   **no chip and no error**. The user believes the file attached.
2. **Upload result landing in the wrong conversation** — an upload is keyed to `activeId` at
   fetch time, but its result is applied to whatever `activeId` is current when the `await`
   resolves. Switching conversations mid-upload injects the chip/attachment into the new
   conversation while the bytes were stored under the old one.

Mostly frontend (`app.js` + `styles.css`), plus **one minimal backend change**: the upload
endpoint's error bodies must be emitted as `text/plain` so the client can display them
truthfully (see F1 in Review).

## Affected areas

- `src/Agent42.Backend/wwwroot/app.js` — `handleFiles`, `renderAttachmentPreview`,
  `clearPendingAttachments`, and the pending-state declarations (lines ~48–56, 446–532).
- `src/Agent42.Backend/wwwroot/styles.css` — a new error chip variant next to
  `.attach-chip--warn` (~line 920+).
- `src/Agent42.Backend/AgentApi.cs` — the three upload-rejection responses
  (`AgentApi.cs:103,108,144`), switched from `Results.BadRequest(string)` (which JSON-encodes)
  to a `text/plain` body.
- `tests/Agent42.Tests/PwaAssetsTests.cs` — source-text asset tests (the repo has no JS
  runner by design). Check `tests/Agent42.Tests/AgentApiTests.cs` for any assertion on the
  rejection body/content-type and update it to match the new `text/plain` shape.

## Context & constraints

*Judgment and evidence the executor can't cheaply re-derive. Read the code at these paths;
this section does not restate it.*

- **Mode A evidence.** `app.js:455` `if (!res.ok) continue;` drops failures silently. The
  server has actionable messages — `"No file was uploaded."`, `"Upload exceeds the 10 MB limit."`,
  `"Only image and document (PDF, Office, text) attachments are supported."`
  (`AgentApi.cs:103,108,144`) — but returns them via `Results.BadRequest(string)`, which
  **JSON-encodes** the value, so the raw body is quote-wrapped (`"No file was uploaded."`) with
  `Content-Type: application/json` — the mirror of the `gotchas.md` entry "bare string returns
  serve text/plain, not JSON". A prod **500** (e.g. `IngestAsync` throwing on a failed embedding
  call) has an empty body, not one of these strings. So neither a naive `res.text()` (shows
  quotes for 400s) nor `res.json()` (throws on the empty 500 body, re-swallowing the failure)
  is right on its own. **Resolution (F1):** change the three server rejections to a `text/plain`
  body (`Results.Text(message, "text/plain", statusCode: 400)` or equivalent) so the body is a
  clean string; the client then reads `const message = (await res.text()).trim()` and falls back
  to a generic `"Upload failed."` when the body is empty (covers the 500). This app registers no
  ProblemDetails middleware (`Program.cs`), so a 500 body is empty, not JSON.
- **No notification system exists.** The frontend has only a subtle mic-permission hint and
  `console.warn` (`app.js:929,981`) — there is no toast/banner. **Decision:** surface upload
  failures through the **attachment strip** as a dismissible error chip, reusing the exact
  pattern the just-merged warn chip uses (`renderAttachmentPreview` branch + a
  `.attach-chip--*` style). This keeps the change additive and avoids introducing a
  notification subsystem (rejected as scope creep). The warn-chip work in PR #26 is the
  **exemplar to copy**: `renderAttachmentPreview` (`app.js`), `.attach-chip--warn`
  (`styles.css`), and `Frontend_WarnsWhenUploadedDocumentYieldedNoText` (`PwaAssetsTests.cs`).
- **Mode B is a race, not a normal-flow bug.** `activeId` is set at `app.js:42,294,620,663`;
  **every** conversation-switch path calls `clearPendingAttachments()`, which clears both
  pending arrays (`app.js:530-532`), and all three switch entry points early-return while
  `streaming` — `startAgentChat` (`app.js:293`), `selectConversation` (`app.js:619`),
  `startNewConversation` (`app.js:662`); `deleteConversation` reaches a switch only via
  `startNewConversation` (`app.js:219`). `send()` sets `streaming = true` synchronously
  (`app.js:698`) before its first `await`, so a switch can't happen during a send, and a stale
  chip can't persist across a *completed* switch. The remaining hole: `handleFiles` is **not**
  guarded against a switch during its own in-flight `await` — the `fetch` uses `activeId` live
  (`app.js:454`) and the result is pushed live (`app.js:458`). **Fix:** capture
  `const uploadId = activeId` at function entry, use `uploadId` for the fetch URL, and after
  each `await` bail if `activeId !== uploadId` (the user moved on) — via a `break`/flag, not a
  bare `return`, so the post-loop `fileInput.value = ""` reset still runs (see F2). This both
  targets the upload deterministically and discards a result that would land in the wrong
  conversation.
- **No ADR conflict.** ADR 0004 (attachments-in-blob) and 0007/0008 (document RAG) govern the
  data path, not upload error/render UX. This change is additive; no `/decide` needed.
- **No JS test runner (gotcha).** Per `.agentic/gotchas.md` (speech entry) the repo tests
  frontend assets as **C# source-text heuristics** served through `StaticAssetsServerFixture`
  (`PwaAssetsTests.cs`). Assertions prove the branch/guard *exists*, not its runtime behavior —
  the accepted convention here. Coverlet's `coverage_new_code_pct_min` therefore applies to
  the C# surface only; the JS change is covered by the asset heuristics, so flag JS coverage
  as N/A in the gate self-report rather than treating it as a miss.

## Steps

### Step 1 — Mode B: guard `handleFiles` against a mid-upload conversation switch
- **Files:** `src/Agent42.Backend/wwwroot/app.js` (`handleFiles`).
- **Do:** capture `const uploadId = activeId;` at the top of `handleFiles`; build the request
  URL from `uploadId` (`attachmentsUrl(uploadId)`); after the `await fetch` (and after
  `await res.json()`), if `activeId !== uploadId` stop applying results — do not push into
  `pendingAttachments`/`pendingDocuments`. **Use a `break`/flag, not a bare `return`**, and/or
  wrap the loop so the existing `fileInput.value = ""` reset (`app.js:465`) always runs (a
  `return` from inside the loop would skip it and block re-picking the same file — F2). Keep
  the existing per-file try/catch.
- **Verify:** `PwaAssetsTests` (Step 4) asserts `/app.js` contains the `uploadId` capture and
  an `activeId !== uploadId` guard. Run
  `dotnet test Agent42.slnx --filter FullyQualifiedName~PwaAssetsTests`.

### Step 2 — Backend: emit upload-rejection bodies as `text/plain` (F1)
- **Files:** `src/Agent42.Backend/AgentApi.cs` (the three rejections at `:103,:108,:144`);
  check `tests/Agent42.Tests/AgentApiTests.cs`.
- **Do:** change `Results.BadRequest("<msg>")` to return the message as a `text/plain` body
  with the 400 status (e.g. `Results.Text("<msg>", "text/plain", statusCode: StatusCodes.Status400BadRequest)`),
  so `res.text()` on the client yields a clean, unquoted string. Grep `AgentApiTests.cs` for
  any assertion on these responses (status/body/content-type) and update to the new shape.
- **Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentApiTests` green.

### Step 3 — Mode A: surface non-OK uploads as an error chip
- **Files:** `src/Agent42.Backend/wwwroot/app.js` (state decl, `handleFiles`,
  `renderAttachmentPreview`, `clearPendingAttachments`), `src/Agent42.Backend/wwwroot/styles.css`.
- **Do:** replace `if (!res.ok) continue;` with: `const message = (await res.text()).trim() || "Upload failed.";`
  then push a synthetic notice into a new `pendingNotices = []` array
  (`{ fileName: file.name, message }`) and `renderAttachmentPreview()`. In
  `renderAttachmentPreview`, render each notice as a dismissible chip — classes
  `attach-chip attach-chip--doc attach-chip--error`, a **label span with class
  `attach-chip__doc`** (required for the color rule below to apply) reading
  `⚠️ <file> — upload failed`, `title` = the server message, and an × button that removes it.
  Clear `pendingNotices` in `clearPendingAttachments` and include it in the
  `attachmentPreview.hidden` emptiness check. Apply the Step-1 `uploadId` guard to the notice
  push too (a failed upload for an abandoned conversation must not chip the new one). Add
  `.attach-chip--error` to `styles.css` next to `.attach-chip--warn`: `border: 1px solid var(--red)`,
  and `.attach-chip--error .attach-chip__doc { color: var(--red); white-space: normal; }`.
- **Verify:** `PwaAssetsTests` asserts an `attach-chip--error` render on the `!res.ok` branch
  and `/styles.css` contains `.attach-chip--error`. Same filter as Step 1.

### Step 4 — Extend the asset tests (written alongside Steps 1–3)
- **Files:** `tests/Agent42.Tests/PwaAssetsTests.cs`.
- **Do:** add focused `[Fact]`(s): Mode-B — assert `/app.js` contains `uploadId` and
  `!== uploadId`; Mode-A — assert `/app.js` renders `attach-chip--error` on the failure branch
  and no longer contains the bare `if (!res.ok) continue;` swallow, and `/styles.css` contains
  `.attach-chip--error`. **Tie assertions to the observable render (`attach-chip--error`), not
  to the specific body-read call** (F4). Match the existing comment idiom ("source-text
  heuristic … no JS test runner by design").
- **Verify:** `dotnet test Agent42.slnx` (full suite) green; `dotnet format --verify-no-changes`
  clean.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Proving test | File |
|---|----------------------|--------------|------|
| AC1 | A non-OK upload is surfaced to the user (visible, dismissible), not silently dropped | asset heuristic: app.js renders `attach-chip--error` on the `!res.ok` branch (bare `continue` gone); styles.css has `.attach-chip--error` | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC2 | An upload result returning after a conversation switch is not applied to the new conversation | asset heuristic: app.js captures `uploadId` and guards `activeId !== uploadId` | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC3 | Upload-rejection messages reach the client as clean text (not quote-wrapped JSON) | AgentApiTests asserts the rejection is `text/plain` with the unquoted message | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC4 | Existing image/document happy path + empty-document warn chip unchanged | existing tests stay green | `PwaAssetsTests.cs` (warn test), `AgentApiTests.cs` (upload tests) |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80` — applies to the C# surface; the JS change is covered by the
  source-text asset heuristics (no JS runner by design), so self-report JS as N/A, not a miss.
- `cognitive_complexity_max: 15`
- `dependencies.vulnerable_packages: 0`
- CI hard blocks: build, tests, `dotnet format`, vulnerable packages, doc-path integrity.
- Branch: `fix/*` off `main`; MR required (main is protected).

## Out of scope

- **Server-side cleanup of orphaned ingested chunks** for a conversation that was uploaded to
  but never sent (never `TouchAsync`'d into the index, so never deletable via the UI). A real
  but separate privacy/storage concern needing a backend change — not this frontend fix.
- **Blocking conversation switches during an upload** (an `uploading` flag mirroring
  `streaming`). Rejected in favour of capture-and-discard: lighter UX, lets the user keep
  moving while an upload finishes.
- Retry UI for failed uploads; auto-dismiss timers.
- In-app OCR for scanned PDFs (separate feature/ADR).
- Any backend change **beyond** the `text/plain` error-body switch in Step 2 (no new
  endpoints, no change to the upload success path or the ingestion pipeline).

## Review

Critic pass (fresh-context `critic` subagent) — verdict **changes recommended**, all findings
accepted and folded in:

- **F1 (HIGH) — accepted.** `Results.BadRequest(string)` JSON-encodes, so error bodies are
  quote-wrapped and a 500 has an empty body; a naive `res.text()` would show quotes and a
  `res.json()` would throw on the 500 and re-swallow it. Fixed by adding **Step 2** (server
  emits `text/plain` error bodies) and a robust client read with a generic fallback (Step 3).
  This relaxes the original "frontend-only" framing — now recorded in Overview/Affected areas.
- **F2 (MEDIUM) — accepted.** A guard `return` inside the loop would skip the post-loop
  `fileInput.value = ""` reset and block re-picking the same file. Steps 1 & 3 now require a
  `break`/flag (or a `finally`) so the reset always runs.
- **F3 (LOW) — accepted.** Corrected drifted line citations (`:455` swallow; streaming guards
  at `:293/:619/:662`; delete→switch via `:219`).
- **F4 (LOW) — accepted.** AC1 / Step 4 now tie the test to the observable `attach-chip--error`
  render, not the literal `res.text()` token.
- **Also folded in:** the error chip's label span must carry class `attach-chip__doc` for the
  `--red` color rule to apply (noted in Step 3).

Critic confirmed sound (no change): the Mode-B race fix is correct and complete (`send()` is
genuinely switch-safe), no ADR conflict, the `.attach-chip--error`/asset-test shapes match the
merged warn-chip exemplar, and `planned-against: a809504` is a valid ancestor of HEAD whose
intervening commits don't touch the upload path.

## Implementation notes (divergence from plan)

Executed on branch `fix/upload-awareness-failure-modes`. HEAD matched `planned-against: a809504`
exactly at execution time — no drift. Two deviations from the plan text, both benign:

- **AC3's rejection assertion landed in `AttachmentTests.cs`, not `AgentApiTests.cs`.** The plan's
  Affected-areas and AC→test table pointed at `AgentApiTests.cs`, but the pre-existing upload-rejection
  test (`Upload_RejectsUnsupportedTypeAndOversize`) actually lives in `AttachmentTests.cs`
  (`AgentApiTests.cs` only holds the document-*accept* tests). The assertion was strengthened there
  in place — asserting `text/plain` content-type and the raw unquoted body for both the unsupported
  and oversize rejections — rather than duplicating a new test in the wrong file. AC3 is covered.
- **Same-MR doc-debt fix (not in plan):** the `gotchas.md` entry "A browser's upload Content-Type is
  unreliable for documents" quoted the now-deleted `if (!res.ok) continue;` silent-swallow behavior
  present-tense. Updated that clause to reflect the new error-chip surfacing (CLAUDE.md hard rule 2 —
  fix invalidated doc claims in the same MR). The critic surfaced this; the plan hadn't anticipated it.

Everything else was implemented as written. F1/F2/F4 resolutions all honored (verified by the critic).
Quality gate: `AgentApi.cs` coverage 98.81% (coverlet); duplication/cognitive-complexity gates
SKIPPED (no tooling in repo — only coverlet, per `quality-gates.yaml`); JS coverage N/A (source-text
asset heuristics, no JS runner by design).
