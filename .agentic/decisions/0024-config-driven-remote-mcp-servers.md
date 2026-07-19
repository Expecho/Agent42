---
id: 0024
date: 2026-07-19
status: accepted
tags: [mcp, tools, per-user, egress, security, agent-framework]
---

# Title: Config-driven remote MCP servers — per-user, encrypted, gated by one `mcp` tool key

## Context
Users want remote tool servers (Dataverse, GitHub-style) wired in without bespoke integration per
server. Every MCP server exposes the same discovery interface, and its tools convert to `AITool`
(`McpClientTool : AIFunction : AITool`, confirmed against `ModelContextProtocol` 1.4.1), which merges
directly into the `List<AITool>` `AgentGrain.BuildAgent` already builds. So adding a server can be a
config entry, not a code change. This is the MCP analogue of user-configurable model providers
(ADR 0018): the same per-user, encrypted-secret, version-cached, no-restart shape — and it inherits
ADR 0018/0013's egress concern, now reachable from a tool server the user added.

## Decision
A signed-in user adds/edits/deletes their own **remote HTTP MCP servers** from the web UI
(Preferences → MCP servers). Config is a **per-user overlay grain** (`IUserMcpConfigGrain`, riding the
existing `"agent-state"` provider on a new `"mcp-servers"` state — **no new Cosmos container**), with
secrets **encrypted** (a dedicated `IMcpSecretProtector`, purpose `Agent42.Mcp.Secret.v1`, distinct
from the provider protector's scope) before the grain sees them, masked to `hasHeaderValue` on read,
blank-on-update = keep. A singleton `IUserMcpToolProvider` connects to the user's enabled servers
(version-cached, best-effort with a timeout, bounded connection lifetime), and `AgentGrain` resolves
its tools **once in `OnActivateAsync` before the first `BuildAgent`**, merging them gated by **one new
`ToolCatalog` group key `mcp`** (`SupportsTools && enabled.Contains(mcp)`), intersected with model
capability. Each MCP tool's return value is truncation-wrapped (a `DelegatingAIFunction`) to fit the
2 MB session (ADR 0004). Auth modes are **staged by risk**: None/Header (Phase 1, shipped), MSAL
ClientCredentials (Phase 2), InteractiveOAuth (Phase 3, its own MR).

## Alternatives considered
- **Per-server code / bespoke integration** — rejected: defeats the whole ask; every MCP server speaks
  the same protocol, so one config-driven path serves all.
- **App-global (admin) MCP servers in `appsettings`** — rejected: mirrors ADR 0018, config is per-user
  and secrets must be encrypted per-user, not in operator config.
- **Reuse `IProviderSecretProtector`** for MCP secrets — rejected: its DP purpose is a private const,
  not a parameter, so a distinct scope is impossible; and MCP secrets are a separate concern whose key
  rotation should be independent.
- **Per-tool catalog entries** (map each MCP tool name to a key) — rejected: MCP tool names are
  user-defined and dynamic, so the whole group is gated by one `mcp` key; MCP tools are the documented
  exception to the "every registered tool maps to a catalog key" guard.
- **`HttpClientTransportOptions.AdditionalHeaders` vs a `DelegatingHandler`** for Header auth — chose
  `AdditionalHeaders` for the static case (Phase 1): no DI-resolved `HttpClient`, so the
  `AddServiceDefaults` resilience pipeline is never inherited (the named-`HttpClient` gotcha is avoided
  by construction). Phase 2's dynamic MSAL tokens will need the handler + a plain `HttpClient`.
- **SSRF-guard the MCP endpoint** — rejected: the endpoint comes from the authenticated user's own
  persisted config, never a model or a live request, so — exactly like a user provider endpoint
  (ADR 0018) — it carries no `PrivateAddressGuard` and may be `localhost` (a self-hosted MCP server is a
  valid target). It is never accepted from a tool argument or request body.
- **Never-evict version cache** (the ADR 0018 exemplar's shape) — rejected: an `McpClient` is a live
  socket, so an unbounded cache leaks one connection per config change. The provider retains the most
  recent 2 versions per user and disposes older on eviction (an in-flight grain sees a disposed client
  as a skipped tool — tolerated).

## Consequences
- **Enables** remote tool servers with zero per-server code; a user's change takes effect on the next
  activation of each of their conversation grains, no restart (accepted staleness, like ADR 0018).
- **Headline egress consequence:** an MCP server sees conversation content, and its **tool results are a
  new inbound prompt-injection channel** — the same framing as ADR 0011/0013. The egress surface only
  opens once a user deliberately adds a server AND enables the `mcp` tool. `mcp` is off-by-default for
  existing **explicit** tool-savers (ADR 0021's semi-breaking note) and on via `null == all` for
  never-configured users — but a user with zero servers gets zero MCP tools regardless.
- **Best-effort:** a dead/slow MCP server is skipped with a timeout, never fatal to activation or the
  rest of the tool set.
- **Follow-up:** Phase 2 (MSAL ClientCredentials — the Dataverse path) and Phase 3 (InteractiveOAuth,
  a new inbound auth surface — re-run `critic`) are separate MRs. Offline (DevHost/tests) exercises the
  CRUD/UI via single-user adapters; overlay→tools reflection is prod-wired. Spec:
  `.agentic/specs/completed/plan-remote-mcp-servers.md` (stays active until Phase 3 lands, then moves to `completed/`).
