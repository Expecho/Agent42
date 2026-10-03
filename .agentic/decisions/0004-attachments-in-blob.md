---
id: 0004
date: 2026-07-08
status: accepted
tags: [attachments, storage, grains, multimodal]
amended_by: 0084   # artifacts: artifact tool-call content is compacted out of the session at the persist sites
---

# Store image attachments in blob storage, send them to the model as SAS URLs

## Context
Users can attach images to a prompt for the model to analyse. The durable bytes cannot
live in grain state: the Agent Framework `AgentSession` (the full message history) is
serialized into a single Cosmos item on every turn (`AgentGrain.cs`), and Cosmos hard-caps
an item at **2 MB**. A single real phone photo base64-encodes to several MB, so inlining
bytes (`DataContent`) into the session would blow that limit on the first real photo.

A Step 1 spike against the real `gpt-4o` deployment established two facts that shaped the
design: (a) Azure OpenAI **accepts a private-blob user-delegation SAS URL** as image input
(it described a private-container image reached only via SAS); and (b) it **re-fetches a
prior turn's image URL on every subsequent turn** — after a serialize→deserialize (grain
reactivation) with the blob deleted, the next turn failed with `HTTP 400 … Failed to
download image`.

## Decision
Image bytes live in a **private Azure Blob container**, addressed by the per-user,
per-conversation grain key (`{userId}/{conversationId}/{id}`, ADR 0001 scoping). They never
enter the serialized session. Two different URLs serve two consumers (an `IAttachmentStore`
abstraction with a blob impl for prod and an in-memory impl for dev/tests):

- **The model** gets a short-lived **user-delegation SAS URL** as a `UriContent` part — a
  short string the session serializes as a reference, not bytes. Key-free: the container
  app's managed identity holds *Storage Blob Data Contributor*, which also authorizes
  `generateUserDelegationKey`, so no account key exists (shared-key auth is disabled).
- **The browser** gets a first-party display proxy
  (`GET /api/conversations/{id}/attachments/{attachmentId}`) — authenticated by Easy Auth
  (ADR 0002), user-scoped by grain key. The page never holds a SAS.

Because prior-turn URLs are re-fetched (spike fact b), the grain **rehydrates on read**: before
every model call it refreshes the SAS on every attachment `UriContent` already in the session
(`AgentGrain.RehydrateAttachmentUrlsAsync`, called from both `ChatAsync` and `ReceiveReminder`),
so a SAS only has to outlive a single turn. This must be per-turn, not merely on activation — an
Orleans grain stays warm across many turns, so an activation-only refresh would let a SAS lapse
mid-conversation and 400 on the next turn.
Attachment metadata is persisted per user turn in `AgentGrainState.TurnAttachments` (one
entry per user turn, in order) so history joins each turn to its images by index.

## Alternatives considered
- **Inline bytes in the session (`DataContent`)** — simplest, but blows the 2 MB Cosmos
  item cap on the first real photo. Rejected as the primary path; kept only as the spike's
  documented fallback had the SAS-URL approach failed (it did not).
- **Rehydrate only on activation** — cheaper, but wrong: a warm grain serves many turns without
  reactivating, so a 1 h SAS would expire mid-conversation and the next turn would 400. Refreshed
  per model call instead (the delegation key is cached, so re-minting is cheap).
- **A long-lived SAS instead of rehydrate-on-read** — a *user-delegation* SAS is capped at
  **7 days** by the delegation key lifetime, so a conversation reopened later would break on
  re-fetch. A truly long-lived SAS needs an account key, which the key-free design rejects.
  Rehydrate-on-read is robust to any SAS lifetime.
- **Azure AI Search / vector store now** — out of scope; that is document RAG, a separate
  future plan. This ADR is images only.

## Consequences
- A new Azure Storage account + private `attachments` container is provisioned in Bicep
  (`infra/modules/storage.bicep`). The blob endpoint is passed to the container app as
  `Storage__BlobEndpoint`.
- **The Storage Blob Data Contributor grant is in Bicep** (`infra/modules/storage-role-assignment.bicep`),
  contingent on a one-time manual elevation: it is classic `Microsoft.Authorization/roleAssignments`,
  which a plain Contributor cannot create, so the deploy SP was granted **Role Based Access Control
  Administrator** on `rg-agent42` (done 2026-07-08). Unlike the OpenAI grant — which stays a manual
  `az rest` step because its account is in a *different* RG where a cross-RG Bicep role module fails —
  the storage account is in the deploy RG, so once the SP can write role assignments there, Bicep
  handles it. If that elevation is removed or the SP recreated, fall back to a manual grant (see gotchas).
- Bytes staying out of Cosmos is a **prod-blob property** the offline unit harness cannot
  exercise (the in-memory store returns non-expiring placeholder URLs), so it is verified by
  the Step 1 spike + code review rather than a unit test — honestly out of unit scope.
- Additive to ADR 0001 (grain-per-conversation) and ADR 0002 (Easy Auth), not a
  supersession. Local full-stack (Azurite) supports upload/display/history but cannot deliver
  images to the real model — Azure OpenAI can't fetch a `localhost` SAS URL.
