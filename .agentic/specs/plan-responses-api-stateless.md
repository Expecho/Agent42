# Plan: Responses API support (stateless) for OpenAI-compatible providers

planned-against: claude/responses-api-support-s34w64 @ e0af5ee

> Base note: planned on the designated feature branch (not `main`). Base commit
> `e0af5ee` carries ADR 0014 and this spec (both committed); the code diff (commit
> `7e79e2d`) is the multi-provider baseline. Executor branches from `e0af5ee` so
> ADR 0014 is present in the checkout.

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
  is built on and egresses content outside the tenant (ADR 0014 Consequences). **Evidence the
  method exists and sets `store=false`:** Microsoft Learn API ref for
  `OpenAI.Responses.OpenAIResponseClientExtensions.AsIChatClientWithStoredOutputDisabled`
  (assembly `Microsoft.Agents.AI.OpenAI.dll`, package `Microsoft.Agents.AI.OpenAI v1.13.0` — the
  repo's pin; transitive `Microsoft.Extensions.AI.OpenAI` supplies `AsIChatClient(ResponsesClient)`).
  Its doc remark: "This corresponds to setting the 'store' property in the JSON representation to
  false." Step 1 still confirms this at runtime, not just from the doc.
- **The per-model API choice must be a testable seam, not only a lambda.** Extract the
  Chat-vs-Responses decision for a given `ConfiguredModel?` into a small pure helper (e.g. an
  internal method/enum on the factory) so a unit test can assert *which path a flagged model
  takes* without a network call — two distinct qualified ids are always distinct+cached regardless
  of API, so caching/identity assertions alone cannot prove the branch fired (critic HIGH-1).
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
4. **Statelessness is observed, not assumed** (critic HIGH-2): confirm the adapter method
   resolves at the pinned version AND that `store=false` actually took effect — e.g. the outbound
   request carries `store:false`, or the returned response id is **not** retrievable server-side
   afterwards. A single round-trip passing (1)–(3) does not by itself prove nothing was stored.
5. **Capture the distinguishing handle** for the two adapters (concrete inner type behind the OTel
   wrapper, or `GetService<ChatClientMetadata>()` fields) so Step 4's seam test has something
   concrete to assert against if the pure-helper seam alone is deemed insufficient.
**Gate:** if (1), (3), or (4) fails, stop — the design premise is wrong, re-open ADR 0014. If (2)
fails, narrow scope: forbid switching an existing conversation between a Chat and a Responses model
(document as a constraint + a grain-level guard), and record a gotcha. Steps 2–5 proceed only
after this gate. *Verify:* the spike prints all four content types true, both round-trips OK, and
`store=false` confirmed; record the results (and the distinguishing handle) inline in the spec.

### Step 2 — Add the config flag — `ModelProviderOptions.cs`
Add `public bool UseResponsesApi { get; set; }` to `ConfiguredModel`, XML-doc'd (points at ADR
0014; states stateless/OpenAI-compatible-only). *Verify:* `dotnet build Agent42.slnx`.

### Step 3 — Branch the client build via a testable seam — `ChatClientFactory.cs`
Extract the per-model API decision into a small pure helper (e.g.
`internal static bool UseResponses(ConfiguredModel? model)` or an enum-returning method) and call
it from `Provider.OpenAICompatible`'s lambda: look up the `ConfiguredModel` by id
(case-insensitive, as `GetCapabilities` does at :71-72), then build the Responses vs Chat client
accordingly. Add the scoped `OPENAI001` pragma at the Responses call site only. Foundry lambda
unchanged. The seam is what makes the branch unit-testable without a network call (critic HIGH-1).
*Verify:* Step 4 tests.

### Step 4 — Tests — `ModelProviderTests.cs`
Assert on the **seam**, not just client identity: a Responses-flagged `ConfiguredModel` selects the
Responses path and an unflagged one selects Chat — a test that **fails if the branch is inverted or
dead** (this is the AC4 fix; identity/caching assertions alone cannot prove it). Add a
Responses-enabled model to the `github` provider in `Options()` and additionally assert: it builds
a non-null client with no network; capability flags are still read per the model's own entry
(orthogonality, AC5). If Step 1 found a runtime handle that distinguishes the built adapters
(metadata/type), add that assertion too; otherwise state in the test comment that adapter selection
beyond the seam is covered only by the Step 1 spike. Mirror the no-I/O construction style of the
file. *Verify:* `dotnet test Agent42.slnx --filter ModelProviderTests`.

### Step 5 — Docs (same MR) — `/sync-docs`
Add **one** gotcha anchored to shipped code: Responses is stateful-by-default → must use
`AsIChatClientWithStoredOutputDisabled` (evidence: `ChatClientFactory.cs`). Do **not** add a gotcha
for "no stable `Azure.AI.OpenAI` exposes the accessor" — that is version rationale already recorded
in ADR 0014 and would be the paraphrase/drift pattern gotchas.md:5-11 warns against (critic LOW-4).
Update the CLAUDE.md ADR-0013 note to mention 0014 if warranted. Fold in any Step-1 gate finding.
*Verify:* doc-path integrity (`/audit-docs` spot check).

## Acceptance criteria → test mapping
| # | Acceptance criterion | Verification | Where |
|---|---|---|---|
| AC1 | Runtime facts confirmed (history serializes; content types present; **store=false observed**) | Manual gate (recorded in spec) | `scratchpad/responses-spike.cs` |
| AC2 | Cross-deserialize outcome known; design gated on it | Manual gate (recorded in spec) | `scratchpad/responses-spike.cs` |
| AC3 | A `UseResponsesApi` model builds a valid `IChatClient`, no network | `ResponsesModel_BuildsClient` | `ModelProviderTests.cs` |
| AC4 | The seam **selects Responses** for a flagged model and **Chat** for an unflagged one (fails if branch inverted/dead) | `Seam_SelectsResponsesForFlaggedModel` | `ModelProviderTests.cs` |
| AC5 | Capability flags still per-model, independent of the API flag | `ResponsesModel_CapabilitiesFromConfig` | `ModelProviderTests.cs` |
| AC6 | Foundry path unchanged (seam returns Chat for AzureOpenAI-kind models) | `Seam_SelectsChatForFoundry` + existing `Factory_CachesOneClientPerQualifiedId` green | `ModelProviderTests.cs` |

> AC1/AC2 are **manual gates**, not automated tests: `responses-spike.cs` is not in the test
> project and never runs under `dotnet test`/CI. Their results must be pasted into this spec at
> Step 1 so the cold-handoff reader sees them (critic LOW-5).

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

## Review (critic pass, 2026-07-15)
Verdict: withhold approval until the two HIGH findings and MEDIUM-3 are addressed. All five findings
accepted and folded in:
- **HIGH-1** (tests can't prove the branch fires — identity/caching is API-agnostic): Step 3 now
  extracts a pure seam; Step 4/AC4/AC6 assert on the seam so an inverted/dead branch fails.
- **HIGH-2** (mechanism asserted without evidence; gate didn't verify statelessness): added the
  Microsoft Learn evidence for `AsIChatClientWithStoredOutputDisabled` + its `store=false` contract
  to Constraints; added Step 1 gate criterion (4) that observes `store=false` at runtime, and (5) to
  capture the adapter-distinguishing handle.
- **MEDIUM-3** (base ref/ADR uncommitted): ADR 0014 + this spec committed at `e0af5ee`; header and
  base note corrected to branch from there.
- **LOW-4** (gotcha (b) duplicates ADR): dropped; only the code-anchored gotcha (a) remains.
- **LOW-5** (AC1/AC2 mislabelled "Test"): relabelled manual gates, with a note that results are
  recorded in-spec at Step 1.
No finding was rejected.
```
