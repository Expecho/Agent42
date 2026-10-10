---
id: 0092
date: 2026-10-10
status: accepted
tags: [conversations, grain, session, search, artifacts, attachments, frontend, no-migration]
supersedes: []
amends: [0087]
---

# Edit and regenerate the last turn in place, and fork a conversation into a new one

## Context
The chat had no way to fix a typo in the last message, get a fresh reply, or branch from an earlier point.
The API had no endpoint that truncates or copies a conversation. Mutating the session in place already has
two precedents: the ADR 0068 retention prune and the ADR 0078 stopped-turn append both edit the
`TryGetInMemoryChatHistory` list. Everything the session does not hold has to be maintained by hand:
`TurnAttachments`, `GeneratedImages`, artifact versions, the incognito ordinals and the search index. The user
chose the hybrid model on 2026-10-10.

## Decision
- **Edit and regenerate truncate the last whole session turn in place, then re-run it.**
  `POST /api/chat/{id}` takes trailing `replaceLastTurn` / `expectedUserTurnCount` /
  `expectedLastUserTextHash`. The truncation runs as late as possible: after the persona lookup, the index touch
  and the bind, right before the stream starts. It is written at once. It stays in memory only
  when the turn is incognito AND the conversation has never been written; a conversation with a stored record
  always takes the persisted path, whatever the client's flag says.
  A refusal is a 409 with no stream; a half token is a 400.
- **Fork never touches the source.** `POST /api/conversations/{id}/fork` returns 201 with a server-generated
  id. The fork cuts a COPY of the session after a whole exchange. The new grain copies every kept blob (user
  images, generated images, artifact versions) under its own owner key with the same id. It copies the persona
  snapshot verbatim, and it is listed only after its session is written, with the source's group and agent
  label and the title `"<source> (fork)"`, capped first so the 60-char index cap never cuts the suffix.
- **Concurrency token = projected user-turn count + hex SHA-256 of the last projected user text.** The browser
  computes the hash with `crypto.subtle`. Every client position maps to a session position through
  `ProjectHistory()`, the one walk that also feeds `GetHistoryAsync`.
- **Refusals:** a run in flight (checked first, before `VisibleHistory` can hide a research turn), a Copilot
  chat, a stale token, a blank last session user turn, and (truncate only) no configured model. Fork also needs
  a source that is **listed**, which is one check that refuses incognito, unknown and other users' ids.
  It also refuses a source that served an incognito turn on its current activation, which would otherwise
  persist that turn into the fork. A blob that is missing or cannot be copied is logged and never fails the
  fork: an image keeps its id (and 404s, as the source's would), and an artifact version is dropped.
- **Artifacts:** the `(id, version)` pairs harvested from create/update results in the removed or excluded
  messages are dropped, and a record left empty goes with them. A new version is numbered after the LAST
  version, not after the count, so a fork that dropped a dangling version never reissues a number.
- **Not copied into a fork:** reminders, RAG documents and chunks, web documents, both fetch allowlists
  (they are a security control and fail closed), shared state, the share link and the pin.

## Alternatives considered
- **Fork-only (edit = fork + rewrite).** Rejected by the user: a typo fix should not leave a second conversation
  behind every time.
- **A branch tree on one conversation ("< 2/3 >").** Rejected: it needs a persisted tree in a session that ADR
  0004 caps at 2 MB, plus a new projection for search, recovery and sharing. A fork gives a separate, ordinary
  conversation at no new storage shape.
- **A deferred write of the truncation.** Rejected: the in-memory session stays truncated, and the next
  unrelated write would persist it anyway. That is less predictable, not safer.
- **Snapshot the session and restore it if the following run fails.** Rejected: the snapshot would have to live
  across two grain calls, which means a new turn-scoped field with the rule 6 reset ritual at every run site.
  That is too much for a failure the client already recovers from by putting the text back in the composer.
- **A count-only token.** Rejected: it can be replayed. A second tab loaded at count N and sending after an edit
  restored the count to N would silently replace the freshly generated turn.
- **A total-turn (ordinal) token.** Rejected: the total differs between a live and a reloaded render (one
  exchange can project to several assistant turns), so the DOM cannot compute it.
- **A fork that references the source's blobs.** Rejected: the attachment proxy and the artifact routes resolve
  a blob by `(ownerKey, id)`, so every image and artifact would 404 in the fork.
- **Cutting a fork mid-exchange.** Rejected: it can orphan a tool result.
- **"Keep only the artifacts retained history references."** Rejected: a continuous chat's retention prune
  already drops early turns while their artifact records stay, so that rule would delete live artifacts.

## Consequences
- **Narrows ADR 0087's rejected alternative.** ADR 0087 rejected "rewind or replace the failed turn on the
  server" as high risk, because it rewrites the stored session of a grain whose history can grow without bound.
  This feature does rewrite it. The risk is acceptable here because the rewrite is user-initiated and explicit,
  removes whole turns from the tail only (the exact mirror of the ADR 0068 prune), is refused while any run is
  in flight, and is guarded by a concurrency token. ADR 0087's own automatic re-send is unchanged; moving it
  onto `replaceLastTurn` is a possible follow-up.
- A truncation re-projects the whole conversation into the search index (`pruned: true`) before the first
  stream byte, because there is no delete-by-ordinal. This is bounded by the 2 MB session.
- **Residuals:**
  - An edited or regenerated turn's original text stays in long-term memory (ADR 0056 has no delete path).
  - Reminders a removed turn scheduled keep firing until they are cancelled.
  - A URL from edited-away user text stays fetchable in that conversation.
  - Blobs of removed turns and dropped versions are orphaned (the existing purge gap).
  - A share link (ADR 0035, a live view) shows the truncation.
  - Editing the first message does not retitle the conversation.
- A Copilot chat (ADR 0085) refuses all three operations until the SessionFs transcript and the grain mirror
  can be truncated together.
- No persisted shape changes: `ConversationFork`, `ConversationForkResult` and `HistoryEditOutcome` cross the
  grain boundary only.
- Spec: `.agentic/specs/completed/plan-edit-regenerate-fork.md`.
