---
id: 0033
date: 2026-07-25
status: accepted
tags: [models, providers, foundry, chat-completions, api-version, openai, config, bugfix]
# amends (in-body, not superseded_by): 0014, 0017
---

# Route the Foundry Chat Completions path over `/openai/v1`, off the classic `api-version` data plane

## Context
A Foundry (`ProviderKind.AzureOpenAI`) provider with `UseResponsesApi` **off** — the Chat Completions
path — failed every turn with **"api version not supported"**. `ChatClientFactory.Provider.Foundry`
built that path through `AzureOpenAIClient.GetChatClient(model)`, which targets Azure's classic data
plane: `/openai/deployments/{model}/chat/completions?api-version=…`. The `api-version` query is not
ours to pick — the `AzureOpenAIClient` supplies it from `AzureOpenAIClientOptions.ServiceVersion`,
whose **default is the newest value the SDK knows**. The pinned stable **`Azure.AI.OpenAI 2.1.0`** caps
that enum at **`V2024_10_21`** (`2024-10-21`) — there is no newer stable release, and hard-rule #4
forbids pinning a prerelease from memory. Newer Foundry deployments reject `2024-10-21`, so the whole
Chat path was unusable on a current account. (Spike 2026-07-25: enumerated the `2.1.0`
`ServiceVersion` enum — `{V2024_06_01, V2024_10_21}`, default `V2024_10_21`.)

The Responses path (ADR 0014) never had this problem: it reaches Foundry's GA **"v1"** surface at
`{endpoint}/openai/v1`, which carries **no `api-version`** at all. ADR 0017 already established the
auth for that surface — the resource key rides the **`api-key` request header** via a custom
`ApiKeyHeaderPolicy` (the OpenAI SDK's default `ApiKeyCredential` would send `Authorization: Bearer`).

## Decision
Build the Foundry **Chat Completions** path the same way the Responses path is built: a stable
`OpenAIClient` pointed at `{endpoint}/openai/v1` with the existing `ApiKeyHeaderPolicy`, then
`GetChatClient(model).AsIChatClient()`. This removes `api-version` from the Chat path entirely —
the model/deployment name rides in the request body (`/openai/v1/chat/completions`), not the URL.
No package change (the `/openai/v1` chat surface is reachable on the already-pinned `OpenAI 2.12.0`,
exactly as the Responses surface is). The endpoint still comes only from config — SSRF invariant
intact (ADR 0013). Applies to **every** Foundry provider, shipped or user-added (ADR 0018), because
both route through `Provider.Foundry`.

**Embeddings are deliberately left on `AzureOpenAIClient`.** The Foundry embedding generator
(`GetEmbeddingClient(model)`, ADR 0030) still uses the classic client. Embeddings are an opt-in,
unreported-here path, and the `/openai/v1/embeddings` surface is unverified in this change; moving it
without a live check would risk a working path. If a Foundry embedding model ever hits the same
`api-version` wall, apply the identical `/openai/v1` treatment then.

## Alternatives considered
- **Pin a supported `api-version`** (set `AzureOpenAIClientOptions.ServiceVersion`, or make it a config
  knob). Rejected: the stable `2.1.0` SDK has nothing newer than `2024-10-21` to pin, so a working
  version would need a prerelease `Azure.AI.OpenAI` (against hard-rule #4) or an unbounded config knob
  the operator must keep re-guessing as Azure retires versions. The v1 surface exists precisely so
  callers stop tracking `api-version`.
- **Tell the user to reconfigure Foundry as an `OpenAICompatible` provider** pointed at
  `{endpoint}/openai/v1`. Rejected: it works by accident (Azure v1 also accepts the key as a Bearer
  token), abandons the documented `api-key` mechanism, loses the `AzureOpenAI:*` inheritance
  (ADR 0017), and pushes an internal transport detail onto the user for a first-class provider kind.
- **Move embeddings to `/openai/v1` in the same change.** Deferred (not rejected): same fix, but
  unverified surface and out of the reported scope — see Decision.

## Consequences
- **Both Foundry chat wire paths now share the `/openai/v1` endpoint.** `ChatClientMetadata.ProviderUri`
  can no longer tell Chat from Responses (it used to: `/openai/v1` vs the plain endpoint). The no-I/O
  test discriminator is now the underlying SDK client each path exposes via `GetService` — a
  `ResponsesClient` on the Responses path, a `ChatClient` on the Chat path (verified to survive the OTel
  wrapper the factory applies). `ResponsesProvider_WiresResponsesEndpoint` was reworked onto that handle.
- **The `api-key`-header policy is now load-bearing on the Chat path too**, not just Responses. Do not
  "simplify" the Chat client to `ApiKeyCredential` — that regresses to the undocumented Bearer position
  (ADR 0017). The class doc and the ADR-0017 gotcha are updated to reflect this.
- **`AzureOpenAIClient` survives only for the embedding client** inside `Provider.Foundry`. It no longer
  serves any chat traffic.
- **No behavioural change to the Responses path, capabilities, discovery, or the picker.** This is a
  transport fix for one branch; `GetCapabilities`, `UseResponses`, `SupportsHostedTools`, and the
  fatal-blank-key rule are untouched.
- **Live confirmation is the deploy gate.** The fix is unit-tested with no I/O (the SDK builds the client
  without a network call); the real "api version not supported" symptom clears only against a live
  Foundry account, which is the manual post-deploy check.

## Amendment (2026-10-17): embeddings moved to `/openai/v1` too
The deferred alternative came due. A user's Foundry provider configured with a **project** endpoint
(`https://{resource}.services.ai.azure.com/api/projects/{name}`) got `HTTP 400 "API version not
supported"` on every embeddings call, so every document upload returned 500. Embeddings now use a stable
`OpenAIClient` with the same `ApiKeyHeaderPolicy`. Nothing in `Provider.Foundry` uses
`AzureOpenAIClient` any more.

**One difference from chat: the base URL is the resource root.** A live probe that day found:

- the project-scoped `/openai/v1` serves `chat/completions` (200) but returns **404 on `/embeddings`**;
- `{resource}/openai/v1/embeddings` returns 200 with 3072 dimensions.

So `Provider.FoundryEmbeddingsEndpoint` drops any `/api/projects/{name}` suffix before appending
`/openai/v1`. The resource key authenticates there, because a project belongs to its resource.

The upload endpoints also stopped leaking provider errors as a bare 500. `DocumentIngestionService` now
wraps a `ClientResultException` from the generator in `EmbeddingProviderException`. Browser upload maps
it to 502 with the provider's message, and `/api/agent-run` handles it like its other ingest
rejections.
</content>
</invoke>
