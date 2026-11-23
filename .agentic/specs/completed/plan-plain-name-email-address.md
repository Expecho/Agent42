# Plan: opt-in plain-name inbound email address per stored agent

planned-against: main @ 4b2e016

## Overview
Today every stored agent has a random, secret address `agent-<32 base32>@in.agent42.nl` (ADR 0096). Add a per-agent
**opt-in** (default off) that swaps it for a readable address built from the agent's name (`Calendar Agent` →
`calendar-agent@in.agent42.nl`). Amends ADR 0096 (new ADR 0141). The cost is real: the address stops being a
credential (0096 D3), so anybody who guesses it can push text into the agent. The plan therefore ships the opt-in
together with an explicit exposure decision (warning, tool-off suggestion, rate limit), not just the naming.

## Decisions made with the user (interview, do not re-litigate)
- **Uniqueness: first come, first served, global.** Across all users. A second claimant is refused (409), never
  auto-suffixed.
- **Rename: the address follows the name.** Rename moves the address; the old one stops resolving immediately. A rename
  whose new slug is taken (by anyone, including the user's own other agent) is **refused (409) and nothing changes**.
- **Collision at opt-in: refuse, release immediately.** Opt-in answers 409 "address taken, rename the agent first".
  Delete or opt-out frees the name at once; mail to it then 406s. No quarantine/tombstone.
- **Charset: ASCII slug, reject if empty.** Lowercase, strip diacritics, runs of non-`[a-z0-9]` → single `-`, trim `-`,
  max 40 chars. A name that slugs to nothing (all CJK, all symbols) cannot opt in; the editor says why. No SMTPUTF8.
- **Reserved names are refused** (list below).
- **No rotation:** a plain-name address cannot be regenerated; `regenerateEmail` on an opted-in agent is a 400.

## Affected areas (verified against the tree)
- `src/Agent42.Agents/Email/EmailAddress.cs` — add `PlainName.Slugify`/reserved check next to it (new `PlainEmailName.cs`).
  `Hash`, `TokenFromAddress`, `Format` are reused unchanged: a plain slug is just another local part.
- `src/Agent42.Agents/Email/IEmailAddressDirectoryGrain.cs` + `EmailAddressDirectoryGrain.cs` — `RegisterAsync`
  currently **overwrites**; plain names need a compare-and-claim.
- `src/Agent42.Agents/StoredAgents/AgentDefinition.cs` — new trailing field; `AgentRegistryGrain.cs` (`SaveAsync`
  update path ~l.43-126, `InsertAsync`, `DeleteAsync`, `EmailDirectory`/`DeregisterEmailAsync`); `IAgentRegistryGrain.cs`
  (`AgentSaveResult`, doc).
- `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` (POST/PUT l.36-84) and `AgentContracts.cs`
  (`AgentUpsertRequest`).
- `src/Agent42.Backend/Email/AgentApi.Email.cs` (`ResolveEmailRecipientAsync`, `AcceptEmailAsync`),
  `EmailRegistration.cs`, new `PlainEmailRateLimiter.cs` beside `MailgunReplayGuard.cs`.
- Frontend: `wwwroot/index.html` (`agentEmailSection`, l.~405), `wwwroot/js/agents.js` (`renderEmailAddress`,
  `pendingEmailRegenerate`, save payload l.~286), `wwwroot/js/dom.js` (element refs), `wwwroot/sw.js` `CACHE` bump
  (rule 3; 4 tests pin the CACHE string, grep `agent42-shell-v` in tests).
- Doc comments that go stale: `AgentDefinition.cs` remarks on `EmailToken` (rotation is the only revocation),
  `IAgentRegistryGrain.cs:33-35` ("the incoming EmailToken is ignored"). Fix in step 8.
- Docs: new `.agentic/decisions/0141-…md`, banner on 0096, `docs/features/custom-agents.md`, CLAUDE.md feature map row
  ("Stored agents … inbound email address") gains 0141, gotchas entries below.
- Tests: `AgentRegistryGrainTests.cs`, `EmailAddressDirectoryGrainTests.cs`, `EmailAddressTests.cs`,
  `EmailApiTests.cs` + `EmailApiFixture.cs`, a new `PlainEmailNameTests.cs`.

## Context & constraints
- **Design: reuse `EmailToken`.** When opted in, `EmailToken` holds the slug instead of the random token, flagged by a
  new `[Id(14)] bool? EmailPlain` (nullable, trailing, default null = random; Cosmos JSON binds a missing key to null,
  rule 2 + the `Kind` precedent in `AgentDefinition.cs`). Everything downstream (directory key = `EmailAddress.Hash`,
  endpoint resolution, `Format`, the editor's `emailToken@domain`) works unchanged. Prove the field on the Cosmos JSON
  path in `GrainStorageSerializerTests`.
- **One address per agent (0096 D4).** Opting in *replaces* the random address (old hash deregistered); opting out
  mints a fresh random token (the old plain name is released). Existing agents untouched until someone opts in.
- **Namespace overlap with random tokens.** Random tokens live in the same directory, so a plain name could in
  principle equal one. 160-bit tokens make that unguessable; the atomic claim below is the real guard, no prefix ban.
- **Atomic claim is the crux.** The directory grain is single-threaded per hash, so it is the only race-free arbiter.
  Add `Task<bool> TryClaimAsync(string userId, string agentId)`: succeeds if empty or already held by the same
  `(userId, agentId)` (idempotent, heals crash leftovers); otherwise returns false. `RegisterAsync` stays for the random
  path (overwrite is fine there, 160 bits). New grain method needs an `[Alias]`.
- **Stale-claim healing, same user only.** A claim whose agent no longer exists (crash) must not block a name forever,
  but a **cross-user** registry-to-registry check (`IAgentRegistryGrain.GetAsync` on the holder's registry) can deadlock
  (A->B and B->A, non-reentrant grains, no deadlock detection; critic finding). So: cross-user conflict = always refuse,
  no verification call; orphans are prevented by ordering instead (below). When the holder is the *same user*, read
  `_state` directly (never call own grain): if that agent is gone or is not `EmailPlain` with that slug, take the claim over.
  The takeover uses a new compare-and-clear, `DeregisterIfHeldByAsync(userId, agentId)`, never the unconditional
  `DeregisterAsync`, so a holder who re-claims between check and clear is not wiped (check-then-act race).
- **Ordering (the invariant, from the gotcha "Two directories now (ADR 0096)": a revoked address must never keep
  resolving):** opt-in/rename = `TryClaim(new)` (refuse before any change) → `Deregister(old)` → write registry state.
  A crash after the deregister leaves state pointing at an unregistered address: fail-closed. **Healing is unconditional:**
  on every save of a plain agent the registry calls the idempotent `TryClaimAsync` for the *current* slug even when the
  slug is unchanged (one cheap grain call; critic finding: "same slug = no directory traffic" would leave the address dead
  after a crash). Same for opt-in over a random token. A crash after the claim only leaves a harmless same-owner claim.
  **Opt-out is a regenerate, not a delete:** deregister the slug FIRST, then mint the random token, write state, register
  it (otherwise a crash leaves the slug live on an agent that now shows a random address). Delete keeps registry-write then
  deregister (the endpoint's existence re-check is the backstop; an orphan claim keeps the name blocked for other users,
  acceptable and stated in the ADR).
- **Refusal channel.** The registry must be able to say "conflict" without persisting: extend `AgentSaveResult` with a
  nullable `EmailError` (`taken | invalid | reserved | empty | not-plain`), endpoint maps to 409 / 400 with a text
  body like the existing refusals. Create path (`POST`) never opts in; opt-in is PUT-only, like `RegenerateEmail`.
- **`PlainEmail` semantics on PUT:** `null` = keep, `true` = opt in, `false` = opt out. In the update path
  `EmailPlain` and `EmailToken` are forced from `previous` (like `Kind`, `AgentRegistryGrain.cs:107`) unless this save is
  an explicit opt-in/out/rename, so a client that omits the field (Android app, scripts) can never silently drop the
  flag. The agent JSON carries `emailPlain`, which the editor reads.
- **Rename trigger.** In `SaveAsync` update, if `previous.EmailPlain == true` the new slug is derived from the incoming
  `Name`; same slug still re-claims idempotently (healing, above). Because rename of an opted-in agent can now fail, the editor must surface the
  409 text instead of a generic save error.
- **Reserved list** (static, in `PlainEmailName`, unit-tested): `postmaster abuse hostmaster webmaster admin
  administrator root security noreply no-reply mailer-daemon support info contact help billing privacy legal sales
  www mail smtp imap agent agents agent42 test`. RFC 2142 roles + future operator addresses on a catch-all domain.
  Also refuse any slug equal to `agent-` + 32 chars of `[a-z2-7]` (cannot collide by choice).
- **Exposure decision (the ADR 0141 content).** Mailgun's HMAC authenticates the transport, not the sender, and 0096
  already names the composing channels (0056 memory with no delete path, 0011/0027 fetch+search exfil, 0094 webhook,
  0112 `past_chats`). With a guessable address an attacker needs no leak. Mitigations, in order of strength:
  1. **Rate limit (server, new):** `PlainEmailRateLimiter`, in-process sliding window per agent id, applied in
     `AcceptEmailAsync` after resolution **only for plain-name agents**; default 30/hour, `Email:PlainNameMaxPerHour`
     (0 = unlimited). Over the limit → **406** (never retried, Mailgun's rule from 0096) + warning log. Single-replica
     in-process like `MailgunReplayGuard` (rule 12); resets on restart; state that in the ADR. Rejected: per-`From`
     limiting (spoofable) and a `From` allowlist (breaks auto-forward, 0096 D3).
  2. **Editor confirm on opt-in (client):** names the channels, and if the agent's `EnabledTools` is `null` (all) or
     includes `memory`, `past_chats`, the fetch/search tools or an enabled webhook, lists them and offers a one-click
     "turn those off" before enabling. A persistent note on the address row says "anyone who guesses this address can
     instruct this agent".
  3. **Server-side refusals (added after review; the user can veto):** the email door runs any stored agent, and a
  guessable address means unauthenticated prompt injection. So opt-in, **and every later save while plain**, is refused
  (400, `unsafe-tools`) when the agent is a **Copilot kind** (shell/file tools auto-approved, ADR 0107/0097; this also
  forbids the per-chat-only kinds) or when `EnabledTools` is `null` (= all, ADR 0021) or contains `memory` (no delete
  path, ADR 0056) or `past_chats` (ADR 0112). The editor's "turn those off" button makes this one click. Other channels
  (fetch/search 0011/0027, `start_fleet_task` 0115, side-effecting MCP tools, push, outbound webhook 0094) are **warned
  about in the confirm, not blocked**; record them as accepted residual risk. This departs from 0112's "no code gate"
  for this door only, because 0112's door had a secret address.
  4. **The ADR must say plainly:** the rate limit bounds cost and noise, **not injection** (one email can exfiltrate), and
  adds a DoS channel (an attacker can use the quota so the owner's legitimate auto-forwards 406); name squatting
  (`calendar`, `receipts`) is possible under first-come; the no-prefix-ban reasoning holds only because the email
  directory is separate from the API-key directory.
- **Delete / opt-out of a name:** the name is released immediately (decision above); a later email to it 406s via the
  existing unknown-recipient path (identical answer for unknown and deleted, 0096).
- Egress/ownership notes: this is *inbound* only, so no ADR 0013 egress extension; say so in the ADR. Not an owner-tier
  matter (0121). `/api/email-in` stays an exact `excludedPaths` entry; no Bicep change, and `EasyAuthExclusionTests`
  stays green.
- Gotchas to read before coding: `.agentic/gotchas.md` entries "Two directories now (ADR 0096)",
  "An inbound email token must be case-insensitive" "Each credential namespace gets its own directory grain
  type", the `excludedPaths` entry, and rule 2 (trailing `[Id(n)]`, never a collection expression).
- Exemplar to copy for the claim/regenerate ordering: `AgentRegistryGrain.SaveAsync` regenerate branch.

## Steps
1. **Slug + reserved rules** (parallelizable with 2). New `src/Agent42.Agents/Email/PlainEmailName.cs`:
   `string? Slugify(string name)` (null when empty), `bool IsReserved(slug)`, max length const. Verify:
   `dotnet test --filter PlainEmailNameTests` — diacritics, spaces/runs, CJK→null, 40-char cut without trailing `-`,
   reserved, uppercase, the `agent-<32>` shape.
2. **Atomic claim** (parallelizable with 1). `IEmailAddressDirectoryGrain.TryClaimAsync` and `DeregisterIfHeldByAsync` + grain impl (new `[Alias]`es).
   Verify: `EmailAddressDirectoryGrainTests` — empty claim, same-holder idempotent, other holder refused,
   claim after deregister succeeds, `ResolveAsync` returns the claimant, concurrent claims (two tasks) → exactly one wins; `DeregisterIfHeldBy` clears only for the matching holder.
3. **Data model.** `AgentDefinition [Id(14)] bool? EmailPlain = null`; `AgentUpsertRequest` gains `bool? PlainEmail = null`
   (PUT only; pass **by name**, the positional-append scar noted at `AgentApi.StoredAgents.cs:58`). Verify:
   `GrainStorageSerializerTests` round-trips an agent with and without the field on the Cosmos JSON path.
4. **Registry logic** (needs 1-3). `SaveAsync` update path: opt-in, opt-out, rename-follows, regenerate-refused-when-plain,
   stale-claim healing, `AgentSaveResult.EmailError`; `DeleteAsync` already deregisters `EmailToken` (verify it covers the
   plain slug). Keep each method under S3776 15 (`SaveAsync` is already near it): `SaveAsync` gains exactly ONE call into a new
   `EmailAddressLifecycle` class in the same folder (ADR 0050/0051, as `HindsightRegistration` did), a small state
   machine with one method per transition (`OptIn`, `Rename`, `OptOut`, `Heal`), never one `Apply`. The unsafe-tools
   refusal lives here too. Verify: `AgentRegistryGrainTests` —
   `PlainEmail_OptIn_ReplacesRandomAddress`, `_Collision_Refused_NothingChanged`, `_OtherUsersCollision_Refused`,
   `_Rename_MovesAddress_OldStopsResolving`, `_RenameToTaken_Refused_OldStillWorks`, `_RenameToEmptySlug_Refused`,
   `_OptOut_MintsRandom_ReleasesName`, `_Delete_ReleasesName`, `_Regenerate_Refused`, `_StaleClaim_IsHealed`,
   `_SameUserHolder_NoDeadlock`, `_CrossUserOrphan_Refused`, `_CrashAfterDeregister_ResaveHeals` (pre-seed a directory
   state with the old slug deregistered), `_OptOut_CrashAfterDeregister_SlugDead`, `Put_OmitsPlainEmail_PreservesFlag`,
   `_CaseVariantName_CollidesAcrossUsers`, `_CollidesWithRandomToken` (pre-seed), `_CopilotKind_Refused`,
   `_NullOrMemoryOrPastChatsTools_Refused`, `_ToolsEnabledLaterWhilePlain_Refused`.
5. **Endpoint mapping.** PUT maps `EmailError` → 409 (taken) / 400 (invalid, reserved, empty, regenerate-on-plain) with
   text bodies. Verify: new cases in `AgentApiTests` (find the PUT exemplar there).
6. **Rate limiter** (parallelizable with 5). `PlainEmailRateLimiter` + registration in `EmailRegistration.AddInboundEmail`
   (reads `Email:PlainNameMaxPerHour`), wired in `AcceptEmailAsync` after `ResolveEmailRecipientAsync`, plain agents only.
   Verify: `EmailApiTests` — plain address delivers, the 31st within the hour is 406 (the Mailgun replay token stays claimed: assert a replay of the same token answers 401, and the warning log), a random
   address is never limited, `PlainEmailMaxPerHour=0` disables; `EmailApiFixture` gets the option.
7. **Editor UI** (needs 5). Opt-in toggle + live preview of the derived address (client slug mirrors the server, which
   stays the authority), warning/confirm with the tool list and "turn those off", hide Regenerate while plain, show 409
   text, rename hint ("renaming this agent changes its address"). Bump `sw.js` `CACHE` and fix the 4 pinning tests.
   Verify in DevHost (`dotnet run --project tools/Agent42.DevHost`, UI at http://localhost:61326): opt in, copy address,
   POST to `/api/email-in` as in `EmailApiFixture`'s signed curl shape, rename, collision. Frontend has no unit tests
   beyond source-text guards; state UI as hand-verified.
8. **Docs/ADR** (after 4-7). `/decide` for ADR 0141 (amends 0096 and records the exposure decision, rejected
   alternatives: auto-suffix, owner-scoped prefix, follow-frozen address, quarantine, SMTPUTF8, hard tool gate, per-From
   limit); add the amendment banner to 0096 (also its "D3" and "regenerate is the only revocation" lines now have an
   exception); update `docs/features/custom-agents.md` and the CLAUDE.md feature-map row; update the gotchas entry
   "Two directories now" with the claim/rename order and add a gotcha: *a plain-name address is not a credential, so
   rotation and the 160-bit assumption no longer hold; never rely on the address for secrecy*. `/sync-docs`.
9. **Gate run.** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → tests
| AC | Test | File |
|----|------|------|
| Default off; an existing/new agent keeps its random address and it still resolves | `ExistingAgent_NoPlainFlag_KeepsRandomAddress`, existing `EmailIn_*` | AgentRegistryGrainTests / EmailApiTests |
| Name → slug (spaces→`-`, lowercase, diacritics, 40 cap) | `Slugify_*` | PlainEmailNameTests |
| Non-ASCII-only / symbol-only name cannot opt in | `PlainEmail_OptIn_EmptySlug_Refused` | AgentRegistryGrainTests |
| Reserved names refused | `IsReserved_*`, `PlainEmail_OptIn_Reserved_Refused` | PlainEmailNameTests / AgentRegistryGrainTests |
| Global first-come uniqueness, across users | `TryClaim_OtherHolder_Refused`, `PlainEmail_OtherUsersCollision_Refused` | EmailAddressDirectoryGrainTests / AgentRegistryGrainTests |
| Race: two simultaneous claims, one winner | `TryClaim_Concurrent_OneWinner` | EmailAddressDirectoryGrainTests |
| Opt-in collision → 409, nothing changed | `PlainEmail_Collision_Refused_NothingChanged`, `PutAgent_PlainEmailTaken_Returns409` | AgentRegistryGrainTests / AgentApiTests |
| Opt-in replaces random address (old stops resolving) | `PlainEmail_OptIn_ReplacesRandomAddress` | AgentRegistryGrainTests |
| Rename moves address; old stops resolving | `PlainEmail_Rename_MovesAddress_OldStopsResolving` | AgentRegistryGrainTests |
| Rename to a taken/empty slug refused, old address still works | `PlainEmail_RenameToTaken_Refused_OldStillWorks` | AgentRegistryGrainTests |
| Delete and opt-out release the name | `PlainEmail_Delete_ReleasesName`, `_OptOut_MintsRandom_ReleasesName` | AgentRegistryGrainTests |
| No rotation of a plain address | `PlainEmail_Regenerate_Refused`, `PutAgent_RegenerateOnPlain_Returns400` | AgentRegistryGrainTests / AgentApiTests |
| Stale claim does not block a name; no self-deadlock | `PlainEmail_StaleClaim_IsHealed`, `_SameUserHolder_NoDeadlock` | AgentRegistryGrainTests |
| New field survives Cosmos JSON storage with no migration | `AgentDefinition_EmailPlain_RoundTripsJson` | GrainStorageSerializerTests |
| Plain address receives mail end to end, case/`+tag` insensitive | `EmailIn_PlainAddress_Resolves`, `_Uppercased_`, `_PlusTag_` | EmailApiTests |
| Rate limit: over cap → 406, plain only, 0 disables | `EmailIn_PlainAddress_OverLimit_406`, `_RandomAddress_NotLimited`, `_LimitZero_Unlimited` | EmailApiTests |
| `/api/email-in` still exact-excluded, Easy Auth tests green | `EasyAuthExclusionTests` (unchanged) | EasyAuthExclusionTests |
| ADR amends 0096 and doc claims match code | `/audit-docs`, doc-path integrity in CI | — |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)
| Gate | Threshold | Measured by |
|------|-----------|-------------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet) |
| `cognitive_complexity_max` | 15 (S3776, build error) | `dotnet build Agent42.slnx` |
| `vulnerable_packages` | 0 | CI package-freshness; no new packages in this plan |
| format | clean | `dotnet format Agent42.slnx --verify-no-changes` |
Report SKIPPED, never an estimate, if a measurement can't run (hard rule 3). Branch `feature/plain-name-email`; never main.

## Out of scope
- Sender allowlists, SPF/DKIM checks on `From`, or replies (0096 D1/D3 stand).
- A server-side tool gate on opted-in agents (recorded as accepted residual risk).
- Custom domains, aliases decoupled from the name, multiple addresses per agent, name quarantine, SMTPUTF8.
- Changing the Copilot/per-chat-only kinds themselves, or `/api/email/config`.
- Any memory deletion path (0056). Backfill of existing agents.
- Mailgun route changes: the existing catch-all route on `in.agent42.nl` already delivers any local part (confirm in the
  ADR as a manual prerequisite check, not tested here).

## Review
Critic verdict: sound outline, 2 high / 4 medium / 3 low. All accepted and folded in above: heal on same-slug resave (H1),
same-user-only healing with compare-and-clear and no cross-registry call (H2), server-side Copilot/unsafe-tools refusal and
honest ADR wording on rate limit/DoS/squatting (M3), `PlainEmail` null = keep (M4), test gaps (M5), state-machine split (M6),
stale doc/line refs (L7), opt-out deregisters first (L8), ADR notes (L9). Rejected: none.
Decision for the user: M3's server-side refusal departs from ADR 0112's "no code gate".

## Implementation divergences (2026-11-23)
- **Stale-claim test** is "the same-user holder no longer has `EmailToken == slug`" (plan: "is not `EmailPlain` with that slug"), so a live random-token holder is never stolen from. A test pins `_CollidesWithRandomToken`.
- **Delete** releases a plain slug with `DeregisterIfHeldBy` (not the unconditional deregister), so a crash leftover cannot wipe another user's later claim.
- **Refusal runs before the API-key regenerate** in `SaveAsync`, so a refused address change changes nothing at all.
- **Rate-limit tests** use a fixture cap of 3 (`EmailApiFixture.PlainMaxPerHour`), not 30; limit 0 = unlimited is a `PlainEmailRateLimiterTests` unit test, not an API test. The warning log on 406 is not asserted.
- **Error codes** are `taken | reserved | empty | not-plain | unsafe-tools` (no `invalid`: create never opts in, so nothing produces it). `AgentSaveResult.EmailError` is `[Id(2)]`.
- **Editor** was not exercised in DevHost (hand-verification still open). The client slug mirror strips `\p{M}`; the server stays the authority. The editor's "turn those off" unticks `memory`/`past_chats` after the confirm; Copilot/research kinds get the box disabled.
- **Known, accepted:** a save omitting `EnabledTools` is refused `unsafe-tools` while plain; a dead slug taken by another user after a crash refuses saves until rename/opt-out (ADR 0141 Consequences).
- Pre-existing, unrelated: `Live*` tests fail with 401 (no Azure credentials here); `FleetTaskServiceTests.Events_Clarify_ReplayedAfterReload` failed once in a full run and passes alone.
