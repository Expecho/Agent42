# Agent42

A minimal chat agent hosted in Microsoft Orleans, using the Microsoft Agent Framework against Azure OpenAI, with a React + Vite frontend.

## Architecture

```
React + Vite (5173)  ──HTTP──▶  ASP.NET Core + Orleans Silo (5099)
                                       │
                                       ▼
                                  ConversationGrain
                                  (per conversation id)
                                       │
                                       ▼
                                Azure OpenAI gpt-5-chat
```

- **Backend** — `src/Agent42.Backend/` — co-hosts ASP.NET Core minimal-API and an Orleans silo (`UseLocalhostClustering`, `AddMemoryGrainStorage`). One `ConversationGrain` per conversation id wraps a `ChatClientAgent` (Microsoft Agent Framework) and persists the agent session + chronological message log via Orleans `IPersistentState`.
- **Frontend** — `src/agent42-web/` — Vite + React + TypeScript chat UI. Stores the conversation id in `localStorage`.
- **Persistence** — in-memory grain storage (dev only — state is lost on silo restart).

## Prerequisites

- .NET 10 SDK
- Node.js 22+ and npm
- An Azure OpenAI deployment named `gpt-5-chat` at `https://agent42.cognitiveservices.azure.com/`

## Configuration

The endpoint and deployment name are committed to `src/Agent42.Backend/appsettings.json`. The API key is stored via .NET User Secrets — never in source.

```powershell
dotnet user-secrets set "AzureOpenAI:ApiKey" "<your-key>" --project src/Agent42.Backend
```

> **Security note:** the API key initially used during scaffolding was shared in chat. Rotate it in the Azure portal (resource → Keys and Endpoint → Regenerate Key 1), then update User Secrets with the new value.

The frontend reads `VITE_API_URL` from `src/agent42-web/.env.local` (default `http://localhost:5099`).

## Run

In two terminals:

```powershell
# terminal 1 — backend (silo + API on http://localhost:5099)
dotnet run --project src/Agent42.Backend
```

```powershell
# terminal 2 — frontend (http://localhost:5173)
cd src/agent42-web
npm install   # first time only
npm run dev
```

Open http://localhost:5173 and chat. "New conversation" clears the local id and starts a fresh grain.

## HTTP API

- `POST /api/conversations` → `{ id }`
- `POST /api/conversations/{id}/messages` body `{ "text": "..." }` → `{ reply }`
- `GET /api/conversations/{id}/messages` → `[{ role, content, timestamp }, ...]`

## Tests

End-to-end Playwright test (requires backend + frontend already running):

```powershell
cd src/agent42-web
npx playwright test
```

The test sends a fact, then asks a follow-up that depends on the conversation memory — exercising both the silo and `ChatClientAgentSession` rehydration.

## Production hardening (out of scope)

- Swap `AddMemoryGrainStorage` for `AzureTable`, `AdoNet`, or `Cosmos` so state survives silo restarts.
- Add request authentication (the API is currently open).
- Compact long histories (rolling window or summarization) before they exceed model context.
- Add streaming via Orleans Streams or `[AlwaysInterleave]` `IAsyncEnumerable<string>`.
- Tune `MessagingOptions.ResponseTimeout` and `HttpClient.Timeout` past the default 100s for slow LLM calls.
