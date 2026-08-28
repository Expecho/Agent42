---
id: 0008
date: 2026-07-10
status: accepted
tags: [rag, documents, retrieval, agent-framework, context-provider]
supersedes: 0007   # partial — only ADR 0007's retrieval mechanism (the agentic search_documents tool)
amended_by: 0056   # the ungated shape rejected below was later adopted, for memory rather than RAG
---

# Always-on TextSearchProvider retrieval, replacing the agentic search_documents tool

## Context
ADR 0007 shipped document RAG with an **agentic `search_documents` tool**: the model chose when to
retrieve. It explicitly rejected "always-on `AIContextProvider` auto-injection" because that "queries
the index every turn and can inject irrelevant context." That was the right call against the framework
of the day. Since then the pinned **Microsoft Agent Framework 1.13** ships a first-class
`TextSearchProvider` (an `AIContextProvider`) that performs retrieval automatically before every model
invocation (`BeforeAIInvoke`) and carries built-in multi-turn context (`RecentMessageMemoryLimit`) for
vague follow-ups a tool-call misses. This ADR revisits *only* the retrieval mechanism; ADR 0007's
storage/ingest/scoping design is unchanged and remains authoritative.

## Decision
Retrieve document context with an always-on `TextSearchProvider` (`SearchTime = BeforeAIInvoke`,
`RecentMessageMemoryLimit = 5`), replacing the agentic `search_documents` tool. The provider's search
callback is the same embed-query → owner-scoped `IDocumentIndex.SearchAsync` path the tool used. It is
**attached only when the conversation has at least one ingested document** (`DocumentNames.Count > 0`);
document-free conversations get exactly today's provider set and make **zero** embedding calls. Injected
retrieval results are **excluded from session storage** (see Consequences).

## Alternatives considered
- **Keep the agentic `search_documents` tool (ADR 0007 status quo)** — a hand-rolled reimplementation
  of what 1.13 now ships natively, and it misses the vague-follow-up handling `RecentMessageMemoryLimit`
  gives for free. Rejected: delete code we now get from the framework.
- **`TextSearchProvider` in `OnDemandFunctionCalling` mode** — exposes retrieval as a model-invoked tool,
  i.e. re-creates today's agentic tool via the framework. Rejected: it is not the always-on behavior chosen.
- **Always-on but *ungated*** — this is exactly what ADR 0007 rejected: an embedding round-trip every turn
  in every chat, and a broken embedding deployment breaking *all* conversations. Rejected in favour of the
  `DocumentNames.Count > 0` gate below, which is what makes the reversal safe.
  **This rejection was later overridden — see ADR 0056.** Long-term memory has no per-conversation
  analogue (spanning conversations is the point), so it ships ungated, with a per-turn cost that is
  *worse* than the one rejected here: two third-party HTTP calls carrying conversation content, rather
  than one embedding round-trip inside the tenant. Read 0056 before citing this bullet as settled.
- **Migrate storage to `Microsoft.Extensions.VectorData`** — deferred to its own spec
  (`.agentic/specs/plan-rag-vectordata-abstractions.md`); `TextSearchProvider` needs a plain search
  delegate, not a `VectorStoreCollection`, and the concrete connectors are preview-only (hard rule 4).

## Consequences
- **Directly rebuts ADR 0007's rejection reason.** 0007 feared "queries the index every turn and can
  inject irrelevant context." The **`DocumentNames.Count > 0` gate** confines the per-turn embedding
  round-trip to conversations that actually have documents — plain chats are entirely unaffected and a
  broken embedding deployment can only degrade document conversations. `RecentMessageMemoryLimit = 5`
  keeps retrieval relevant across vague follow-ups. Within a document conversation retrieval does run each
  turn: the deliberate, accepted cost of the always-on behavior.
- **Session-storage safety (the reason for a Step-1 spike).** `BeforeAIInvoke` injects the result as a
  `ChatRole.User` message tagged `AgentRequestMessageSourceType.AIContextProvider`. The spike found that
  with the default chat-history provider this injected message is **stored into the session's chat
  history** and carries an `AgentRequestMessageSourceAttribution` struct in `AdditionalProperties` that
  the framework's default source-gen serializer cannot handle — so the grain's existing per-turn
  `SerializeSessionAsync` **throws `NotSupportedException` every turn** (a hard break, beyond the 2 MB
  growth ADR 0004 warns about). **Fix:** set `ChatClientAgentOptions.ChatHistoryProvider` to an
  `InMemoryChatHistoryProvider` whose `StorageInputRequestMessageFilter` excludes `AIContextProvider`
  (and `ChatHistory`) messages. Its `StateKey` matches the implicit default, so **existing serialized
  sessions round-trip with no state migration**. Retrieval still reaches the model; injected result text
  is never persisted (verified across 3 turns).
- **Scope is the retrieval mechanism only.** Ingest-only documents, the owner-scoped Cosmos NoSQL vector
  container with no create-on-use, extraction/chunking, the `text-embedding-3-large` embedding, the
  prod-only vector path with an in-memory offline index, and `DocumentNames` awareness injection are all
  **unchanged** from ADR 0007. Only awareness wording changes (retrieval is now automatic, not "call the
  tool"), and the `search_documents` tool is removed.
- **Deletes** the `search_documents` `AIFunction` and its `search_documents` naming across product/test
  comments. Retrieval no longer surfaces a tool-call chip in the UI (it is silent/automatic) — accepted.
- Builds on ADR 0004 (2 MB session cap — the storage filter defends it) and partially supersedes ADR 0007
  (its retrieval decision only). Implemented under `.agentic/specs/completed/plan-rag-textsearchprovider.md`.
