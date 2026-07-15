# Plan: Responses API support (stateless) on the Foundry provider

planned-against: claude/responses-api-support-s34w64 @ ee0f989

> Base note: planned on the designated feature branch (not `main`). ADR 0014 (revised
> 2026-07-15) and this spec are committed on this branch. **This is a revision of the
> original plan**, which targeted OpenAI-compatible (API-key) providers; it was reworked
> onto the Foundry provider once two facts landed: GitHub Models (our only wired
> OpenAI-compatible provider) does **not** support Responses, while Foundry does and is
> reachable on the **stable** `OpenAI 2.12.0` client via its GA `/openai/v1` surface. See the
> ADR 0014 revision note.

## Overview
Add a provider-level `UseResponsesApi` flag (ADR 0014). A Foundry provider with the flag set has
**all** its models built through the OpenAI **Responses** API run **statelessly** (`store=false`)
instead of Chat Completions, so hosted server-side tools become reachable later (ADR 0011 context)
while Agent42 keeps holding all conversation history client-side (ADR 0004). Reached with **no
package change**: the stable `OpenAI 2.12.0` client, pointed at `{endpoint}/openai/v1` with a bearer
policy sourced from the container app's **managed identity**, exposes `GetResponsesClient`, and the
already-pinned `Microsoft.Agents.AI.OpenAI 1.13.0` supplies the stateless adapter. The prerelease
`Azure.AI.OpenAI` path is **not** used (ADR 0014 Alternatives).

## Governing docs
- `/.agentic/decisions/0014-responses-api-stateless-mode.md` — the decision (revised): Foundry,
  provider-level, stable-SDK-via-`/openai/v1`, stateless-mandatory, all-or-nothing.
- `/.agentic/decisions/0013-multi-provider-model-catalog.md` — the catalog this extends: per-provider
  credential/endpoint resolution, the config-only-endpoint SSRF invariant, capability gating.
- `/.agentic/gotchas.md` — "A named HttpClient silently inherits AddServiceDefaults' retry pipeline"
  (the entry near line 166). Relevant as a *do-not*: see Constraints.

## Affected areas (verified against the tree)
- `src/Agent42.Backend/ModelProviderOptions.cs` — add `UseResponsesApi` to **`ProviderOptions`**
  (provider-level, NOT `ConfiguredModel` — Foundry models are ARM-discovered, not declared).
- `src/Agent42.Backend/ChatClientFactory.cs` — in `Provider.Foundry`: when the flag is set, build the
  stable `OpenAIClient` at `{endpoint}/openai/v1` with a managed-identity bearer policy and a client
  lambda of `GetResponsesClient().AsIChatClientWithStoredOutputDisabled(model)`; otherwise the
  existing `AzureOpenAIClient` + `GetChatClient(model).AsIChatClient()`. `Provider.Foundry` already
  receives `ProviderOptions`, so no signature change. `Provider.OpenAICompatible` is untouched.
- `src/Agent42.Backend/ModelProviderBuilder.cs` — no signature change expected; `Provider.Foundry` is
  already called with the bound `ProviderOptions`, so the flag flows. Confirm.
- `tests/Agent42.Tests/ModelProviderTests.cs` — new cases (see AC table). Exemplar for style; note its
  comment: constructing an OpenAIClient/response client does **no network I/O**.
- `src/Agent42.Backend/appsettings.json` — leave the shipped `foundry` provider's flag **off**
  (documenting turning it on is out of scope; ADR 0014 keeps prod on Chat).
- `scratchpad/responses-spike.cs` — the runtime confirmation harness (Step 1).

## Context & constraints (not re-derivable cheaply)
- **Reach Foundry Responses on the stable SDK — no prerelease `Azure.AI.OpenAI`.** The path is a
  stable `OpenAIClient` whose `OpenAIClientOptions.Endpoint` is `{foundry-endpoint}/openai/v1`, then
  `client.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(model)`. `AzureOpenAIClient`
  (`Azure.AI.OpenAI 2.1.0`, our pin) has **no** response accessor, and the version that does is
  prerelease — we do not go there (ADR 0014). **Evidence the pieces exist at the pinned versions:**
  `GetResponsesClient` and the auth hooks `AuthenticationPolicy`/`GenericActionPipelinePolicy` are in
  `OpenAI 2.12.0`; `AsIChatClientWithStoredOutputDisabled` (whose `includeReasoningEncryptedContent`
  defaults true — **Step 3 passes `false`**, see the Step-1 results) is in `Microsoft.Agents.AI.OpenAI
  1.13.0` (both confirmed present in the built assemblies). Its doc remark: "This corresponds to
  setting the 'store' property in the JSON representation to false" — and the Step-1 spike observed
  `"store":false` on the wire.
- **The managed-identity bearer construction is settled and compiles at the pinned versions** (Step-1
  spike); the only residual unknown is the **live token exchange** against `/openai/v1` (Step-1 gate
  criterion 1). Use the `OpenAIClient(AuthenticationPolicy, OpenAIClientOptions)` ctor — **no
  `ApiKeyCredential` placeholder, no policy-ordering hack** — with
  `System.ClientModel.Primitives.BearerTokenPolicy(tokenProvider, scope)`, where `tokenProvider` is a
  small `AuthenticationTokenProvider` subclass bridging Azure's `TokenCredential` (scope
  `https://cognitiveservices.azure.com/.default`) — the spike's `AzureIdentityTokenProvider` is the
  reference to lift into `Provider.Foundry`. `BearerTokenPolicy` handles token acquisition + refresh,
  so no static-token staleness. **Reuse the managed identity already flowing into `Provider.Foundry`;
  do not introduce an `IHttpClientFactory`/named `HttpClient`** — that inherits AddServiceDefaults'
  retry pipeline (gotchas, ~line 166) and `AgentGrain` is non-reentrant, so a retried inference call
  multiplies the stall on the user's turn. Same reasoning as the existing comment at
  `ChatClientFactory.cs:127-133`. Extension usings: `OpenAI.Responses` (the adapter),
  `Microsoft.Extensions.AI` (`ChatClient.AsIChatClient()`).
- **Stateless is mandatory, not a default to override.** `AsIChatClientWithStoredOutputDisabled` —
  never the plain `AsIChatClient()` on the response client, which stores server-side and breaks the
  `TryGetInMemoryChatHistory` invariant the grain is built on (`AgentGrain.cs:554`). For a Foundry
  account the store is inside the tenant, so the driver here is the **invariant**, not cross-tenant
  egress. Step 1 confirms `store=false` at runtime, not just from the doc.
- **The per-provider API choice must be a testable seam, not only a lambda.** Extract the
  Chat-vs-Responses decision for a given `ProviderOptions` into a small pure **`public`** helper
  (`public static bool UseResponses(ProviderOptions options)`) so a unit test can assert *which path a
  flagged provider takes* without a network call — two distinct qualified ids are always
  distinct+cached regardless of API, so caching/identity assertions alone cannot prove the branch
  fired (critic HIGH-1, carried forward). It **must be `public`, not `internal`**: the repo has no
  `InternalsVisibleTo` and every tested Backend member is public (`MultiProviderChatClientFactory`,
  its nested `Provider`, `ModelProviderBuilder`), so an `internal` helper would not compile from
  `Agent42.Tests` — matching the established visibility pattern is the fix, not a new
  `InternalsVisibleTo` (critic MEDIUM-1).
- **The call site needs TWO scoped experimental suppressions.** `AsIChatClientWithStoredOutputDisabled`
  is `[Experimental("MAAI001")]` and `ResponsesClient`/`GetResponsesClient()` are
  `[Experimental("OPENAI001")]` — scope `#pragma warning disable OPENAI001` **and** `MAAI001` at the
  call site (or a targeted `NoWarn`); do not blanket-suppress. The Step-1 spike confirmed `OPENAI001`
  alone does not build.
- **All-or-nothing per provider, and the cross-API deserialize has two triggers, not one** (critic
  MEDIUM-4). The flag routes *every* model of the flagged Foundry provider through Responses; there is
  no per-model mix. There are **four** `DeserializeSessionAsync`-onto-a-freshly-built-agent sites:
  activation (`AgentGrain.cs:94-97`), persona rebind (`313-314`), `350-351`, and the user model-switch
  in `ChatCoreAsync` (`397-398`). A cross-API blob (Chat written, Responses read, or vice versa)
  arises **(a)** when a conversation switches to a Chat model on another provider — the switch path —
  **and (b)**, more insidiously, when the **flag is flipped between a blob write and read across a
  deployment**: that fires on the **activation path (94-97) on the same model id with no user switch**.
  Both are covered only if Step-1 criterion 3 shows cross-deserialize works **symmetrically**.
- **SSRF invariant holds unchanged:** the endpoint still comes only from config
  (`Provider.Foundry(..., endpoint, ...)`, with `/openai/v1` appended in code); this change reads a
  bool and adds a bearer policy, no new network input. Keep it that way (ADR 0013 "Not an SSRF sink").
- **OTel wrapper is untouched:** `Build()` still wraps whatever `provider.Client(model)` returns in
  `UseOpenTelemetry` — the branch is *inside* the client lambda, below the wrapper.
- **Capabilities are orthogonal.** `UseResponsesApi` selects the wire API; a Foundry provider still
  reports `ModelCapabilities.Full` (the assumption predates ADR 0013) — the Responses switch does not
  change what the grain gates on.

## Steps

### Step 1 — Confirm the pending runtime facts (GATE) — `scratchpad/responses-spike.cs`
**The spike is already written and compiles clean at the pinned versions** (all API surface verified —
bearer construction, adapter, session/streaming); only the live run remains. Run it with
`FOUNDRY_ENDPOINT`, `FOUNDRY_MODEL`, and `az login`:
`FOUNDRY_ENDPOINT=https://<account>.openai.azure.com FOUNDRY_MODEL=gpt-4o dotnet run scratchpad/responses-spike.cs`
— it prints a PASS/FAIL gate summary to paste below. The account's own model deployments must exist;
authenticate with a local `TokenCredential` (`DefaultAzureCredential`/`AzureCliCredential`, standing in
for the container app's managed identity). Note: the spike lives inside the repo tree, so
`scratchpad/Directory.Build.props` + `Directory.Packages.props` (both gitignored) isolate it from the
repo's Central Package Management — without them CPM rejects the `#:package` versions. **The spike MUST pin the exact shipped versions via
`#:package` directives — `OpenAI@2.12.0`, `Microsoft.Agents.AI.OpenAI@1.13.0`, `Azure.Identity`
current-stable — and MUST NOT pull a prerelease `Azure.AI.OpenAI` (or any version above 2.1.0).** A
file-based app resolves independently of `Directory.Packages.props`; if it drifts to newer/preview
assemblies it can confirm `GetResponsesClient` / `AsIChatClientWithStoredOutputDisabled` / the
bearer-auth hooks against APIs that are **not** in the pinned 2.12.0 / 1.13.0 build — a green spike
Step 3 then can't reproduce (critic MEDIUM-3). Confirm the facts the offline spike could not:
1. **Bearer auth on `/openai/v1` works** — a stable `OpenAIClient` built with a token-bearer auth
   policy (no API key) successfully calls `GetResponsesClient()` and gets a reply. **Capture the
   exact working client-construction code** — it is the pattern Step 3 copies.
2. The stateless adapter serializes **full client-side history** into the session blob
   (`TryGetInMemoryChatHistory` non-empty after `SerializeSessionAsync`→`DeserializeSessionAsync`).
3. **Cross-deserialize:** a Chat Completions session blob deserializes onto a Responses agent and vice
   versa (the model-switch rehost path, `AgentGrain.cs:394-398`).
4. Streamed updates carry `TextContent` / `FunctionCallContent` / `FunctionResultContent` /
   `UsageContent` (what `ChatCoreAsync`'s `switch` at `AgentGrain.cs:511-532` depends on).
5. **Statelessness is observed, not assumed** (critic HIGH-2): confirm `store=false` actually took
   effect — e.g. the outbound request carries `store:false`, or the returned response id is **not**
   retrievable server-side afterwards. A round-trip passing (2)–(4) does not by itself prove nothing
   was stored.
6. **Capture a no-I/O distinguishing handle** for the two built clients — **required, not optional**:
   the pure-helper test proves the *helper* returns the right bool but NOT that `Provider.Foundry`
   actually calls it and builds the matching client (a dead/inverted branch leaves the helper test
   green — critic MEDIUM-2). Find a handle the built `IChatClient` exposes without a network call —
   e.g. `GetService<ChatClientMetadata>()` whose `ProviderUri` is the `/openai/v1` endpoint on the
   Responses path and the plain Foundry endpoint on the Chat path — so Step 4 can assert the wiring
   mandatorily. If **no** no-I/O handle exists at all, that is itself a gate finding to record (the
   Foundry branch would then have no automated CI guard, since the spike never runs under `dotnet
   test`).

**Explicitly deferred** (critic LOW-5): this gate does **not** verify that reasoning items round-trip
through the session via `includeReasoningEncryptedContent: true` (ADR 0014 calls it load-bearing *if*
a reasoning Foundry model is ever flagged). Out of scope now — the flag ships off and the default is
gpt-4o — but if a reasoning model is later flagged, this is unproven and the streaming `switch`
(`AgentGrain.cs:511-532`) silently drops content types it doesn't case on; re-open then.
**Gate:** if (1), (2), (4), or (5) fails, stop — the design premise is wrong, re-open ADR 0014. If (3)
fails, narrow scope — but note a user-switch guard is **not sufficient** (critic MEDIUM-4): the
activation-path deserialize (`AgentGrain.cs:94-97`) fires on a flag-flip with no user switch, so the
fallback must also gate **enabling the flag on live conversations** on symmetric cross-deserialize
(forbid the switch path *and* treat flag-flip-on-existing-conversations as blocked). Document as a
constraint + a grain-level guard, and record a gotcha.
Steps 2–5 proceed only after this gate. *Verify:* the spike prints bearer auth OK, all four content
types true, both round-trips OK, and `store=false` confirmed; record the results (and the working
construction + distinguishing handle) inline in this spec.

#### Step-1 gate RESULTS — 2026-07-15 — ✅ GREEN (ran against `agent42.openai.azure.com`, `gpt-4o`, `AzureCliCredential`)
| Criterion | Result |
|---|---|
| (1) bearer auth on `/openai/v1` | **PASS** — `OpenAIClient(BearerTokenPolicy(AzureIdentityTokenProvider), options)` at `/openai/v1` authenticated; gpt-4o completed a tool-calling turn. |
| (2) history serializes into session blob | **PASS** — 4 messages, 866 bytes, non-empty after serialize→JSON string→deserialize. |
| (3a) Chat blob → Responses agent | **PASS** |
| (3b) Responses blob → Chat agent | **PASS** (MEDIUM-4 cross-API worry retired; both directions work.) |
| (4) Text / FunctionCall / FunctionResult / Usage content streamed | **PASS** (all four). |
| (5) `store=false` observed on the wire | **PASS** — the outbound `/responses` body literally carries `"store":false`. |
| (6) no-I/O distinguishing handle | **PASS** — `ChatClientMetadata.ProviderUri` differs: Responses `…/openai/v1`, Chat (AzureOpenAIClient) `…/`. **Step-4 wiring assertion uses `ProviderUri`.** |

**Two implementation-shaping findings from the run:**
- **`includeReasoningEncryptedContent` MUST be `false`.** The helper defaults it `true`, which sends
  `include:["reasoning.encrypted_content"]`; **gpt-4o (non-reasoning) rejects it with HTTP 400**
  (`Parameter: include — Encrypted content is not supported with this model`). Since the shipped/default
  Foundry models are non-reasoning, Step 3 builds
  `GetResponsesClient().AsIChatClientWithStoredOutputDisabled(model, includeReasoningEncryptedContent: false)`.
  (A reasoning model would want `true`, but those are deferred — LOW-5.)
- **Verified construction to lift into `Provider.Foundry`** (the spike's `AzureIdentityTokenProvider` +
  `BearerTokenPolicy`): `new OpenAIClient(new BearerTokenPolicy(new AzureIdentityTokenProvider(cred,
  ["https://cognitiveservices.azure.com/.default"]), scope), new OpenAIClientOptions { Endpoint =
  {baseEndpoint}/openai/v1 })`. Chat path stays `new AzureOpenAIClient(baseEndpoint, cred)` (stable
  `Azure.AI.OpenAI 2.1.0`) at the **plain** endpoint — which is what makes (6)'s `ProviderUri` differ.

### Step 2 — Add the config flag — `ModelProviderOptions.cs`
Add `public bool UseResponsesApi { get; set; }` to **`ProviderOptions`**, XML-doc'd (points at ADR
0014; states provider-level/all-Foundry-models/stateless/stable-SDK-via-`/openai/v1`). *Verify:*
`dotnet build Agent42.slnx`.

### Step 3 — Branch the Foundry client build via a testable seam — `ChatClientFactory.cs`
Extract the per-provider API decision into a small pure **`public`** helper
(`public static bool UseResponses(ProviderOptions options)` — must be public, see Constraints/MEDIUM-1)
and call it from `Provider.Foundry`:
when true, build the stable `OpenAIClient` at `{endpoint}/openai/v1` with the Step-1 bearer
construction and a lambda of
`GetResponsesClient().AsIChatClientWithStoredOutputDisabled(model, includeReasoningEncryptedContent: false)`
(**`false` is required** — `true` 400s non-reasoning models, see Step-1 results); otherwise the
existing `AzureOpenAIClient` + `GetChatClient(model).AsIChatClient()` at the **plain** endpoint. Add the scoped
`OPENAI001` **and** `MAAI001` pragmas at the Responses call site only. `Provider.OpenAICompatible`
unchanged. The seam is
what makes the branch unit-testable without a network call (critic HIGH-1). *Verify:* Step 4 tests.

### Step 4 — Tests — `ModelProviderTests.cs`
Assert on the **seam**, not just client identity: a Responses-flagged `ProviderOptions` selects the
Responses path and an unflagged one selects Chat — a test that **fails if the branch is inverted or
dead** (AC4). Add a Responses-enabled Foundry provider to `Options()` and additionally assert: it
builds a non-null client with no network (AC3); a non-Responses Foundry provider still selects Chat
(AC6). **Also assert the *wiring* mandatorily** (AC4): call `GetClient(...)` for a flagged Foundry
provider and assert the Step-1 no-I/O handle (e.g. `GetService<ChatClientMetadata>().ProviderUri`
ending `/openai/v1`) — so a **dead or inverted `Provider.Foundry` branch fails even though the
pure-helper test stays green** (critic MEDIUM-2). If Step 1 found no no-I/O handle at all, this
wiring test cannot be written: say so in the test comment and treat it as the recorded gate finding
(the seam test then guards only the helper, not the wiring). Mirror the no-I/O construction style of
the file. *Verify:*
`dotnet test Agent42.slnx --filter ModelProviderTests`.

### Step 5 — Docs (same MR) — `/sync-docs`
Add **one** gotcha anchored to shipped code: Responses is stateful-by-default → must use
`AsIChatClientWithStoredOutputDisabled` (evidence: `ChatClientFactory.cs`). Consider a second, if
Step 1 proved non-obvious: the managed-identity **bearer-against-`/openai/v1`** construction (evidence:
`ChatClientFactory.cs` + spike) — add it only if the incantation is a real trap, not a paraphrase of
the ADR (gotchas.md's paraphrase/drift warning; critic LOW-4). Do **not** add a gotcha for the version
rationale — that lives in ADR 0014. Update the CLAUDE.md ADR-0013 note to mention 0014. Fold in any
Step-1 gate finding. *Verify:* doc-path integrity (`/audit-docs` spot check).

## Acceptance criteria → test mapping
| # | Acceptance criterion | Verification | Where |
|---|---|---|---|
| AC1 | Runtime facts confirmed (**bearer auth on `/openai/v1`**; history serializes; content types present; **store=false observed**) | Manual gate (recorded in spec) | `scratchpad/responses-spike.cs` |
| AC2 | Cross-deserialize outcome known; design gated on it | Manual gate (recorded in spec) | `scratchpad/responses-spike.cs` |
| AC3 | A `UseResponsesApi` Foundry provider builds a valid `IChatClient`, no network | `ResponsesProvider_BuildsClient` | `ModelProviderTests.cs` |
| AC4 | The seam selects Responses for a flagged provider and Chat for unflagged, **and the built client is wired to the matching path** — fails if the `Provider.Foundry` branch is inverted or dead. Handle confirmed by Step-1: assert `GetService<ChatClientMetadata>().ProviderUri` ends `/openai/v1` (Responses) vs `/` (Chat). | `Seam_SelectsResponsesForFlaggedProvider` + `ResponsesProvider_WiresResponsesEndpoint` | `ModelProviderTests.cs` |
| AC5 | Capability reporting unchanged by the flag (Foundry still `Full`) | `ResponsesProvider_CapabilitiesUnchanged` | `ModelProviderTests.cs` |
| AC6 | An unflagged Foundry provider still selects Chat; `OpenAICompatible` path unchanged | `Seam_SelectsChatForUnflaggedFoundry` + existing `Factory_CachesOneClientPerQualifiedId` green | `ModelProviderTests.cs` |

> AC1/AC2 are **manual gates**, not automated tests: `responses-spike.cs` is not in the test project
> and never runs under `dotnet test`/CI. Their results must be pasted into this spec at Step 1 so the
> cold-handoff reader sees them (critic LOW-5).

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)
- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3 · coverage_new_code_pct_min: 80 · cognitive_complexity_max: 15
- vulnerable_packages: 0 (hard block) · add_policy: latest-stable (no new package expected)
- Branch: never commit to `main`; work stays on `claude/responses-api-support-s34w64` + MR.
- CI hard blocks: build, tests, `dotnet format`, vulnerable packages, doc-path integrity.

## Out of scope
- **Enabling Responses on the shipped `foundry` prod provider** — the flag lands off in prod config;
  turning it on is a separate, deliberate change.
- **Per-model Responses on Foundry / a second `foundry-responses` provider entry** — rejected in ADR
  0014; the flag is provider-level and all-or-nothing.
- **Responses on OpenAI-compatible providers** — GitHub Models does not support it; revisit if a
  Responses-capable OpenAI-compatible endpoint is added.
- **Hosted tools** (server-side web_search/file_search) — the payoff Responses unlocks, but a later
  decision; retiring the SearXNG sidecar (ADR 0011) is not touched here.
- **Stored (`store=true`) mode** — rejected in ADR 0014.
- **UI/picker changes** — `UseResponsesApi` is a server-side transport choice, not user-selectable.

## Review
**First critic pass (original draft, 2026-07-15):** HIGH-1 seam, HIGH-2 store=false evidence, MEDIUM-3
committed base, LOW-4 gotcha dedup, LOW-5 manual-gate labelling — all addressed and carried into this
revision.

**Second critic pass (Foundry rework, 2026-07-15):** clean on ADR consistency, affected-area accuracy
(`Provider.Foundry(ProviderOptions, Uri, TokenCredential)` flows the flag with no signature change;
capability reporting genuinely orthogonal), package pins, cited line refs, and the SSRF/named-HttpClient
invariants. Five findings, all folded in:
- **MEDIUM-1** (an `internal` seam helper won't compile from the test project — no `InternalsVisibleTo`
  in the repo): Constraints + Step 3 now mandate a **`public`** helper.
- **MEDIUM-2** (AC4's "fails if branch dead/inverted" over-claimed — a pure-helper test can't prove
  `Provider.Foundry` calls it): Step-1 criterion 6 now **requires** a no-I/O handle; Step 4 adds a
  **mandatory wiring assertion** (`ResponsesProvider_WiresResponsesEndpoint`); AC4 marked contingent on
  that handle existing.
- **MEDIUM-3** (spike could false-green against unpinned/preview assemblies): Step 1 now mandates the
  spike **pin the exact shipped versions** and never pull a prerelease `Azure.AI.OpenAI`.
- **MEDIUM-4** (cross-deserialize under-identified; user-switch fallback misses the flag-flip case):
  Constraints now enumerate the four deserialize sites and both triggers; the gate fallback now also
  blocks flag-flip on live conversations, not just user switching.
- **LOW-5** (reasoning round-trip unverified): Step 1 marks it **explicitly deferred**.

No finding required re-opening ADR 0014.

**Step-1 spike authoring (2026-07-15)** surfaced two doc corrections, folded into both this spec and
ADR 0014: the accessor is `GetResponsesClient()` (no arg — the model rides on the adapter), and the
call site needs **`MAAI001` as well as `OPENAI001`** (`OPENAI001` alone does not build). The bearer
construction (`OpenAIClient(AuthenticationPolicy, options)` + `BearerTokenPolicy` + an
`AuthenticationTokenProvider` over the Azure `TokenCredential`) is verified to compile at the pinned
versions.

**Step-1 gate RAN — ✅ green (2026-07-15)**, against `agent42.openai.azure.com`/gpt-4o (results table in
Step 1). All six criteria pass, including the live token exchange (criterion 1) and `store=false` on the
wire (criterion 5). The run added one implementation-shaping finding — **`includeReasoningEncryptedContent`
must be `false`** (its `true` default 400s non-reasoning models) — and confirmed the Step-4 wiring handle
(`ChatClientMetadata.ProviderUri`). **The gate is closed; Step 2 may proceed.**

## Implementation notes — where it diverged from the plan (2026-07-15, executed)

Steps 2–5 implemented on `claude/responses-api-support-s34w64` (see below). Divergences from the plan:

1. **Branch policy.** Executed on the existing `claude/responses-api-support-s34w64` branch rather than
   nesting a fresh `feature/responses-api-stateless` off it. That branch is the pushed, non-protected
   branch this plan was `planned-against:`, with ADR 0014 + the plan committed on it and no open PR; a
   nested branch would split one logical feature across two branches. Hard rule #1 (no protected-branch
   commits) is satisfied — this is not `main`.

2. **`AzureIdentityTokenProvider` is `public` and directly unit-tested — beyond the plan's Step 3/4.** The
   plan mandated `public` for the `UseResponses` seam only. Implementation also made the bearer bridge
   (`MultiProviderChatClientFactory.AzureIdentityTokenProvider`) public and added `BearerBridge_MapsAzureTokenToBearer`
   — a no-I/O test asserting the token→`AuthenticationToken` relabel via the existing `StaticTokenCredential`.
   **Driver:** the bridge's token methods execute only on a live exchange, so the new-code coverage gate
   (`≥80%`) otherwise sat at ~50% for that region; the test lifts new-code coverage to **100%**. Mirrors the
   established public-for-test precedent (`Provider`). The live exchange stays the manual Step-1 gate (AC1);
   this test covers only the mapping. Recorded in ADR 0014 Consequences.

3. **Pragma scope widened.** The plan placed `OPENAI001`/`MAAI001` "at the Responses call site only"; the
   `OpenAIClient(AuthenticationPolicy, options)` ctor is *also* `[Experimental("OPENAI001")]`, so both
   pragmas wrap the whole Responses branch (ctor + accessor + adapter). Still scoped, not blanket.

4. **Docs.** One gotcha added (three compiler-invisible Foundry-Responses traps: stateless-mandatory,
   `includeReasoningEncryptedContent:false`, dual-suppression); CLAUDE.md gained an ADR-0014 bullet.
   Shipped-source comments reference ADR 0014 / the plan's Step-1 table rather than the gitignored spike
   harness (critic Finding 2).

**Quality gate (evidence, this session):** new_issues 0 (build clean, `dotnet format` exit 0, no new
warnings in changed files) · new_security_issues 0 (endpoint config-only, SSRF invariant intact) ·
coverage_new_code 100% (coverlet cobertura — every new line hit) · vulnerable_packages 0 (no package
added). duplication (≤3) and cognitive_complexity (≤15) **SKIPPED** — no measuring tool in the repo
(`quality-gates.yaml`: "self-reported only"); the new methods are trivially simple. 257 tests pass.

**Critic pass (post-implementation, 2026-07-15):** substantially clean, no HIGH/MEDIUM. Named-HttpClient
and SSRF invariants confirmed respected; AC3–AC6 + AC1-slice tests confirmed non-vacuous. Three LOW
findings — Finding 1 (unrecorded public bridge/test) recorded here + in ADR; Finding 2 (dead spike
pointer in source) fixed; Finding 3 (gotcha/ADR overlap) rated acceptable, no change.
