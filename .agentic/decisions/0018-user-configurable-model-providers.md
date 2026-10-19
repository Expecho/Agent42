---
id: 0018
date: 2026-07-16
status: accepted
tags: [models, providers, config, security, data-egress, secrets, encryption, data-protection, per-user, ssrf]
supersedes: 0013, 0017 (invariant scope only — see "Partial supersession")
---

# A signed-in user configures their own model providers, without a redeploy

> **Extended by [ADR 0103](0103-anthropic-provider-kind.md) (2026-10-18):** a third user-selectable kind,
> `Anthropic` (Claude). The same rules apply: an encrypted key, a required absolute http/https endpoint (the UI
> pre-fills `https://api.anthropic.com`) and declared models.

> **Amends [ADR 0013](0013-multi-provider-model-catalog.md) and
> [ADR 0017](0017-foundry-provider-api-key-auth.md).** Two load-bearing invariants are
> re-scoped, not overturned: 0013's "the endpoint always comes from **config**, never from a
> request" and 0017's "secrets come from **operator-managed env**, one source of truth." Both
> stay `status: accepted` and authoritative for the **operator baseline**; see "Partial
> supersession" for exactly what moves.
>
> **Amended by [ADR 0019](0019-remove-baseline-chat-providers.md) (2026-07-17) —
> availability-floor scope only.** The "boot-time availability floor" below is removed for
> **chat**: the operator `Models:` set now ships **empty**, so the app boots with zero chat
> providers and chat is **only** the per-user overlay. The best-effort "a user override that
> resolves invalid falls back to baseline" rule still stands but is vacuous for chat (the baseline
> is empty). The **embedding** endpoint/key remains the sole fixed availability path. Everything
> else here stays authoritative. `status: accepted` on purpose — see 0019's "Partial supersession".

## Context
The `Models:` provider catalog was bound once from `appsettings`/env at startup and frozen for
the process lifetime (`MultiProviderChatClientFactory` + `CompositeModelCatalog` are singletons;
changing a provider needed a redeploy). The operator (the app's single user, personal project)
wants to wire new endpoints — Ollama/vLLM, a second Foundry account, GitHub Models — **without a
deploy**, and to manage the API keys themselves rather than through operator env vars.

That contradicts two decisions head-on. ADR 0013 rests on the endpoint coming only from config
(the reason it is not an SSRF sink). ADR 0017 rests on secrets coming only from operator env (one
source of truth, blank Foundry key fatal). Letting a user add a provider from the web UI means an
endpoint and a key that arrive from an authenticated **request** and persist **per-user** — so it
ships as a superseding decision that re-scopes both invariants rather than silently breaking them.

## Decision
**Baseline ⊕ per-user overlay, resolved async on grain activation.** The env/`appsettings`
provider set stays exactly as today and remains the **boot-time availability floor**: the app
still starts on it, and 0017's "blank baseline Foundry key is fatal at startup" rule still holds
for that baseline. A per-user `UserProviderConfigGrain` persists an **overlay** (added providers +
overrides, keys encrypted). A single `UserProviderResolver` merges *baseline ⊕ overlay* at the
options level into an effective provider set for a `userId`; both the per-user chat-client factory
(`IUserChatClientFactoryProvider`, resolved once in `AgentGrain.OnActivateAsync`) and the per-user
model catalog (`IUserModelCatalogProvider`, behind `/api/models`) consume it. A user override that
resolves invalid — e.g. blanks the Foundry key — **falls back to the baseline-built entry**: user
overlays are best-effort and **never fatal**; only the operator baseline is on the availability
path. Per-user factories/catalogs are cached keyed by a **version stamp** the overlay grain bumps
on every save, so `/api/models` and every new/newly-activated grain pick up a change with no
restart.

## Partial supersession — exactly what moves (0013 + 0017)
- **0013's SSRF invariant is re-scoped, not dropped.** The endpoint may now come from an
  **authenticated user's own persisted config** — but still **never** from a model or a live
  request. That is what keeps it off the `fetch_web_page` SSRF path (ADR 0011). A user-configured
  endpoint therefore has **no SSRF address-guard** and deliberately accepts `localhost`/private
  hosts (`http://localhost:11434` — Ollama is a supported target). Validation is only: absolute
  `http`/`https`, valid `Kind`.
- **0017's secret-source invariant gains a second, per-user source.** Baseline secrets still come
  from operator env (one source of truth for the *baseline* Foundry key + embeddings). A user's
  overlay keys are a **separate** source: persisted **encrypted** (`IDataProtector`) in the
  per-user overlay grain's Cosmos item, decrypted only when building a client, **never** returned
  to the client (`GET` masks to `hasKey: bool`), and **never** entering the 2 MB session (a
  distinct `"providers"` state name, ADR 0004).
- **Everything else in both ADRs stays authoritative:** the qualified `provider:model` first-colon
  split, capability flags, the cross-tenant egress position (0013); the `api-key`-header auth
  mechanism, Foundry inheritance from `AzureOpenAI:*`, blank-baseline-key-is-fatal (0017). 0013 and
  0017 keep `status: accepted` and carry a forward-pointing in-body amendment to here — deliberately
  **no `superseded_by:` frontmatter tag**, because the active-ADR scan (anchored to the frontmatter
  key, `grep -rL "^superseded_by:"` — see the decisions README) would otherwise drop these two
  heavily-referenced ADRs from the active set (the same reasoning 0017 itself used for 0013/0014).

## Alternatives considered
- **App-global (admin) provider config instead of per-user.** Rejected: this is a personal app;
  per-user is the natural grain shape (mirrors `UserProfileGrain`), keeps the operator env baseline
  as an independent availability floor, and needs no "who may edit global config" gate.
- **Migrate the operator env providers into the per-user store.** Rejected: the env baseline must
  remain the availability floor 0017 guarantees. The overlay is **additive**; there is no migration
  and no state-format change (trailing-default `Version`, empty overlay = baseline behaviour).
- **Make `IChatClientFactory.GetClient`/`GetCapabilities` async so the grain resolves per-turn.**
  Rejected: those are called from ~8 non-async grain sites. Instead the grain resolves a
  *user-scoped* factory **once** in `OnActivateAsync` (async) into `_chatClientFactory`; the sync
  interface is unchanged.
- **Duplicate the Foundry inheritance/skip/fatal rules in the resolver.** Rejected: those rules
  live only in `ModelProviderBuilder` (one source of truth). Refactored to a per-provider
  `TryBuildProvider` the resolver reuses (merge-then-try-build-else-keep-baseline), so the resolver
  adds no second copy of the rules.
- **Live-invalidate already-activated conversation grains on save.** Out of scope: an active grain
  picks up config on its next activation (Orleans deactivates idle grains). Full live-reload of
  in-flight grains is deferred — the pragmatic reading of "hot-reload" for a one-user app.

## Consequences
- **A user can now send their conversation anywhere they configure.** ADR 0013's headline —
  picking a third-party model sends document excerpts, image SAS URLs, and the user's street
  address outside the Entra tenant — now applies to endpoints the **user themselves** added. This
  is accepted: the config comes from the authenticated user's own deliberate action, the default
  stays Foundry, and a third-party pick stays explicit per conversation.
- **Key-ring persistence is mandatory in prod, and its posture is precise.** The user *secrets* are
  DP-encrypted; the Data Protection **master key** itself sits in a `dataprotection` blob container
  protected only by **Storage SSE + RBAC** (no `ProtectKeysWith…`) — not "encrypted at rest" in the
  envelope-key sense. Cosmos's own at-rest encryption still covers the persisted ciphertext. The
  key ring is persisted via the MI `PersistKeysToAzureBlobStorage(BlobClient)` overload, **gated on
  the prod `Storage:BlobEndpoint` branch** — the connection-string overload cannot work with
  shared-key auth disabled on the account. DevHost/tests use the default ephemeral ring (no
  persisted secrets there). **A restart with an ephemeral ring in prod would make every stored
  secret undecryptable** — this is why the prod branch is mandatory.
- **Deleting/renaming a provider degrades, never breaks.** A conversation pinned to a now-removed
  qualified id falls back to the default via `MultiProviderChatClientFactory.Resolve` — the same
  legacy-id degradation 0013 already relies on, inherited per-user. No migration.
- **Hot-reload is honest, not total.** `/api/models` and every new/newly-activated grain reflect a
  save immediately (version-stamped cache). An already-activated grain reflects it on next
  activation. Live-invalidation of in-flight grains is out of scope.
- **Manual prerequisite:** the `dataprotection` blob container (added to `storage.bicep`); the app
  MI already holds Storage Blob Data Contributor, so no new RBAC.
