# Agent42

**A personal AI agent you can run yourself.** Chat with any model you bring (Azure AI Foundry, GitHub Models,
Anthropic, Ollama, any OpenAI-compatible endpoint), give the agent tools, memory and schedules, and when a task is
too big for one agent, hand it to a **fleet** that designs its own team, runs it and judges the result.

## Get it running

| I want to | Go to |
|---|---|
| Try it on my machine, no Azure account | [Run locally](setup/local.md) |
| Host it with Docker and Postgres | [Self-hosting](features/self-hosting.md) |
| Run it in Docker with Azure storage and real Entra sign-in | [Docker with Entra sign-in](setup/docker-entra.md) |
| Deploy it to Azure Container Apps | [Deploy to Azure](setup/azure.md) |
| Wire up Telegram, Slack, GitHub sign-in and the like | [Integration setup](integrations/README.md) |

After the first start, open **Preferences → model providers** and add a model. Agent42 ships without one, so the
chat box stays disabled until you do.

## What you can do with it

- **Chat** with streaming replies, saved history, search, pins, groups and share links.
- **Use tools:** web search, scheduled reminders, long-term memory, documents and images, your own MCP servers.
- **Build agents** with their own instructions, tools and memory, or use the built-in Research agent.
- **Hand big jobs to a fleet** of agents on their own page.
- **Take it with you:** install it as an app, get push notifications, or talk to it from Telegram, Slack or Discord.

See every feature in the [feature overview](features/README.md).
