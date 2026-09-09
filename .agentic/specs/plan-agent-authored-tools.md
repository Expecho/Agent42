# Plan — Agents author their own tools

planned-against: main @ 05e1982

**Status: DESIGN NOTE, not an executable plan.** The Agent42 side of this is *configuration*
— two MCP server entries and one stored agent — so there is no per-step `/execute-plan` shape
to gate. The build lives in the sibling repo `Expecho/mcpserver`. This file exists to hold the
decisions and the non-obvious constraints before they age out of a session.

## Overview

**Goal:** an agent can write a new tool and then use it, without a human editing this repo.

**Why now:** the user asked whether agents can be given the capability to write their own
tools. Nothing in `.agentic/` covers it — no ADR, no spec, no branch (verified at `05e1982`:
`grep -ril "write_tool\|tool forge\|agent-authored\|Copilot SDK" .agentic/` → no matches).
The ten `*tool*` branches are all about *selecting* tools that already exist (ADR
0021/0026/0048/0058), never about authoring new ones.

**Shape:**

```
  ┌─ Agent42 (unchanged code) ────────────┐   ┌─ gateway host (your hardware) ──────────┐
  │                                        │   │                                          │
  │  "Tool Builder" agent                  │   │  forge-authoring  (MCP)                  │
  │   EnabledTools = [mcp:{authoringId}]  ─┼──►│    write_tool / run_tool                 │
  │                                        │   │    list_tools / delete_tool              │
  │                                        │   │         │                                │
  │                                        │   │         ▼                                │
  │                                        │   │   Copilot CLI runtime in a container     │
  │                                        │   │   (shell + file ops, deny-by-default,    │
  │                                        │   │    gated by OnPermissionRequest)         │
  │                                        │   │         │                                │
  │  every other agent                     │   │         ▼                                │
  │   EnabledTools = [mcp:{runtimeId}, …] ─┼──►│  forge-runtime  (MCP)                    │
  │                                        │   │    the generated tools, first-class      │
  └────────────────────────────────────────┘   └──────────────────────────────────────────┘
                                                              │
                                          promotion (optional, slower, PR-gated)
                                                              ▼
                                    Copilot cloud agent REST API → PR in Expecho/mcpserver
```

## Decisions taken in the planning conversation

The user's explicit calls. Do not relitigate; if one must change, that is a new round.

- **D1 — Executable tools, out-of-process, behind MCP.** Not in-process. See C3.
- **D2 — A dedicated builder agent.** The user's idea, and the best structural move here:
  `AgentDefinition [Id(8)] EnabledTools` already expresses "only this agent may author code",
  so privilege separation needs no new mechanism.
- **D3 — TWO MCP servers, not one** — `forge-authoring` and `forge-runtime`. See C6; this
  does not work with a single server.
- **D4 — Dispatch for the builder, promoted first-class tools for consumers.** Each role gets
  the property it needs and pays with the one it does not. See C1/C2.
- **D5 — The Copilot SDK agent is the executor**, containerized on the gateway. Not a
  hand-rolled sandbox. The Copilot *cloud* agent REST API is the optional promotion path.
- **D6 — Copilot Studio is rejected** (the user raised it first; the disambiguation to GitHub
  Copilot came after). See "Rejected alternatives".

## Affected areas

**Agent42 — no code.** Verified against the tree at `05e1982`: `BuildAgent` already merges
tools from `_mcpServers` with no per-server code, gates them on the three-level `mcp` ⊃
`mcp:{id}` ⊃ `mcp:{id}:{tool}` hierarchy (ADR 0026/0058), truncation-wraps every result
(ADR 0024), and `GET /api/tools` already renders per-tool checkboxes from `KnownToolNames`.

What the user does instead of an MR:
- Preferences → MCP servers: add `forge-authoring` and `forge-runtime`.
- Leave `SeedsFetchableUrls` **off** on both (ADR 0067) — see C9.
- Create the "Tool Builder" stored agent with `EnabledTools = [mcp:{authoringId}]` only.
- Consumer agents get `mcp:{runtimeId}` (the coarse key — see C6).

**New — `Expecho/mcpserver`:** a `ToolForge.Mcp` beside `Calendar.Mcp`, behind the existing
`Mcp.Gateway`. Tool registry, the Copilot-backed executor, the permission policy.

## Context & constraints

### C1 — The lag is NOT "until the next activation". There are TWO caches in series.

This is the crux and the easiest thing to get wrong.

```
forge server          UserMcpToolProvider._caches[userId]        AgentGrain._mcpServers
(real tool list)  →   keyed by overlay Version, process-wide  →  snapshot taken in
                      (a hit returns WITHOUT connecting)          OnActivateAsync
```

The overlay `Version` bumps only on a **config write** (`IUserMcpConfigGrain.SaveAsync`), and
`RecordToolNamesAsync` deliberately does **not** bump it. So a new tool appearing on an
already-configured server produces no bump, and even a brand-new grain activation takes a
cache hit and returns the **stale** list. Both caches must turn over.

**Corrected 2026-09-09 — ADR 0069 made this ONE action, and the paragraph below used to say two.**
Pressing **Test** on the runtime server now does both halves: `McpConnectionTester` calls
`IUserMcpConfigGrain.InvalidateToolCacheAsync()` *first* — unconditionally, not gated on the names
having changed — and only then the non-bumping `RecordToolNamesAsync`. So the cached tool set turns
over and the picker's checkboxes appear from the same press. The superseded advice was: "touch the
server config (any save) → version bumps → next activation reconnects; *then* press Test."

Two carve-outs from ADR 0069 survive and still bite here. A **failed** test invalidates nothing. So
does a **zero-tool** connect: the tester returns "connected, but the server exposes no tools" above
the invalidation, which is exactly the state a runtime surface is in before the first tool is
written — so the very first promotion may still need a second Test once a tool exists.

**This is not purely a cost.** It makes promotion a deliberate act, so nothing reaches consumer
agents by accident.

### C2 — Dispatch is immune to both caches, which is the whole reason the builder uses it.

`write_tool` / `run_tool(name, args)` / `list_tools` / `delete_tool` are a **constant** tool
list, so a stale cache is still a *correct* cache and nothing needs invalidating. The knowledge
of "what tools exist" moves to the far side of both caches, into the forge's own registry.

That is what makes write → run → read error → fix possible inside one turn. Without it the
builder writes a tool it cannot test until a config save and a new chat — unusable.

The consumer's trade is the mirror image: it did not write the tool, so it needs the real JSON
schema, and new entries in the tool *list* must cross both caches.

|          | Knows the schema?      | Needs it now? | Gets                                |
|----------|------------------------|---------------|-------------------------------------|
| Builder  | yes — it just wrote it | **yes**       | dispatch, no schema, zero latency   |
| Consumer | no                     | no            | first-class tool, full schema, lag  |

### C3 — Never run the Copilot CLI runtime inside the Agent42 container.

Microsoft's own guidance is to run agents with shell or file permissions in a containerized
environment. The Agent42 container fails that badly: shell and file access there sit inside the
Orleans silo holding managed identity, Cosmos, blob storage, the DP key ring and every user's
grain factory. `infra/main.bicep` pins `minReplicas = maxReplicas = 1` (ADR 0054), so it is
**one container shared by all users**, and `AgentGrain` is non-reentrant, so a shell command
blocks the whole turn. The MCP process boundary is the entire point of D1.

### C4 — Agent42 has NO approval loop. The gate lives outside it.

Deliberately: it is what makes `run_skill_script` safe today (`DisableRunSkillScriptApproval`
stays false, so it is an `ApprovalRequiredAIFunction` and invoking it throws, because no
`AgentFileSkillScriptRunner` is registered — the gotcha says "do not clean up either").

The Copilot SDK brings its own: `SessionConfig.OnPermissionRequest` returning a
`PermissionDecision`, deny-by-default for shell/file/URL. It is a **programmatic policy hook**,
not necessarily an interactive prompt, so a whitelist policy needs no human in the loop.

**`ApprovalRequiredAIFunction` behaves oppositely in the two hosts** — inert-and-throwing in
Agent42, honoured via a default `OnPreToolUse` hook in the Copilot SDK. Do not reason about one
from the other. And note the SDK's warning: supply your own `OnPreToolUse` and the default
approval hook is *not* installed; you own enforcement entirely.

### C5 — This revisits ADR 0049's script ban. Cite it; do not silently contradict it.

ADR 0049 blocks scripts **twice** — the normalizer drops them and `AllowedScriptExtensions = []`
makes the framework refuse to discover them. That decision stands **for skills inside this
process**. This plan does not weaken it: execution moves out of the process entirely. Any ADR
written for this work must say so explicitly, or it reads as a reversal.

### C6 — Privilege separation requires the SERVER split, not per-tool keys.

`mcp:{serverId}` grants **all** tools of that server, `write_tool` included. So a single forge
server forces per-tool keys on every consumer — and then every newly generated tool needs an
edit to every consumer agent definition, which does not scale.

Two servers make separation structural: consumers select the coarse `mcp:{runtimeId}` and
auto-inherit new tools forever, while `write_tool` is simply not on that server. Bonus: a
*disabled* server is never dialled (ADR 0064), so the authoring surface can be switched fully
off between building sessions.

### C7 — The builder agent's API key is a code-execution key.

ADR 0022 mints a per-agent key and `/api/agent-run` is on `excludedPaths` — publicly reachable
and self-enforcing. A holder of the **builder's** key can push a prompt that writes and runs
code. Treat it as the most sensitive key in the system, or do not mint one.

### C8 — The rule the model obeys lives in the TOOL DESCRIPTIONS.

ADR 0067's lesson, and it applies directly: update only a skill and a compliant model behaves
differently while every test passes. `McpToolLimits.MaxToolResultLength` is **8 KB**, so
generated tools are for summaries and computations, not data dumps — and that belongs in the
forge's own tool descriptions, which the model reads every turn.

### C9 — A new, sharper member of ADR 0056's durable-injection list.

Chain: injected text (an ADR 0027 followed link, an ADR 0024 MCP result, an ADR 0049 uploaded
SKILL.md) → the agent writes a tool → the tool persists and runs on every future call. Worse
than ADR 0067's URL seeding, which launders *data*; this launders *code*.

D2's privilege separation is the mitigation that matters: only one agent, with a deliberately
narrow input surface, can author. Hence C12.

### C10 — Package resolution is unresolved, and must not be recalled (CLAUDE.md rule 4).

Sources disagree: Learn shows `dotnet add package Microsoft.Agents.AI.GitHub.Copilot` plain;
GitHub's docs show `GitHub.Copilot.SDK` **plus** `Microsoft.Agents.AI.GitHub.Copilot
--prerelease`; a devblog claims v1.0 stable in C# and Python. Resolve with `dotnet add package`
in the sibling repo. Affects `mcpserver` only.

### C11 — A cold forge container stalls `OnActivateAsync`.

`UserMcpToolProvider.ConnectTimeout` is **10 s per server, sequentially**, awaited by the
non-reentrant `OnActivateAsync`. Two forge servers that are both cold cost up to 20 s on the
first chat after an idle period. Keep the containers warm, or accept it.

### C12 — The builder must have the SMALLEST tool set in the app.

Counterintuitive but load-bearing: it is the one agent that can write code, so it must not also
carry the widest inbound injection surface. No `web_search` / `fetch_web_page` /
`read_web_document`, and **no `memory`** — retain has no delete path (ADR 0056) and the
builder's transcripts are source code. A new agent's `EnabledTools` is an explicit list, so
memory (on by default elsewhere) is off unless ticked. Do not tick it.

### C13 — Continuous mode suits the builder, with one caveat.

One ongoing conversation about the tool library is the right shape (ADR 0016), and ADR 0068's
24 h window now bounds it. But the window bounds **age, not size** — one oversized tool source
in a single turn can still brick inside the day. The 8 KB truncation covers the tool-*result*
half only.

## Steps

**Agent42 (configuration, no MR):**
1. Add `forge-runtime` in Preferences → MCP servers; press Test; confirm per-tool checkboxes.
2. Add `forge-authoring`; leave it disabled between building sessions.
3. Create the "Tool Builder" stored agent — `EnabledTools = [mcp:{authoringId}]`, continuous,
   no web tools, no memory (C12). Do not mint an API key (C7).
4. Add `mcp:{runtimeId}` to the consumer agents that should see generated tools.

**`Expecho/mcpserver`:**
5. `ToolForge.Mcp` skeleton behind `Mcp.Gateway`, two endpoints (authoring / runtime).
6. Tool registry + persistence, scoped by the credential Agent42 presents (Header auth is the
   simplest start).
7. The Copilot-backed executor in a container: deny-by-default `OnPermissionRequest`, a
   whitelist policy, wall-clock timeout **shorter than** Agent42's 10 s `ConnectTimeout` (C11),
   no network by default, read-only FS, memory/CPU caps.
8. Promotion: expose registered tools as first-class MCP tools on `forge-runtime`.
9. *(Optional, later)* the Copilot cloud agent REST path → PR into `mcpserver`.

**Back in Agent42, after it works:**
10. `/decide` an ADR. It must cite ADR 0049 (C5), ADR 0056 (C9), ADR 0024/0026/0058 (the path
    it rides), and record the security position as the substance.

## Acceptance criteria

- **AC1** The builder writes a tool and calls it **in the same turn** (C2).
- **AC2** A consumer agent cannot call `write_tool` — it is not on its server (C6).
- **AC3** A tool generated after a consumer agent was defined is picked up with **no edit** to
  that agent's definition (the coarse-key property, C6).
- **AC4** A generated tool attempting a shell command outside the whitelist is **denied** by
  the permission policy, and the denial is visible to the builder (C4).
- **AC5** Disabling `forge-authoring` in Preferences means no authoring tool is offered to any
  agent, and the server is never dialled (ADR 0064).
- **AC6** A tool result over 8 KB is truncated, not persisted whole (C8, ADR 0024).

## Open questions

- **Q1 — Which gate?** The permission-policy (fast) path, the PR (cloud-agent) path, or both.
  Both compose; picking neither means unreviewed execution, which is defensible for a
  single-operator project **but must then be written down rather than assumed**.
- **Q2 — Package resolution** (C10). Resolve in `mcpserver`, not from memory.

## Out of scope

- Any Agent42 code change. If one becomes necessary, this stops being a design note and needs
  a real `/plan`.
- In-process execution, in any form (C3).
- Copilot Studio (see below).
- A same-turn path for *consumers*. The ADR 0066 deferred-rebuild shape would be the way, and
  it is deliberately not taken: D4 gives the immediacy to the only role that needs it.

## Rejected alternatives

- **In-process code execution in the silo.** C3. Also reverses ADR 0049 (C5).
- **Copilot Studio** (`Microsoft.Agents.AI.CopilotStudio`). The client "does **not** expose
  Agent Framework tool types (function tools, code interpreter, file search, hosted/local MCP)
  at the client" — so `BuildAgent` becomes moot and the conversation loses RAG, memory, skills,
  scheduling, location, every user MCP server and image generation at once. It is also a
  *human* low-code authoring portal, so it does not do tool-building in any case.
- **Agent writes a SKILL.md instead of code.** Genuinely cheaper (reuses the ADR 0049 upload
  path, zero execution) and still a good idea on its own — but it produces reusable
  *procedure*, not a callable tool. Kept as a possible companion, not a substitute.
- **`code_interpreter` alone** (ADR 0015, already shipped). Per-turn scratch code with no
  schema, name or reuse. It is the answer to "compute something", not to "gain a tool".

## Measurements

Recorded 2026-09-09, while executing `plan-toolforge-mcp.md`. **The two halves are at very
different levels of evidence, and the difference is the point of this section.** The forge itself
was built and measured end to end; the Agent42-side cache questions (M1–M5) were **not run** and
must not be read as answered.

### Measured — the forge (Repo A, `feature/toolforge-mcp`)

| What | Value | How |
|---|---|---|
| Runtime tool mutation is real | a tool added after `StartAsync` is listed, callable, and gone after removal | throwaway server, real `tools/list` over JSON-RPC |
| The promotion API | `McpServerOptions.ToolCollection` (mutable), **not** a list-tools handler | same spike |
| A stored JSON-schema string → a tool | subclass `AIFunction`, override `JsonSchema`; `McpServerToolCreateOptions` has `OutputSchema` but **no input-schema property**, and `AIFunctionFactory` derives a schema by reflecting over parameters a runtime tool does not have | same spike |
| Reaching it through the host seam | `builder.Services.Configure<McpServerOptions>(o => o.ToolCollection = …)` off `IMcpServerBuilder.Services` | same spike |
| Promotion latency, server side | immediate — written on `:8080`, advertised on `:8081` on the very next `tools/list`, no restart | two surfaces in one process, one registry |
| Requirements install once, then reuse | first run **5.5 s**, second **0.72 s**; marker count stays **1** | `httpx` in the built image |
| Suite | 128 passed, 0 failed, 0 warnings, Release | `dotnet test McpServer.slnx -c Release` |

**One planning assumption was wrong.** K12 says "`python3` is not on the Windows dev machine". A
real Python **3.11.9** is at `C:\Python311\python.exe`; what is absent is the *name* `python3` —
the one on `PATH` is the Windows Store stub, which is why an earlier `python3 -c` failed. So the
executor tests ran locally rather than skipping, and the interpreter probe resolves `python.exe`
on Windows. The skip mechanism is kept: it is a capability probe, not a Windows check, and the
network half of K12 is untouched — a pip install still belongs only in the image smoke job.

**A per-call budget does not exist.** `HttpClientTransportOptions` exposes `ConnectionTimeout`,
documented as covering only "the initial connection to the SSE server", and **no** per-request
property. The real ceiling on the caller's side is the transport `HttpClient`'s 100 s default. So
the forge's run timeout is **chosen, not derived**: 30 s, well under that, so the forge kills the
process and returns a traceback rather than the client giving up first and leaving an orphaned
process and no diagnosis. The cost is stated rather than avoided — `AgentGrain` is non-reentrant
for the whole turn, so a tool that runs the full window blocks that conversation for that long.

**The network shape (H3) is settled and verified.** On the compose network the gateway resolves
`toolforge` to a container address and reaches both surfaces (`/health` → 200 on 8080 and 8081).
On the default bridge a plain `docker run` joins, the same name does **not** resolve. `docker
compose up` is therefore the documented deployment, and the README now says why.

### NOT measured — the Agent42 side (M1–M5)

**Not run. No values.** Nothing below should be cited as an answer.

**The blocker is a configured chat model, not deployment** — an earlier draft of this section said
"real DNS", which contradicts K8 of the executing plan: the measurement target is ToolForge on
**loopback via the AppHost**, and public deployment gates production use rather than measurement.
What actually blocks every one of M1–M5 is that they need a model to *reason* — write a buggy tool,
read its traceback, fix it — so they need an operator session with their own provider credentials.
DevHost cannot stand in: it registers a single-user tool provider and an offline connection factory,
so it never reads the config grain or performs a handshake, which is exactly what M3 and M4 are about.

| | Question | Status |
|---|---|---|
| M1 | Builder writes a buggy tool, reads the traceback, fixes it, runs it green — one conversation | **not run** |
| M2 | A tool whose `requirements` name a third-party package installs and runs *through Agent42* | **not run** (proven server-side; the Agent42 leg is not) |
| M3 | After pressing Test on the runtime server, does a **new** conversation see and call the tool? | **not run** |
| M4 | Does a **warm** consumer chat see it without deactivating? *Predicted no.* | **not run** — still the cache nothing pins |
| M5 | With the authoring server's `Enabled` unticked, is the write path gone while runtime tools still work? | **not run** |

The forge half of M1 and M2 *is* proven: the fix loop (broken → `ZeroDivisionError` → rewritten →
green) and a third-party import both run in the test suite and in the built image. What is unproven
is Agent42's behaviour around them — which is exactly where C1's two caches live.
