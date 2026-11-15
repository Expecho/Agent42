# Streaming replies

Answers appear as they are written. Text arrives word by word, tool calls show up as they happen, and a token-usage line appears when the reply ends.

## What you see

- **Text** fills in live as the model writes.
- **Tool calls** show as small chips while the agent works. When a single turn makes more than three calls, they are grouped under an "N tool calls" summary that collapses when the turn ends. Reload the chat and the same chips are restored.
- **Suggestions** (up to three follow-up prompts) appear after the reply.
- **Usage** at the end shows the tokens used.
- Some agents show extra live elements, such as the checklist of the [Research agent](research-agent.md), or a **Reconnect** button when a [remote MCP server](remote-mcp-servers.md) needs you to sign in again. Reasoning appears in its own section; see [Reasoning output](reasoning-output.md).

## Stop a reply

Press the **Stop generating** button next to the composer. The reply so far is kept as part of the conversation, exactly as you saw it.

## If the connection drops

A backgrounded phone app can lose its connection mid-reply, but the agent keeps working. When you come back, the app re-reads the conversation and shows the finished reply in place. You only see an error if nothing was saved.
