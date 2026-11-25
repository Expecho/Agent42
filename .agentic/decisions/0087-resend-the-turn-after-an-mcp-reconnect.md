---
id: 0087
date: 2026-10-08
status: accepted
tags: [mcp, oauth, frontend, stream-events, pwa, no-migration]
supersedes: []
amends: [0081]
amended_by: [0092]   # edit/regenerate now rewrite the session tail on an explicit user request; the rejection below is narrowed, not reversed
---

# Re-send the turn by itself once the user has reconnected its MCP server from the transcript

## Context
ADR 0081 put a Reconnect button in the transcript for an InteractiveOAuth server whose grant is dead. The
button opens the sign-in in a new tab, and the user then has to type their message again. 0081 listed
"auto-retrying the failed turn" as out of scope. The reauth notice arrives once the turn is already running,
either before the model starts or after a tool call returned 401. The model has therefore already answered
without those tools, and the chat tab never learns that the sign-in finished.

## Decision
This change is frontend only. A turn's first `mcpReauthRequired` event creates one `watchMcpReconnect`
(`mcp.js`), and pressing Reconnect arms it. The OAuth return tab posts `{result, serverId}` on the
`BroadcastChannel` `agent42-mcp-oauth` from `initMcpOAuthReturn`. The chat tab also checks again on every
`visibilitychange`. `GET /api/mcp-servers` decides whether the sign-in finished: every server of the turn
must have `hasToken && !needsReauth`. The tab then calls `send(prompt, { attachments })`, which sends the
same prompt as a **new** turn and keeps the failed turn in the history.

Guards:
- Only the newest turn is re-sent, because the next `send()` cancels the watch.
- The conversation must still be open.
- A turn that is still streaming re-sends from its own `finally`.
- The replay leaves the composer and its pending attachments alone.

## Alternatives considered
- **Rewind or replace the failed turn on the server.** Rejected: it means rewriting the stored session of a
  grain whose history can grow without bound (ADR 0016/0068). That is high risk, and the only gain is not
  having a duplicate prompt in the history.
  *Narrowed by ADR 0092:* a user-initiated, token-guarded, tail-only, whole-turn rewrite (edit and
  regenerate) is now accepted. This automatic re-send still does not rewind anything.
- **Poll the server list on a timer.** Rejected: it sends requests the user gains nothing from. The user
  coming back to the tab is exactly when the check is useful, and the channel covers desktop instantly.
- **Only the BroadcastChannel.** Rejected: an installed PWA opens `window.open` in the system browser, and
  that tab cannot reach the PWA's channel. The visibility re-check is the path that works everywhere.
- **Carry the conversation id through the OAuth `state` and navigate back.** Rejected: ADR 0081 already
  chose a new tab so the open chat survives, and this would be a second round trip design.
- **A "Send again" button after the reconnect.** Rejected: the user asked for it to happen automatically,
  and pressing Reconnect already shows they want the turn to go through.

## Consequences
- After the sign-in tab finishes, the chat answers the original question with the tools, with no retyping.
- Accepted: the history holds the prompt twice, along with the tool-less reply in between.
- Accepted: if the failed turn made side-effecting calls through *other* tools, the re-send can repeat them.
  This is rare, and the model can see the earlier turn.
- A turn with two servers that both need a sign-in re-sends once, only after both are back. Reconnecting just
  one of them sends nothing.
- No backend change, no persisted field, and no new egress, because it re-sends a prompt the user already
  sent. `sw.js` `CACHE` goes to `agent42-shell-v50`. Pinned by `McpReauthAssetsTests`.
