---
id: 0070
date: 2026-09-11
status: accepted
tags: [mcp, agents, api-key, security, egress, excluded-paths, no-migration]
supersedes: []
extends: [0024]
amends: [0002, 0022, 0042, 0068]
---

# Stored agents are callable by an external MCP client

> **The inverse direction of every other MCP decision in this repo.** ADRs 0024/0025/0026/0058/0064/0067/0069
> are all *client* side — Agent42 consuming someone else's server. This is the first time Agent42 *serves* the
> protocol. Nothing in `.agentic/` forbids that; the governing discipline is ADR 0042's — a publicly reachable
> path self-enforces its credential and fails closed.

## Context

The only external entry point into an agent is `POST /api/agent-run` (ADR 0022), and it is deliberately
fire-and-forget: a key holder pushes a prompt in and reads **nothing** back. That is the right shape for a
webhook or a cron job. It is useless for the thing the user actually wants, which is an agent usable *from
another agent host* — Claude Desktop, Claude Code, Copilot, or another Agent42 — where the whole point is that
the caller receives the answer.

A2A was evaluated first and deferred by the user pending a stable .NET release. The blocker is **shape, not
maturity**: its hosting package registers agents by name at startup and serves one card per host, which cannot
express per-user agents created at runtime. MCP's server SDK is stable, is the same package family this repo
already pins for its client side, and its per-request model fits.

## Decision

A stored agent carries an opt-in flag. With it set, a holder of that agent's existing ADR 0022 key can call it
as an `ask_agent` tool over a JSON-RPC surface at `/api/agent-mcp`, and gets the agent's reply back.

**Credential.** The existing per-agent key, reusing the ADR 0022 key-hash directory grain verbatim. No new
credential type, no OAuth on the server side, and the tool list stays static — one tool, whichever agent the
key names.

**Enforcement is middleware over the route prefix, not a check inside the tool method.** `MapMcp` serves
`initialize`, `tools/list` and `tools/call` from **one** endpoint and the SDK enforces nothing of its own, so
an in-tool check alone would leave the handshake anonymously reachable the moment the `excludedPaths` entries
land. `AgentMcpKeyGate` resolves the key before routing, publishes the caller on `HttpContext.Items`, and
fails closed with a **uniform** 401 that does not disclose whether an agent exists. This is ADR 0062's
dashboard-gate reasoning applied unchanged: a security control must not rest on a third-party package
propagating an endpoint convention.

**Not mapped from `MapAgentApi`.** Ten call sites invoke it — two hosts and eight test fixtures — and minimal
API infers every mapped endpoint's parameters up front, so a dependency one host lacks breaks routing for
*every* endpoint in that host. `MapMcp` throws outright without `AddMcpServer().WithHttpTransport()`. Each host
calls `AddAgentMcpServer()` / `UseAgentMcpKeyGate()` / `MapAgentMcpServer()` itself, the ADR 0062 precedent.
Consequently `ProjectLayoutTests.AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` does not apply and
must not be extended to cover this.

**The call blocks**, with the wait bounded at `AgentMcpLimits.RunTimeout`, rather than a start-plus-poll pair.

**Turns are listed** in the owner's sidebar with the agent's identity, and are **excluded from long-term
memory** (ADR 0056/0057).

### Three things that are easy to get backwards

**1. The timeout bounds the WAIT, never the turn.** The session write sits *after* the streaming loop, inside
the iterator body, so a consumer that stops pulling leaves the iterator suspended at a `yield return` and the
turn is **never persisted**: tokens spent, the conversation listed with a title and no messages, the search
index never updated, and no client-side recovery that could repair it. ADR 0046's property is narrower than it
looks — Kestrel discards *writes* while the endpoint keeps pulling, which is not the same as abandoning the
enumerator. So the drain runs to completion on a detached task and `Task.WhenAny` races only the wait.

**2. A namespaced conversation id is necessary but not sufficient.** Three defences are required and the first
draft shipped only one:

1. Mint `mcp-{guid}` and validate a supplied id against the **exact** shape. The grain key is raw string
   concatenation and is also a blob prefix and a document-index partition, so `mcp-a/../b` must never be
   accepted. Non-canonical GUID spellings are rejected too, so exactly one string addresses a conversation.
2. **Compare the existing binding's agent id with the caller's.** `BindAgentAsync` is immutable-once-bound, so
   a conversation already bound to agent B keeps B's persona, prompt, tools and history — and a holder of
   agent A's key supplying that id would run **as B** and read B's content back. The refusal is byte-identical
   to the malformed-id one, so existence cannot be probed.
3. Reject the `agent-` shape on the way in: ADR 0016 ids collapse every chat onto one non-reentrant grain, so
   an agent reachable at its own continuous grain could block on itself until the timeout.

**3. Memory is excluded at BIND time, not through a turn-scoped grain field.** `_turnLocation`, `_ephemeral`,
`_interactiveTurn` and `_webDocumentIngested` are a family that lives for the *activation* and is cleared in
three places; any new per-turn field inherits that trap. The binding's `EnabledTools` is materialised into
state once and read as a value on every rebuild. The list is **filtered, never rebuilt** — a null selection is
the `null == all` sentinel and expands to `AllSelectableToolKeys`, which carries the `skills` and `mcp`
wildcards.

### What this amends

- **ADR 0002/0022/0042** — the `excludedPaths` hole list goes from four entries to **six**. Both
  `/api/agent-mcp` and `/api/agent-mcp/*` are required and neither is spare: measured, `MapMcp` registers one
  endpoint that answers at the bare prefix *and* its trailing-slash form, while a bare entry matches
  **exactly**. A single bare entry is how ADR 0035 shipped a dead feature with the whole suite green.
- **ADR 0068** — the 24-hour retention window now covers a **second** id prefix. ADR 0068's gate detects
  continuous chats by shape; MCP conversations are admitted to it because their growth is worse rather than
  merely similar: an external caller reusing one id has no sidebar row and no reason to ever start a fresh
  conversation, so nothing else bounds that session. This is a deliberate widening of 0068's scope, not a
  detail.
- **ADR 0036** — `ChatAsync` gains a trailing `interactive` parameter, following the `ephemeral` precedent on
  the same method. It sets `_interactiveTurn = true`, which `SuggestFollowUpsAsync` gates on, so draining it
  without this would pay one extra permanently-discarded model call on every invocation.

## Alternatives considered

- **A2A instead of MCP.** Deferred by the user; see Context. This decision does not prejudge it.
- **Build it in the sibling `Expecho/mcpserver` repo.** Set aside because it needs a reply-returning endpoint
  here anyway, making it this work plus a second repo.
- **One endpoint exposing every agent of a user, with a dynamic per-request tool list.** Needs a new
  user-level credential, and the SDK's dynamic-list handler API is unverified.
- **Draining `EnqueuePromptAsync`.** The background path discards the stream, so it has no reply to return.
  A comment in `AgentGrainTests` notes that non-reentrancy makes a following `GetHistoryAsync` wait for the
  enqueued turn, but the test itself polls defensively — so that ordering is not a mechanism to rely on.
- **Suppressing memory with a turn-scoped field.** See decision point 3.
- **Truncating an over-long prompt instead of refusing it.** The text lands verbatim in the *owner's* 2 MB
  session and is re-sent on every later turn, so silently dropping the tail spends someone else's budget and
  hides the loss from a caller who could simply have sent less. The *reply* cap is the opposite call — the
  turn already ran and is already persisted, so refusing would bill the owner and return nothing.

## Consequences

**Headline: turning the flag on silently upgrades an already-distributed key from write-only to read.**
ADR 0022's key could push a prompt, image bytes and document content *in*. This surface hands the reply
*back*, which can carry RAG excerpts (ADR 0007), the user's street address (ADR 0012), tool and web content,
and prior turns of a reused conversation. A key handed to a cron job months ago becomes a **read credential**
the moment the owner ticks the box, with no re-consent. The editor therefore offers a key regeneration on the
OFF → ON transition — an offer, not a forced rotation, and the only place in the UI that says this out loud.

**Exposure grants an external caller whatever the owner's per-agent selection already allows** (ADR 0021).
That is worth stating plainly rather than burying: for an agent carrying a ToolForge-style runtime MCP server
that reaches unsandboxed code execution. Exposure itself only ever **narrows** — memory is removed — but it
narrows from a set the owner chose for a browser, and it does not re-ask.

**Two unbounded-growth paths, one mitigated and one recorded.** `ConversationIndexGrain` holds one unbounded
list in one Cosmos item with no cap and no prune, so minting a conversation per call eventually fails **every**
`TouchAsync` for that owner — including their own first browser turn. A per-owner cap is out of scope and this
is a **recorded residual**. The second path, a caller reusing one id forever, is closed by the ADR 0068
widening above.

**Self-reference is possible and bounded.** An owner can add this surface as one of their own MCP servers (the
server list is deliberately not address-guarded, ADR 0024). The timeout bounds it, and because the timeout no
longer abandons the turn, doing so is not destructive.

**Telemetry attribution is unchanged and wrong in the same way `/api/agent-run` is.** On an excluded path
there is no principal header, so MCP spend lands under the `local-dev` fallback in the cross-user usage
dashboard (ADR 0031), and the content reaches `/logs` (ADR 0054) in full. Pre-existing; noted, not fixed,
because fixing it is a change to that older surface too.

**No rate limiting.** A real gap, named rather than silently omitted.

**Infra:** two `excludedPaths` entries in `container-app.bicep` — the only infra change. No new secret, no new
Cosmos container, no new grain, no new folder (`Mcp/` exists in both projects), no new `AgentApi.*` partial.
No migration: `[Id(10)] McpExposed` is trailing and defaulted, so every stored agent deserializes unexposed.
Service-worker cache bumped `agent42-shell-v33` → `-v34`.

**Out of scope:** streaming or progress notifications back to the caller; file or image arguments on the tool;
MCP resources and prompts; server-side OAuth.

## Verification

`dotnet test Agent42.slnx` — **1273/1273 green**; `dotnet format --verify-no-changes` clean.

**Step 0 spike** (`ModelContextProtocol.AspNetCore` 2.2.0, file-based app outside the repo) settled three
questions and **corrected one plan premise**:

- `MapMcp("/api/agent-mcp")` registers exactly **one** route, `POST /api/agent-mcp/`. There is no `/sse` or
  `/message` sub-route — those belong to the legacy SSE transport, and `EnableLegacySse` is false by default.
  The plan had assumed several routes; the two-entry conclusion survives, its reason does not.
- A tool method reaches `HttpContext` through `IHttpContextAccessor` in stateless mode
  (`httpContextNull=False path=/api/agent-mcp`).
- `SessionMode = Stateless`, `Stateless = True` are the defaults; the full handshake succeeded with no
  session id. Also measured, and not in the plan: `AddMcpServer()` alone makes `MapMcp` **throw** —
  `.WithHttpTransport()` is mandatory.

**Tests.** 29 in `tests/Agent42.Tests/AgentMcpServerTests.cs` (AC1–AC13 plus the id-shape theory), one in
`AgentGrainTests.cs` (`McpConversation_IsPruned`, AC14), added rows in `EasyAuthExclusionTests.cs` (AC15),
two in `ProjectLayoutTests.cs` (AC16, including a per-host gate-ordering guard), one in
`AgentEditorAssetsTests.cs` (AC17).

**Mutation-checked**, because a security test that passes vacuously is worse than none:

| Mutation | Result |
|---|---|
| Gate not installed in the fixture | 14 of 29 fail |
| `WithoutMemory` made a pass-through | `AskAgent_MakesNoMemoryCalls` fails |
| `interactive: true` | `AskAgent_DoesNotGenerateFollowUps` fails |
| Binding comparison removed | `AskAgent_RejectsAnotherAgentsConversation` fails |
| Drain abandons the enumerator on timeout | `AskAgent_TimedOut_StillPersistsTheTurn` fails |

The last is the one worth keeping: it is the "obvious" implementation, and AC10 exists to reject it.

**The DevHost leg was run**, with a **real MCP client** (`ModelContextProtocol` 2.2.0 over Streamable HTTP)
and headless Chrome over CDP. Measured:

- Anonymous `initialize`, a wrong key, and a valid key for an **unexposed** agent all → `401` with the
  identical body `Missing or invalid X-Api-Key.`
- A valid key for an exposed agent → handshake completes, `tools/list` advertises `ask_agent`.
- A call returns the agent's reply and a minted id (`mcp-4c7dcfdc-…`); a second call with that id continues
  the same conversation, and `/api/conversations/{id}/messages` shows both turns.
- `mcp-a/../b`, `agent-someagent` and `not-an-mcp-id` → `Unknown conversationId.`
- Agent A's key against agent B's conversation → `Unknown conversationId.` (the AC6 hijack, live).
- A 32 001-character prompt → `Prompt exceeds the 32000 character limit.`
- Conversations listed for the owner carry the right `agentName` and title.
- The editor checkbox renders in the API panel, reads `true` for an exposed agent, shows
  `http://localhost:61326/api/agent-mcp`; unticking, saving and reopening reads back `false`, and the same key
  then gets `401` while a still-exposed agent's key gets `200`.
- Both `/api/agent-mcp` and `/api/agent-mcp/` answer `200` — the two `excludedPaths` entries, observed.

**Unverified residuals.** The timeout path is covered only by the unit test: DevHost's canned chat client has
no slow-stream lever, so a genuine 120-second overrun was never driven end to end. The `excludedPaths`
behaviour itself is asserted against the Bicep by a matcher that *models* the measured ADR 0042 semantics —
the real Easy Auth gate is exercised only in production, as it has been since ADR 0035. And no real external
host (Claude Desktop, Copilot) has connected; the client used was the SDK's own.
