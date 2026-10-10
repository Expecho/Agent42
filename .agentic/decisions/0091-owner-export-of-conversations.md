---
id: 0091
date: 2026-10-10
status: accepted
tags: [conversations, export, api, frontend, egress]
supersedes: []
extends: [0035, 0034]
amends: []
---

# The owner can export conversations as Markdown, JSON or a ZIP, one at a time or all in parts

## Context
Share links (ADR 0035) only work online, and they strip tool calls on purpose. The owner had no way to keep or
archive a copy of their own chats. The 2026-10-10 feature review asked for four forms: Markdown, JSON with full
fidelity, a ZIP with the files, and "export all".

## Decision
- **`GET /api/conversations/{id}/export?format=markdown|json|zip`** (default `markdown`; unknown → 400). The JSON
  (`schema: agent42.conversation-export/1`) holds the conversation info, `historyMode`, the model, the agent binding,
  the shared state (ADR 0089), the artifact metadata, and **`turns` exactly as `/messages` returns them**. In ZIP mode
  a separate `files` map gives each file's path in the archive, so no record has to be extended to carry it. The ZIP
  holds `conversation.md`, `conversation.json`, `attachments/…`, `artifacts/{id}/v{n}.{ext}` and `manifest.json`.
  The manifest lists blobs that are gone (`missing`) instead of failing.
- **`GET /api/export?after={id}`**: "export all", **one ZIP part of up to 25 conversations per request**
  (`ExportLimits.BulkBatchSize`). It is keyset-paged on the conversation id. The headers `X-Export-Total`,
  `X-Export-Part`, `X-Export-Parts` and `X-Export-Next` are set before the body. `src/Agent42.Backend/wwwroot/js/export.js` follows
  `X-Export-Next` and saves each part. A conversation whose gather fails goes into the manifest under `errors`, and
  the part carries on.
- **Tool calls are kept**, unlike in the public share response (ADR 0035/0080). That strip exists because widening a
  *public* link would be third-party egress (ADR 0013). This is the owner's own `/messages` projection, handed to the
  owner behind Easy Auth. **It is not an ADR 0013 egress**, so don't "fix" export to match the share strip.
- **The index is the gate.** Every route resolves the id through the caller's `IConversationIndexGrain` first. An
  id that isn't listed is a 404 before any `IAgentGrain` is activated. That one check covers an incognito chat (never
  indexed, ADR 0034), another user's id, and an id that was never used.
- **Artifact versions are exported as raw source.** The extension comes from the kind (`html`, `svg`, `md`, and `txt`
  for code). `ArtifactTheme` is never applied.
- **The UI**: the share dialog, renamed "Share or export", gets three download links, so they inherit its gate (a
  persisted, non-empty, non-ephemeral chat, across all four topbar transitions). Preferences → General gets "Export
  all conversations".

## What is not in an export
- **Reasoning.** It is stripped before storage (ADR 0071), so there is nothing to export. Making it exportable would
  mean storing it, with the 2 MB-session consequences of ADR 0004. That is a separate decision.
- **Uploaded document bytes.** Documents are chunked and indexed, never stored (ADR 0007).
- **A continuous agent's history beyond 24 h** (ADR 0068). The Markdown header says so, and the JSON has
  `historyMode: "continuous"`.
- Per-turn timestamps, the long-term memory (ADR 0056), the Copilot SessionFs transcript (ADR 0085), and group names.

## Alternatives considered
- **One bulk request for everything.** Each conversation costs a grain activation, and activation connects the
  user's MCP servers one after another (10 s timeout each). A large account would exceed ACA's 240 s request
  timeout. This is the reason `/api/search/reindex` is batched too.
- **Offset paging like reindex's `skip`.** If the list gains or loses a conversation during the walk, offset paging
  skips or repeats one. Reindex can live with that because it is idempotent. An export that silently drops a chat
  cannot.
- **A background job that writes to blob and sends a push link.** It needs new state, a retention policy for the
  blobs, and a second download surface, all for a personal-scale account. Paging gets the same result with none of
  those.
- **Assembling the ZIP in the browser.** That needs a ZIP library, but the frontend has no build step (ADR 0041). It
  would also fetch every image again through the proxy.
- **A separate topbar export button.** It would have to work out the share dialog's gate again, and that gate
  already has a history of a missed transition.

## Consequences
- **The archive is built in a disk-backed buffer and then drained to the response. It is not streamed directly.**
  Even the async `ZipArchive` APIs dispose each entry's stream synchronously (see gotchas), and Kestrel disallows sync
  IO. `FileBufferingWriteStream` keeps up to 1 MB in memory and spills the rest to a temp file. A failure while
  building returns a plain 500. A failure while draining aborts the connection, so the download fails visibly
  instead of leaving a truncated archive.
- **An exported `.html` artifact opened from disk runs its script at a `file://` origin, without the CSP `sandbox`
  header that protects `/frame` (ADR 0084).** This is an accepted residual: it is the owner's own download, there is no
  app origin, cookie or API in reach, and `/content` already serves the same bytes.
- Export isn't available while a turn is streaming: `openShareLink` returns early when `streaming` is set.
- Gathering reads the same five grain getters the UI already uses. There is no export-specific grain method and no
  persisted-state change.

## Tests
`ConversationMarkdownTests` (renderer, fences, names), `ConversationExportApiTests` (formats, 404 gate, missing blob,
entry names, the bulk keyset walk with insert/delete during the walk, owner scoping), and
`ConversationExportAssetsTests` (dialog links, export-all wiring, SW `SHELL`).
