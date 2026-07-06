# Agent42

A personal chat agent hosted in Microsoft Orleans: one grain per conversation wraps a
Microsoft Agent Framework `ChatClientAgent` against Azure OpenAI (AI Foundry), with
streaming replies, durable scheduled reminders, a model picker, and a vanilla-JS web UI
served from the backend. Deployed to Azure Container Apps behind Entra ID Easy Auth.

## Architecture

```
wwwroot UI (vanilla JS)  ──HTTP/NDJSON──▶  ASP.NET Core minimal API + Orleans silo
                                                    │
                                                    ▼
                                        AgentGrain  (key: {userId}/{conversationId})
                                        ├─ Agent Framework ChatClientAgent + tools
                                        ├─ session persisted as grain state → Cosmos DB
                                        └─ durable reminders (schedule_prompt) → Cosmos DB
                                                    │
                                                    ▼
                                        Azure OpenAI (AI Foundry model deployments)
```

- **Backend** — `src/Agent42.Backend/` — minimal API (`AgentApi.cs`) co-hosted with an
  Orleans silo; streams each turn as NDJSON events (text deltas, tool calls, token usage).
- **Agents** — `src/Agent42.Agents/` — `AgentGrain` (chat, model switching, reminders,
  runtime skills from `Skills/*/SKILL.md`), `ConversationIndexGrain` (per-user list).
- **Frontend** — `src/Agent42.Backend/wwwroot/` — static vanilla JS (marked + DOMPurify);
  deliberately no SPA build.
- **Infra** — `infra/` (Bicep) + `.github/workflows/deploy.yml` (build → ACR → deploy).

## Run locally

```powershell
# Offline dev loop — canned LLM, in-memory grains, no Azure or Docker needed.
# UI at http://localhost:61326
dotnet run --project tools/Agent42.DevHost

# Full stack — Aspire orchestration with the Cosmos emulator (needs Docker).
dotnet run --project src/Agent42.AppHost
```

Running `src/Agent42.Backend` standalone requires a real Azure OpenAI endpoint
(`AzureOpenAI:Endpoint`) and an identity with the `Cognitive Services OpenAI User` role;
locally `DefaultAzureCredential` picks up your `az login`.

## Test

```powershell
dotnet test Agent42.slnx
```

xUnit + Orleans `TestingHost` with a fake chat client — no network needed.

## HTTP API

- `POST /api/chat/{conversationId}` body `{ "message": "...", "model": null }` → NDJSON stream of events
- `GET  /api/conversations` → the signed-in user's conversations, most recent first
- `GET  /api/conversations/{id}/messages` → visible history for resuming a conversation
- `GET  /api/conversations/{id}/model` → the model this conversation currently uses
- `GET  /api/models` → model deployments available in the AI Foundry account

In production the user is the Entra ID principal injected by Easy Auth; locally
everything maps to a single `local-dev` pseudo-user.

## Deployment

Push to `main` → `.github/workflows/deploy.yml` builds the container, pushes to ACR, and
deploys `infra/main.bicep`. See `.agentic/gotchas.md` before touching infra — several
constraints there (Cosmos region/containers, custom domain, role grants) are invisible
in the code.

## Contributing / agent docs

Commands, hard rules, and the knowledge layout live in `CLAUDE.md`; decisions in
`.agentic/decisions/`; non-obvious constraints in `.agentic/gotchas.md`.
