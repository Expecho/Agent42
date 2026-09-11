# Plan — Expose stored agents as an MCP server

planned-against: main @ c2958c5

## Overview

Make a stored agent callable **by an external MCP client** (Claude Desktop, Claude Code, Copilot,
another Agent42) as a tool, and return the agent's reply to the caller. Today the only external
entry point is `/api/agent-run` (ADR 0022), which is deliberately fire-and-forget: a key holder can
push a prompt in and read nothing back.

Why now: the user wants agents usable from other agent hosts. A2A was evaluated first and deferred
(see "Rejected alternatives") because its .NET hosting is preview-only and boot-time static. MCP's
server SDK is stable, is already the same package family this repo pins for its client side, and its
per-request model fits per-user runtime-created agents.

This is the **inverse direction** of every existing MCP ADR in the repo (0024/0025/0026/0058/0064/
0067/0069 are all client-side). Nothing in `.agentic/` forbids serving a protocol surface; the
governing discipline is ADR 0042's: a publicly reachable path self-enforces its credential and fails
closed.

## Affected areas

Verified present against the tree at c2958c5.

**New files**
- `src/Agent42.Backend/Mcp/AgentMcpServerRegistration.cs` — DI, the prefix gate, endpoint mapping
- `src/Agent42.Backend/Mcp/AgentMcpKeyGate.cs` — middleware over the route prefix (C10)
- `src/Agent42.Backend/Mcp/AgentMcpTools.cs` — the `[McpServerToolType]` tool surface
- `src/Agent42.Backend/Mcp/AgentMcpRunner.cs` — key resolution, bind check, drain, budget
- `src/Agent42.Agents/Mcp/AgentMcpLimits.cs` — the numeric budgets and the id shape
- `tests/Agent42.Tests/AgentMcpServerTests.cs`

**Modified**
- `Directory.Packages.props` + `src/Agent42.Backend/Agent42.Backend.csproj` — add `ModelContextProtocol.AspNetCore`
- `src/Agent42.Backend/Program.cs` — `AddAgentMcpServer()`, `UseAgentMcpKeyGate()`, `MapAgentMcpServer()`
- `tools/Agent42.DevHost/Program.cs` — the same three calls, so the feature is exercisable offline
- `src/Agent42.Agents/StoredAgents/AgentDefinition.cs` — trailing `[Id(10)]` opt-in flag
- `src/Agent42.Agents/Conversations/IAgentGrain.cs` — trailing `interactive` parameter on `ChatAsync` (C13)
- `src/Agent42.Agents/Conversations/AgentGrain.cs` — honour that parameter where `_interactiveTurn` is set
- `src/Agent42.Agents/Conversations/RetentionLimits.cs` + `AgentGrain.Retention.cs` — admit the MCP id prefix (C11)
- `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` + its contracts — carry the flag through upsert/response
- `src/Agent42.Backend/wwwroot/js/agents.js`, `dom.js`, `index.html`, `styles.css` — the agent-editor checkbox
- `src/Agent42.Backend/wwwroot/sw.js` — `CACHE` bump (mandatory for any `wwwroot/js/` change, ADR 0041)
- `infra/modules/container-app.bicep` — two new `excludedPaths` entries
- `tests/Agent42.Tests/SharedApiFixture.cs` — a hook so a fixture can map the MCP surface (see C14)
- `tests/Agent42.Tests/EasyAuthExclusionTests.cs` — new covered/uncovered rows
- `tests/Agent42.Tests/AgentEditorAssetsTests.cs` — the checkbox's source-text guard
- `tests/Agent42.Tests/ProjectLayoutTests.cs` — the AC12 source-text half
- `.agentic/decisions/` — a new ADR (step 11)

**Deliberately NOT modified**
- `src/Agent42.Backend/Api/AgentApi.cs` — see C1. This is **not** an `AgentApi.*` partial.
- `ProjectLayoutTests.ExpectedFolders()` — no new folder; `Mcp` is already in both projects' lists.

## Context & constraints

Findings the executor cannot cheaply re-derive. Each carries its evidence path. C10 to C14 came out
of the adversarial review; the Review section at the end records what was rejected and why.

**C1 — The endpoint must NOT be mapped from `MapAgentApi`.** Ten call sites invoke it
(`src/Agent42.Backend/Program.cs:539`, `tools/Agent42.DevHost/Program.cs:223`, eight test fixtures).
Minimal-API infers every endpoint's parameters up front, so a dependency one host does not register
breaks routing for *every* endpoint in that host — this has bitten the repo twice already (the
`IMcpSecretProtector` and `PushConfig` entries in `.agentic/gotchas.md`). `MapMcp` additionally requires
`AddMcpServer()` in DI. Follow the ADR 0062 precedent: the Orleans dashboard maps its own routes from
`Program.cs` and is deliberately not an `AgentApi.*` group. A host that does not want the surface simply
does not call it. Consequence: `ProjectLayoutTests.AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot`
does not apply and must not be extended to cover this.

**C2 — Two `excludedPaths` entries are needed, not one.** ADR 0042 measured the matcher: a bare entry
matches **exactly**, and a `/*` entry is a prefix that keeps its trailing slash and also matches an empty
remainder. `MapMcp(pattern)` registers several endpoints under the prefix, including the prefix itself.
So this needs **both** `/api/agent-mcp` and `/api/agent-mcp/*`, exactly as ADR 0035 needed both `/shared`
and `/shared/*`. A single bare entry is how ADR 0035 shipped a dead feature with the whole suite green.
This takes the hole list from four entries to six and amends ADR 0002/0022/0042. There is no exact-list
assertion to update, only added rows.

**C3 — Do not add a turn-scoped grain field to suppress memory.** `_turnLocation`, `_ephemeral`,
`_interactiveTurn` and `_webDocumentIngested` are a family declared together and cleared in three places;
the gotcha "A grain's turn-scoped field lives for the ACTIVATION" states that any new per-turn field
inherits the trap. Memory exclusion happens at **bind time** instead, through `AgentBinding.EnabledTools`:
pass the agent's selection with `ToolCatalog.Memory` removed. Verified end to end during review:
`BindAgentAsync` materialises the list into state (`AgentGrain.cs:364`), `EffectiveEnabledTools()` reads
state first (`AgentGrain.BuildAgent.cs:303`), and `BuildMemoryProvider` reads it **as a value** and returns
null without the key (`src/Agent42.Agents/Memory/AgentGrain.Memory.cs:57`). Handle `null == all` by
filtering `ToolCatalog.AllSelectableToolKeys`; both the `skills` and `mcp` wildcards live in that list and
must survive, so filter, never rebuild.

**C4 — Namespacing the conversation id is necessary but NOT sufficient.** The grain key is
`{userId}/{conversationId}` and the key directory yields the owner's userId, so a caller-supplied id
reaches that owner's conversations. Three separate defences are required, and the earlier draft of this
plan shipped only the first:
1. Mint `mcp-{guid}`, and validate a supplied id against the **exact** shape, not a prefix.
   `AgentKey` is raw string concatenation (`src/Agent42.Backend/Api/AgentApi.cs:48`) and the result is also
   a blob prefix and a document-index partition, so `mcp-a/../b` must never be accepted.
2. **Compare the existing binding's agent id with the caller's.** `BindAgentAsync` is immutable-once-bound
   and returns early when `_state.State.AgentId` is set (`AgentGrain.cs:351-354`). So a conversation already
   bound to agent B keeps B's persona, prompt, tools and history, and a holder of agent A's key supplying
   that id would run as B and read B's content back. Resolve `GetAgentBindingAsync()` and fail closed on a
   mismatch.
3. Reject anything in the `agent-` shape on the way in, for the ADR 0016 reason below.
The first defence alone also does not address the continuous-agent hazard: ADR 0016 ids start with `agent-`
and collapse every chat onto one non-reentrant grain, so an agent reachable at its own continuous grain
could block on itself until the timeout.

**C5 — Drain `ChatAsync`, not `EnqueuePromptAsync`.** The background path discards the stream, so it
returns no reply text. A comment in `AgentGrainTests` notes that non-reentrancy makes a following
`GetHistoryAsync` wait for the enqueued turn, but the test itself polls defensively, so that ordering must
not be relied on as a mechanism. Draining and concatenating `TextDelta` is what `/api/chat/{id}` already
does and returns deterministically.

**C6 — Enumerate the drain to completion on a detached task, and bound only the WAIT.** This reverses the
earlier draft, which had it backwards. The persist sits **after** the streaming loop inside the iterator
body (`AgentGrain.cs`: the `await foreach` over `RunStreamingAsync` yields, then
`PersistTurnUnlessEphemeralAsync()` runs about ten lines later). A consumer that stops pulling leaves the
iterator suspended at a `yield return`, so the session is **never written**: tokens spent, conversation
listed with a title and no messages, search index never updated, and no client-side recovery exists to
repair it. ADR 0046's property is narrower than it looks — Kestrel discards *writes* while the endpoint
keeps pulling, which is not the same as abandoning the enumerator. Therefore: start a task that enumerates
to completion, `Task.WhenAny` it against the timeout, return a timeout error to the caller when the timeout
wins, and let the task finish and persist. Observe its exception so it cannot surface as unobserved.

**C7 — Watch the type names.** The namespace is flat `Agent42.Backend` (ADR 0039/0040, load-bearing for
Cosmos `$type` on the Agents side). The SDK contributes `ModelContextProtocol.Server.McpServer`. No actual
collision exists today, but prefix every new type `AgentMcp*` and keep the SDK's server namespace import
narrow.

**C8 — Budget both directions.** Cap the inbound prompt, because it lands in our 2 MB session (ADR 0004),
and cap the returned reply for the caller's context and a bounded contract. Both numbers live in
`AgentMcpLimits`, never inline.

**C9 — No new HttpClient is introduced**, so the named-client resilience gotcha does not apply. If the
implementation grows one, `RemoveAllResilienceHandlers()` is mandatory.

**C10 — The credential must be enforced by middleware over the prefix, not inside the tool.** `MapMcp`
registers the whole JSON-RPC surface — `initialize`, `tools/list`, session setup — and the SDK enforces
nothing. With only a check inside the tool method, every one of those is anonymously reachable once the
`excludedPaths` entries land, which is precisely what the `excludedPaths` gotcha forbids. ADR 0062 solved
the identical problem for a third-party package's own routes and its reasoning transfers verbatim: a
security control must not rest on the package propagating an endpoint convention. Copy
`src/Agent42.Backend/Orleans/DashboardOwnerGate.cs`. Keep the runner's own resolution as defence in depth.

**C11 — Two unbounded-growth paths, one of which has its mitigation structurally excluded.**
`ConversationIndexGrain` holds one unbounded list in one Cosmos item with no cap and no prune
(`src/Agent42.Agents/Conversations/ConversationIndexGrain.cs:10,44`), so minting a conversation per call
eventually fails **every** `TouchAsync` for that owner, including their own first browser turn. And a caller
who reuses one id grows that session without bound, where ADR 0068's 24-hour prune cannot help because it
detects by the `agent-` prefix (`AgentGrain.Retention.cs:240`). Admit the MCP prefix to that retention gate —
one added constant, and a 24-hour window is the right policy for a machine-driven conversation. Record the
index-item ceiling as a residual; a per-owner cap is out of scope.

**C12 — Flipping the flag on silently upgrades an already-distributed key.** ADR 0022's key is write-only:
its headline records pushing a prompt, image bytes and document content *in*. This surface hands the reply
*back*, which can carry RAG excerpts (ADR 0007), the user's street address (ADR 0012), tool and web content,
and prior turns of a reused conversation. A key handed to a cron job months ago becomes a read credential
the moment the owner ticks the box, with no re-consent. `AgentRegistryGrain.SaveAsync` already takes a
regenerate flag; offer a regenerate when the flag flips on, and make this the ADR's headline consequence.

**C13 — `ChatAsync` turns on the follow-up model call.** It sets `_interactiveTurn = true`, and
`SuggestFollowUpsAsync` gates on exactly that (`AgentGrain.cs:664-667`), so draining it would pay one extra
discarded model call on every invocation — which is why `EnqueuePromptAsync` sets the flag false. Add a
trailing optional `interactive` parameter to `ChatAsync`, following the `ephemeral` precedent on the same
method, and pass false.

**C14 — The test host does not exist yet.** `SharedApiFixture` hard-codes `App.MapAgentApi()` with no hook
for an extra mapped group and no `AddMcpServer()` (`tests/Agent42.Tests/SharedApiFixture.cs:52-60`). Most ACs
need a host that maps the MCP surface, so building that fixture hook is real work, not a detail of step 5.

**Decisions the user made in the interview**
- Surface lives **in Agent42** as its own mapped group, not in the sibling `Expecho/mcpserver` repo. The
  sibling-repo variant was set aside because it needs a reply-returning endpoint here anyway, making it this
  work plus a second repo.
- Agent is identified by **the existing ADR 0022 per-agent key**, reusing the key-hash directory grain
  verbatim. No new credential type, and the tool list stays static.
- The tool call **blocks** with a bounded timeout, rather than a start-plus-poll pair.
- Turns are **listed** in the owner's sidebar, but **excluded from long-term memory**.

**Rejected alternatives**
- A2A instead of MCP: deferred by the user pending a stable .NET release. The blocker is shape, not
  maturity: the hosting package registers agents by name at startup and serves one card per host.
- A single endpoint exposing every agent of a user via a dynamic per-request tool list: needs a new
  user-level credential, and the SDK's dynamic-list handler API is unverified (see step 0).
- Suppressing memory with a turn-scoped field: see C3.

**Governing ADRs and gotchas to read before starting**
- `.agentic/decisions/0022-per-agent-api-key-and-async-run-endpoint.md` — the credential, the directory grain, the fail-closed 401 discipline
- `.agentic/decisions/0042-wildcard-easy-auth-exclusions-and-forwarded-proto.md` — the measured matcher behind C2
- `.agentic/decisions/0024-config-driven-remote-mcp-servers.md` — the client-side counterpart this inverts
- ADR 0062 — the prefix-middleware precedent behind C10
- ADR 0057 and ADR 0056 — the memory gate C3 relies on
- ADR 0046 — read it carefully; C6 is where the earlier draft misread it
- ADR 0016 and ADR 0068 — the continuous-agent brick and the prefix-shaped retention gate behind C11
- `.agentic/gotchas.md`: "An Easy-Auth `excludedPaths` endpoint is publicly reachable", "A grain's turn-scoped field lives for the ACTIVATION", "Remote MCP servers: a live-socket cache…" (its route-matcher third point, and its "deliberately not SSRF-guarded" note), "An agent tool's RETURN value is persisted", the continuous-agent 2 MB entry
- Exemplars to copy: `src/Agent42.Backend/Orleans/DashboardOwnerGate.cs` (the gate),
  `src/Agent42.Backend/Documents/DocumentIndexRegistration.cs` (registration shape),
  `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` (`ResolveAgentRunCallerAsync`, the uniform-failure shape)

## Steps

**Step 0 — Spike the SDK's server surface (blocking, do this first).**
Use the `spike-dotnet` skill, outside the repo tree, pinned to the resolved package version. Confirm, with
output pasted into the report: (a) `MapMcp("/api/agent-mcp")` accepts a route pattern, and **exactly which
routes it registers**, which settles C2's entry list and C10's prefix; (b) whether a `[McpServerTool]` method
can receive `HttpContext` in stateless mode; (c) that stateless is the default in this version. Note that
C10 makes (b) a convenience rather than a load-bearing requirement, since the gate resolves the key before
routing — but if (b) is false, say so, because the runner then needs the owner passed to it another way.
*Verify:* spike output quoted in the step report.

**Step 1 — Add the package.** Resolve the version with `dotnet add package ModelContextProtocol.AspNetCore`
(hard rule 4: never write a version from memory; `add_policy: latest-stable`). Expect it to match the existing
`ModelContextProtocol` pin; if it does not, report that rather than forcing alignment. Pin in
`Directory.Packages.props`, reference from `Agent42.Backend.csproj`.
*Verify:* `dotnet build Agent42.slnx`.

**Step 2 — The opt-in flag.** Trailing `[Id(10)] bool McpExposed = false` on `AgentDefinition`. It becomes the
**eleventh positional parameter** while both endpoint call sites pass nine
(`AgentApi.StoredAgents.cs:23,33`), so pass it **by name** or it is silently defaulted — the ADR 0065
`ToolDescriptor` precedent. Thread it through the upsert request, the save response and the editor UI.
`AgentBinding` does **not** gain it: exposure is routing, not persona, the call ADR 0016 made for `Continuous`.
*Verify:* round-trip assertion in `AgentMcpServerTests`; `GrainStorageSerializerTests` still green (a missing
property binding onto a positional record on the Cosmos JSON path is the real risk — ADR 0045's scar).

**Step 3 — Limits and the id shape.** `AgentMcpLimits` in `src/Agent42.Agents/Mcp/`: run timeout, max prompt
chars, max reply chars, the `mcp-` prefix constant and the exact-shape validator (C4.1, C8).
*Verify:* a small unit test for the validator, including `mcp-a/../b` and an `agent-` id.

**Step 4 — Retention.** Admit the MCP prefix to the ADR 0068 gate in `RetentionLimits` /
`AgentGrain.Retention.cs` (C11). This is a deliberate widening of ADR 0068's scope and must be called out in
the ADR, not slipped in.
*Verify:* a retention test asserting an `mcp-` conversation prunes on the same 24-hour rule.

**Step 5 — The fixture hook.** Give `SharedApiFixture` a seam for an extra mapped group plus `AddMcpServer()`
(C14), without changing what the existing eight fixtures map.
*Verify:* the whole suite still green before any MCP behaviour exists.

**Step 6 — The gate.** `AgentMcpKeyGate` middleware over the prefix, copied from `DashboardOwnerGate`:
require `X-Api-Key`, resolve it through `ApiKey.Hash` → `IAgentKeyDirectoryGrain` → registry, require
`McpExposed`, and fail closed with a **uniform** failure that does not disclose whether an agent exists.
Installed before the mapping, and before `UseDefaultFiles`/`UseStaticFiles` for the same reason ADR 0062
gives. Stash the resolved owner on `HttpContext.Items` for the runner.
*Verify:* `dotnet test` filtered to `AgentMcpServerTests` (the anonymous-`initialize` and wrong-key cases).

**Step 7 — The runner.** `AgentMcpRunner`: take the owner from the gate, validate or mint the conversation id
(C4.1), check the existing binding's agent id (C4.2), `TouchAsync` the index, `BindAgentAsync` with memory
filtered out (C3), then drain on a detached task bounded by the wait (C6) with `interactive: false` (C13), and
cap the reply (C8). Extract the resolve half to stay under the complexity gate, as `ResolveAgentRunCallerAsync`
does.
*Verify:* `dotnet test` filtered to `AgentMcpServerTests`.

**Step 8 — The tool surface.** `AgentMcpTools`: one `[McpServerTool]` `ask_agent(prompt, conversationId?)`
returning the reply and the conversation id so a caller can continue. The description is the contract the model
actually reads — ADR 0067's lesson — so it must state the id rule, the budget and that the turn is recorded by
the owner. Thin; delegates to step 7.
*Verify:* same test class.

**Step 9 — Ingress.** Add `/api/agent-mcp` and `/api/agent-mcp/*` to `globalValidation.excludedPaths` in
`infra/modules/container-app.bicep`, with rows in `EasyAuthExclusionTests` proving the prefix and a sub-path
are covered while `/api/agent-mcpzzz` is not. Parallelizable with step 10.
*Verify:* `dotnet test` filtered to `EasyAuthExclusionTests`.

**Step 10 — Frontend.** The editor checkbox, wired like `agentContinuousInput` (exported from `dom.js`, so
`ProjectLayoutTests.EveryNamedImport_IsActuallyExportedByItsTargetModule` binds). Offer the key regenerate
when the flag flips on (C12). Bump `sw.js`'s `CACHE`.
*Verify:* `dotnet test` filtered to `AgentEditorAssetsTests` and `ProjectLayoutTests`.

**Step 11 — Exercise it offline.** Run DevHost, tick the flag, and drive the endpoint with a real client
handshake. Record measured values: the routes `MapMcp` registered, an anonymous `initialize` refused, a
successful call, a wrong-key refusal, a rejected foreign id, a cross-agent id refused, a second call continuing
the same conversation, and a timed-out call whose turn still persists. Recent ADRs all record this leg; do not
skip it and do not claim it if a driver is unavailable.
*Verify:* observed output pasted into the step report.

**Step 12 — Documentation.** `/decide` for the ADR. It **amends** 0002/0022/0042 (hole list to six) and 0068
(the retention prefix), **extends** 0024 (first server-side MCP surface), and **relates to** 0056/0057 and 0070.
Headline consequences to state, not bury: C12's write-only-to-read upgrade, C11's two growth paths, and that
exposure grants an external caller whatever the owner's per-agent selection already allows — which for an agent
carrying the ToolForge runtime server reaches unsandboxed code execution. `/gotcha` for C6's misreading of ADR
0046 and C4's three-part defence. Update the MCP bullet in `CLAUDE.md`, then `/sync-docs`.
*Verify:* the doc-path integrity job named in `.github/workflows/pr-checks.yml`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | An exposed agent answers an `ask_agent` call with its reply text | `AskAgent_ReturnsTheAgentsReply` | `tests/Agent42.Tests/AgentMcpServerTests.cs` |
| AC2 | An anonymous protocol call (`initialize`, `tools/list`) is refused before routing | `AgentMcp_AnonymousProtocolCall_IsRefused` | same |
| AC3 | A missing or unknown key is refused uniformly, without disclosing whether the agent exists | `AskAgent_MissingOrUnknownKey_IsRefusedUniformly` | same |
| AC4 | A valid key for an agent with the flag off is refused identically to AC3 | `AskAgent_NotExposed_IsRefusedIdentically` | same |
| AC5 | A malformed or traversal-shaped conversation id is rejected, including an `agent-` one | `AskAgent_RejectsForeignConversationId` | same |
| AC6 | A valid key for agent A against a conversation bound to agent B is refused | `AskAgent_RejectsAnotherAgentsConversation` | same |
| AC7 | A second call with the returned id continues the same conversation | `AskAgent_ContinuesAnExistingConversation` | same |
| AC8 | The conversation is listed for the owner with the agent's identity | `AskAgent_ListsTheConversation` | same |
| AC9 | An MCP turn makes **zero** memory calls, recall and retain both | `AskAgent_MakesNoMemoryCalls` | same |
| AC10 | A timed-out call still persists its turn and leaves it readable | `AskAgent_TimedOut_StillPersistsTheTurn` | same |
| AC11 | No follow-up suggestion call is made on an MCP turn | `AskAgent_DoesNotGenerateFollowUps` | same |
| AC12 | A reply over the cap is truncated, and an over-long prompt is refused | `AskAgent_BudgetsPromptAndReply` | same |
| AC13 | The opt-in flag round-trips through upsert, storage and response | `McpExposed_RoundTrips` | same |
| AC14 | An `mcp-` conversation prunes on the 24-hour retention rule | `McpConversation_IsPruned` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC15 | Both new ingress entries cover the prefix and sub-paths, and no neighbouring stem | added rows | `tests/Agent42.Tests/EasyAuthExclusionTests.cs` |
| AC16 | The mapping is absent from the API composition root | `AgentMcp_IsNotMappedFromTheApiCompositionRoot` | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| AC17 | The editor checkbox is wired and its dom export exists | added assertions | `tests/Agent42.Tests/AgentEditorAssetsTests.cs` |

AC9 deliberately asserts the **effect**, not the selection list. The repo's own measured lesson on the memory
gate is that a list-shaped assertion is the weak one, and `MemoryDeselected_NeitherRecallsNorRetains` is the
fixture shape to copy. AC10 fails against the earlier draft's design, which is the point of having it. AC16
is a source-text guard and belongs in `ProjectLayoutTests` by convention, not in the behaviour class.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time.

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `.agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | `SonarAnalyzer.CSharp` S3776 at build time, at `error` |
| `vulnerable_packages` | 0 | CI package-freshness job |
| `outdated_packages` | report (soft) | CI |
| `add_policy` | latest-stable | step 1 resolves from the feed |

Enforcement: local is `soft_warning`, CI is `hard_block`. Expect steps 6 and 7 to press on the complexity gate;
the answer is extraction, per ADR 0050's note, never a ternary that buys the metric.

## Out of scope

- A2A. Deferred separately; this plan does not prejudge it.
- A dynamic per-user tool list, and one endpoint exposing every agent.
- Streaming or MCP progress notifications back to the caller.
- File or image arguments on the tool. The browser and `/api/agent-run` paths keep that job.
- OAuth on the server side. The per-agent key is the only credential.
- MCP resources and prompts. Tools only.
- Broadening an agent's own capabilities. Exposure **narrows** (memory is removed, C3) and grants nothing
  beyond the owner's existing per-agent selection (ADR 0021) — which is itself the thing to state plainly in
  the ADR, because that selection may already include unsandboxed code execution.
- A per-owner conversation cap for C11's index-item ceiling. Recorded as a residual.
- Rate limiting. A real gap, named as a residual rather than silently omitted.
- Telemetry attribution. On an excluded path there is no principal header, so MCP spend lands under the
  `local-dev` fallback in the cross-user usage dashboard (ADR 0031) and the content reaches `/logs` (ADR 0054).
  Pre-existing for `/api/agent-run`; noted, not fixed.

## Review

A `critic` subagent reviewed the first draft against the code. Its three high findings were confirmed
directly in the source before acceptance, and all were legitimate.

**Accepted and fixed**
- The protocol surface was unauthenticated; only the tool call self-enforced. Now C10 and AC2.
- `BindAgentAsync` is immutable-once-bound, so a namespaced id alone allowed a cross-agent hijack. Now C4.2
  and AC6. The first draft's sentence "id shape is the whole defence" was itself the defect.
- C6 had ADR 0046 backwards: abandoning the enumerator loses the turn, because the persist sits after the
  streaming loop inside the iterator. Rewritten; now AC10.
- `ChatAsync` sets `_interactiveTurn`, buying a discarded follow-up call per invocation. Now C13 and AC11.
- Flipping the flag upgrades an existing key from write-only to read. Now C12.
- `ConversationIndexGrain` is unbounded and ADR 0068's prune excludes the `mcp-` prefix. Now C11, step 4, AC14.
- Missing affected areas: `dom.js`, the fixture seam, `AgentEditorAssetsTests`, the positional-parameter trap
  on step 2, the retention files. All added.
- AC7 asserted the selection list rather than the effect. Replaced by AC9.
- The out-of-scope list contradicted C3 by claiming exposure changes nothing. Reworded.
- The `MapAgentApi` call-site count was ten, not eleven; one hit was a string literal in a test.

**Noted, not acted on**
- Self-reference through the owner adding this surface as one of their own MCP servers (the server list is
  deliberately not address-guarded). One sentence in the ADR; the timeout bounds it and C6 now makes the
  timeout safe.
- Telemetry attribution under the `local-dev` fallback, and that an external client can set the `X-MS-*`
  headers on an excluded path. Both pre-existing for `/api/agent-run`; recorded in Out of scope rather than
  fixed here, because fixing them is a change to that older surface too.

## Implementation notes — where this diverged from the plan

Executed 2026-09-11 on `feature/expose-agents-as-mcp-server`, from `main @ c2958c5` (the plan's
`planned-against` ref, so no drift). Shipped as ADR 0070.

**C2's premise was wrong; its conclusion survived.** The plan asserted that "`MapMcp(pattern)` registers
several endpoints under the prefix, including the prefix itself", and derived the two `excludedPaths`
entries from that. Step 0 measured otherwise: `MapMcp` registers **exactly one** route, `POST
/api/agent-mcp/`, and there is no `/sse` or `/message` sub-route (`EnableLegacySse` is false by default —
`GET /api/agent-mcp/sse` → 404). Two entries are still required, for a different reason: that single route
matches **both** `/api/agent-mcp` and `/api/agent-mcp/` (both answered 405 to a GET, so the route matched and
only the verb was wrong), while a bare entry matches exactly. The Bicep comment and ADR 0070 both record the
corrected reason rather than the plan's.

**One prerequisite the plan did not name.** `AddMcpServer()` alone makes `MapMcp` throw
`InvalidOperationException` at startup — `.WithHttpTransport()` is mandatory. Step 0's (c) question
("confirm stateless is the default") was answered in the affirmative and was where this surfaced.

**Step 0's (b) came back true**, so the runner takes the owner from `HttpContext.Items` via
`IHttpContextAccessor` as hoped, and the fallback the plan reserved was not needed.

**Two things were added that the plan did not list.**

1. `AgentMcpRunner.RunAsync` takes an **optional `TimeSpan? timeout`**, defaulting to
   `AgentMcpLimits.RunTimeout`. Without it AC10 is unreachable — the fake model cannot be made to run for
   two minutes — and AC10 is the test carrying the whole point of C6. Documented in the method as a seam,
   the `TimeProvider` precedent from ADR 0068.
2. `IsRetentionWindowed()` **replaces** `IsContinuousConversation()` rather than being widened in place.
   Leaving the old name while it matched two prefixes would have made the method's name a lie at the one
   call site that decides whether a conversation is ever pruned.

**Step 4 was larger than "one added constant."** `RetentionLimits`' own class remarks claimed continuous
chats were the only windowed kind, so the doc had to move with the gate (hard rule 2).

**Two controls were added beyond the AC table**, because AC9 and AC11 are absence assertions and would pass
just as well against a cluster where memory and follow-ups never fire at all:
`AskAgent_MemoryExclusionIsThisSurfaceOnly` and `AskAgent_FollowUpSuppressionIsThisSurfaceOnly` drive a
browser turn on the same cluster and assert the opposite. A third,
`AgentMcp_ToolsList_AdvertisesAskAgentToAValidKey`, is the same guard for AC2.

**Five mutations were checked** rather than the plan's implied none; the table is in ADR 0070's Verification.
The load-bearing one implements the plan's rejected design (stop pulling when the budget is spent) and turns
exactly one test red.

**AC12's outbound half is a direct unit assertion, not end to end.** The fake model cannot be made to emit
64 000 characters, so `CapReply` is exercised directly and the test says so in a comment rather than implying
coverage it does not have.

**Step 10 put the checkbox in the API panel, not General.** It is meaningless without the key beside it, and
the regenerate offer (C12) has to be adjacent to the control that triggers it. The plan said "wired like
`agentContinuousInput`", which is where the General panel would have put it.

**Two extra source-text guards** beyond AC16: `Hosts_GateTheAgentMcpSurfaceBeforeServingIt` pins, per host,
that the gate is installed and sits above the static-file middleware. AC16 alone proves the mapping is not in
the API composition root but nothing proves the gate came with it, and no test executes `Program.cs`.

**Two stale `agent42-shell-v33` assertions** live outside `PwaAssetsTests` (in `SearchAssetsTests`), which
the cache-bump step has to update too.

**Everything else went as planned.** Steps 1, 2, 3, 5, 6, 7, 8, 9 and 11 needed no deviation; step 11's
DevHost leg was run with a real MCP client and headless Chrome, and its measured values are in ADR 0070.

## Post-review changes (the `critic` pass on the finished diff)

Two blocking findings, both real and both fixed in-branch rather than deferred:

- **Three enumerations of the `excludedPaths` hole list still said "four"** — ADR 0002's amendment banner,
  the gotcha this plan names as required reading, and `CLAUDE.md`, which then contradicted itself two
  bullets apart. The plan's C2 said "there is no exact-list assertion to update, only added rows", which is
  true of the *tests* and misled the execution into thinking no doc debt existed. Hard rule 2 covers it.
- **A faulting turn had no handling.** `ChatCoreAsync` has no try/catch around `RunStreamingAsync`, so a
  model or provider failure throws out of the enumerator — and the raw exception would have reached an
  unauthenticated caller, against the "curated, never the exception" rule. C6 reasoned carefully about the
  timeout and not at all about a fault, and the ADR as first written claimed the empty-listed-conversation
  state was closed when it was closed only for the timeout. The fault is now curated and logged; the orphan
  it leaves is recorded rather than claimed closed, because `/api/agent-run` leaves the same one and closing
  it is a change to that older surface. `FakeChatClient` gained a `FaultingStreamMarker` so the branch is
  reachable at all, and two tests cover it.

Four smaller findings taken:

- `AgentMcpResult.IsError` was computed and never read — the tool returned a bare string, so every refusal,
  timeout and fault arrived as `isError: false`, indistinguishable from a reply. It now returns a
  `CallToolResult`. Mutation-checked: hard-coding `IsError = false` fails 7 of 31 tests.
- The timeout message said "call again with this conversationId to read it". `ask_agent` has no read mode —
  `prompt` is required — so following it queues a second billed turn behind the first on a non-reentrant
  grain. Reworded to say exactly that.
- `Task.Delay(timeout)` was never cancelled, leaving a 120-second timer rooted after every successful call
  on a path with no rate limiting.
- `CapReply` returned `MaxReplyChars + 13`. The marker is now counted inside the cap, and the test asserts
  `<= MaxReplyChars` rather than merely "shorter than the input".

Two findings were taken as documentation rather than code: no away-reply push fires on this path (ADR 0028's
rule is keyed to the two async paths and `ask_agent` drains `ChatCoreAsync`) and the retention gate matches a
bare `mcp-` prefix while the id validator requires the exact minted shape. Both are defensible as built and
both now say so in ADR 0070's Consequences. The uniform-401 claim was softened to "uniform in status and
body", since the resolve path has three distinguishable cost tiers.

Final: **1275/1275 green**, lint clean, new-code coverage 95.0% against a threshold of 80.
