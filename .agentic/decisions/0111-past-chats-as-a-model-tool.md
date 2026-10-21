---
id: 0111
date: 2026-10-21
status: accepted
tags: [search, tools, selection, recall, cosmos, full-text, egress, untrusted, no-migration]
supersedes: []
extends: [0061, 0021, 0098]
amends: [0061, 0070, 0096]
---

# The model can search and read the user's other conversations (`past_chats`)

## Context
Chat search (ADR 0061) lets the **user** search every message in their non-incognito conversations through the sidebar. The
**model** could not. A conversation sees only its own history, Hindsight (ADR 0056/0057) recalls extracted facts rather
than what was said, and RAG covers one conversation's documents. Hermes Agent ships "search across past sessions",
and the 2026-10-21 comparison named it as the cheapest idea to borrow: the projection, owner scoping and incognito
exclusion already exist.

## Decision
One new selectable key, **`past_chats`**, covering two built-in tools:

- **`search_past_chats(query)`** returns relevance-ranked snippets from the user's other conversations. Each comes with
  the conversation's title, last-activity date, `conversationId` and `ordinal`.
- **`read_past_chat(conversationId, ordinal)`** returns a bounded window of turns around a hit.

The budgets live in `PastChatLimits`: 8 hits, at most 2 per conversation; a 2-before/3-after window with 2 KB per turn;
10 calls per turn on `TurnContext` (ADR 0106). A successful result of either tool is **compacted** to a stub after the
turn (the ADR 0098 shape). No new container, grain state, Bicep or frontend.

### Decisions inside that, each with its rejected alternative

1. **Read the projection, never the other conversation's grain.** `read_past_chat` uses a new
   `IChatSearchIndex.GetTurnsAsync`. *Rejected: `IAgentGrain(other).GetHistoryAsync()`.* A self-call deadlocks the
   non-reentrant grain. Activating another conversation connects its MCP servers. And an activation's in-memory
   incognito turns are exactly what the index never holds. Cost: the index is best-effort (ADR 0061), so a window can
   have gaps. The result then says "some turns are missing", and never passes for complete.
2. **Ranked any-term search through a new port method, `SearchRankedAsync`.** Cosmos uses `FullTextContainsAny` +
   `ORDER BY RANK FullTextScore`, the BM25 switch ADR 0061 declared the full-text policy for. The in-memory twin ranks
   by distinct terms matched, then occurrences. It is **not** BM25, so tests pin only "any term" and "more terms
   first". *Rejected: reusing the sidebar's `CONTAINS`.* Keyword queries from a model would almost never be an exact
   substring. *Rejected: changing the sidebar too.* Users type substrings, and that query stays as it was.
3. **Terms are inlined as escaped literals, passed variadically.** `FullTextScore`'s term arguments must be string
   literals, and the account rejects the array form some docs show (`SC2241`, measured on prod 2026-10-21). Terms come from
   `PastChatQuery.Terms` (letters and digits only, lower-cased, at most 8 × 64 chars) and are JSON-escaped with the
   relaxed encoder. A test pins that a quote, a backslash or a bracket cannot leave its literal.
4. **Ownership is the partition plus the conversation list.** Every index call is single-partition on the grain-key
   user id, the id `ChatSearchProjection` writes with. Every hit is joined against `IConversationIndexGrain.GetConversationsAsync()`,
   which supplies title and date and **drops** conversations the user no longer has, including ADR 0068's permanent
   orphans. `ConversationIndexGrain` makes no outbound grain calls, so the AgentGrain→index call cannot form a cycle.
5. **The current conversation is excluded** from search, and reading it is refused. Hits are over-fetched at the
   index ceiling (50) before the current conversation, orphans and the per-conversation cap filter them.
6. **ON by default** (`AllSelectableToolKeys`, the ADR 0057 memory precedent). Per ADR 0021 it lands **OFF** for anyone
   who ever pressed Save, and for agents bound before this release. Not migrated.
7. **Copilot chats get it** through `CopilotFunctions` → `SelectableTools`. The tools need only the index and a grain
   call on the activation scheduler. Compaction does not reach Copilot's own SessionFs transcript; that is accepted.
   Research agent (`EnabledTools = [web_search]`) and fleet (`FleetToolCatalog`) do not get it.
8. **`ask_agent` strips it at bind time** (amends ADR 0070). `AgentMcpRunner.WithoutOwnerScopedTools`, formerly
   `WithoutMemoryOrPush`, already removed memory because recall "would hand that caller content from the owner's
   unrelated conversations". These tools do that verbatim. The filter only narrows.

## Residuals and egress (rule 13)
- **Untrusted text reaches trusted chats, unlabelled. This is a user decision.** Email-started (ADR 0096) and
  `/api/agent-run` conversations are indexed and therefore findable. Hits are **not** fenced as untrusted, unlike
  `read_document`'s web documents. The user chose that over "own chats only" and "labelled". Do not add a fence without
  revisiting this. **Titles** are a second unfenced channel: a title is the first prompt (60 chars), which an email
  sender or API caller writes.
- **Externally driven runs gain a read path over the whole history.** `ask_agent` is gated (decision 8). **Inbound email
  and `/api/agent-run` are not.** An email sender, or an API-key holder (a weaker credential than the owner's login),
  can tell a `null == all` agent bound after this release to search the owner's chats and leak the result through
  its outbound channels (fetch, webhook). The mitigation is ADR 0096's: point email, and agent-run keys you hand out,
  at an agent whose `EnabledTools` excludes `past_chats`, `memory` and the fetch tools. Extending the bind filter to
  these doors is a small change if this proves too loose.
- **Cross-provider movement (an ADR 0013 extension).** Providers are per user (ADR 0018). A chat held on one provider can
  now be quoted into a chat running on another (Azure OpenAI → Anthropic → GitHub Copilot), so a provider receives
  text it never saw. The text stays within one user's own providers. With `memory` on, a reply built on recalled text is
  also retained to Hindsight.
- **The Cosmos ranked query is unverified offline.** Its SQL text is pinned by tests, but its execution needs a prod
  smoke. A first prod run (2026-10-21, Data Explorer) got past `FullTextContainsAny` with no enrollment error, but
  rejected the array form of `FullTextScore` with `SC2241`, so the builder emits the variadic form. A full successful
  run of the variadic query is still pending. If `ORDER BY RANK` is refused, the fallback is local to `CosmosChatSearchIndex.SearchRankedAsync`: one
  `CONTAINS` per term, ranked in-process with `InMemoryChatSearchIndex.RankByTerms`. The port and the tools do not change.

## Consequences
- Two port methods on `IChatSearchIndex` (every implementer and test double gains them).
- `CompositeIndexMatchesTheSearchQueryTests` now iterates every SQL builder. An `ORDER BY RANK` is recognised as needing
  no composite index.
- Each per-conversation cap and window number is a constant in `PastChatLimits`, and a test pins that the read window
  fits its total.
