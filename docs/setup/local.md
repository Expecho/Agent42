# Run Agent42 locally

A step-by-step guide to running Agent42 on your own machine. There are three ways, and none needs an Azure account.
Pick one, follow its section, then [add a model provider](#add-your-first-model-provider): Agent42 ships with no model
and no key, so the chat composer stays disabled until you add one.

| | [A. Offline dev host](#a-offline-dev-host) | [B. Full stack (Aspire)](#b-full-stack-with-aspire) | [C. Docker Compose](#c-docker-compose-self-host) |
|---|---|---|---|
| For | Hacking on the UI and API | Developing the backend against real stores | Actually using it day to day |
| Needs | .NET 11 SDK | .NET 11 SDK + Docker | Docker only |
| Model | A canned reply generator (no real model) | Your own providers | Your own providers |
| Storage | In memory (lost on restart) | Cosmos DB emulator + Azurite | Postgres + a volume (kept) |
| Sign-in | None (user `local-dev`) | None (user `local-dev`) | None (user `local-dev`) |
| UI | http://localhost:61326 | URL in the Aspire dashboard | http://localhost:8080 |

> **There is no authentication in any local mode.** Every request is the single user `local-dev`. Keep these on
> localhost. To expose one, put a reverse proxy that authenticates in front of it.

## Prerequisites

- **Git**, and a clone of the repo.
- **.NET 11 SDK** for A and B. Check with `dotnet --version`.
- **Docker** (Desktop, Engine or Podman with a compatible CLI) for B and C. The full stack (B) also needs the **Aspire CLI 17+** (`aspire update --self`): the AppHost no longer uses the Aspire SDK, so DCP and the dashboard come from the CLI bundle.
- Nothing else: no Node, no build step. The frontend is plain ES modules served from `src/Agent42.Backend/wwwroot/`.

```bash
git clone https://github.com/Expecho/Agent42.git
cd Agent42
dotnet restore Agent42.slnx      # A and B only
```

## A. Offline dev host

```bash
dotnet run --project tools/Agent42.DevHost
```

Open <http://localhost:61326>. It serves the real web UI and the real API, with conversations held in memory and a canned chat
client that answers with sample markdown, so you can try streaming, the model picker, artifacts, documents (a
deterministic fake embedder), image generation (a canned PNG) and the research agent without any model.

Limits: nothing is saved across restarts, and it never calls a real model. Use B or C for that.

## B. Full stack with Aspire

```bash
dotnet run --project src/Agent42.AppHost
```

The first run pulls container images, so give it a few minutes. Aspire starts the backend plus:

| Resource | Role |
|---|---|
| Cosmos DB emulator | Grain state and reminders (the database and containers are created for you) |
| Azurite | Blob storage for attachments |
| SearXNG | The web-search sidecar |

Open the Aspire dashboard (the URL is printed in the console) to find the backend's address, its logs and its traces.
The AppHost makes `local-dev` an owner, so `/dashboard` and `/logs` open for you.

Differences from production: the Cosmos emulator has no vector search, so documents use an in-memory index that is lost
on restart. A hosted model also cannot fetch an image from your machine's blob emulator, so attached images are shown in
the UI but not delivered to the model.

### Optional feature keys

The backend reads these from [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) when you run
it locally. Each missing one just switches its feature off, and Preferences → Configuration shows which are off.

```bash
dotnet user-secrets set "Geocoding:GoogleApiKey" "<key>" --project src/Agent42.Backend
```

See [Feature keys](#feature-keys-all-modes) for the full list.

## C. Docker Compose (self-host)

This is the mode to use if you just want to run it. It keeps everything in Postgres and on named volumes.

```bash
cp .env.example .env     # on Windows PowerShell: Copy-Item .env.example .env
# edit .env, then:
docker compose up -d
```

This pulls the published images (`ghcr.io/expecho/agent42-backend` and `agent42-searxng`, for amd64 and arm64), so you
need neither the .NET SDK nor a build. To run your own changes instead, build from the checkout:

```bash
docker compose -f compose.yaml -f compose.build.yaml up -d --build
```

By default you get `latest`, which follows `main`. To pin a release or a commit, set `AGENT42_VERSION` in `.env` (for
example `1.2.3` or `sha-abc1234`). `AGENT42_REGISTRY` points compose at another registry, such as a fork's.

`.env` holds the values compose reads:

| Variable | Required | What it is |
|---|---|---|
| `POSTGRES_PASSWORD` | yes | Password of the database user. It is also embedded in a connection string, so use plain letters and digits. |
| `SEARXNG_SECRET` | yes | Any long random string. The search sidecar refuses to start without it. |
| `GH_OAUTH_CLIENT_SECRET` | no | Only for "Sign in with GitHub" (see [`self-hosting.md`](../features/self-hosting.md#github-copilot)). |

Open <http://localhost:8080>. The first start pulls two images, so it takes a minute or two (a few more if you build).

Day-to-day commands:

```bash
docker compose logs -f app      # watch the app
docker compose pull && docker compose up -d           # update to the newest published image
docker compose down             # stop, keep your data
docker compose down -v          # stop and DELETE all data
```

**Backups:** the data lives in the volumes `agent42-postgres`, `agent42-data` and `agent42-copilot`. Back up the first two.
The key ring that decrypts your stored provider keys is in `agent42-data`, so a database backup without it loses your
provider keys.

What differs from the Azure deployment, and the limits of this mode (no auth, images sent as bytes, no document-search
index, Copilot shell disabled, one replica), are in [`self-hosting.md`](../features/self-hosting.md). Read its "Limits"
section before exposing the app to anyone else.

## Add your first model provider

Open **Preferences → model providers** and add one. Agent42 supports Azure AI Foundry, any OpenAI-compatible endpoint,
GitHub Models, Anthropic and local runtimes such as Ollama or vLLM. For a local Ollama, use the OpenAI-compatible base URL
(`http://host.docker.internal:11434/v1` when Agent42 runs in Compose, `http://localhost:11434/v1` otherwise).

Then pick a default chat model. For documents add an **embedding** model, and for voice or images add a speech or image
model. Provider keys are stored encrypted and never shown again.

> Choosing a third-party model sends that conversation to that provider. That is why the pick is always explicit.

## Feature keys (all modes)

Everything optional. In user secrets use the key as written; in Compose or any environment use `__` for `:` (for example
`Geocoding__GoogleApiKey`) and add it to the `app` service's `environment`.

| Key | Enables | Notes |
|---|---|---|
| `Geocoding:GoogleApiKey` | Addresses for the location tool | Without it, location returns coordinates only |
| `Hindsight:ApiKey` | Long-term memory | **Sends every non-incognito turn to Hindsight.** Not setting it is the only off switch |
| `Push:VapidPublicKey`, `Push:VapidPrivateKey`, `Push:VapidSubject` | Web Push | Generate a pair with `npx web-push generate-vapid-keys`; the subject is a `mailto:` or `https:` contact URL. Replace the committed public key with yours |
| `GitHub:OAuth:ClientId`, `GitHub:OAuth:ClientSecret` | "Sign in with GitHub" (Copilot) | Needs your own GitHub OAuth App, see [`self-hosting.md`](../features/self-hosting.md#github-copilot) |
| `Email:InboundDomain`, `Email:MailgunSigningKey` | Inbound email to an agent | Needs a public URL and Mailgun, so it only suits a deployment |
| `Access:Owners:0` | `/dashboard`, `/logs`, diagnostics | Set to `local-dev` locally; compose and the AppHost already do |
| `Storage:Provider` | `local` selects Postgres and files | Set by `compose.yaml`; absent means Azure |

Each integration that needs an account or registration has its own guide in [`docs/integrations/`](../integrations/README.md).

## Check that it works

1. Open Preferences → **Configuration**. Every feature shows ok, degraded or off, with the reason, and never a secret.
2. Send a message. The reply should stream in.
3. Compose only: `docker compose ps` shows `app`, `postgres` and `searxng` running, and `init-volumes` exited with 0.

## Run the tests and linters

```bash
dotnet test Agent42.slnx
dotnet format Agent42.slnx --verify-no-changes
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Composer is disabled, picker empty | No model provider yet. Add one under Preferences → model providers. |
| `docker compose up` stops with "set POSTGRES_PASSWORD in .env" | `.env` is missing or a value is empty. |
| `app` restarts, logs mention Postgres | The password in `.env` changed after the volume was created. Reset the password in Postgres, or `docker compose down -v` to start fresh (deletes data). |
| Web search returns nothing | `searxng` is not running (`docker compose logs searxng`) or `SEARXNG_SECRET` is empty. |
| GitHub sign-in redirects to the wrong place | Browse at `localhost`, not `127.0.0.1`, and register the same callback URL. |
| `/dashboard` or `/logs` answer 404 | `local-dev` is not an owner. Set `Access__Owners__0=local-dev` (compose and the AppHost do this for you). |
| Port 8080 or 61326 already in use | Stop the other process, or change the port mapping in `compose.yaml` / `launchSettings.json`. |

## Next

- Deploying it for real, with sign-in: [Deploy to Azure](azure.md).
- How each feature works: [`docs/features/`](../features/README.md).
