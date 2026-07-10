# Plan (deferred stub): migrate the document index to Microsoft.Extensions.VectorData

`planned-against: main @ 05af23b` — **status: DEFERRED, not yet planned in full.**

## Why this exists
Split out of the RAG-vs-AgentFramework comparison (see
`.agentic/specs/plan-rag-textsearchprovider.md`). The user chose to **defer** migrating our storage
layer from the custom `IDocumentIndex` (3 methods) to the `Microsoft.Extensions.VectorData`
abstractions (`VectorStore` / `VectorStoreCollection<TKey,TRecord>` + `[VectorStoreKey]` /
`[VectorStoreData]` / `[VectorStoreVector]` attribute-mapped POCOs), as used by the AF
`AgentWithRAG_Step02` sample and the SK Cosmos NoSQL connector docs.

## The core tension to resolve before planning this
- **Only the abstractions are stable.** `Microsoft.Extensions.VectorData.Abstractions` is available
  stable (10.1.0 on the feed). Both concrete connectors we'd want are **preview-only** —
  `Microsoft.SemanticKernel.Connectors.CosmosNoSql` and `Microsoft.SemanticKernel.Connectors.InMemory`
  (`1.67.0-preview`) — which violates hard rule 4 (pin latest stable).
- **The SK Cosmos connector create-on-uses**, which 403s against our `disableLocalAuth: true` account
  (ADR 0007's original rejection reason still holds).
- So a *faithful* migration means **hand-writing two custom `VectorStoreCollection<string, ChunkDocument>`
  subclasses** (a no-create-on-use Cosmos one + an in-memory one), each implementing ~12 abstract
  members (Upsert×2, Get×3, Delete×2, Search, CollectionExists/Ensure×3, GetService, Dispose).
  Evidence: `Z:\Packages\.nuget\packages\microsoft.extensions.vectordata.abstractions\10.1.0\lib\net8.0\Microsoft.Extensions.VectorData.Abstractions.xml`.
- **No functional gain**: `TextSearchProvider` (the other plan) needs a plain search delegate, not a
  `VectorStoreCollection`. This migration is interface-standardization only.

## Open questions for the future planning session
1. Is the standardization worth hand-writing + owning two connectors, given zero behavior change and
   the hard-rule-4 wall on the ready-made ones?
2. Would waiting for a **stable** `Connectors.CosmosNoSql` / `Connectors.InMemory` release (that also
   supports pointing at a pre-created container without create-on-use) change the calculus?
3. If pursued: does it need a new ADR (superseding ADR 0007's "raw CosmosClient, not a connector"
   alternative), and can the Bicep-declared container schema (`ownerKey`/`embedding`/lowercase `id`)
   be preserved exactly through the attribute-mapped POCO + `JsonSerializerOptions`?

Do **not** start this until the `TextSearchProvider` plan has shipped and the questions above are
answered.
