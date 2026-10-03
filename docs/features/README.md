# Agent42 feature docs

Developer documentation for each user-facing feature listed in the top-level
[README](../../README.md). Each page describes what the feature is, how **Microsoft Agent
Framework** and **Orleans** fit in, a flow diagram, and code snippets from the implementation.

> **Two recurring seams to know before reading any page:**
> - **`AgentGrain`** is the Orleans grain, one per conversation, keyed `{userId}/{conversationId}`.
>   Its state persists to Cosmos; it implements `IRemindable` for durable reminders.
> - **`AgentGrain.BuildAgent(modelId)`** is where Orleans state and per-user config become a
>   Microsoft Agent Framework `ChatClientAgent` (tools, context providers, instructions). Almost
>   every feature passes through it. Model/tool/persona changes use the same
>   *snapshot → rebuild → restore-session* dance so history survives.

## Chat

| Feature | Page |
|---------|------|
| Streaming replies (NDJSON, live-only events, stop, stream recovery, tool-call folding and replay) | [streaming-replies.md](streaming-replies.md) |
| Conversation history (sidebar, resume, delete, search, pins, groups, incognito, share links, follow-ups) | [conversation-history.md](conversation-history.md) |
| Model picker (switch mid-conversation) | [model-picker.md](model-picker.md) |
| Reasoning output (live-only "thinking") | [reasoning-output.md](reasoning-output.md) |
| Rich inline rendering (diagrams, code, math, charts) | [rich-rendering.md](rich-rendering.md) |
| Artifacts (pages, apps, SVG, documents and code in a sandboxed side pane, with versions) | [artifacts.md](artifacts.md) |
| Voice: dictation, hands-free loop, server-side speech | [voice-and-speech.md](voice-and-speech.md) |

## Bring your own models

| Feature | Page |
|---------|------|
| User-configurable providers, encrypted keys, default model | [bring-your-own-models.md](bring-your-own-models.md) |

## Tools the agent can use

| Feature | Page |
|---------|------|
| Web search (`web_search` + `fetch_web_page`) | [web-search.md](web-search.md) |
| Current location | [current-location.md](current-location.md) |
| Scheduled reminders | [scheduled-reminders.md](scheduled-reminders.md) |
| Skills (markdown playbooks, per-skill selection, uploads) | [skills.md](skills.md) |
| Code interpreter (hosted) | [code-interpreter.md](code-interpreter.md) |
| Choosing which tools an assistant may use | [tool-selection.md](tool-selection.md) |
| Long-term memory (Hindsight) | [long-term-memory.md](long-term-memory.md) |

## Files, images & documents

| Feature | Page |
|---------|------|
| Image attachments (vision) | [image-attachments.md](image-attachments.md) |
| Document Q&A (RAG, incl. `read_web_document`) | [document-qa.md](document-qa.md) |
| Image generation | [image-generation.md](image-generation.md) |

## Custom agents

| Feature | Page |
|---------|------|
| Build agents, continuous/per-chat memory, external API-key runs, MCP exposure | [custom-agents.md](custom-agents.md) |
| The seeded Research agent: plan/search/reflect loop with a live checklist | [research-agent.md](research-agent.md) |

## Remote MCP servers

| Feature | Page |
|---------|------|
| Add your own tools via MCP (auth options, OAuth refresh and Reconnect, connection test, per-server toggle, local bridge for on-machine servers) | [remote-mcp-servers.md](remote-mcp-servers.md) |

## Notifications & sharing (installed Android/Chromium PWA)

| Feature | Page |
|---------|------|
| Push notifications | [push-notifications.md](push-notifications.md) |
| Android Share sheet | [android-share-target.md](android-share-target.md) |
| Install as an app + sign-in (incl. identity alias) | [pwa-and-sign-in.md](pwa-and-sign-in.md) |

## Preferences & account

| Feature | Page |
|---------|------|
| Configuration health | [configuration-health.md](configuration-health.md) |
| Usage dashboard, `/logs` console, Orleans dashboard, App Insights export | [usage-and-diagnostics.md](usage-and-diagnostics.md) |

---

*Code is the source of truth (see [`CLAUDE.md`](../../CLAUDE.md)). These pages are pointers and
rationale — on any conflict, trust the code and fix the page. Decisions and their rationale live
in [`.agentic/decisions/`](../../.agentic/decisions/) (ADRs); non-obvious constraints in
[`.agentic/gotchas.md`](../../.agentic/gotchas.md).*
