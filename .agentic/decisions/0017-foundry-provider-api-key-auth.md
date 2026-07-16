---
id: 0017
date: 2026-07-16
status: accepted
tags: [models, providers, foundry, auth, api-key, managed-identity, responses-api, embeddings, config, infra]
supersedes: 0013, 0014 (credential mechanism only)
---

# Authenticate the Foundry provider with an Azure OpenAI API key, not managed identity

> **Amended by [ADR 0018](0018-user-configurable-model-providers.md) (2026-07-16) — secret-source
> scope only.** Baseline secrets still come from **operator-managed env** (one source of truth for
> the baseline Foundry key + embeddings), and a blank **baseline** Foundry key is still fatal at
> startup. ADR 0018 adds a **second, per-user** secret source: a signed-in user's overlay keys,
> persisted **encrypted** and best-effort (a user override that resolves blank falls back to the
> baseline — never fatal). Everything else here stays authoritative. `status: accepted` on purpose
> — see 0018's "Partial supersession" note.

## Context
The Azure AI Foundry provider (ADR 0013) reached the model data plane with the container app's
managed identity — a `TokenCredential` on the Chat path, and a managed-identity **bearer policy** on
the Responses `/openai/v1` path (ADR 0014). The operator wants Foundry reachable with **key-only**
operation: no managed identity on the inference path. That contradicts the credential decisions in
0013/0014, so it ships as a superseding decision. The change is scoped to the **Foundry/OpenAI data
plane only** — blob storage and Cosmos keep their own identity/emulator auth.

Two facts from the Step-1 spike (2026-07-16, live against `agent42.services.ai.azure.com`, deployment
`gpt-5.4`) settle *how* the key authenticates: on the **Chat** path `AzureOpenAIClient` +
`AzureKeyCredential` sends Azure's `api-key` header itself; on the **Responses** `/openai/v1` path the
OpenAI SDK's `ApiKeyCredential` would send `Authorization: Bearer <key>`, but Azure reads the resource
key from the **`api-key` request header** — so a custom `api-key`-header `AuthenticationPolicy` is
needed there (the correct key gets past auth on both header positions, a wrong key 401s; `api-key` is
Azure's documented mechanism, so that is what ships). An Azure OpenAI data-plane key **cannot**
authenticate ARM, so managed-identity model **discovery** is dropped with it.

## Decision
`Provider.Foundry` takes an **API key**, not a `TokenCredential` (full replacement, no MI fallback).
Chat → `AzureOpenAIClient(endpoint, AzureKeyCredential)`; Responses → `OpenAIClient` at
`{endpoint}/openai/v1` with a custom **`api-key`-header** policy. The key resolves like the other
Foundry settings — provider-level `Models:Providers:<key>:ApiKey`, else the top-level
`AzureOpenAI:ApiKey` — a **single source** the embedding client also reads. Because Foundry is the
**availability path**, a blank resolved key is **fatal at startup** (thrown in `ModelProviderBuilder`,
mirrored by the Bicep `@minLength` on the key). ARM discovery is off for the shipped provider
(`Discover: false`); the picker offers **exactly** the deployments the provider **declares** in config,
and the **default deployment must be one of them** — `CompositeModelCatalog` only synthesizes the default
as a fallback when *nothing* is declared, so a default absent from a non-empty declared list vanishes from
the picker (keep `AzureOpenAI:DeploymentName` / the Bicep `azureOpenAIDeploymentName` in sync with the
`Models` list).

**Partial supersession of 0013/0014 — credential mechanism only.** Everything else in both ADRs stays
authoritative: the qualified `provider:model` id split, capability flags, the cross-tenant egress
position (0013); stateless-mandatory Responses, the two experimental suppressions,
`includeReasoningEncryptedContent: false` (0014). 0013 and 0014 keep `status: accepted` and carry a
forward-pointing in-body amendment to here — deliberately **no `superseded_by` tag**, because the
active-ADR scan (`grep -rL "superseded_by"`, `README.md`) would otherwise drop these two
heavily-referenced ADRs from the active set (the same trap `0007`'s `# partial` tag falls into).

## Alternatives considered
- **Keep managed identity; add the key as an optional override / MI fallback.** Rejected: the operator
  chose full replacement — key-only operation, no MI on the inference path. A dual path is dead config.
- **`Authorization: Bearer <key>` on `/openai/v1` (the OpenAI SDK's `ApiKeyCredential` default).**
  Rejected as the shipped mechanism: the spike showed Azure *also* accepts the resource key in the
  Bearer slot, but `api-key` is Azure's documented resource-key header — Bearer there is undocumented
  and could regress. The custom `api-key`-header policy is the deliberate choice.
- **`AzureOpenAIClient.GetResponsesClient` (native Azure Responses accessor).** Rejected: first appears
  in `Azure.AI.OpenAI 2.2.0-beta`; no stable release above `2.1.0`, and a prerelease pin is barred by
  hard rule 4 (same reasoning as 0014). The `api-key`-header policy on the stable `OpenAIClient` is the
  only viable stable Responses path.
- **Keep ARM discovery via a separate credential.** Rejected: a data-plane key can't authenticate ARM,
  and re-introducing an MI purely for discovery defeats key-only operation. Discovery degrades to
  declared models (`CompositeModelCatalog` already degrades to the default when nothing is listed).

## Consequences
- **A blank Foundry key is now FATAL (a deliberate reversal).** ADR 0013's "a missing key degrades, not
  fails" rule applies to **third-party** providers layered on Foundry; Foundry is the availability path
  itself, so its key must fail fast like the endpoint check and `SEARXNG_SECRET`'s `@minLength`. The
  throw lives in `ModelProviderBuilder.Build` (testable — `BlankFoundryKey_IsFatal`), and the Bicep
  param follows the availability-path **shape** of `searxngSecret` — unconditional secret + unconditional
  env + a `@minLength` (here `@minLength(32)`; `searxngSecret` itself uses 16) — **not** the optional
  `githubModelsToken`/`googleMapsApiKey` `empty(x) ? []` shape.
- **One key, one source of truth.** Both the Foundry provider (via inheritance) and the embedding client
  read `AzureOpenAI:ApiKey`, threaded as a single `AzureOpenAI__ApiKey` Container App secret/env var —
  no second `Models__Providers__foundry__ApiKey`. Extends the endpoint-inheritance invariant to the key.
- **Model discovery is off for the key-based Foundry provider.** The ARM machinery
  (`AzureModelCatalog`/`ArmClient`) stays in the tree for any future discovering provider, but the
  shipped `foundry` provider sets `Discover: false` and **declares** its deployments in `appsettings`.
  Adding a deployment is a config edit to that `Models` list.
- **`gpt-4o` is no longer a valid default.** The Step-1 spike found the account has **no `gpt-4o`
  deployment** (its chat deployments include `gpt-5.4`, `gpt-4.1`, `gpt-5.1`, `o3`, `model-router`,
  `claude-*`). With discovery off, an unreachable default would 404 every turn, so the default deployment
  moved to **`gpt-5.4`** (appsettings + Bicep). `includeReasoningEncryptedContent: false` verified fine
  on it.
- **The managed identity is no longer needed for inference/embeddings.** The container app MI keeps its
  blob (user-delegation SAS) and Cosmos roles; the AI Foundry account grants ("Cognitive Services OpenAI
  User", "Reader") are no longer required for the app to function (leaving them is harmless).
- **Manual prerequisite:** create the `AZURE_OPENAI_API_KEY` GitHub Actions secret (the Azure OpenAI
  resource key). Without it the deployment fails validation (`@minLength`) — by design.
- **Runtime confirmation — DONE (Step-1 gate, 2026-07-16, ✅ green** against
  `agent42.services.ai.azure.com` / `gpt-5.4`). Chat streamed a completion under `AzureKeyCredential`;
  Responses `/openai/v1` under the `api-key`-header policy returned content **and fired hosted
  `web_search` with `store=false`**; a wrong key 401s on both surfaces. The gitignored spike harness is
  ephemeral — the working header (`api-key`) is recorded here and in gotchas.
