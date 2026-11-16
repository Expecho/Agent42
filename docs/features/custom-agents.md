# Custom agents

Build named agents with their own persona and tool set, run them per chat or in continuous memory, and drive them from outside the browser with an API key or as an MCP tool.

## Create an agent

Open the agent editor, give the agent a name, description, model and instructions (its persona), and choose its tools. Starting a chat with it copies the persona into that conversation, so later edits to the agent don't change chats already started.

You can choose the type when you create an agent: Standard or **Copilot**. The type can't be changed afterwards.

Every user also gets a seeded Research agent. See [Research agent](research-agent.md).

## Per-chat or continuous

- **Per-chat** (default): each start is a fresh conversation.
- **Continuous**: every start resumes one ongoing conversation with that agent.

Continuous chats only keep the last 24 hours of messages, so they don't grow forever. Deleting a continuous agent's conversation resets it fully. A conversation's tool set is fixed when it starts, so a continuous agent started before a new tool existed won't get that tool until you reset it.

## Run it from outside the browser

Each agent has its own API key, shown **once** when you create it. A webhook, cron job or tool like n8n can send it a prompt and files with the key, with no browser session. The request returns right away and the result appears as a new conversation you open later. Images, image generation and document search all work in these runs.

Regenerate the key in the editor if it leaks. The old key stops working at once.

## Call it from another MCP host

Turn on the agent's MCP exposure and Claude Desktop, Copilot or any MCP client holding its API key can call it as an `ask_agent` tool. Unlike the API run, the call waits and returns the reply, plus a conversation id to continue. The turn also appears in your sidebar.

For safety, an agent called this way never gets memory, push notifications, past chats or the agent fleet, because a key holder shouldn't reach those. Turning exposure on lets an already-shared key read replies, so the editor offers to regenerate the key next to the toggle.

## Copilot agents

A Copilot agent runs its chats on the GitHub Copilot agent loop, using your own model and Agent42's tools and skills. It also has built-in shell, file and URL tools, auto-approved, working in its own directory. The operator can turn those built-ins off with `Copilot:BuiltInToolsEnabled`.

Copilot agents are always per-chat, take no documents or images, and refuse incognito chats. Continuous agents refuse them too, since they share one saved history; every other agent can start an incognito chat. Every signed-in user can use them, and the shell can read the server's own files and credentials, so treat it as trusted-deployment only.

To run on your GitHub Copilot plan, use **Sign in with GitHub** (Preferences → model providers). The editor then offers your plan's `github-copilot:<model>` models for the Copilot type. If you disconnect GitHub, the agent refuses ("GitHub isn't connected") instead of switching models, and a rejected token gets a "Sign in with GitHub again" reply.

See also [Tool selection](tool-selection.md).
