# Integration setup guides

Some features need something set up **outside Agent42** before they work: an account somewhere, an app
registration, a redirect URI, a secret. Each integration that needs manual setup gets a page here: what to create,
where, in what order, and what you see when a step is skipped.

| Integration | Guide | Enables |
|---|---|---|
| Chat apps (Telegram, Slack, Discord; per user, no operator setup) | [chat-apps/](chat-apps/README.md) | Talking to Agent 42 from your own bot |

Calendars and similar services are reached like any other remote MCP server: add the server in
Preferences → MCP servers and connect it there. See [Remote MCP servers](../features/remote-mcp-servers.md).

**No page here contains a secret value.** Name the setting, never its value.
