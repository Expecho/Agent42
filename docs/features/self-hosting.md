# Self-hosting

> Run Agent 42 on your own machine with `docker compose up`, with no Azure account. A second storage provider,
> `Storage:Provider = local`, keeps everything in Postgres and on a volume (ADR 0127). Production still runs the default,
> `azure`, unchanged.

## Run it

```bash
cp .env.example .env          # set POSTGRES_PASSWORD and SEARXNG_SECRET
docker compose up -d --build
```

Open <http://localhost:8080>. Then add a model provider under Preferences, because no chat, embedding, image or speech model
ships with the app (ADR 0019): until you add one the composer is gated.

`compose.yaml` starts four things:

| Service | What it is |
|---|---|
| `app` | The Backend image (`src/Agent42.Backend/Dockerfile`), bound to `127.0.0.1:8080` |
| `postgres` | `pgvector/pgvector:pg17`: grain state, reminders, document vectors, chat search, the gateway lock |
| `searxng` | The web-search sidecar built from `infra/searxng` |
| `init-volumes` | A one-shot that makes the volumes writable for the app's non-root user, then exits |

Data lives in three named volumes: `agent42-postgres`, `agent42-data` (attachments, uploaded skills, fleet files, Copilot
session files, the Data Protection key ring) and `agent42-copilot` (chat workspaces for the Copilot agent kind).
`docker compose down` keeps them; `docker compose down -v` deletes everything.

## What is different from the Azure deployment

| | `azure` (production) | `local` (compose) |
|---|---|---|
| Grain state and reminders | Cosmos DB | Postgres, through Orleans' ADO.NET providers; the same `$type` JSON |
| Documents (RAG) | Cosmos vector search | pgvector, exact cosine scan per owner, **no ANN index** |
| Chat search | Cosmos full text (BM25) | Postgres full text (`ts_rank_cd`), ranks differently |
| Attachments, skills, fleet, Copilot files | Blob Storage | files under `Storage:Local:Root` |
| Model sees an attached image | a short-lived SAS URL | the app inlines the bytes into the request |
| Chat gateway lease | Blob lease | Postgres advisory lock |
| Sign-in | Entra ID Easy Auth | **none** (every request is `local-dev`) |

## Limits and things to know

- **No authentication.** Every request is the single user `local-dev`. The compose file binds the app to loopback and the
  app logs a warning at startup. To reach it from elsewhere, put a reverse proxy that authenticates in front. Do not publish
  port 8080 as it is.
- **Images are sent as bytes.** A hosted model cannot fetch a URL on your machine, so each request carries the images inline.
  An image over 4 MB, or images beyond 16 MB in one request, are replaced by a short text note, oldest first, and the chat
  carries on. Tune with `Storage:Local:InlineImageMaxBytes` and `Storage:Local:InlineImagesTotalBytes`.
- **Secrets at rest.** Providers' API keys are encrypted with the Data Protection key ring, but the ring itself sits
  unencrypted in `agent42-data` (as in Azure, where storage encryption is the only protection). Protect that volume and
  your backups of it.
- **No data migration** between `azure` and `local`. Pick one per deployment.
- **Document search scans.** Without an ANN index, retrieval reads every chunk of the one conversation it searches. That is
  fine for a personal agent. The vector size is `Storage:Local:DocChunksDimensions` (default 3072); changing it needs a new
  database.
- **Copilot built-in shell is off.** The Copilot agent kind's shell, file and URL tools run inside the app container with no
  prompt, so with no authentication in front they could read the database password and the key ring. Compose sets
  `Copilot__BuiltInToolsEnabled=false`; the Copilot kind still works with Agent 42's own tools (ADR 0085). Flip it only behind
  an authenticating proxy.
- **One replica.** Run one `app` container per database (the gateway lock stops a second one from running your bots twice).
- **Web search is not private.** SearXNG forwards each query to public search engines.
- **Signal is off** (it needs the `signalEnabled` deploy gate and an Azure Files volume). Telegram, Slack and Discord work.

## Settings

| Key | Meaning |
|---|---|
| `Storage__Provider` | `local` to self-host; absent or `azure` is production. Anything else fails at startup |
| `Storage__Local__Root` | The directory the file stores write under (required with `local`) |
| `ConnectionStrings__postgres` | The Postgres connection string (required with `local`) |
| `WebSearch__Endpoint` | The SearXNG address, `http://searxng:8888` in compose (required) |
| `Copilot__BuiltInToolsEnabled` | `false` in compose: no shell for Copilot chats (see Limits) |
| `Copilot__WorkspaceRoot` | Where Copilot chats keep their working directory |
| `Access__Owners__0` | `local-dev`, so the dashboard and `/logs` open for you |

The Configuration tab (Preferences) shows the provider and marks the Azure-only keys as not applicable.

## See also

- [`.agentic/decisions/0127-self-hosting-local-storage-provider.md`](../../.agentic/decisions/0127-self-hosting-local-storage-provider.md)
- [`configuration-health.md`](configuration-health.md)
- [`image-attachments.md`](image-attachments.md)
