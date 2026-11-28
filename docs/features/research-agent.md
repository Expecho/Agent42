# Research agent

Every user gets a built-in Research agent. It plans a question into steps, searches and reads the web for each one, checks its own answer, and shows a live checklist while it works.

## Find and use it

A "Research" agent appears in your agent list the first time you open it with a model configured. Start a chat with it and ask a question. It is marked with a **research** badge.

You can edit its name, persona, model and tools. You can't change its type or its per-chat history mode. If you delete it, it doesn't come back. It starts with only the `web_search` tool, which covers searching and reading pages.

## What happens during a question

1. The agent writes a short plan as a checklist.
2. It works through each item with web search and page reads, ticking each one off.
3. It answers once the plan is done and a judge agrees the answer holds up: it states a conclusion, its claims are backed by pages actually read, and it says plainly what it couldn't confirm.

A **Research plan · 2/4** panel sits at the top of the reply bubble while it works. It is live only: a reloaded chat shows the answer and its tool calls, not the checklist. However many rounds the agent takes (at most 6), the chat keeps one answer, with every search it made attached.

## Always per-chat

The *continuous* toggle is disabled for the research agent, because its turns are large and can't share one ongoing history.

## Limits you may notice

- If you background the tab, the page rejoins the running research turn when you come back. Only when it cannot (the turn failed, the server restarted, or it is an incognito chat opened elsewhere) can it show "Transmission failed" even though it completes; reload to see the answer.
- Called through the agent MCP endpoint, a research turn can exceed the two-minute wait, and the usual "still running" behavior applies.
- A model that doesn't support tools gets a plain agent without the checklist.
- The judge uses the conversation's own provider, so nothing new leaves your setup, but it adds one model call per finished round. If you turn on memory, it also runs once per round.
- Web pages ingested mid-run only become searchable documents after the turn ends.

See [Streaming replies](streaming-replies.md) and [Custom agents](custom-agents.md).
