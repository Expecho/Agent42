# Tool selection

Choose which tools an assistant may use: web search, location, reminders and more. You can set a personal default, override it per conversation, or fix it inside a custom agent.

## Where to choose

- **Personal default**: **Preferences → Tools**. It applies to every new regular chat.
- **Per conversation**: the **Tools** button in the composer. Not available in agent chats.
- **Per agent**: the **Tools** tab in the agent editor. See [Custom agents](custom-agents.md).

Until you press Save for the first time, everything is on.

## Available tools

| Tool | What it gives the assistant |
|------|-----------------------------|
| Web search | Searches the web. See [Web search](web-search.md). |
| Location | Your device location. See [Current location](current-location.md). |
| Scheduling | Prompts that run later. See [Scheduled reminders](scheduled-reminders.md). |
| Code interpreter | Runs Python in a hosted sandbox. See [Code interpreter](code-interpreter.md). |
| Long-term memory | Remembers things across chats. See [Long-term memory](long-term-memory.md). |
| Read web documents | Fetches a PDF or Office document from a link into the chat. |
| Read whole documents | Reads a document start to end, for summaries and full lists. |
| Push notifications | Sends a notification to your devices. See [Push notifications](push-notifications.md). |
| Artifacts | Builds pages, diagrams and documents in a side pane. See [Artifacts](artifacts.md). |
| Browser tools | Asks you to confirm actions and reads your time zone and selected text. |
| Shared state | A checklist and notes you and the assistant both edit. |
| Past chats | Searches and reads your other conversations. |
| Agent fleet | Hands long jobs to the [agent fleet](agent-fleet.md). |
| Skills | One row per skill. See [Skills](skills.md). |
| MCP servers | One row per server, with a row for each of its tools. See [Remote MCP servers](remote-mcp-servers.md). |

The current date and document search are always on.

## Rules to know

- Selection only narrows. A tool also needs a model that supports it, so a model without tool support gets none, and the code interpreter needs a Responses API model.
- **Long-term memory is the exception.** It isn't a tool, so a text-only model still gets it when it is selected. It also needs the operator's key.
- **New tools start off after you've saved.** If you pressed Save before a tool existed, open **Preferences → Tools** and save again to pick it up.
- Two tools grant more than they sound like: **Read web documents** fetches from the Agent 42 server and keeps what it finds, and **Push notifications** puts text the assistant writes on your lock screen.
- A change to your personal default may not reach a chat that is already open until you reopen it. Per-conversation and per-agent changes apply immediately. This matters most for **Long-term memory**: switching it off in Preferences may not stop it in a chat that is already open.
