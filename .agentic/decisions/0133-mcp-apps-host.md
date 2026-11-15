---
id: 0133
date: 2026-11-15
status: accepted
tags: [mcp, security, egress, frontend]
supersedes: []
extends: [0024, 0084, 0089, 0095, 0109]
amends: []
---

# Host MCP Apps (SEP-1865) in the chat

## Context
An MCP tool result was flattened to bounded text (ADR 0004, 0109). MCP Apps (extension `io.modelcontextprotocol/ui`, spec
2026-01-26) lets a tool declare an interactive view (`_meta.ui.resourceUri` to a `ui://` resource, MIME
`text/html;profile=mcp-app`) that the host renders in a sandboxed iframe, fed the tool input and result over a postMessage
JSON-RPC dialect. Servers exist already (Claude, VS Code Copilot, Goose host it). The view is third-party HTML from a
user-configured MCP server, so hosting it is a security decision first.

## Decision
1. **Capability, per server.** `StoredMcpServer.AppsEnabled` (trailing `[Id(15)]`, a SETTING with no `Keep` clause). When on,
   the client advertises the extension (`McpElicitationClient.Options(router, appsEnabled)`, the one helper both factories call),
   app-only tools are hidden from the model, and app payloads flow. **It defaults off for every stored server** (no silent egress
   widening, cross-cutting 13); the editor pre-ticks it for a newly added server.
2. **Visibility is one rule with two writers.** `McpAppMeta.ModelTools` drops tools whose `_meta.ui.visibility` lacks `"model"`;
   `UserMcpToolProvider` and `McpConnectionTester` both call it, before the discovered names are derived.
3. **Server-served frame, CSP header as the protection.** `GET /api/mcp-apps/{serverId}/frame?uri=` serves the view with a CSP
   built by the pure `McpAppCsp` from the origins the server declared. No second origin. **A deliberate deviation from the spec's
   "MUST use a double iframe on a different origin"**: the CSP `sandbox` directive (never `allow-same-origin`) gives the document an
   opaque origin even as a top-level page, the header cannot be loosened by the view, and the view still talks to `window.parent`.
   The policy substitutes `'none'`/`data:` where the spec's own default says `'self'` (which matches every same-origin URL) and
   adds `form-action 'none'`. Declared domains are parsed, not escaped: https/wss origins only, no IP literal, no local/internal
   name, never our own host (nor a wildcard covering it), no bare-TLD or global wildcard, at most 16 per list.
4. **ADR 0013 extension.** Honoring `connectDomains`, `resourceDomains`, `frameDomains` and `baseUriDomains` makes a
   server-declared origin an egress path for whatever the view holds, chosen by a third party. The artifact-style
   self-navigation hole also applies (a view can navigate itself). The user's control is the per-server switch.
5. **The model's result and the view's result differ.** The model keeps the shaped, bounded text. The view gets the raw
   `CallToolResult`, captured in `TruncatingMcpTool` onto the run's `TurnContext` (`RecordAppResult`, keyed by call id) and attached
   to `ToolCallEnd` as `AgentStreamEvent [Id(22)] McpApp`. It never enters the session. Only an interactive run whose browser
   advertised the `mcp_app` capability (a name in the `clientTools` list, the ADR 0095 shape) receives it.
6. **Replay.** The raw result (at most 64 KB) is saved to a blob and a `McpAppRecord` is appended to
   `AgentGrainState.McpApps` (`[Id(19)]`, at most 20 per conversation, oldest dropped) at the end-of-turn persist. History projects
   `ChatToolCall.McpApp` (the view reference only); the result comes from the owner-only result route. Incognito stores nothing.
   Purge, a retention prune and an edit or regenerate drop the records of removed calls; a fork copies none; a share link
   strips tool calls wholesale. Blobs are orphaned, not deleted (the store has no delete, the same gap as artifacts).
7. **A replayed card is inert** until the user clicks "Activate app": `tools/call`, `ui/message` and `ui/update-model-context` are
   refused, so a reload never re-runs a server tool a view calls on init.
8. **`ui/message` and `ui/update-model-context` are a prompt-injection channel** and are confirm-gated with the shared confirm
   dialog. A message is an ordinary user turn, only when nothing is streaming. Context is held per card and sent as `appContext` on
   every request while that card stays open (open views, not one-shot), capped at 12 KB across all open cards on the client
   (a view past it is refused), and shown as a labeled, untrusted trailing message (`AppContextProvider`, ADR 0104 shape), per turn, allowed
   on incognito, never stored.
9. **Out of scope / declined:** `pip` display mode (the current mode is returned), iframe `allow=` permissions (not honored),
   public share links and Android (text chip only), fleet, Copilot kind, chat gateway, agent-run and email runs, a blob delete path.

## Alternatives considered
- **Spec-literal sandbox proxy page / separate sandbox origin.** Rejected: the proxy is same-origin here anyway, and a second
  origin costs DNS, certificate, Bicep and Easy Auth work for isolation the CSP `sandbox` header already gives.
- **Static-only or deny-all egress.** Rejected by the user: views would not be able to load their own data.
- **Live-only cards (no replay).** Rejected by the user: a reload would turn every card into a bare chip.
- **Honor per-tool selection for a view's `tools/call`.** Not done: the selection is per agent and the request carries no agent
  context. A view may call any tool of its own server visible to `"app"`.

## Consequences
- A server with apps on can show third-party script in the chat and reach its declared origins; the switch is the control.
- A server that changes its `ui://` resource can show an old stored result in a new view (the HTML is read live, only the result
  is stored). Accepted.
- Tests: `McpAppCspTests`, `McpAppsApiTests`, `McpAppRegistryTests`, `McpAppsGrainTests`, `McpAppsLifecycleTests`,
  `McpAppsAssetsTests`, `McpAppsCapabilityTests`, plus the serializer and Android contract fixtures.
