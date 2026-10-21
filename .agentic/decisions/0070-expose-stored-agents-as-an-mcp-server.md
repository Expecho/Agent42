---
id: 0070
date: 2026-09-11
status: accepted
tags: [mcp, agents, api-key, security, egress, excluded-paths, no-migration]
supersedes: []
extends: [0024]
amends: [0002, 0022, 0036, 0042, 0068]
---

> **Amended by [ADR 0111](0111-past-chats-as-a-model-tool.md) (2026-10-21).** The bind filter, renamed
> `WithoutOwnerScopedTools` (it was `WithoutMemoryOrPush`, the name the notes below use), now also removes `past_chats`.
> Those tools return the owner's other conversations verbatim, which is the recall risk this ADR stripped memory for,
> in a stronger form.

> **Amended by [ADR 0109](0109-chrome-extension-bridge-and-mcp-image-results.md) (2026-10-20).** With the Chrome
> extension connected, a turn run through this surface can make the owner's Chrome run JavaScript **without any
> prompt** (`evaluate`, a user decision). 0076's "each write still meets the helper's prompt" does not hold for it.
> Narrow the bridge server's tools (ADR 0058) for agents exposed here.

> **Amended by [ADR 0076](0076-local-bridge-for-machine-local-mcp-servers.md) (2026-09-17).** A turn run
> through this surface may now use a user's **local-bridge** MCP tools — actions in the owner's signed-in
> browser — because `WithoutMemoryOrPush` removes no MCP keys and any turn kind may use a connected bridge
> (a user decision). Every non-read-only call still meets the confirmation prompt on the owner's machine,
> but a key holder can now cause that prompt to appear, and read-only tools (page text, browsing history)
> run without one.

> **Amended by [ADR 0075](0075-model-invoked-push-notification.md) (2026-09-16).** 0075 adds a
> model-invoked `send_push_notification` tool that may fire from any path, including the `ChatAsync` this
> surface drains — which would have reversed the "lands in the owner's sidebar **silently**" property
> below by default, since the new key is in `AllSelectableToolKeys` and this surface binds `null == all`.
> It does not: the bind-time filter that already removed memory now removes push notifications too
> (`WithoutMemoryOrPush`), so the silence below still holds. The one sentence to read with care is the
> comparison to `/api/agent-run`, which keeps the tool — that asymmetry is now deliberate and argued in
> the filter's docstring, not an oversight.

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
fails closed with a **uniform** 401 that does not disclose whether an agent exists. Uniform in *status and
body*: the resolve path still has three distinguishable cost tiers — a blank key returns immediately, an
unknown key after one directory grain call, and a valid key for an unexposed or deleted agent after a
directory **plus** a registry call — so a timing oracle is a residual, not something the wording should be
read as closing. This is ADR 0062's
dashboard-gate reasoning applied unchanged: a security control must not rest on a third-party package
propagating an endpoint convention.

**Not mapped from `MapAgentApi`.** Ten call sites invoke it — two hosts and eight test fixtures — and minimal
API infers every mapped endpoint's parameters up front, so a dependency one host lacks breaks routing for
*every* endpoint in that host. `MapMcp` throws outright without `AddMcpServer().WithHttpTransport()`. Each host
calls `AddAgentMcpServer()` / `UseAgentMcpKeyGate()` / `MapAgentMcpServer()` itself, the ADR 0062 precedent.
Consequently `ProjectLayoutTests.AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` does not apply and
must not be extended to cover this.

**The call blocks**, with the wait bounded at `AgentMcpLimits.RunTimeout`, rather than a start-plus-poll pair.

**Refusals ride back in band**, as a `CallToolResult` with `IsError` set, never as a JSON-RPC error response.
The two are not the same thing: a protocol error is a transport failure the calling model can only surface to
its user, while `IsError` is the model-readable signal that lets it act on "pass back this conversationId" or
"the prompt was too long". Returning a bare string would make every refusal arrive as `isError: false`,
indistinguishable from a reply.

**Turns are listed** in the owner's sidebar with the agent's identity, and are **excluded from long-term
memory** (ADR 0056/0057).

### Three things that are easy to get backwards

**1. The timeout bounds the WAIT, never the turn.** The session write sits *after* the streaming loop, inside
the iterator body, so a consumer that stops pulling leaves the iterator suspended at a `yield return` and the
turn is **never persisted**: tokens spent, the conversation listed with a title and no messages, the search
index never updated, and no client-side recovery that could repair it. ADR 0046's property is narrower than it
looks — Kestrel discards *writes* while the endpoint keeps pulling, which is not the same as abandoning the
enumerator. So the drain runs to completion on a detached task and `Task.WhenAny` races only the wait.

**This closes the state for the TIMEOUT and not for a FAULT, and the difference is worth being exact about.**
`ChatCoreAsync` has no try/catch around `RunStreamingAsync`, so a model or provider failure throws straight
out of the enumerator — that is what the browser renders as "Transmission failed" (ADR 0046). The persist
never runs, and `TouchAsync` has already listed the conversation, so a faulted turn leaves exactly the empty
listed conversation described above. That is the same orphan `/api/agent-run` leaves on a faulted background
turn, and closing it is a change to that older surface too, so it is **recorded here rather than claimed
closed**. What this surface does do is **curate** the failure — the reason is a fixed string, never the
exception, following the `ConfigHealth.ProbeAsync` discipline, because the caller is not Entra-authenticated
and must not receive a provider's message, endpoint or stack.

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

**No push of any kind, and that is a choice rather than an omission.** ADR 0028's rule is "send only on the
two async paths, never from `ChatCoreAsync`", and `ask_agent` drains `ChatCoreAsync` — so an MCP turn lands in
the owner's sidebar silently, while `/api/agent-run`, the other external-caller surface on the same
credential, does push. That is deliberate: the caller already has the reply in hand, so the notification would
tell the owner about a conversation that is already complete and already read. Anyone comparing the two
surfaces would otherwise read the difference as an oversight. **ADR 0075 kept this property deliberately**
rather than letting its model-invoked tool erode it: an exposed agent has `push_notification` filtered out
at bind time, so neither kind of notification reaches the owner from this surface.

**The retention gate is deliberately broader than the id validator.** `IsRetentionWindowed` matches a bare
`mcp-` prefix while `IsValidConversationId` requires the exact minted shape, so a *browser* conversation whose
client-supplied id happens to start `mcp-` is stamped and 24-hour-pruned too. ADR 0016's `agent-` prefix has
the same property, so this is consistent with precedent rather than new — but the two notions of "an MCP
conversation" differ, and a reader assuming they are the same set will be wrong.

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

**Tests.** 31 in `tests/Agent42.Tests/AgentMcpServerTests.cs` (AC1–AC13, the id-shape theory, and two on a
faulting turn — one that the failure is curated, one that the listed conversation is left empty), one in
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
| `CallToolResult.IsError` hard-coded false | 7 of 31 fail |

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

**Post-review changes.** A `critic` pass on the finished diff found two blocking issues, both real and both
fixed here rather than deferred: three separate enumerations of the `excludedPaths` hole list still said
"four" (ADR 0002's banner, the gotcha the plan names as required reading, and `CLAUDE.md`, which then
contradicted itself), and a faulting turn had no handling at all — the raw provider exception would have
reached an unauthenticated caller and the ADR claimed a state was closed that was closed only for the
timeout. Four smaller findings were also taken: `AgentMcpResult.IsError` was computed and dropped on the
floor, so every refusal reached the caller as `isError: false`; the timeout message told the caller to do
something `ask_agent` cannot do (it has no read mode, so "call again with this conversationId" queues a
second billed turn behind the first); `Task.Delay(timeout)` was never cancelled on the success path; and
`CapReply` returned `MaxReplyChars + 13`. ADR 0062's incidental "exactly four entries" was anchored to its
own date rather than renumbered, since renumbering every ADR on each growth is what caused the drift.

**Unverified residuals.** The timeout path is covered only by the unit test: DevHost's canned chat client has
no slow-stream lever, so a genuine 120-second overrun was never driven end to end. The `excludedPaths`
behaviour itself is asserted against the Bicep by a matcher that *models* the measured ADR 0042 semantics —
the real Easy Auth gate is exercised only in production, as it has been since ADR 0035. And no real external
host (Claude Desktop, Copilot) has connected; the client used was the SDK's own.
