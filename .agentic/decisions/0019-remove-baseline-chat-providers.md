---
id: 0019
date: 2026-07-17
status: accepted
tags: [models, providers, config, per-user, availability, embeddings, security, data-egress]
supersedes: 0018 (availability-floor invariant only), 0017 (blank-baseline-Foundry-key-is-fatal, chat scope only)
---

# Chat providers are fully per-user — the app ships no baseline chat provider

> **Amends [ADR 0018](0018-user-configurable-model-providers.md) and narrows
> [ADR 0017](0017-foundry-provider-api-key-auth.md).** ADR 0018 made the operator `Models:`
> set the **boot-time availability floor** ("the app always starts on it"). This decision
> removes that floor for **chat**: the app ships **zero** chat providers and chat is **only**
> the per-user overlay. 0017's "blank *baseline* Foundry key is fatal" is retired **for chat**
> but survives **for embeddings** — the embedding endpoint/key is the one remaining fixed model.
> Both prior ADRs keep `status: accepted` and carry a forward-pointing in-body note; see
> "Partial supersession".

## Context
ADR 0018 shipped per-user model providers as an **overlay** on top of an operator baseline
(`Models:Providers` in `appsettings.json`: `foundry` + `github`), with that baseline as the
availability floor — the app always booted with at least the Foundry chat provider, and a blank
baseline Foundry key was fatal at startup (0017). The operator (the app's single user, personal
project) wants chat **entirely self-managed per user**: no operator-shipped default chat model, and
a fresh user who has configured nothing sees an empty picker rather than a Foundry model they never
chose. That contradicts 0018's availability-floor invariant head-on, so it ships as a superseding
decision. The one fixed model that must remain is the **embedding** model
(`text-embedding-3-large`) for document RAG (ADR 0007/0008), which has no per-user story.

## Decision
**The `Models:` mechanism stays; the baseline chat set ships empty; the grain stays valid with no
model via a no-op client.** `Models:Providers` is `{}` — the app boots with zero chat providers.
Chat providers are **only** the per-user overlay (ADR 0018). A user with no configured provider
sees an **empty picker** and a **gated composer** (the frontend disables send and shows a hint), and
their conversation grain keeps a valid `_agent`/`_session` by wrapping a shared no-op
`NoModelChatClient` — so every non-inference grain operation (history, bind, register-document,
reminder, rehydrate) keeps working unguarded. Only the two sites that actually **run** the model
act on the no-model signal (`string.IsNullOrEmpty(_chatClientFactory.DefaultModelId)`):
`ChatCoreAsync` **re-resolves** the user's factory and rebinds if a provider was added in the same
activation (else yields a one-line notice), and `ReceiveReminder` guards the same way. A provider
must declare **≥1 model** (API validation), so "factory has providers ⟺ a usable default exists".
The **embedding** endpoint/key (`AzureOpenAI:Endpoint`/`ApiKey`/`EmbeddingDeploymentName`) stays
**fatal-if-blank** at startup — it is now the sole availability path.

## Partial supersession — exactly what moves (0018 + 0017)
- **0018's availability-floor invariant is removed for chat.** The env/`appsettings` `Models:`
  set is no longer a floor: it ships empty, so the app boots with **zero** chat providers and a
  user with an empty overlay gets an empty picker. Everything else in 0018 stays authoritative —
  the baseline ⊕ overlay merge, the version-stamped per-user cache, the best-effort "a user
  override that resolves invalid falls back to baseline" rule (now vacuous for chat, since the
  baseline is empty), the no-inheritance rule, DP-encrypted secrets, and the re-scoped SSRF
  invariant (a user endpoint is still not `PrivateAddressGuard`ed).
- **0017's "blank baseline Foundry key is fatal" narrows to embeddings.** There is no baseline
  Foundry **chat** provider to guard, so that rule is retired for chat. The **embedding** client
  reads `AzureOpenAI:ApiKey` directly and is the availability path, so the `Program.cs` fatal
  check and the Bicep `@minLength(32)` guard on the key **stay** — reframed as "the embedding key
  is the availability path", not "the Foundry chat key is".
- **Everything else in both ADRs stays authoritative:** the qualified `provider:model`
  first-colon split, capability flags, the cross-tenant egress position, the `api-key`-header
  auth mechanism. 0018 and 0017 keep `status: accepted` and carry a forward-pointing in-body
  amendment note here — deliberately **no `superseded_by:` frontmatter tag** (the active-ADR scan
  is anchored to that key; adding it would drop these heavily-referenced ADRs from the active set,
  the same reasoning 0017/0018 used).

## Alternatives considered
- **Seed a Foundry provider from `AzureOpenAI:*` on first run.** Rejected by the operator: the
  point is an empty picker until the user configures a provider, not a default they never chose.
- **Leave `_agent`/`_session` null when there is no model and guard all ~6 deref sites.** Rejected
  as fragile — each site is a miss-risk. The no-op `NoModelChatClient` keeps every site non-null
  because `BuildAgent` only *wraps* the client and never invokes inference, collapsing the problem
  to the two sites that actually run the model.
- **Let a user Foundry provider inherit `AzureOpenAI:*`.** Rejected: keep 0018's no-inheritance
  rule — a user provider declares its own endpoint + key, so the operator key never reaches a
  user-chosen endpoint.
- **A dedicated `Error` stream-event type for the no-model notice.** Rejected: a `TextDelta` +
  `Done` (mirroring the vision-withheld notice) suffices and is ephemeral (never persisted).
- **Remove `ArmClient`/`AzureModelCatalog` now that ARM discovery is fully dead.** Deferred:
  orthogonal and harmless; a future cleanup.

## Consequences
- **A fresh user cannot chat until they add a provider.** This is the intended posture: the empty
  picker + gated composer + (defense in depth) a grain notice make "configure a provider first"
  explicit. The default is no longer Foundry — there is no default — which keeps 0013's
  cross-tenant egress position intact (a third-party pick is still deliberate).
- **The embedding endpoint/key is the sole fixed availability path.** A blank `AzureOpenAI:ApiKey`
  or `EmbeddingDeploymentName` is still fatal at startup (embeddings, not chat). The 4 chat-only
  `AzureOpenAI:*` keys (`DeploymentName`, `SubscriptionId`, `ResourceGroup`, `AccountName`) and
  their Bicep params/env vars are removed; `Endpoint`, `ApiKey`, `EmbeddingDeploymentName`,
  `TenantId` stay.
- **A provider must declare ≥1 model.** The API rejects a zero-model provider (400), closing the
  "keyed but modelless provider ⇒ `providers.Count==1` yet `DefaultModelId==""`" gap.
- **`ModelProviderBuilder`'s baseline throw is now dead in prod** (the baseline is empty) but the
  mechanism + `BlankFoundryKey_IsFatal` test stay intact — a non-empty baseline is still validated.
- **DevHost/AppHost keep canned models** so offline chat still works; the empty-provider path is
  unit-tested, not exercised offline.
