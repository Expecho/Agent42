---
id: 0139
date: 2026-11-22
status: accepted
tags: [models, routing, defaults, chat-gateway, per-user, no-migration]
supersedes: []
extends: [0018]
amends: [0137]
---

# Title: Auto can be the user's default model

## Context
ADR 0137 made `auto` a chat-picker value only and refused it at `PUT /api/default-model`, so a user who wanted every new chat
routed had to re-pick Auto in each one. The user asked for Auto to be selectable as the default whenever it is available.

## Decision
- `PUT /api/default-model` accepts `auto` (any case, trimmed) and stores the literal `auto` in `UserProviderConfig.Default`.
  No new field and no migration: the existing string already round-trips through `GET /api/providers`.
- **`auto` is never a resolved model.** `UserProviderResolver.NamesBuiltProvider` rejects it (it has no colon, so it would parse
  as a legacy id on the unkeyed provider and pass), so `EffectiveDefault` falls through to the baseline or first declared model.
  `ModelCatalogResult.Default`, `IChatClientFactory.DefaultModelId`, fleet, stored agents, email-in and reminders all keep a
  real model, as before.
- `/api/models` gains a trailing `AutoDefault` bool: the stored default is `auto` AND the user still has a router. It is the
  only signal the picker reads, so a stale `auto` (router removed) quietly means "the real default".
- Browser: the Preferences default select offers Auto from the same `auto` flag as the composer, and `loadModels({ pin: true })`
  points a new chat at Auto when `autoDefault` is set.
- Chat apps: a plain-chat turn with no `/model` override sends `auto` when the Auto default is live (`AutoDefaultAsync`).
  `/model default` therefore means Auto for such a user. Agent conversations still keep their agent's model.
- Everything else that takes a model still refuses `auto` (`AutoModelRefusal`): fleet, stored agents, the agent grain's bind.

## Alternatives considered
- **Return `default: "auto"` from `/api/models`** — every consumer of the catalog default (fleet, analyzer, lessons) would then
  have to learn to reject it. A separate flag keeps `Default` a real model.
- **Resolve Auto in the resolver** — the resolver builds client factories and has no router; routing stays grain-side (ADR 0137).

## Consequences
- ADR 0137's "`auto` is refused at every other server entry that takes a model" no longer includes the default-model endpoint.
- Egress is unchanged (ADR 0137): Auto sends the last user message to the user's own router only while they have one.
- The default select's "First available" blank still clears the preference.
