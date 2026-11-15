# Agent42

**A personal AI agent you can run yourself.** Chat with any model you bring (Azure AI Foundry, GitHub Models,
Anthropic, Ollama, any OpenAI-compatible endpoint), give the agent tools, memory and schedules, and when a task is
too big for one agent, hand it to a **fleet** that designs its own team, runs it and judges the result.

One Orleans grain per conversation wraps a Microsoft Agent Framework agent. Replies stream into a vanilla-JS web UI
(an installable PWA, with an Android app), a Telegram, Slack or Discord bot, or an HTTP API.

## Start here

| I want to | Go to |
|---|---|
| Try it on my machine, no Azure account | [Run locally](setup/local.md) |
| Host it with Docker and Postgres | [Self-hosting](features/self-hosting.md) |
| Deploy it to Azure Container Apps | [Deploy to Azure](setup/azure.md) |
| Hand a big task to a team of agents | [Agent fleet](features/agent-fleet.md) |
| Understand how a feature works | [Feature docs](features/README.md) |
| Wire up Telegram, Slack, OAuth and the like | [Integration setup](integrations/README.md) |

The source, issues and architecture decision records (ADRs) live in the
[GitHub repository](https://github.com/Expecho/Agent42); the ADRs are in
[`.agentic/decisions`](https://github.com/Expecho/Agent42/tree/main/.agentic/decisions).
