# Plan (deferred stub): migrate the document index to Microsoft.Extensions.VectorData

`planned-against: main @ 5bb17c2` — **status: DEFERRED, not yet planned in full.**
Re-evaluated 2026-07-11 (after the `TextSearchProvider` plan shipped); decision reaffirmed: **defer**.

## Why this exists
Split out of the RAG-vs-AgentFramework comparison (see
`.agentic/specs/completed/plan-rag-textsearchprovider.md`). The user chose to **defer** migrating
our storage layer from the custom `IDocumentIndex` (3 methods) to the `Microsoft.Extensions.VectorData`
abstractions (`VectorStore` / `VectorStoreCollection<TKey,TRecord>` + `[VectorStoreKey]` /
`[VectorStoreData]` / `[VectorStoreVector]` attribute-mapped POCOs), as used by the AF
`AgentWithRAG_Step02` sample and the SK Cosmos NoSQL connector docs.

## 2026-07-11 re-evaluation (the questions below are now answered)
The `TextSearchProvider` plan shipped (ADR 0008, `.agentic/decisions/0008-always-on-textsearchprovider-retrieval.md`;
merged in commit 5bb17c2). That was the gating precondition. On re-check the calculus is **unchanged — still defer**:

- **The hard-rule-4 wall still stands.** Re-queried the feed 2026-07-11:
  `Microsoft.Extensions.VectorData.Abstractions` is stable (now **10.7.0**, up from 10.1.0), but both
  concrete connectors remain **preview-only** — `Connectors.CosmosNoSql` `1.52.0-preview`,
  `Connectors.InMemory` `1.23.0-preview` (evidence: `dotnet package search … --prerelease`). Taking
  either as a dependency still violates hard rule 4 (pin latest *stable*), and the SK Cosmos connector
  still create-on-uses, which 403s against our `disableLocalAuth: true` account (ADR 0007's rejection
  reason holds).
- **A rule-compliant path exists but is pure interface-standardization.** We *can* hand-write two custom
  `VectorStoreCollection<string, ChunkDocument>` subclasses (a no-create-on-use Cosmos one reusing
  `CosmosDocumentIndex`'s SQL + an in-memory one reusing the cosine scan) that depend on **only the
  stable abstractions package** — no preview connector, so **no hard-rule-4 violation**. Feasibility
  confirmed against the 10.1.0 abstractions XML: each subclass must implement the full abstract surface
  (~12 members — `Upsert`×2, `Get`×3, `Delete`×2, `Search`, `CollectionExists`,
  `EnsureCollectionExists`, `EnsureCollectionDeleted`, `GetService`, `Dispose`), of which we'd use only
  Upsert/Search/Delete and `NotSupportedException` the rest.
- **Zero functional gain.** `TextSearchProvider` (now shipped) takes a plain search **delegate**, not a
  `VectorStoreCollection` — the current 3-method `IDocumentIndex` already serves it. The migration
  changes no behavior; it only grows owned surface (~3 methods → ~24 members across two impls) to match
  the sample's shape.
- **User decision 2026-07-11: "nice-to-have but not now."** Reaffirmed defer — not worth owning ~24
  hand-written members + the owner-scoping rework (below) for no behavior change.

### Answers to the original open questions
1. *Worth hand-writing + owning two connectors for zero behavior change, given the hard-rule-4 wall on
   the ready-made ones?* — **No, not now.** Rule-compliant via custom subclasses, but zero functional
   gain and a large owned-surface increase.
2. *Would a stable `Connectors.CosmosNoSql` / `Connectors.InMemory` change the calculus?* — **This is the
   revisit trigger.** A stable connector that can also point at a **pre-created** container (no
   create-on-use, to survive `disableLocalAuth`) would remove both the hard-rule-4 objection and most of
   the hand-written surface, making standardization cheap enough to reconsider.
3. *New ADR + preserve the Cosmos schema?* — **Yes, if pursued.** Needs an ADR superseding ADR 0007's
   "raw `CosmosClient`, not a connector" alternative; the Bicep schema (`ownerKey`/`embedding`/lowercase
   `id`) is preservable via an attribute-mapped POCO (`[VectorStoreKey]` id, `[VectorStoreData]`
   ownerKey/fileName/page/chunkIndex/text, `[VectorStoreVector(3072)]` embedding) + `JsonSerializerOptions`.

## The genuine design wrinkle to solve before planning this (new finding)
Owner-scoping does **not** map onto `VectorStoreCollection<TKey,TRecord>` for free. Today `ownerKey` is
the **first parameter of every `IDocumentIndex` call** and the Cosmos **partition key**, so scoping is
baked into the API. The abstraction is keyed only by record id (`TKey`) and has no owner concept — so
per-owner scoping must move into `VectorSearchOptions.Filter` / a `GetAsync` LINQ expression + an
`ownerKey` record field (with `ownerKey` still the Cosmos partition key). Implementable and safe, but it
means the "standard" collection stays owner-aware internally, which partly undercuts the point of
standardizing. Resolve this shape (per-owner collection instance vs per-query filter) as the first design
step whenever this is picked up.

## Revisit trigger
Pick this up only if **either**: (a) `Microsoft.SemanticKernel.Connectors.CosmosNoSql` **and**
`Connectors.InMemory` reach a **stable** release that supports pointing at a pre-created container without
create-on-use — re-check with `dotnet package search <id>` (no `--prerelease`); **or** (b) a concrete new
need appears (e.g. swapping vector stores, or reusing standard VectorData tooling) that the current
3-method `IDocumentIndex` can't cheaply serve. Until then, the custom `IDocumentIndex` + raw
`CosmosClient` (ADR 0007) stays authoritative.
