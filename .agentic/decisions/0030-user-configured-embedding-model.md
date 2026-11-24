---
id: 0030
date: 2026-07-21
status: accepted
tags: [rag, documents, embeddings, model-providers, config, security]
amends: [0007, 0017, 0019]
extends: [0018]
---

# User-configured embedding model — remove the operator embedding availability floor

## Context
Document RAG (ADR 0007) embedded chunks with a fixed `text-embedding-3-large` deployment
authenticated by `AzureOpenAI:ApiKey` + `AzureOpenAI:EmbeddingDeploymentName`, both **fatal-if-blank
at startup** (Program.cs). After ADR 0019 made chat fully per-user this was the *last* operator
"availability floor" and the one remaining hardcoded model — the asymmetry this ADR removes. It also
kept a committed/operator Azure OpenAI key alive (flagged in a repo security review) that nothing else
needed.

## Decision
The embedding model is a **normal per-user model**: declared under a provider (ADR 0018) with a new
opt-in `SupportsEmbeddings` flag and **chosen as the user's "default embedding model" in Preferences**
(`PUT /api/default-embedding-model`, mirrors `PUT /api/default-model`). It is resolved through the
**existing per-user `IChatClientFactory`** via a new `GetEmbeddingGenerator()` / `DefaultEmbeddingModelId`
(mirrors ADR 0020's `GetImageGenerationService` — a `Func` on `Provider`, no new interface, no new
every-host registration). The operator floor is **removed**: no `AzureOpenAI:ApiKey`/`EmbeddingDeploymentName`
fatal checks, no operator embedding generator, no `AZURE_OPENAI_API_KEY` GitHub secret. The app boots with
no embedding capability until a user configures one.

## Alternatives considered
- **A parallel `IUserEmbeddingGeneratorProvider` seam** (own version cache + offline adapter + register-in-
  every-host). Rejected: re-created the register-in-every-host ripple that repeatedly bit `IMcpSecretProtector`/
  `PushConfig`/`IUserMcpToolProvider`; the ADR 0020 factory-extension shape needs neither.
- **Make the Cosmos `doc-chunks` vector policy dimension-configurable.** Rejected: a container can't hold
  mixed-dimension vectors and existing data would need re-provisioning. Kept fixed at 3072; ingest
  **validates-and-rejects** a model whose vectors don't fit.
- **Tag each chunk with its embedding model and filter retrieval.** Rejected as scope: switching the
  embedding model is **delete-to-reingest** (documented), not a migration this ADR builds.
- **Keep `AzureOpenAI:*` as an optional operator fallback embedding default.** Rejected: retains the
  operator config the ask eliminates; embeddings are now purely the per-user overlay (symmetric with 0019).

## Consequences
- **Enables** document RAG on any OpenAI-compatible or Foundry embedding model the user adds — no manual
  Foundry deployment prerequisite, no operator secret. Removes the `AZURE_OPENAI_API_KEY` secret entirely
  (Bicep param, ACA secret, `deploy.yml` wiring all gone); `AzureOpenAI:Endpoint`/`ApiKey` remain only as
  the **dormant** Foundry-provider inheritance source, `AzureOpenAI:TenantId` still binds `DefaultAzureCredential`.
- **No embedding model configured is a supported state:** the composer **blocks attaching documents** (images
  still allowed; server rejects too, defense-in-depth), and retrieval on an existing document conversation
  **degrades to empty context — never throws** (the ADR 0019 no-model-chat discipline extended to embeddings).
- **Accepted downsides:** (1) the chosen embedding model must produce **3072-dimension** vectors for the prod
  container; a mismatch is a clear ingest error. (2) Changing the embedding model invalidates existing vectors
  → **delete-and-reingest**. (3) **ConfigHealth loses its embedding checks** — `BuildStatic` is pure (no grain
  I/O, ADR 0029) so it can't see a per-user pick, and there is no operator embedding config left; visibility
  moves to the Model providers tab's embedding selector. (4) Egress unchanged from ADR 0013 — a third-party
  embedding endpoint sees document text.
- Zero migration: trailing `[Id(5)]` `SupportsEmbeddings` on `StoredModel`, trailing `[Id(3)]`
  `EmbeddingDefault` on `UserProviderConfig`. Offline (DevHost/tests) keeps the deterministic 128-dim
  generator behind the same per-user factory seam.

## Notes moved from gotchas.md (2026-11-24)

- **"Embeddings are a per-user model, not an operator prerequisite":** as of **ADR 0030** the embedding model is
  **per-user**, not a fixed operator deployment. There is **no** `AzureOpenAI:EmbeddingDeploymentName`, **no** eager
  `AzureOpenAI:ApiKey` startup check, and **no** operator `IEmbeddingGenerator` singleton — all removed. A user
  declares an embedding model on a provider (the `SupportsEmbeddings` opt-in flag, like ADR 0020's image flag) and
  picks it as the **default embedding model** in Preferences (`PUT /api/default-embedding-model`); ingestion and
  retrieval resolve the generator via **`IChatClientFactory.GetEmbeddingGenerator()`** (a `Func` on `Provider`,
  mirroring `GetImageGenerationService` — **no** new provider interface, **no** new every-host registration). Two
  behaviours are load-bearing: **upload** of a document with no embedding model configured is rejected (composer
  gates it client-side; server 4xx as defense-in-depth), while **retrieval** on an existing document conversation
  with no embedding model **degrades to empty context — never throws** (the ADR 0019 no-model-chat discipline
  extended to embeddings). The `text-embedding-3-large` deployment is still an Azure resource a user's Foundry
  provider points at, but it is **not** a repo prerequisite and needs no RBAC (API-key auth).
  `AzureOpenAI:Endpoint`/`ApiKey` survive ONLY as the **dormant** Foundry-provider inheritance source;
  `AzureOpenAI:TenantId` still binds `DefaultAzureCredential`. The `AZURE_OPENAI_API_KEY` GitHub secret + ACA secret
  are **removed**.
