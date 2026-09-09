---
id: 0070
date: 2026-09-09
status: accepted
tags: [mcp, tools, code-execution, sibling-repo, no-migration, no-infra, egress]
supersedes: []
extends: [0024, 0026, 0058]
amends: []
---

# ToolForge: an agent writes, tests and publishes its own tools

> **No Agent42 code changes.** The build lives in the sibling repo Expecho/mcpserver, as
> ToolForge.Core and ToolForge.Mcp beside Calendar.Mcp, behind the existing Mcp.Gateway. On this
> side it is configuration: two MCP server entries (ADR 0024) and two stored agents. This ADR exists
> because the decisions bind Agent42's threat model and its tool-name space, and because four calls
> reverse the merged design note `.agentic/specs/plan-agent-authored-tools.md`.
>
> **Paths into the sibling repo are named bare, never in backticks** — `check-doc-paths.mjs` scans
> every ADR and would fail CI on a path that does not exist in *this* repo. Same rule ADR 0063 follows.

## Context

Agent42 can already *select* tools at three levels of granularity (ADR 0021/0026/0048/0058), but every
tool it can select was written by a human and shipped in a release. The user asked for agents that
gain capabilities without a repo edit or a deploy.

The design note settled the architecture and was merged (PR #121). Building it surfaced four calls the
user made explicitly, two of which reverse that note. They are recorded here rather than left as drift.

## Decision

**A dedicated Tool Builder agent writes Python, runs it to test it, reads the traceback, fixes it, and
publishes it. Every other agent of that user can then call the result.**

Two MCP servers, two host names, two ports, one process, one registry:

- an **authoring** surface carrying exactly four tools — write, run, list and delete — given only to
  the builder agent, and
- a **runtime** surface carrying **only** promoted tools and no authoring tool at all, given to
  consumer agents.

Privilege separation is `AgentDefinition [Id(8)] EnabledTools`, which already expresses "only this
agent may author code" — no new mechanism.

### The four calls, and what they reverse

- **The GitHub Copilot SDK is NOT the executor** (reverses design-note D5). The builder's intelligence
  is any model the user has already selected (ADR 0018/0013); the forge contains no LLM. This deletes a
  package, a PAT secret, a CLI runtime and the design note's open packaging question.
- **No sandbox and no execution gate** (reverses the design note's execution-gate scope, and with it
  **design-note step 7** — deny-by-default permission handling, container-per-run, no-network default,
  resource caps — and **design-note AC4**, "a generated tool attempting a shell command outside the
  whitelist is denied". Neither survives). Generated code runs as a plain subprocess with full network,
  filesystem and arbitrary dependencies.
- **Python**, not C# file-based apps, for model fluency.
- **CodeAct plus Hyperlight is rejected**, with a re-entry trigger. See Alternatives.

### What binds Agent42

### This does NOT reverse ADR 0049's script ban

ADR 0049 blocks scripts in uploaded skills **twice** — the normalizer drops them, and the provider is
handed an empty `AllowedScriptExtensions` so the framework refuses to discover them. **That decision
stands, unweakened, and this ADR does not touch it.** It has to be said out loud, because "generated
code runs as a plain subprocess with no sandbox" reads like a reversal of it otherwise.

The distinction is *where the code runs*. ADR 0049's ban is about executing script content **inside
the Agent42 process**, next to the conversation, the DP-protected secrets and the managed identity.
Here execution leaves that process entirely: a different container, on different hardware, reached
over HTTP, holding none of those. Nothing in this work makes an uploaded SKILL.md's scripts runnable,
and if that is ever wanted it is ADR 0049's decision to revisit, not this one's.

### A reserved-name list is the only defence — against *static* Agent42 names

**Enforced at write time, because there is nowhere else.**
`src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` merges MCP tools into the *same flat list* as the
static and always-on tools with **no name de-duplication**, and `src/Agent42.Agents/Tools/ToolCatalog.cs`
keeps an unmapped dynamic MCP name **unconditionally** — so nothing on this side filters a collision
out. The builder chooses the name, so the forge refuses every static Agent42 tool name (`get_date`,
`web_search`, `fetch_web_page`, `read_web_document`, `code_interpreter`, `get_current_location`, the
three scheduling names and the three skills names) plus its own four. **A tool added to Agent42 in a
later release must be added to that list in the sibling repo**, or a previously-written tool can
shadow it.

**The list does not cover a collision with ANOTHER MCP server's tool, and that residual stays open.**
A written tool named `list_events` collides with the calendar server's tool for any agent that has
both selected, and nothing on either side rejects it — the forge cannot know what other servers a
user has configured, and Agent42 does not check. It is narrower than the static case (it needs two
servers selected together, and the names are visible in the tools picker) and it is not closed.

**The result budget is 8 KB**, matching `McpToolLimits.MaxToolResultLength`, because a tool's return
value is persisted into the 2 MB session (ADR 0004). Per ADR 0067's lesson, the rule the model actually
obeys lives in the **tool descriptions**, which travel in the JSON schema every turn — so the budget,
the "return, do not print" contract and the reserved-name rule are all written there, not only in a
document a model may never read.

## Consequences

**The headline is a new and very large one: the gateway API key is the only thing between the public
internet and arbitrary code execution on the operator's hardware.** With no sandbox, the design note's
C9 chain — injected text becomes a written tool becomes a durable capability re-run forever — is fully
open. The builder agent's narrow tool set is the only mitigation, and it is a real one: the builder has
no web tools and no memory, so the usual inbound injection channels (ADR 0027 followed links, ADR 0024
tool results, ADR 0049 uploaded skills) do not reach it by default. Accepted explicitly by the user.

**Three configuration rules are part of the decision, not deployment detail.** Getting any of them
wrong reopens the door the two-surface split exists to close:

- **`SeedsFetchableUrls` stays OFF on both servers** (ADR 0067). A tool the model wrote returning URLs
  the model chose, seeded into the fetch allowlist, is the laundering step ADR 0067 describes with the
  authoring loop removed from it.
- **The builder agent gets no per-agent API key** (ADR 0022). `/api/agent-run` is on `excludedPaths`
  and self-enforcing, so a holder of the *builder's* key can push a prompt that writes and runs code.
  It is the most sensitive key the system could mint; the answer is not to mint it.
- **ToolForge gets its own server API key**, not the one the scraper already uses. They are separate
  containers and the key is a per-container value, so sharing it would hand arbitrary code execution
  to everyone who was given a key for scraping web pages, and tie the two rotations together.

**A promoted tool is a fourth durable-injection surface in the ADR 0056 sense.** Its output enters a
conversation like any MCP result, so it can be retained into a memory bank with no delete path.

**The builder's narrow tool set protects the author, not the arguments.** Consumer agents *do* have
web tools and memory, and their arguments reach the Python process. So a single written tool that
passes an argument into `eval`, `os.system` or a shell hands the runtime surface to every inbound
channel Agent42 has — ADR 0027's followed links, ADR 0024's tool results, ADR 0049's uploaded skills.
Nothing inspects a written tool for that. It is a consequence of no-sandbox rather than a separate
decision, but it is the specific door, and ADR 0067's discipline is to name it.

**Egress is unchanged in kind but new in extent** (ADR 0013/0024): conversation content reaches a
server the user configured, as with every MCP server. What is new is that the *content of the tool* is
model-written, so what leaves is no longer bounded by what a human wrote.

**ToolForge gets its own container, and that is not a sandbox.** Mcp.Gateway runs the scraper, calendar
and proxy as three web applications in one process whose environment holds the gateway API key and the
Google OAuth client id and secret; generated code there would read them out of the process environment.
Its own container removes that, and nothing else — inside it the code still has full network and
filesystem.

**Two operator-facing consequences.** The gateway now requires two more host-name variables and exits 2
without them, so the compose file, the environment sample and the existing CI job all had to change.
And `docker compose up` becomes the documented deployment rather than a preference: ToolForge is reached
by service name, which Docker resolves only on a user-defined network, so a bare run of the gateway
comes up healthy and fails *only* the two ToolForge host names.

**Promotion is one user action, not two** (ADR 0069): pressing Test on the runtime server invalidates
that user's cached tool set and records the tool names in the same press. Two carve-outs still apply — a
failed test and a **zero-tool** connect invalidate nothing, and a runtime surface with no tools written
yet is exactly that zero-tool case.

**Unverified residual, stated plainly.** The forge itself is measured end to end, including the fix
loop and a third-party dependency install. The Agent42-side questions (M1–M5 in the design note's
Measurements section) were **not run**. The blocker is a **configured chat model**, not deployment:
these measurements run against ToolForge on loopback via the AppHost, and public DNS gates production
use rather than measurement. Every one of M1–M5 needs a real model to reason — write a buggy tool,
read the traceback, fix it — and the offline canned client cannot, so this waits on an operator
session with their own provider credentials. In particular, whether a **warm** consumer chat picks up
a newly promoted tool without deactivating remains unobserved: the same residual ADR 0069 recorded,
and still the one cache nothing pins.

## Alternatives considered

- **The GitHub Copilot SDK as executor** (design-note D5). Reversed: a package, a PAT secret and a CLI
  runtime to do what the user's already-selected model does.
- **CodeAct plus Hyperlight.** Rejected on three counts: it needs KVM or Hyper-V on the host; it
  reimposes the isolation this ADR rejects, and harder (no libraries, no network without an allowlist);
  and decisively, its capability model bridges back to host-registered tools, so generated code could
  only compose capabilities already written in C# — inverting a forge whose purpose is adding
  capabilities without a repo edit. **Re-entry trigger: if the no-sandbox call is ever walked back,
  re-evaluate this before any hand-rolled sandbox.**
- **C# file-based apps.** Rejected for model fluency; it would have avoided needing a second Dockerfile
  and a Python toolchain entirely.
- **One MCP server instead of two.** The two surfaces differ only in which tools they advertise, so a
  single server would hand consumer agents the authoring tools. The separation is the privilege
  boundary, and it is asserted by tests on both sides.
- **A stubbed-executor slice first.** Abandoned by the user: the registry, both surfaces, gateway
  wiring, Docker and CI are identical either way, so slicing saved only the executor while costing a
  throwaway build and a second planning round.
