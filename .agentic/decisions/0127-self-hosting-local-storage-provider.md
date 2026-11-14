---
id: 0127
date: 2026-11-14
status: accepted
tags: [storage, self-hosting, postgres, orleans, attachments, docker, no-migration, egress]
supersedes: []
extends: [0001, 0004, 0111]
amends: [0001, 0004, 0111]
---

# A second storage provider: Postgres and a filesystem volume, selected by `Storage:Provider`

## Context
Agent42 was hard-wired to Azure storage: Cosmos DB (grain state, reminders, document vectors, chat search) and Blob
Storage (attachments, skills, fleet files, Copilot session files, the chat-gateway lease, the Data Protection key ring).
Running it needed an Azure account. The goal is `docker compose up` with no Azure account, while production stays
exactly as it is. The user chose Postgres for the database side and to keep both providers.

## Decision
1. **`Storage:Provider` = `azure` | `local`.** Absent means `azure`, so Bicep needs no change. An unknown value fails
   startup rather than falling back (a typo must not point a self-hoster at a Cosmos account they lack).
2. **`local` = Postgres + one filesystem root (`Storage:Local:Root`).** Each provider's wiring is its own static class in
   `Storage/` (`AzureStorageRegistration`, `LocalStorageRegistration`) behind `AddStorageProvider` / `UseStorageProvider`.
3. **Grain state and reminders use Orleans' ADO.NET providers** (`Microsoft.Orleans.Persistence.AdoNet`,
   `Microsoft.Orleans.Reminders.AdoNet`), not an owned `IGrainStorage`. A spike (plan Step 0) showed they write the same
   `$type`/`$id` JSON the Cosmos path does (ADR 0039), as UTF-8 bytes in a `bytea` column, and that a reminder survives a
   silo restart. The packages ship no SQL, so the app embeds Orleans' v10.3.1 Postgres scripts and applies them once under
   an advisory lock (they are not idempotent).
4. **Blob-shaped data is files.** `LocalFileStore` is the only place a key becomes a path (rejects rooted, `..`, empty
   segments, backslash, colon, device names, trailing dot/space); writes are temp + rename. Skills, fleet, Copilot
   sessions and attachments sit on it, keyed as the Azure stores key them. The Data Protection ring persists under the
   same root (ADR 0018).
5. **Attachments: a constant reference URL, rewritten to bytes at request time.** The model read URL is persisted in the
   session (2 MB cap, ADR 0004), so a `data:` URL cannot live there, and a hosted model cannot reach this host. The store
   returns `agent42-attachment://local/{ownerKey}/{id}`; `AttachmentInlineChatClient` (in
   `MultiProviderChatClientFactory.Build`, just above `ToolImageChatClient` and below OTel, local only) swaps it for `DataContent` on the way to the model and
   leaves the persisted session alone. Over a per-image (4 MB) or per-request (16 MB) ceiling, the oldest images become a
   text placeholder and the turn proceeds.
6. **Gateway lease = a Postgres advisory lock on a held connection** (ADR 0111's Blob lease, same contract).
7. **Document index = pgvector `vector(N)` with exact cosine search, no ANN index in v1**: HNSW/IVFFlat cannot index a
   `vector` above 2000 dimensions and the default is 3072. Search is always scoped to one owner, so an exact scan over one
   user's chunks is acceptable for a personal agent. **Chat search = `tsvector`/`ts_rank_cd`**, which ranks differently from
   Cosmos BM25 (accepted; "relevant" is the contract).
8. **No authentication for self-hosters** (its own ADR): every request is `local-dev`, the compose file binds the app to
   `127.0.0.1`, local mode logs a startup warning, and compose sets `Copilot:BuiltInToolsEnabled=false` (ADR 0107): an
   auto-approved shell in the container could otherwise read the database password and the key ring for any caller. **No data migration** between providers.
9. **Egress: none new.** The SearXNG sidecar still forwards queries upstream (gotchas: SearXNG is self-hosted, not private);
   the compose file says so.

## Alternatives considered
- **SQLite.** Rejected: one writer, no vectors without an extension, and Orleans' ADO.NET support is built for server databases.
- **MinIO / S3 for blobs.** Rejected for v1: one more container, and the seams use a handful of blob features. It can still
  come later behind the same ports.
- **An owned `IGrainStorage` + `IReminderTable` over Npgsql.** Rejected: the spike showed the packaged providers meet the
  JSON-parity and reminder requirements, so owning that code buys nothing.
- **A `data:` URL as the read URL.** Rejected: megabytes of base64 in the persisted session breaks the 2 MB cap.
- **An app-served signed image URL for model fetches.** Rejected: needs inbound reachability from the model provider, an
  auth-bypass endpoint and a public base URL.
- **Silent fallback to `azure` on an unknown provider.** Rejected, see Decision 1.

## Consequences
- Both providers must keep the persisted-state rules (trailing `[Id(n)]`, no collection expression into `IReadOnlyList<T>`,
  no `bool = true` defaults): the Postgres path is proved by Testcontainers tests that skip when Docker is absent.
- Local mode has no ANN index and a different search ranking than production. Known limits, documented.
- Local mode is single-replica like production (rule 12); the advisory lock is the guard against a second container on one
  database.
- ADR 0001 (Cosmos persistence), 0004 (Blob attachments, SAS read URLs) and 0111 (Blob lease) describe the `azure` provider
  and are unchanged for it.
