---
id: 0024
date: 2026-07-19
status: accepted
tags: [mcp, tools, per-user, egress, security, agent-framework]
amended_by: [0026, 0064, 0076, 0095, 0121]
---

# Title: Config-driven remote MCP servers — per-user, encrypted, gated by one `mcp` tool key

> **Amended by [ADR 0121](0121-open-signin-owner-tier.md) (2026-11-09).** The "may be `localhost`, no SSRF guard" rationale for user MCP endpoints assumed
> about two trusted users. With sign-in open, `Header` auth lets a user choose the header name and value against the app's
> own loopback (for example `X-MS-CLIENT-PRINCIPAL-ID`). That exposure is **accepted, not mitigated** (user decision,
> 2026-11-09).

> **Amended by [ADR 0076](0076-local-bridge-for-machine-local-mcp-servers.md) (2026-09-17) — a server that
> dials in.** A new auth mode, `LocalBridge`, reaches an MCP server that listens only on the user's machine:
> a helper there opens a WebSocket to `/api/local-bridge` and the backend speaks MCP over it. Such a server
> has **no endpoint** (validation exempts that mode only), its credential is a grain-owned bridge-key hash
> rather than a secret this API encrypts, and revoking writes close its live socket. Everything below still
> governs it — it yields ordinary `McpServerTools`, gated, truncated and best-effort like any other server.

> **Amended by [ADR 0064](0064-mcp-connection-test-endpoint.md) (2026-09-04) — management endpoints are no
> longer socket-free.** The management routes below were pure config CRUD that never opened a connection;
> `POST /api/mcp-servers/{id}/test` now connects to one named server **on explicit user request**, so the
> user is told whether what they saved actually works. Nothing else changes: the lazy, best-effort
> activation path, the encryption discipline and the egress shape are all untouched, and the test dials the
> same server with the same credentials the chat path already uses.

> **Amended by [ADR 0026](0026-per-mcp-server-selectable-tools.md) (2026-07-19) — MCP gate scope
> only.** The "gated as one whole `mcp` group key" decision below (title, Decision, and the
> per-tool-catalog-entries alternative) is re-scoped to **per-server** gating: each server is
> selectable as `mcp:{id}` and the bare `mcp` key becomes an all-servers **wildcard** (kept in
> `AllSelectableToolKeys`, removed from `Descriptors`). Everything else here stays authoritative.

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
  (ADR 0018) — it carries no SSRF address-guard and may be `localhost` (a self-hosted MCP server is a
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
- **The inbound channel gained a second-order effect in [ADR 0067](0067-mcp-results-seed-the-fetch-allowlist.md)
  (2026-09-05).** A server the user marks `SeedsFetchableUrls` may put the URLs from its tool results into
  the conversation's fetchable-URL allowlist, so a tool result no longer merely *injects text* — it can
  make our own container **fetch and durably ingest** an attacker-authored URL (ADR 0066's RAG index, and
  through ADR 0056 a memory bank with no delete path). Per-server, opt-in, off by default. The egress
  property recorded above is **unchanged**: this endpoint's arguments were always model-chosen.
- **Follow-up:** Phase 2 (MSAL ClientCredentials — the Dataverse path) and Phase 3 (InteractiveOAuth,
  a new inbound auth surface — re-run `critic`) are separate MRs. Offline (DevHost/tests) exercises the
  CRUD/UI via single-user adapters; overlay→tools reflection is prod-wired. Spec:
  `.agentic/specs/completed/plan-remote-mcp-servers.md` (stays active until Phase 3 lands, then moves to `completed/`).
