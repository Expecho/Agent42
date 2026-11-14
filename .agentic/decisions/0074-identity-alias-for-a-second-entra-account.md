---
id: 0074
date: 2026-09-13
status: accepted
tags: [auth, identity, azure, security, telemetry, configuration, no-migration]
supersedes: []
extends: []
amends: [0001, 0002, 0009, 0062]
---

# A second Entra account signs in as an existing user, by config-driven identity alias

> **Amended by [ADR 0121](0121-open-signin-owner-tier.md) (2026-11-09).** `allowedPrincipals` is removed when sign-in opens, so it is no longer "the only tenant
> gate"; `Access:Owners` plus the test that every alias key and value is an owner replace it. Usage is now scoped by the
> (aliased) object id, so both accounts of one person share one report; the row label is still the real email.

## Context
The operator holds two Entra accounts in the tenant — `Expecho@users.noreply.github.com`, which every byte of
production data is keyed to, and `msn@2000miles.nl`, which they want to use from now on. Both must keep
working.

`userId` is the Entra object id read from `X-MS-CLIENT-PRINCIPAL-ID` (`CurrentUser.ResolveUserId`), and it is the
key for effectively all durable state: Cosmos `agent-state` grain keys (`{userId}/{convId}` for
conversations, bare `userId` across eleven other state types), `doc-chunks` partition keys
(`{userId}/{convId}`), the `chat-messages` partition key `/userId` (ADR 0061), the `reminders` container,
blob `attachments` (`{userId}/{convId}/{id}`) and `skill-packages` (`{userId}/{skillId}.zip`), the
`UserId` stored *inside* `AgentKeyDirectoryGrain` and `ShareLinkDirectoryGrain` state, and the Hindsight
`BankId` (ADR 0056).

A real data migration was scoped and **rejected by the user**. Cosmos partition keys are immutable, so it
means read-everything/rewrite/re-insert/delete across four containers and two blob containers with the
app down — and the Hindsight memory bank cannot be moved at all, because it is third-party and keyed by
the object id with **no delete or rename path** (ADR 0056). The migration therefore costs the most and
still loses long-term memory.

## Decision
A config-driven **alias map**, `Identity:Aliases`, mapping an additional object id (the key) onto the
object id that owns the data (the value), applied in `CurrentUser.ResolveUserId` — the choke point the
code already maintains so that grain keying and telemetry "can never drift" (`CurrentUser.cs:116`).
**No data moves.** It ships populated, because both object ids were already committed as the Easy Auth
allowlist at `infra/main.bicep:107-111`.

Eight decisions worth recording:

1. **The alias applies to grain keying *and* telemetry `user.id`.** One resolution path is the property
   that comment exists to protect, and splitting it would reintroduce the drift. Attribution survives
   because `user.name` (principal-name header) and `user.email`/`user.full_name`/`user.roles` (claims
   blob) never pass through `ResolveUserId` — a span reads `user.id = <canonical oid>` next to
   `user.name = msn@2000miles.nl`.
2. **Committed config, not a secret.** An object id is an identifier, not a credential, and these two are
   already in `main.bicep`. That is what keeps this feature free of any Bicep, deploy-workflow or
   GitHub-secret change — the `Dashboard:OwnerOid` precedent exactly.
3. **Validation drops, never throws.** Identity aliasing is not on the availability path, so a malformed
   entry degrades to "no alias" and is reported. An app that boots with the alias inert is recoverable;
   one that refuses to boot is not.
4. **A chain is refused whole.** An id appearing as both a key and a value (`A→B, B→C`, or the cycle
   `A→B, B→A`) drops *every* entry touching it, `OrdinalIgnoreCase`. Resolution is a single hop, so
   dropping only the head would leave the tail live — a half-applied authorization grant.
   **Fan-in survives deliberately:** two keys pointing at one value is the legitimate
   three-accounts-one-person shape, and is not a chain.
5. **The `local-dev` fallback is untouched.** The map is consulted only on the header-present branch, so
   the no-principal fallback is returned before any lookup and `local-dev` is refused on both sides of an
   entry. Offline is bit-for-bit unchanged, and the `excludedPaths` rule that a header-less endpoint must
   never trust `GetUserId` is not weakened.
6. **Resolved optionally from DI** (`GetService`), so no host gains a mandatory registration — the
   `/api/speech` pattern. Unregistered means zero aliases, which is the pre-0074 behaviour and can never
   widen access. Its real failure mode is *silent total inertness*, which is why
   `ProjectLayoutTests.Program_BuildsAndRegistersTheUserIdAliasMap` pins the wiring as source text: no
   test executes `Program.cs`.
7. **`/dashboard` inherits the alias** (see the amendment to ADR 0062 below).
8. **The canonical id is required to be a bare hyphenated GUID.** It is concatenated into the grain key
   `{userId}/{conversationId}`, which `AgentGrain` splits on the **first** `/`, and it is a blob-path
   prefix — so requiring the exact form Entra emits makes a re-partitioned key structurally impossible
   rather than merely checked for.

### What this amends
- **ADR 0001** — "Grain keys are `{userId}/{conversationId}`, so one user can never address another's
  conversation" (lines 34-35). Still true of *userIds*; what changes is that two Entra principals can now
  map to one. **An alias entry is an authorization grant**, and the isolation boundary is the userId
  *after* aliasing.
- **ADR 0002** — identity still originates only from Easy Auth headers. An alias is applied after
  extraction and is never a substitute for authentication; `excludedPaths` is unchanged.
- **ADR 0009** — `user.id` is no longer necessarily the requesting principal's Entra oid. The other five
  `user.*` attributes are unchanged, which is what keeps per-user attribution working.
- **ADR 0062** — its security argument was that `/dashboard` "is **not** a third cross-user surface …  it
  is **one principal**". It is now one *person*, who may hold more than one principal. The gate compares
  `GetUserId()`, so an aliased account reaches the dashboard when the canonical oid is the configured
  owner. Correct, but asserted deliberately rather than discovered. (`Dashboard:OwnerOid` ships blank, so
  this is inert today.)

ADR 0005 and ADR 0016 both assume a 1:1 oid↔user correspondence in passing; neither states a rule this
breaks, but both should be read with this ADR.

## Alternatives considered
- **Migrate the data to the new object id.** Rejected by the user. Highest cost, requires downtime and a
  throwaway tool across four Cosmos containers and two blob containers, and *still* loses the Hindsight
  memory bank, which has no rename or delete path. The alias achieves the goal at a fraction of the risk.
- **Alias only the grain key, leaving telemetry raw.** Reintroduces the drift `CurrentUser.cs:116` exists
  to prevent, and buys nothing: `/usage` splits either way (see Consequences).
- **Emit a separate `user.alias_of` attribute.** A new tag on the app's widest read surface for no
  operational need.
- **Make the app registration multi-tenant.** Considered while the second account was believed to be in
  another tenant; moot once corrected, and it would have made `allowedPrincipals` the only tenant gate.
- **Fail fast on a malformed map.** Reserved in this repo for the availability path (`WebSearch:Endpoint`).

## Consequences
- **One person, one dataset, nothing moved.** Conversations, agents, per-agent API keys (ADR 0022), share
  links (ADR 0035), attachments, RAG chunks, the chat-search index and the Hindsight memory bank all keep
  working for both accounts. ADR 0022/0035/0070 needed no change at all: they take the userId from a
  directory grain, never `GetUserId`, and those directories already hold the canonical oid.
- **`/usage` still shows one row per account, not per person.** `UsageSpanProjector.cs:54-57` keys the
  user bucket `user.email → user.name → user.id`, and in production the email is always present — so the
  alias does not merge the rows and was never going to. Unifying them would mean aliasing the email too,
  which would destroy attribution on `/logs`. Recorded as a consequence and pinned by
  `UsageSpanProjectorTests.ChatSpan_WithAnAliasedUserId_StillBucketsOnTheRealEmail` rather than left as a
  surprise.
- **The residual that no validation can close.** An alias *key* that is another allowlisted principal's
  object id would silently strand that person's own data *and* hand them the canonical user's entire
  dataset — conversations, RAG excerpts, street address (ADR 0012), Hindsight memory with no delete path
  (ADR 0056), and the ability to mint public share links (ADR 0035). GUID-parsing cannot detect this.
  `UserIdAliasTests.ShippedAliases_NameOnlyPrincipalsEasyAuthAdmits` bounds the blast radius by requiring
  every id on either side to appear in `authAllowedUserObjectIds`, which with this allowlist means the
  operator's own two accounts — but it cannot prove the grant is *intended*. This is what makes
  "an alias entry is an authorization grant" more than a slogan.
- **A grant added by environment variable is invisible to the shipped-config test.**
  `Identity__Aliases__<oid>=<oid>` set as a container env var or a portal edit never appears in
  `appsettings.json`, so the "reviewable in git" property holds for the committed map only. This is part
  of why the cross-check targets Bicep, which *is* in git.
- **Signing in as the aliased account *before* this ships creates an orphan dataset** under its own object
  id, which nothing later merges. Harmless for a read-only visit, but a turn taken there with
  `Hindsight:ApiKey` set would create a second memory bank with no delete path.
- **The old object id remains the internal identifier permanently.** Nothing renames it, and the new
  account's own oid appears nowhere in storage.
- **Web Push (ADR 0028) merges both devices into one subscription list**, since `IUserPushGrain` is keyed
  by the resolved userId. Intended: it is one person, who should be notified wherever they are.
- **Two aliased principals are indistinguishable from two browser tabs of one account** — they share the
  non-reentrant `AgentGrain`, the per-user MCP socket cache (ADR 0024/0069) and the version-cached chat
  client factory (ADR 0018), all of which already tolerate that.
- No new secret, Cosmos container, grain, folder, package, `excludedPaths` entry, Bicep change or
  frontend change; `Api/` already existed, so `ProjectLayoutTests.ExpectedFolders()` is untouched and no
  service-worker `CACHE` bump is due.
