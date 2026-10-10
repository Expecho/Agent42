---
id: 0094
date: 2026-10-10
status: accepted
tags: [agents, stored-agents, webhooks, egress, data-egress, security, ssrf, signing, secrets, http, orleans, no-migration]
supersedes: []
amends: [0013]
---

# Outbound webhooks for stored agents (an ADR 0013 egress extension)

## Context
Inbound automation exists (ADR 0022 `/api/agent-run`, ADR 0070 MCP), but a finished run only reaches the owner's
phone (ADR 0028 push), never another system. The owner asked for a stored agent to POST each completed turn to a URL
they choose. That sends conversation content — the prompt and the full reply — to a third party, so this is an
**extension of ADR 0013's egress set** (CLAUDE.md rule 13), recorded here as one.

## Decision
A stored agent has at most one outbound webhook. Every completed, **durably persisted** turn of a conversation bound
to that agent POSTs a signed `agent.turn.completed` event to the owner's URL, whatever the entry point: the live UI,
`/api/agent-run`, `ask_agent` over MCP, a fired reminder, an image turn, an edit or regenerate, or any future drain of
`ChatCoreAsync`. The owner configures it in the agent dialog's **Webhook** tab.

1. **"Persisted" is the gate.** `PersistTurnUnlessEphemeralAsync` returns `bool` (false on its ephemeral return), and
   the chat site fires only on `true`. The site never re-reads `_ephemeral` — a turn-scoped field that
   `EnqueuePromptAsync` reassigns during this iterator's yields; the persist reads it after those yields, and the
   `bool` inherits exactly that read, so the gate is as safe as the write it reports on and never weaker. Failed
   turns, the no-model path and the Copilot refusal never reach the persist and never fire. A stopped turn (ADR 0078)
   persists and fires with `stopped: true`. The image turn fires inside its own `if (!_ephemeral)` block, and the
   reminder fires after its own write and **before** the ADR 0075 push early return. Three sites, because neither
   `ReceiveReminder` nor the image turn goes through the chat persist.
2. **Live configuration, not snapshotted.** The webhook is delivery routing, like `Continuous` (ADR 0016) and
   `McpExposed` (ADR 0070), not persona (ADR 0003). The conversation never stores the URL; it fires to whatever the
   agent's webhook grain holds at delivery time.
3. **Its own grain, `IAgentWebhookGrain` keyed `{userId}/{agentId}`,** state `agent-webhook` in the one `agent-state`
   container (no Bicep change). Not fields on `AgentDefinition`: `GET /api/agents` serializes that record verbatim,
   which would echo the encrypted secret to the browser.
4. **Fire and forget.** `AgentGrain` calls `DeliverAsync` as **`[OneWay]`**, inside try/catch, only when the
   conversation is bound — an awaited HTTP call would stall the non-reentrant conversation grain. A delivery can take
   ~40 s (3 × 10 s + 2 s + 8 s), past the 30 s call timeout, so `GetAsync`, `SaveAsync` and `ClearAsync` are
   `[AlwaysInterleave]`, and a delivery re-reads the configuration **after** its send before logging: an interleaved
   clear wins and the log write is dropped. At most `MaxPendingDeliveries` (10) queue; beyond that a delivery is
   logged as `dropped-backlog`. `SendTestAsync` is itself a delivery and is not interleaved.
5. **Standard Webhooks signing.** `webhook-id`, `webhook-timestamp` (unix seconds) and
   `webhook-signature: v1,<base64 HMAC-SHA256(secret, "{id}.{timestamp}.{body}")>`, secret `whsec_<base64 of 32
   random bytes>`. Receivers get off-the-shelf verifiers, and id + timestamp give replay protection.
6. **The secret is minted server-side and shown once** (the ADR 0022 key shape), and the grain stores **only
   ciphertext**, protected in the endpoint by `WebhookSecretProtector` under its own Data Protection purpose (a
   sibling of `McpSecretProtector`). It cannot be hashed like the ADR 0022 key, since HMAC needs it on every send. The
   Backend sender unprotects at send time; a secret the key ring can no longer decrypt records `secret-unavailable`
   and **nothing is sent unsigned**.
7. **The URL is SSRF-guarded, although it is owner config.** The provider-endpoint exemption (gotcha "deliberately NOT
   SSRF-guarded") does not transfer: (a) **Send test** and the delivery log turn every send into a status-code oracle
   for the container's network, which holds the managed identity IMDS mints tokens for; (b) ADR 0022 and ADR 0070 key
   holders trigger turns, and so sends, with no browser session; (c) there is no legitimate private-address use, as
   there is for Ollama on `localhost`. Save time rejects anything but an absolute `https` URL without userinfo and
   with no private IP-literal host (a UX error only). Connect time is the control: the primary handler is
   `GuardedDocumentFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable)` with `AllowAutoRedirect = false`. A
   source-text test pins the wiring (the ADR 0066 lesson). The log keeps a status code and an error class, never a
   response body.
8. **Own named client, own retry (rule 8).** `RemoveAllResilienceHandlers()`, a 10 s timeout, and the sender's own
   loop: 3 attempts, 2 s then 8 s, on transport errors (not a blocked address), timeouts, 5xx and 429; other 4xx are
   final. The sender never throws.
9. **Payload:** `type`, `id`, `timestamp`, `agent {id, name}`, `conversationId`,
   `trigger: interactive|background|reminder`, `reminderName`, `prompt`/`promptTruncated` (4 000 chars),
   `reply`/`replyTruncated` (32 000 chars), `stopped`, `imageCount`, `replacesLastTurn`. Truncated in the grain,
   before the OneWay call. No attachment or SAS URL ever (credential-bearing, ADR 0004/0013), no deep link (there is
   no conversation route), no tool calls or reasoning. `interactive` is the local captured at the top of
   `ChatTurnAsync`, never a re-read of `_interactiveTurn`.
10. **Edit and regenerate (ADR 0092) fire with `replacesLastTurn: true`.** They are a truncate call followed by an
    ordinary `ChatAsync`; the flag rides a trailing `ChatAsync` parameter as a local, never a field the truncate
    sets — an `EnqueuePromptAsync` or reminder running between the two calls would consume it. The truncate is not a
    turn and never fires. A fork copies the binding, so its later turns fire under the fork's id.
11. **Delete clears the webhook first.** `AgentRegistryGrain.DeleteAsync` awaits `ClearAsync` **before** removing the
    agent — the opposite of the API-key directory's order, on purpose. A stale key-directory entry is harmless
    because the registry lookup then fails closed; a stale webhook keeps sending content for orphaned conversations
    (ADR 0003), which never consult the registry. Clearing first fails safe: worst case, the agent survives without a
    webhook.

## Alternatives considered
- **Fields on `AgentDefinition`** — rejected: the record is echoed by `GET /api/agents`, secret included.
- **Snapshot the URL into the conversation binding** — rejected: it is routing, and removing a webhook must stop
  existing conversations firing at once.
- **An awaited send from `AgentGrain`** — rejected: the non-reentrant grain would stall for the whole delivery.
- **`AddServiceDefaults`' retry pipeline** — rejected (rule 8): unbounded against our own budget, and it retries a
  signed POST we cannot see.
- **A durable outbox** — deferred: an event in flight at a silo restart is lost, accepted because the reply is durably
  in the conversation anyway.
- **Leaving the URL unguarded, like provider endpoints** — rejected: the oracle above.
- **A custom signature header** — rejected: every receiver would hand-roll verification.
- **Suppress the event on regenerate, or send a separate `agent.turn.replaced`** — rejected: the receiver would keep a
  reply the user replaced, or get two events for one action with no reply on the first.

## Consequences
- A third egress rule beside push (ADR 0028/0075): the owner's URL receives every bound turn's prompt and reply. It is
  opt-in per agent, owner-configured, and the panel says so plainly.
- No retry after a restart, no redelivery from the log, no failure events, one webhook per agent.
- `IAgentWebhookSender` is a mandatory grain dependency, registered in every silo host (rule 7); the endpoints resolve
  their dependencies optionally and answer 503 without them.
- DevHost registers the Null sender: the guard cannot be opted out of for local development.
