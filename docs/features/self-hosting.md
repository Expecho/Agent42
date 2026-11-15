# Self-hosting

> Run Agent 42 on your own machine with `docker compose up`, with no Azure account. A second storage provider,
> `Storage:Provider = local`, keeps everything in Postgres and on a volume. Production still runs the default,
> `azure`, unchanged.

## Run it

> New to this? [Run locally](../setup/local.md) is the step-by-step guide covering all three local modes. This page is the
> reference for the Compose mode.

```bash
cp .env.example .env          # set POSTGRES_PASSWORD and SEARXNG_SECRET
docker compose up -d          # pulls the published images
```

To build the images from your checkout instead, add the override file:
`docker compose -f compose.yaml -f compose.build.yaml up -d --build`. `AGENT42_VERSION` (default `latest`) pins a release
or a `sha-<commit>` tag, and `AGENT42_REGISTRY` (default `ghcr.io/expecho`) names another registry.

Open <http://localhost:8080>. Then add a model provider under Preferences, because no chat, embedding, image or speech model
ships with the app: until you add one the composer is gated.

`compose.yaml` starts four things:

| Service | What it is |
|---|---|
| `app` | The Backend image (`ghcr.io/expecho/agent42-backend`, built from `src/Agent42.Backend/Dockerfile`), bound to `127.0.0.1:8080` |
| `postgres` | `pgvector/pgvector:pg17`: conversations, reminders, document vectors, chat search, the gateway lock |
| `searxng` | The web-search sidecar (`ghcr.io/expecho/agent42-searxng`, built from `infra/searxng`) |
| `init-volumes` | A one-shot that makes the volumes writable for the app's non-root user, then exits |

Data lives in three named volumes: `agent42-postgres`, `agent42-data` (attachments, uploaded skills, fleet files, Copilot
session files, the Data Protection key ring) and `agent42-copilot` (chat workspaces for the Copilot agent kind).
`docker compose down` keeps them; `docker compose down -v` deletes everything.

## What is different from the Azure deployment

| | `azure` (production) | `local` (compose) |
|---|---|---|
| Conversations and reminders | Cosmos DB | Postgres |
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
  `Copilot__BuiltInToolsEnabled=false`; the Copilot kind still works with Agent 42's own tools. Flip it only behind
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

## GitHub Copilot

"Sign in with GitHub" is hidden until the server has an OAuth App's client id and secret. The id committed in
`appsettings.json` is the production app and redirects to `chat.agent42.nl`, so a self-host needs its own:

1. On GitHub: Settings, Developer settings, OAuth Apps, New. Homepage `http://localhost:8080`, callback
   `http://localhost:8080/api/providers/github/callback`. Generate a client secret.
2. Put the secret in `.env` as `GH_OAUTH_CLIENT_SECRET=...` (git-ignored).
3. Create `compose.override.yaml` next to `compose.yaml` (Compose merges it automatically):

   ```yaml
   services:
     app:
       environment:
         GitHub__OAuth__ClientId: <your client id>
         GitHub__OAuth__ClientSecret: ${GH_OAUTH_CLIENT_SECRET}
   ```

4. `docker compose up -d app`, then open the models tab in Preferences and click **Sign in with GitHub**.

Browse the app at `localhost`, not `127.0.0.1`: the redirect URI is built from the request and must match the callback you
registered. Copilot models run only in Copilot-kind agents (see [`bring-your-own-models.md`](bring-your-own-models.md)).

## See also

- [`.agentic/decisions/0127-self-hosting-local-storage-provider.md`](https://github.com/Expecho/Agent42/blob/main/.agentic/decisions/0127-self-hosting-local-storage-provider.md)
- [`configuration-health.md`](configuration-health.md)
- [`image-attachments.md`](image-attachments.md)
