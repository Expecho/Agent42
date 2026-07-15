---
id: 0015
date: 2026-07-15
status: accepted
tags: [models, providers, responses-api, hosted-tools, web-search, code-interpreter, tools, searxng, capabilities]
---

# Enable OpenAI hosted tools (web search + code interpreter) on the Responses path

## Context
ADR 0014 put a stateless **Responses API** transport behind a per-provider `UseResponsesApi` flag but
explicitly deferred the payoff: *"Unblocks hosted tools later … a future path to retiring the SearXNG
sidecar (ADR 0011)."* This ADR takes that step for the web-search and code-interpreter tools.

Two facts frame it:
- **Hosted server-side tools ride the Responses API only.** Agent Framework's `HostedWebSearchTool` /
  `HostedCodeInterpreterTool` map to OpenAI's native `web_search` / `code_interpreter` tools, which live
  on the Responses surface. On **Chat Completions** the client 400s on them — that is the entire reason
  ADR 0011 stood up the self-hosted SearXNG sidecar and the `web_search` / `fetch_web_page` **shim**
  tools (`AIFunctionFactory`-wrapped grain methods).
- **The grain must know the wire API to choose tools.** ADR 0014 kept `ModelCapabilities` orthogonal to
  the wire API (a Foundry provider reported plain `ModelCapabilities.Full` regardless). Enabling hosted
  tools breaks that orthogonality on purpose: whether hosted tools are safe **is** a function of the wire
  API, so the capability contract has to carry it.

## Decision
Add a **`SupportsHostedTools`** bit to `ModelCapabilities` (default `false`). The factory sets it true
for a Foundry provider running Responses — `ModelCapabilities.Full with { SupportsHostedTools =
Provider.UseResponses(options) }` — reusing the ADR 0014 seam. `SupportsTools` / `SupportsVision` stay
`Full`; only this bit tracks the Responses switch.

`AgentGrain.BuildAgent` then **swaps, not adds**: when `SupportsHostedTools` is set it drops the SearXNG
shims (`web_search` + `fetch_web_page`) and registers `HostedWebSearchTool` + `HostedCodeInterpreterTool`
instead; otherwise the tool set is unchanged. A model that does not support tools at all (ADR 0013) still
gets none, hosted included.

**Swap, not add — and this is forced, not stylistic.** `HostedWebSearchTool` surfaces under the protocol
name **`web_search`** — the *same* name the SearXNG shim uses. Registering both would hand the model two
tools named `web_search`. So on the Responses path the shims come out and the hosted pair goes in; the
model keeps a `web_search` (now server-side) and gains `code_interpreter`.

## Alternatives considered
- **Additive: keep the SearXNG shims AND add the hosted tools.** Rejected: the hosted web search reuses
  the `web_search` name, so the model would carry a duplicate tool name, and even absent the collision a
  second, redundant web search wastes the tool budget and muddies tool choice.
- **A per-conversation / picker toggle for hosted vs shim tools.** Rejected for the same reason ADR 0014
  kept the transport off the picker: this is a server-side consequence of the wire API, not a user knob.
- **Retire the SearXNG sidecar now.** Out of scope. The sidecar still serves every Chat-Completions model
  (the default, and all GitHub Models), so it stays; this ADR only stops *Responses* models from using its
  shims. Retiring it is a later decision once Responses is the default (ADR 0011 lives on until then).
- **Keep capabilities orthogonal and thread the wire API separately into the grain.** Rejected: the grain
  already gates tools on `ModelCapabilities`; a parallel channel for "is this Responses" would duplicate
  the seam. `SupportsHostedTools` is capability-shaped and reads naturally at the one gate.

## Consequences
- **Hosted tool return payloads are UNBUDGETED against the 2 MB session cap — verify before enabling.**
  The gotcha *"an agent tool's RETURN value is persisted, and counts against the 2 MB session cap"* requires
  every tool to bound what it returns; the SearXNG shims do (`WebToolLimits` caps search results and fetched
  page size). The hosted tools run **server-side at OpenAI**, so their outputs — a hosted-search result set,
  or a **code-interpreter** run's output — come back on the response and, in ADR 0014's stateless mode, ride
  into the client-side `AgentSession` the grain re-serializes into one Cosmos item **every turn**. Nothing in
  Agent42 caps them. A large output could reproduce exactly the "bricks the conversation, prod-only" failure
  that gotcha exists to prevent. This is not mitigated here (there is no clean client-side cap on a
  server-side tool's output); it is a **must-address item before turning the flag on** for a live account —
  measure real payloads, and if needed bound history growth or the tools' output.
- **Code interpreter is a NET-NEW capability, not a swap.** Unlike web search (which replaces the shim of the
  same name), `HostedCodeInterpreterTool` adds **model-driven server-side code execution** that the app never
  had. It was requested as the natural hosted pair, and it is sandboxed at OpenAI (no reach into our tenant),
  but it is a new surface: its output is untrusted text that flows back into the conversation (see the payload
  point above and the injection point below). Ships off in prod; weigh it explicitly before enabling.
- **ADR 0014's orthogonality consequence is now nuanced.** `GetCapabilities` no longer returns plain
  `ModelCapabilities.Full` for every Foundry provider — a Responses one returns `Full with {
  SupportsHostedTools = true }`. `SupportsTools` / `SupportsVision` remain untouched by the flag; the ADR
  0014 line is updated to point here.
- **`HostedWebSearchTool.Name == "web_search"` is a trap.** It collides with the shim's name (recorded as
  a gotcha). Anyone re-adding a web tool on the Responses path must account for it, or the model gets a
  duplicate-named tool.
- **The `websearch` skill actively misguides on hosted models.** `src/Agent42.Agents/Skills/websearch/SKILL.md`
  is still advertised (the skills provider stays on for tools-capable models) and its `load_skill` guidance instructs
  the model to follow `web_search` with `fetch_web_page` (the shim's exact-URL, SSRF-guarded fetch, ADR
  0011). On a hosted model `web_search` still exists (now server-side) but `fetch_web_page` **does not** — so
  the model can be told to call a tool that is not registered, i.e. a **failed tool call**, not merely a
  missing convenience. Search itself still works. Reworking (or wire-API-gating) the skill is deferred — it
  ships off in prod (Responses is off by default) — but it is a real rough edge to fix before enabling.
- **Prompt-injection exfiltration surface shifts.** ADR 0011's `fetch_web_page` was an exfiltration
  channel (a tool return persisted into the session that a crafted page could steer). On the hosted path
  that specific channel is gone (no `fetch_web_page`); the hosted `web_search` runs server-side at OpenAI.
  Net exposure is not obviously worse, but it is *different* — worth a fresh look before making Responses
  the default.
- **Runtime behaviour of the hosted tools — VERIFIED (operator-confirmed, 2026-07-15).** The unit tests
  prove the grain *registers* the right tools per wire API (via the fake's `[tools:…]` echo) but not that
  the service invokes them. A live gate (a gitignored `scratchpad/` spike harness) ran against the **prod**
  endpoint `agent42.services.ai.azure.com/openai/v1` (not the `openai.azure.com` host ADR 0014's Step-1
  spike used) and confirmed: bearer `/openai/v1` works there, the service **accepts and fires** hosted
  `web_search` and `code_interpreter`, and `store=false` holds on the wire. The flag was then enabled on
  the shipped `foundry` provider (`appsettings.json`). Re-run that spike if the account/endpoint changes.
- **Default is unchanged.** With `UseResponsesApi` off (prod), no model reports `SupportsHostedTools`, so
  every conversation keeps the SearXNG shims exactly as before. This change is inert until the flag is on.
