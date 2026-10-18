---
id: 0088
date: 2026-10-09
status: accepted
tags: [streaming, protocol, ag-ui, frontend, interop, deferred]
---

# Defer AG-UI protocol support; keep the NDJSON chat stream

## Context
AG-UI is an open event protocol between an agent backend and a frontend. The Agent Framework ships it for .NET
as `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`. On 2026-10-09 that package was **preview-only** on NuGet
(latest `1.24.0-preview.261006.1`), while the repo pins stable `Microsoft.Agents.AI` 1.23.0. Agent42 owns both
ends of its chat wire. `POST /api/chat/{conversationId}` streams NDJSON (`textDelta`, `reasoning`,
`toolCallStart`, `toolCallEnd`, `done`) from `AgentApi.Conversations.cs` into `src/Agent42.Backend/wwwroot/js/send.js` (ADR 0041).
The hard streaming behaviour lives in the conversation grain, not on the wire: turns that outlive the
connection (0046), stop (0078), MCP reconnect and re-send (0087), and artifacts (0083).

## Decision
Do not adopt AG-UI now, and do not replace the NDJSON stream. Revisit only when the .NET package is stable
**and** at least one trigger below is real.

## Alternatives considered
- **Replace NDJSON with AG-UI.** Lost: the two event sets map almost one-to-one, so it is a rename with no
  capability gain. It would also rewrite `send.js` and the stream-recovery path for a client that already exists.
- **Add an AG-UI endpoint now with `MapAGUI` on an `AIAgent`.** Lost: the hosting package runs the agent
  directly and bypasses the grain, which drops recovery, stop and the in-turn MCP reconnect. A grain-backed
  adapter is real work, and no consumer needs it yet. It would also sit on a preview package.
- **Expose agents to third parties through AG-UI instead of MCP/agent-run.** Lost: external access is already
  covered by the MCP server (0070) and `/api/agent-run` (0022). AG-UI only adds value when a third-party *UI*
  is the consumer.

## Consequences
- No new dependency or endpoint, and no preview package in a stable build.
- Revisit triggers: (1) driving Agent42 from an off-the-shelf AG-UI client such as CopilotKit; (2) wanting
  frontend-executed tools or shared agent↔UI state, which is AG-UI's real differentiator; (3) offering stored
  agents to third-party frontends alongside MCP.
- If it is adopted later, add it as **another endpoint next to NDJSON**, routed through the same grain the way
  0070 fronts stored agents. That needs its own ADR with an Easy Auth `excludedPaths` decision (0042) and an
  egress review (0013) if it is reachable by anyone but the signed-in user.
- This mirrors the A2A deferral recorded in 0070, which is also waiting for a stable .NET release.
