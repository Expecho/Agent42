---
id: 0141
date: 2026-11-23
status: accepted
tags: [agents, stored-agents, email, inbound, credentials, prompt-injection, rate-limit, orleans, no-migration]
supersedes: []
amends: [0096]
---

# An agent may opt into a readable inbound email address built from its name

## Context
ADR 0096 gives every stored agent a random address, `agent-<32 base32>@in.agent42.nl`, whose only credential is the
160-bit token (D3). That is hard to remember and ugly in a forwarding rule. The owner wants `calendar-agent@in.agent42.nl`
for chosen agents. The cost is real: a readable address is guessable, so it **stops being a credential** and anybody can
push text into the agent. 0096 already names the channels such text can compose with (0056 memory with no delete path,
0011/0027 fetch and search, 0094 webhook, 0112 `past_chats`); with a guessable address an attacker needs no leak first.

## Decision
A per-agent opt-in, default off, update-only (`PlainEmail` on PUT: `null` keep, `true` opt in, `false` opt out).
- **Storage:** `EmailToken` holds the slug instead of the random token, flagged by `AgentDefinition [Id(14)] bool? EmailPlain`
  (trailing, nullable, no migration). `Hash`, `TokenFromAddress` and `Format` are reused unchanged.
- **Slug:** ASCII, lowercase, diacritics stripped, runs of non-`[a-z0-9]` become `-`, max 40 (`PlainEmailName`). An empty slug
  cannot opt in. A reserved list (RFC 2142 roles, `agent42`, ...) and anything shaped like a random token are refused.
- **Uniqueness:** first come, first served, **global** across users. A conflict is a 409 and nothing changes; there is no
  auto-suffix. The race-free arbiter is `IEmailAddressDirectoryGrain.TryClaimAsync` (single-threaded per hash), with
  `DeregisterIfHeldByAsync` as the compare-and-clear.
- **Rename follows the name**, and the old address stops resolving at once. A rename to a taken, reserved or empty slug is
  refused and the old address keeps working. **No rotation:** `regenerateEmail` on a plain agent is a 400.
- **Release:** delete and opt-out free the name immediately (no quarantine). Opt-out mints a fresh random token.
- **Order (the 0096 invariant, a revoked address never keeps resolving):** claim new, deregister old, write state.
  Opt-out deregisters the slug first. Every save of a plain agent re-claims its current slug idempotently, which heals a crash
  between the deregister and the write. `EmailAddressLifecycle` holds one method per transition (ADR 0050/0051).
- **Stale claims:** only a same-user holder is inspected (read from the registry's own state, never a call to its own
  grain); a claim whose agent is gone or no longer has `EmailToken == slug` (random-token holders never match, so a live address is never stolen) is taken over. A cross-user conflict is always refused with
  no call to the holder's registry, because two non-reentrant registries calling each other can deadlock.
- **Server refusal (departs from 0112's "no code gate" for this door only, because 0112's door had a secret address):**
  opt-in and every later save while plain is a 400 `unsafe-tools` for a Copilot or other per-chat-only kind, for
  `EnabledTools == null` (all, ADR 0021), or when it contains `memory` or `past_chats`. The editor's confirm unticks those
  for the user. Fetch/search, `start_fleet_task`, side-effecting MCP tools, push and the outbound webhook are **warned
  about, not blocked**: accepted residual risk.
- **Rate limit:** `PlainEmailRateLimiter`, in-process sliding window per agent id, plain agents only, default 30/hour
  (`Email:PlainNameMaxPerHour`, 0 = unlimited). Over it answers **406** (never retried) and logs a warning; the Mailgun token
  stays claimed, so a replay is a 401.

## Alternatives considered
- **Auto-suffix on collision** (`calendar-2`): the address changes under the user without notice, and a forwarding rule
  silently points at the wrong agent.
- **Owner-scoped prefix** (`peter.calendar@`): needs a per-user handle the app does not have, and is longer.
- **A frozen address that ignores renames:** the address would lie about the agent. Rejected, so rename moves it.
- **Quarantine / tombstone for freed names:** extra state for a personal app; mail to a freed name simply 406s.
- **SMTPUTF8 / non-ASCII local parts:** unreliable end to end; a name with no ASCII letters cannot opt in instead.
- **A hard tool gate only, no editor warning** or **a warning only, no gate:** the gate stops the two channels that cannot
  be undone (no memory delete path, cross-chat search); the warning covers the rest honestly.
- **Per-`From` limiting or a `From` allowlist:** `From` is spoofable and auto-forward breaks an allowlist (0096 D3).
- **Verifying a cross-user holder through its registry:** can deadlock (above).

## Consequences
- A plain-name address is **not a credential**: rotation, the 160-bit assumption and "regenerate is the only revocation"
  from 0096 no longer hold for it. Never rely on the address for secrecy.
- The rate limit bounds **cost and noise, not injection**: one email can still exfiltrate through an enabled channel. It also
  adds a **denial channel**: an attacker can use up the hour's quota so the owner's own forwards 406. It is in-process,
  single-replica (rule 12) and resets on restart.
- **Name squatting** (`receipts`, `calendar`) is possible under first-come. A name held by a deleted agent whose delete
  crashed mid-way stays blocked for other users until the holder re-saves (an orphan claim, accepted).
- No prefix ban on `agent-` is needed only because this directory is separate from the API-key directory (0096): a plain
  name equal to a random token would collide in the email directory, where the atomic claim refuses it.
- Inbound only: **no ADR 0013 egress extension**, and not an owner-tier matter (0121). `/api/email-in` stays an exact
  `excludedPaths` entry; no Bicep change.
- **Manual prerequisite:** the existing Mailgun catch-all route on `in.agent42.nl` must forward any local part. Confirm it
  in the Mailgun console; nothing here tests it.
- Every save of a plain agent re-claims its slug and re-checks tools, so a save that omits `EnabledTools` (null = all; the Android app, scripts) is refused `unsafe-tools`, and a slug taken by another user after a crash refuses every save until the agent is renamed or opted out. Accepted.
- Existing agents are untouched until someone opts in; there is no backfill.
