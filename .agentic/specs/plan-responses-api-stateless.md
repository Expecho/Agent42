# Plan: Responses API support (stateless) for OpenAI-compatible providers

planned-against: claude/responses-api-support-s34w64 @ 7e79e2d

> Base note: planned on the designated feature branch (not `main`); it currently
> carries only ADR 0014. Executor branches from here.

## Overview
Add a per-model `UseResponsesApi` option (ADR 0014). A model with the flag set is built
through the OpenAI **Responses** API run **statelessly** (`store=false`) instead of Chat
Completions, so hosted server-side tools become reachable later (ADR 0011 context) while
Agent42 keeps holding all conversation history client-side (ADR 0004). Enabled on
**OpenAI-compatible (API-key) providers only** — that path needs no package change
(`OpenAI 2.12.0` already exposes the Responses client). Foundry is out of scope: it would
force a prerelease `Azure.AI.OpenAI` (see ADR 0014).

## Governing docs
- `/.agentic/decisions/0014-responses-api-stateless-mode.md` — the decision, incl. the
  stateful-default trap and the version findings.
- `/.agentic/decisions/0013-multi-provider-model-catalog.md` — the catalog this extends;
  per-model capability flags, the config-only-endpoint SSRF invariant, capability gating.
- `/.agentic/gotchas.md:166` — named HttpClient inherits AddServiceDefaults' retry
  pipeline. Relevant as a *do-not*: see Constraints.

## Affected areas (verified against the tree)
- `src/Agent42.Backend/ModelProviderOptions.cs` — add `UseResponsesApi` to `ConfiguredModel`.
- `src/Agent42.Backend/ChatClientFactory.cs` — branch in `Provider.OpenAICompatible`'s client
  lambda: Responses-vs-Chat per model.
- `src/Agent42.Backend/ModelProviderBuilder.cs` — no signature change expected; confirm the
  flag flows (the factory reads `ProviderOptions.Models`, which already reaches it).
- `tests/Agent42.Tests/ModelProviderTests.cs` — new cases (see AC table). Exemplar for style;
  note its comment: constructing an OpenAIClient/response client does **no network I/O**.
- `src/Agent42.Backend/appsettings.json` — document the flag by example only if a shipped
  provider opts in; otherwise leave prod config unchanged (out of scope to enable in prod).
- `scratchpad/responses-spike.cs` — the runtime confirmation harness (Step 1).

## Context & constraints (not re-derivable cheaply)
- **Reuse the existing `OpenAIClient`.** The Responses path is
  `client.GetOpenAIResponseClient(model).AsIChatClientWithStoredOutputDisabled(model)` on the
  **same** `OpenAIClient` already built in `Provider.OpenAICompatible`. Do **not** introduce an
  `IHttpClientFactory`/named `HttpClient` — that would inherit the retry pipeline (gotchas.md:166)
  and multiply a stall on the non-reentrant grain. Same reasoning the existing comment at
  `ChatClientFactory.cs:127-133` gives.
- **Stateless is mandatory, not a default to override.** `AsIChatClientWithStoredOutputDisabled`
  (`includeReasoningEncryptedContent: true`) — never the plain `AsIChatClient()` on the response
  client, which stores server-side and breaks the `TryGetInMemoryChatHistory` invariant the grain
  is built on and egresses content outside the tenant (ADR 0014 Consequences).
- **The adapter is `[Experimental("OPENAI001")]`.** Scope a `#pragma warning disable OPENAI001`
  to the call site (or a targeted `NoWarn`); do not blanket-suppress.
- **SSRF invariant holds unchanged:** the endpoint still comes only from config
  (`Provider.OpenAICompatible(..., endpoint, ...)`); this change reads a bool, adds no new
  network input. Keep it that way (ADR 0013 "Not an SSRF sink").
- **OTel wrapper is untouched:** `Build()` still wraps whatever `provider.Client(model)` returns
  in `UseOpenTelemetry` — the branch is *inside* the client lambda, below the wrapper.
- **Capabilities are orthogonal.** `UseResponsesApi` selects the wire API; `SupportsTools`/
  `SupportsVision` still gate features per model in the grain. A Responses model still declares its
  capability flags.

## Steps

### Step 1 — Confirm the pending runtime facts (GATE) — `scratchpad/responses-spike.cs`
Run `dotnet run scratchpad/responses-spike.cs` with `OPENAI_API_KEY` (+ endpoint/model for the
target OpenAI-compatible provider). Confirm the three facts the offline spike could not:
1. The stateless adapter serializes **full client-side history** into the session blob
   (`TryGetInMemoryChatHistory` non-empty after `SerializeSessionAsync`→`DeserializeSessionAsync`).
2. **Cross-deserialize:** a Chat Completions session blob deserializes onto a Responses agent and
   vice versa (the model-switch rehost path, `AgentGrain.ChatCoreAsync:394-398`).
3. Streamed updates carry `TextContent` / `FunctionCallContent` / `FunctionResultContent` /
   `UsageContent` (what `ChatCoreAsync`'s `switch` at :511-532 depends on).
**Gate:** if (1) or (3) fails, stop — the design premise is wrong, re-open ADR 0014. If (2) fails,
narrow scope: forbid switching an existing conversation between a Chat and a Responses model
(document as a constraint + a grain-level guard), and record a gotcha. Steps 2–4 proceed only
after this gate. *Verify:* the spike prints all four content types true and both round-trips OK.

### Step 2 — Add the config flag — `ModelProviderOptions.cs`
Add `public bool UseResponsesApi { get; set; }` to `ConfiguredModel`, XML-doc'd (points at ADR
0014; states stateless/OpenAI-compatible-only). *Verify:* `dotnet build Agent42.slnx`.

### Step 3 — Branch the client build — `ChatClientFactory.cs`
In `Provider.OpenAICompatible`'s lambda, look up the `ConfiguredModel` by id (case-insensitive,
as `GetCapabilities` already does at :71-72) and choose the Responses vs Chat client. Add the
scoped `OPENAI001` pragma. Foundry lambda unchanged. *Verify:* Step 4 tests.

### Step 4 — Tests — `ModelProviderTests.cs`
Add a Responses-enabled model to the `github` provider in `Options()` and assert: a
Responses-flagged model builds a client (non-null, no network); it is **distinct** from and
**cached** independently of the same provider's Chat-Completions model; capability flags are still
read per the model's own entry (orthogonality). Mirror the no-I/O construction style of the file.
*Verify:* `dotnet test Agent42.slnx --filter ModelProviderTests`.

### Step 5 — Docs (same MR) — `/sync-docs`
Add gotchas anchored to the shipped code: (a) Responses is stateful-by-default → must use
`AsIChatClientWithStoredOutputDisabled` (evidence: `ChatClientFactory.cs`); (b) no stable
`Azure.AI.OpenAI` exposes the Responses accessor, so Foundry Responses is deferred (evidence: ADR
0014). Update the CLAUDE.md ADR-0013 note to mention 0014 if warranted. Fold in any Step-1 gate
finding. *Verify:* doc-path integrity (`/audit-docs` spot check).

## Acceptance criteria → test mapping
| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | Runtime facts confirmed (history serializes; content types present) | Step 1 spike run (recorded) | `scratchpad/responses-spike.cs` |
| AC2 | Cross-deserialize outcome known; design gated on it | Step 1 spike run (recorded) | `scratchpad/responses-spike.cs` |
| AC3 | A `UseResponsesApi` model builds a valid `IChatClient`, no network | `ResponsesModel_BuildsClient` | `ModelProviderTests.cs` |
| AC4 | Responses & Chat models on one provider are distinct + cached separately | `ResponsesAndChat_AreDistinctCachedClients` | `ModelProviderTests.cs` |
| AC5 | Capability flags still per-model, independent of the API flag | `ResponsesModel_CapabilitiesFromConfig` | `ModelProviderTests.cs` |
| AC6 | Foundry path unchanged (still Chat Completions) | existing `Factory_CachesOneClientPerQualifiedId` still green | `ModelProviderTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)
- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3 · coverage_new_code_pct_min: 80 · cognitive_complexity_max: 15
- vulnerable_packages: 0 (hard block) · add_policy: latest-stable (no new package expected)
- Branch: never commit to `main`; work stays on `claude/responses-api-support-s34w64` + MR.
- CI hard blocks: build, tests, `dotnet format`, vulnerable packages, doc-path integrity.

## Out of scope
- **Foundry Responses** (needs prerelease `Azure.AI.OpenAI`; ADR 0014 defers it).
- **Enabling Responses on a shipped prod model** — the flag lands unused in prod config; turning it
  on for a real provider is a separate change.
- **Hosted tools** (server-side web_search/file_search) — the payoff Responses unlocks, but a later
  decision; retiring the SearXNG sidecar (ADR 0011) is not touched here.
- **Stored (`store=true`) mode** — rejected in ADR 0014.
- **UI/picker changes** — `UseResponsesApi` is a server-side transport choice, not user-selectable.
```
