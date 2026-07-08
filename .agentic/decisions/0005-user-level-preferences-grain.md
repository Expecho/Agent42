---
id: 0005
date: 2026-07-08
status: accepted
tags: [preferences, grains, profile, user-level]
---

# Store user-level preferences in a server-side per-user grain, not localStorage

## Context
The app needed its first *user-level* preference: a durable, cross-device **preferred
language** (BCP-47 tag), intended to seed speech-input dictation later. Until now all
state was per-conversation (`AgentGrain`, ADR 0001) or a per-user *collection*
(`IAgentRegistryGrain`, ADR 0003). A scalar preference that must follow the signed-in
user across devices had no home. The obvious quick option — `localStorage` — is
device-local, so it cannot be a real "profile".

## Decision
Introduce a per-user `IUserProfileGrain` (keyed by the Entra object id from
`HttpContext.GetUserId()`, like `IAgentRegistryGrain`) holding a `[GenerateSerializer]`
`UserProfile` record, exposed over `GET`/`PUT /api/profile`. It is the **source of
truth**; the browser may cache it only as a render hint, always reconciled against the
server response. The grain rides the shared `agent-state` storage provider with a
distinct `"profile"` state name — Orleans partitions by grain type, so **no new Cosmos
container** is added (consistent with ADR 0001 and the pre-declared-containers gotcha).

## Alternatives considered
- **`localStorage`-only** — device-local, so the preference would not follow the user to
  another browser or device; that is precisely the "real profile" the user asked for.
  Rejected as the source of truth (still allowed as an optional client cache).
- **A dedicated Cosmos container / new storage provider for preferences** — unnecessary
  operational and Bicep surface; Orleans' per-grain-type partitioning already isolates
  the profile grain within the shared provider. Rejected as over-provisioning.
- **Reusing `IAgentRegistryGrain`** — it models an editable *collection* with id +
  timestamp bookkeeping; a profile is a singleton per user. Rejected as a shape mismatch.

## Consequences
- First user-level scalar-preferences store; `UserProfile` is built to extend — add new
  fields with the next `[Id(n)]`, never renumber (Orleans serialization is positional).
- No infra change: no new container, no Bicep edit, no new dependency.
- The frontend gains an extensible "Preferences" panel (language is field #1). It stores
  the value *ready* for speech input; wiring an actual mic/dictation button is a
  deliberate follow-up (no speech code exists in the app yet).
- Gotcha surfaced and recorded: a shared **static** default instance for grain state is
  unsafe — Orleans' generated copier can write into it and leak the mutation across every
  grain that started from it. `UserProfile.Empty` is therefore a *factory* (`=> new("")`),
  never a cached singleton.
