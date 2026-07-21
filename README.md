# Agent42

A personal AI chat agent ("Agent 42") hosted in Microsoft Orleans: one grain per
conversation wraps a Microsoft Agent Framework `ChatClientAgent` against any
OpenAI-compatible model, with streaming replies, tools, durable scheduled reminders,
document Q&A, image generation, custom agents, and a vanilla-JS web UI served from the
backend. Deployed to Azure Container Apps behind Entra ID Easy Auth at
[chat.agent42.nl](https://chat.agent42.nl).

**Stack:** .NET 10 · Orleans 10.2 (grains, Cosmos persistence + reminders) · Microsoft
Agent Framework 1.13 · ASP.NET Core minimal APIs · Aspire 13.4 (local orchestration) ·
vanilla-JS frontend · Bicep + GitHub Actions → Azure Container Apps.

## Features

Everything below is what you get as a signed-in user in the web app. For a developer
walkthrough of each feature — how it works, how Agent Framework and Orleans fit in, with flow
diagrams and code — see [`docs/features/`](docs/features/README.md).

### Chat

- **Streaming replies** — answers stream in token by token, with tool calls and token
  usage shown as they happen.
- **Conversation history** — every chat is saved and listed in the sidebar (most recent
  first, grouped), so you can leave and resume any conversation later, or delete one.
- **Model picker** — choose which model answers a conversation, and switch models
  mid-conversation.

### Bring your own models

- **User-configurable providers** — add, edit, and delete your own model providers from
  **Preferences → model providers**: Azure AI Foundry, any OpenAI-compatible endpoint,
  GitHub Models, or a local runtime like Ollama / vLLM.
- **Your keys, encrypted** — provider API keys are stored encrypted, never shown back to
  you, and changes take effect immediately (no restart).
- **Default model** — pick the model new chats start on. The app ships with no built-in
  chat model — you configure your own; until you do, the composer stays gated with an
  empty picker.

> Heads-up: picking a third-party model sends that conversation's content (your messages,
> document excerpts, image links, your location) to that provider, outside the sign-in
> tenant. That's why the pick is always explicit.

### Tools the agent can use

You choose which of these an assistant may use — as a **personal default** (Preferences →
default tools), a **per-conversation** override (the composer's "Tools" popover), or baked
into a **custom agent**. Availability is also capped by what the chosen model supports.

- **Web search** — searches the web and reads pages for current facts, prices, and recent
  events; can follow a named link one click deeper to reach content behind "read more".
- **Current location** — uses your location (with your permission) for local weather,
  what's nearby, travel times, and "where am I".
- **Scheduled reminders** — the agent can set durable reminders that fire later (once or
  repeating) and prompt it to follow up — even when you're not watching.
- **Skills** — reusable markdown playbooks the agent loads on demand when a task matches.
- **Code interpreter** — runs code to compute, analyze, and produce results (on models
  that support it).
- **Document Q&A** — see *Files & documents*.
- The current date/time is always available.

### Files, images & documents

- **Image attachments** — attach images to a message; vision-capable models can see them.
- **Document upload** — drop in PDFs, Office files, or text; the agent automatically pulls
  the most relevant excerpts into its answers (retrieval-augmented), so you can ask
  questions about your own documents.
- **Image generation** — pick an image model to generate a picture from a prompt, or edit
  an existing image in the conversation. Generated images reappear when you reload.

### Custom agents

- **Build your own agents** — create named agents with their own persona/instructions and
  their own enabled tool set.
- **Continuous or per-chat memory** — an agent can start a fresh thread each time, or run
  in *continuous* mode where every start resumes one ongoing conversation.
- **Run agents from outside the browser** — each agent gets its own API key (shown once)
  and a fire-and-forget endpoint, so a webhook, cron job, or automation tool (e.g. n8n)
  can hand it a prompt and files with no browser session. The result shows up as a new
  conversation you open later.

### Remote MCP servers

- **Add your own tools via MCP** — connect your own remote HTTP MCP servers in
  **Preferences → MCP servers**, and their tools become available to the agent with no
  extra setup.
- **Authentication options** — none, a static header, client-credentials, or an
  interactive OAuth sign-in flow; secrets are stored encrypted.
- Each server can be turned on/off individually in the tool picker.

### Notifications & sharing (installed Android/Chromium PWA)

- **Install as an app** — Agent42 installs as a PWA.
- **Push notifications** — get a Web Push notification, titled with the agent's name, when
  an agent finishes a reply while you're away (e.g. a fired reminder or an external run).
- **Android Share sheet** — share text, links, images, or documents from other Android
  apps straight into a new chat or an agent; the content is prefilled and you review and
  send it yourself.

### Preferences & account

- **Sign-in** — single sign-on with Entra ID in production.
- **Configuration health** — a read-only **Preferences → Configuration** tab that shows
  which features are fully configured, degraded, or off, so nothing fails silently.

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
                                     Any OpenAI-compatible model endpoint
                                     (Azure AI Foundry · GitHub Models · Ollama · …)
```

- **Backend** — `src/Agent42.Backend/` — minimal API (`AgentApi.cs`) co-hosted with an
  Orleans silo; streams each turn as NDJSON events (text deltas, tool calls, token usage).
  Serves the static frontend from `wwwroot/`.
- **Agents** — `src/Agent42.Agents/` — `AgentGrain` (chat, model switching, tools,
  reminders, RAG, runtime skills from `Skills/*/SKILL.md`) plus the per-user index,
  provider, MCP, and push grains.
- **ServiceDefaults** — `src/Agent42.ServiceDefaults/` — shared Aspire/OpenTelemetry wiring.
- **AppHost** — `src/Agent42.AppHost/` — Aspire orchestration for the full local stack.
- **Frontend** — `src/Agent42.Backend/wwwroot/` — static vanilla JS (marked + DOMPurify);
  deliberately no SPA build.
- **Infra** — `infra/` (Bicep, incl. a self-hosted SearXNG search sidecar) +
  `.github/workflows/deploy.yml` (build → ACR → deploy).

Conversation content stays client-side in the grain (history is persisted as serialized
session state), so any model — including stateless third-party endpoints — can run a chat.

## Run locally

```powershell
# Restore
dotnet restore Agent42.slnx

# Offline dev loop — canned LLM, in-memory grains, no Azure or Docker needed.
# UI at http://localhost:61326
dotnet run --project tools/Agent42.DevHost

# Full stack — Aspire orchestration with the Cosmos emulator (needs Docker).
dotnet run --project src/Agent42.AppHost
```

Running `src/Agent42.Backend` standalone requires a real Azure OpenAI endpoint
(`AzureOpenAI:Endpoint`) and the Azure OpenAI resource **API key** (`AzureOpenAI:ApiKey`,
put it in user secrets) — chat and embeddings authenticate by key, not your `az login`
identity. The managed identity is still used for blob storage locally via
`DefaultAzureCredential`. Chat models themselves are added per-user at runtime; there is no
built-in chat model.

## Test & lint

```powershell
dotnet test Agent42.slnx                       # xUnit + Orleans TestingHost, fake chat client — no network
dotnet format Agent42.slnx --verify-no-changes # lint
dotnet build Agent42.slnx                       # type check
```

## HTTP API

All routes sit behind Easy Auth (Entra ID) except the automation carve-out
(`/api/agent-run`). In production the user is the Entra ID principal; locally everything
maps to a single `local-dev` pseudo-user.

**Chat & conversations**

- `POST   /api/chat/{conversationId}` — send a message; NDJSON stream of events back
- `GET    /api/conversations` — the user's conversations, most recent first
- `GET    /api/conversations/{id}/messages` — visible history for resuming a conversation
- `DELETE /api/conversations/{id}` — delete a conversation
- `GET    /api/conversations/{id}/model` — the model this conversation currently uses
- `GET    /api/conversations/{id}/agent` — the agent this conversation is bound to
- `POST   /api/conversations/{id}/attachments` — upload an image or document
- `GET    /api/conversations/{id}/attachments/{attachmentId}` — fetch an attachment
- `GET/PUT /api/conversations/{id}/tools` — read / set the per-conversation tool selection

**Models & tools**

- `GET /api/models` — the user's resolved model catalog (baseline ⊕ their provider overlay)
- `GET /api/tools` — the selectable-tool catalog (per-user; includes their MCP servers)
- `GET/POST/PUT/DELETE /api/providers[/{key}]` — manage the user's model providers
- `PUT /api/default-model` — set the user's default model

**Agents**

- `GET/POST/PUT/DELETE /api/agents[/{id}]` — manage custom agents
- `POST /api/agent-run` — external, key-authenticated run of a specific agent
  (`multipart/form-data` + `X-Api-Key`); returns 202 and runs in the background

**Profile, MCP, push, health**

- `GET/PUT /api/profile` — user profile (default model/tools)
- `GET/POST/PUT/DELETE /api/mcp-servers[/{id}]` — manage remote MCP servers
- `GET /api/mcp/oauth/start` · `GET /api/mcp/oauth/callback` — MCP interactive OAuth flow
- `GET /api/push/config` · `POST/DELETE /api/push/subscriptions` — Web Push
- `GET /api/config-health` — configuration health report (never contains a secret value)

## Deployment

Push to `main` → `.github/workflows/deploy.yml` builds the container, pushes it to ACR
(`crexpechohosting`), and deploys `infra/main.bicep` to resource group `rg-agent42`. The
app runs on Azure Container Apps behind Entra ID Easy Auth at chat.agent42.nl.

Several manual prerequisites live outside the code as GitHub Actions secrets — required
ones (`AzureOpenAI:ApiKey`, `SEARXNG_SECRET`) and optional feature keys
(`GOOGLE_MAPS_API_KEY`, `CONTEXT_API_KEY`, `VAPID_PRIVATE_KEY`). See `.agentic/gotchas.md`
before touching infra: Cosmos region/containers, the custom domain, RBAC grants, and the
search sidecar all carry constraints that are invisible in the code.

## Contributing / agent docs

This is a personal, experimental project (Peter Bons) with no external consumers.
Commands, hard rules, and the knowledge layout live in `CLAUDE.md`; architectural
decisions in `.agentic/decisions/` (ADRs); non-obvious constraints in
`.agentic/gotchas.md`; multi-session plans in `.agentic/specs/`.
