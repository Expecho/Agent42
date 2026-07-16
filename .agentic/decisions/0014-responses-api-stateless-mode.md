---
id: 0014
date: 2026-07-15
status: accepted
tags: [models, providers, responses-api, chat-completions, openai, foundry, api-mode, config, data-egress, hosted-tools]
---

# Support the OpenAI Responses API on the Foundry provider, run statelessly

> **Amended by [ADR 0017](0017-foundry-provider-api-key-auth.md) (2026-07-16) — credential mechanism
> only.** The Responses `/openai/v1` path no longer authenticates with the managed-identity **bearer
> policy** described below; it now carries the Azure OpenAI **API key** via a custom `api-key`-header
> policy (0017's Step-1 spike found Azure reads the resource key from the `api-key` header there). The
> `AzureIdentityTokenProvider` bearer bridge is deleted. Everything else here stays authoritative:
> stateless-mandatory, the two experimental suppressions, `includeReasoningEncryptedContent: false`. This
> ADR keeps `status: accepted` on purpose — see 0017's supersession note.

> **Revised 2026-07-15** (same day, pre-implementation). The first draft scoped Responses to
> OpenAI-compatible (API-key) providers and deferred Foundry on the belief that Foundry Responses
> forces a prerelease `Azure.AI.OpenAI`. Two facts overturned that before any code landed: the only
> wired OpenAI-compatible provider (**GitHub Models**) does **not** support the Responses API, while
> **Foundry does** — and Foundry Responses is reachable on the **stable** `OpenAI 2.12.0` client via
> the OpenAI-compatible **`/openai/v1`** surface, so no prerelease Azure SDK is needed. The decision
> below is the revised one. The superseded draft's reasoning is recorded under Alternatives.

## Context
Every model in Agent42 is reached through `IChatClient`, and the only place the wire protocol is
chosen is `ChatClientFactory.cs` — `GetChatClient(model).AsIChatClient()` calls that pin the app to
the **Chat Completions** API (ADR 0013). Chat Completions cannot carry the OpenAI-family **hosted**
tools (server-side web search, file search): our client gets HTTP 400 on `web_search_options`, which
is the whole reason ADR 0011 stood up a self-hosted SearXNG sidecar (~2× the app's compute). The
**Responses API** is OpenAI's newer surface and the one those hosted tools live on.

Three facts settle *where* and *how* Responses can land, established by the 2026-07-15 spike research
(Microsoft Learn API docs + the NuGet version index) and the operator's own confirmation:

- **Of the providers Agent42 actually has, only Foundry supports Responses.** GitHub Models — the one
  wired OpenAI-compatible (API-key) provider — does **not** expose the Responses API. So the earlier
  "enable it on OpenAI-compatible providers" scope would have shipped a flag no configured provider
  could exercise.
- **Foundry Responses is GA and reachable on the pinned stable SDK.** Azure's **v1** API is generally
  available, exposed at the OpenAI-compatible base URL `{endpoint}/openai/v1` with no `api-version`.
  The pinned `OpenAI 2.12.0` client reaches it directly: `OpenAIClient.GetResponsesClient()` → a
  `ResponsesClient` (the model is supplied downstream, on the adapter — see Decision). The client is
  built via the `OpenAIClient(AuthenticationPolicy, OpenAIClientOptions)` ctor with a
  `System.ClientModel.Primitives.BearerTokenPolicy`, so the container app's **managed-identity
  `TokenCredential`** authenticates **without an API key** (verified to compile at the pinned versions
  by the Step-1 spike; the live token exchange against `/openai/v1` is the spike's runtime check).
- **No prerelease package is required.** `AzureOpenAIClient.GetResponsesClient` (the native Azure
  accessor) first appears in the `Azure.AI.OpenAI 2.2.0-beta` line, and there is no stable
  `Azure.AI.OpenAI` above `2.1.0` — but we do **not** go through it. The stateless adapter
  `AsIChatClientWithStoredOutputDisabled` ships in the already-pinned `Microsoft.Agents.AI.OpenAI
  1.13.0`, and the response client in stable `OpenAI 2.12.0`. Zero package change.

## Decision
Add a Responses API mode as a **provider-level option** — a `UseResponsesApi` flag on
`ProviderOptions` — enabled on the **Foundry provider**. When the flag is set, `ChatClientFactory`
builds that provider's models through a stable `OpenAIClient` pointed at `{endpoint}/openai/v1`, with
a **bearer policy sourced from the app's managed-identity `TokenCredential`**, then
`GetResponsesClient().AsIChatClientWithStoredOutputDisabled(model)` (the model rides on the adapter,
not the accessor) — instead of the `AzureOpenAIClient` + `GetChatClient(model).AsIChatClient()` path. Everything upstream (`AgentGrain`,
`ChatClientAgent`, tools, streaming, the OTel wrapper) is unchanged — it only ever sees `IChatClient`.

**Provider-level, all-or-nothing — not per model.** Foundry models are **ARM-discovered, not
declared** in config, so there is no `ConfiguredModel` entry to hang a per-model flag on (that seam
exists only for OpenAI-compatible providers). The flag therefore lives on the provider: setting it
routes **every** model of that Foundry provider through Responses. It is a **server-side transport
choice, not user-selectable** — the model picker is unchanged.

**Run statelessly (`store=false`).** Agent42 holds the whole conversation client-side and persists it
into the 2 MB Cosmos session item as the source of truth (ADR 0004); the grain reads it back via
`TryGetInMemoryChatHistory`. The Responses API is **stateful by default** (responses stored
server-side, later turns referencing them by `previous_response_id` / `ChatOptions.ConversationId`).
That default would **break the client-side-history invariant the whole grain is built on**, and
`AsIChatClientWithStoredOutputDisabled` is the first-class opt-out that re-sends full history each
turn, exactly as Chat Completions does (built with `includeReasoningEncryptedContent: false` — see
Consequences). Statelessness is
therefore **mandatory, not a default to override** — never the plain `AsIChatClient()` on the
response client.

## Alternatives considered
- **Scope Responses to OpenAI-compatible (API-key) providers; defer Foundry** *(the superseded first
  draft)*. Rejected: the only such provider wired is GitHub Models, which does not support Responses,
  so the flag would be unexercisable — and the premise that Foundry needs a prerelease
  `Azure.AI.OpenAI` was wrong (the stable `OpenAI` client reaches the GA `/openai/v1` Responses
  surface).
- **A prerelease `Azure.AI.OpenAI` (`2.9.0-beta.1`) with `AzureOpenAIClient.GetResponsesClient`.**
  Rejected: collides with hard-rule #4's "latest *stable*" default when the stable `OpenAI` client
  already reaches the same GA endpoint. No reason to take a prerelease pin.
- **A per-model `UseResponsesApi` on Foundry models.** Rejected: Foundry models are discovered from
  ARM, not declared, so there is no per-model config seam to attach it to; a provider-level flag fits
  the actual shape and mirrors how the provider (not the model) already carries endpoint/credential.
- **A second Foundry provider entry (e.g. `foundry-responses`) to make Responses opt-in per
  conversation via the picker.** Rejected for this iteration: it would make the transport choice
  user-selectable and list every model twice. Responses-vs-Chat is a transport decision, kept off the
  picker.
- **Stored mode (`store=true`, the Responses default).** Rejected: breaks the client-side-history
  invariant the whole grain is built on. (For a Foundry account the store is inside the tenant, so the
  cross-tenant egress line ADR 0013 draws is not the driver here — the invariant is.)

## Consequences
- **All-or-nothing per provider.** Turning the flag on routes *every* Foundry model through
  Responses; there is no per-model Chat/Responses mix within that provider. A conversation can still
  run a Chat model from another provider (e.g. GitHub Models) — so the model-switch rehost path
  (`ChatCoreAsync`) must cross-deserialize a **Chat** session blob onto a **Responses** agent; the
  spike gates on this.
- **Unblocks hosted tools later.** With a Responses client in place, the OpenAI-family hosted
  `web_search`/file-search tools become reachable — a future path to retiring the SearXNG sidecar
  (ADR 0011) for Foundry. **This is now done for web search + code interpreter — see ADR 0015**, which
  swaps the SearXNG shims for `HostedWebSearchTool`/`HostedCodeInterpreterTool` on the Responses path.
- **A bearer policy replaces the API-key credential on this path.** The stable `OpenAIClient` normally
  takes an `ApiKeyCredential`; here it is built with an auth policy that injects the managed-identity
  `TokenCredential`'s bearer token (scope `https://cognitiveservices.azure.com/.default`). The
  endpoint still comes only from config — no new network input, SSRF invariant intact (ADR 0013).
- **The call site needs TWO scoped experimental suppressions, not one.** `AsIChatClientWithStoredOutputDisabled`
  is `[Experimental("MAAI001")]` (Microsoft.Agents.AI) and the `ResponsesClient` type / `GetResponsesClient()`
  are `[Experimental("OPENAI001")]` — so the site needs a scoped `#pragma warning disable OPENAI001` **and**
  `MAAI001` (or a targeted `NoWarn`). The Step-1 spike confirmed `OPENAI001` alone does not build. Do not
  blanket-suppress.
- **`includeReasoningEncryptedContent` is built `false`, not the helper's `true` default.** The Step-1
  spike found that `true` sends `include:["reasoning.encrypted_content"]`, which **non-reasoning models
  (gpt-4o, the shipped default) reject with HTTP 400** (`Encrypted content is not supported with this
  model`). `true` would only be wanted for a *reasoning* model — to carry reasoning items across turns
  in the client-held history without server storage — and those are deferred (the spec's LOW-5). So the
  factory passes `false`; revisit if a reasoning Foundry model is ever flagged.
- **Default stays Chat Completions.** The shipped `foundry` provider keeps the flag **off** in prod
  config; turning it on for the real account is a separate, deliberate change (out of scope here).
- **The bearer bridge is a `public` type, tested directly (implementation deviation from the plan).** The
  plan authorized `public` for only the `UseResponses` seam. Implementation also made
  `MultiProviderChatClientFactory.AzureIdentityTokenProvider` public and added one no-I/O unit test
  (`BearerBridge_MapsAzureTokenToBearer`) that asserts the token→`AuthenticationToken` relabel ("Bearer",
  expiry propagated) via the existing `StaticTokenCredential`. Driver: the bridge's `GetToken`/`GetTokenAsync`
  only execute on a live token exchange, so without this test the new-code coverage gate (`≥80%`) fell to
  ~50% for that region; the test lifts it to 100%. It mirrors the established public-for-test precedent
  (`Provider` is public for the same reason — the repo has no `InternalsVisibleTo`). The live exchange
  itself remains the manual Step-1 gate (AC1); this test covers only the mapping logic.
- **Runtime confirmation — DONE (Step-1 gate, 2026-07-15, ✅ green** against `agent42.openai.azure.com`
  / gpt-4o). The Step-1 spike (a gitignored `scratchpad/` harness) confirmed at the pinned versions: bearer auth works on the
  v1 surface; `store=false` is observed on the wire (the `/responses` body carries `"store":false`); the
  stateless adapter serializes full history into the session blob (4 messages, 866 bytes); a Chat
  session blob cross-deserializes onto a Responses agent **and vice versa** (so mixing a Chat provider
  and the Responses Foundry provider in one conversation is NOT constrained); and streamed updates carry
  all of `TextContent`/`FunctionCallContent`/`FunctionResultContent`/`UsageContent`. Results are recorded
  in the plan's Step-1 section.
