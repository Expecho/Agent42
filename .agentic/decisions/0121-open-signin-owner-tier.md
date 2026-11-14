---
id: 0121
date: 2026-11-09
status: accepted
tags: [auth, easy-auth, authorization, owner-tier, telemetry, usage, risk-accepted]
supersedes: []
extends: [0062]
amends: [0002, 0018, 0024, 0029, 0031, 0054, 0059, 0062, 0074, 0090, 0097, 0107]
---

# Open sign-in to everybody, with an advisory owner tier

## Context
Until now only two Entra accounts could sign in (Easy Auth `allowedPrincipals`, "assignment required" on the
enterprise app), and ADRs 0029, 0031, 0054, 0059 and 0107 all rest on the premise "every signed-in user is
trusted". The goal (user, 2026-11-09) is that **anyone with a Microsoft account can sign in**, while today's two
accounts keep a privileged owner tier, with three things holding once sign-in is open: `/dashboard` and `/logs` are
owner-only; the usage tab shows only the signed-in user's own usage; and non-owners' prompt and completion text is
kept out of telemetry. Sequencing is load-bearing: the app-side gating (Phase A) ships first, with the allowlist
still on; the identity flip (Phase B, infra, run partly by the user) goes last.

## Decision
1. **Owners are `Access:Owners`**, a committed list of Entra object ids in `appsettings.json` (an id is an
   identifier, not a credential), replacing `Dashboard:OwnerOid`. `IOwnerPolicy` is the one question; it is
   evaluated on the **post-alias** `GetUserId()` (ADR 0074), is case-insensitive, and **fails closed** (empty list or
   no registered policy: nobody is an owner). DevHost and the AppHost set `local-dev` in code, never in shipped config.
2. **`OwnerGate` (404, not 403, before static files) covers `/dashboard`, `/logs`, `/api/diagnostics/*` and
   `/api/config-health`.** Config health is owner-only because its payload is operator information and `?probe=true`
   spends the operator's billable keys.
3. **Usage is own-data-only for everyone, owners included.** The server derives the caller itself; a `user`
   query parameter is ignored and `/api/usage/users` is removed. The scope is the **object id** (new trailing
   `UserId` on `UsageEvent`, `TokenBucket`, `ToolBucket`), never the email: an email claim is mutable and not unique
   across tenants, so keying on it would let a foreign-tenant admin with a spoofed UPN read an owner's usage. One
   `UsageUserKey` function labels rows and matches legacy buckets, so the write and the read cannot drift. Buckets
   written before the id existed (empty `UserId`) match by the old email key **only for an owner** (every pre-change
   row is an owner's). Spans with no user identity land in an "unreported" bucket nobody sees, which is a visible
   change from "all". Because `user.id` is aliased (ADR 0074), both accounts of one person now share one report.
4. **Non-owner prompt and completion text is redacted at the source** by `NonOwnerContentRedactor`, a span
   processor that strips a gen_ai span to the exact-key metadata allowlist (the Application Insights one, plus the
   usage provider-kind tag) unless the span's `user.id` is an owner. **A span with no `user.id` is redacted.** It is
   registered through a new `beforeTraceExporters` hook on ServiceDefaults' `ConfigureOpenTelemetry`, because processors
   run in registration order and a batch exporter can read a span as soon as its `OnEnd` has run: registered after the
   exporters it would redact nothing already shipped. Application Insights already stripped all gen_ai content, so the
   sinks this closes are the **OTLP collector** and the **`/logs` ring**.
5. **`authLoginHint` is removed** (ADR 0090; Phase B): it would pre-fill the owner's email for every stranger.
6. **~~Identity is multi-tenant Entra~~ — superseded by the Phase B amendment below (2026-11-14): access stays
   invite-only.** Original text kept for the revert: multi-tenant issuer on the existing app registration, owner object
   id unchanged, no data migration.

### Amendment (user decision, 2026-11-14): Phase B is invite-only, not open to everybody
Phase A above is unchanged and still matters (invited guests are third parties too). Only the identity change differs:
- **"Assignment required" on the `agent42-easyauth` enterprise app is the one allowlist.** People outside the tenant are
  invited as **B2B guests** and assigned to the app. The audience stays `AzureADMyOrg` and the issuer stays
  tenant-specific, so no principal id changes, there is no consent screen and no "need admin approval", and
  `Access:OwnerTenantId` is not needed (one tenant, one id space).
- **`allowedPrincipals` and `authAllowedUserObjectIds` are removed** (they were a second copy of the list that cost a bicep
  edit and a deploy per person). **`authLoginHint` is removed** (it would pin every guest to one account; the owners see
  Entra's account picker again when their 14-day session expires).
- **Trade-off, accepted:** `allowedPrincipals` was the safeguard "so the restriction survives portal config drift". Without
  it the template cannot catch someone switching "assignment required" off, which would admit every user of the tenant.
  The check is a user-run command, `az ad sp show --id <authClientId> --query appRoleAssignmentRequired` (must print
  `True`), and the post-deploy check that an unassigned account is refused (AADSTS50105). No CI check: the deploy SP's
  Graph read rights are unverified. `EasyAuthSessionTests` pins that the Bicep carries neither `allowedPrincipals` nor
  `login_hint`, and that the issuer stays the tenant's.
- The "invite-only" legal wording stays true. The legal pages still gain the disclosures below.
- Opening it fully later is a separate decision (widen the audience, multi-tenant issuer, assignment off); the original
  Phase B text is in git history at `9a2a2a3`.

### ACCEPTED RISK (user decision, 2026-11-09): the owner tier is advisory, not a security boundary
The Copilot built-in shell (ADR 0107) runs in the backend container. Any signed-in user can use it, un-isolated,
and it can (1) forge `X-MS-CLIENT-PRINCIPAL-ID` against the backend's own localhost (every `X-MS-CLIENT-*` header is
trusted once inside the app) and (2) read the process environment for managed-identity tokens (Cosmos, Blob, the Data
Protection key ring). The same forging prize is reachable **without a shell** through user-supplied MCP and provider
endpoints, which are deliberately not SSRF-guarded and may be localhost (ADR 0018, 0024; `McpAuthMode.Header` lets the
user choose the header name and value). The user was told this and chose to open the shell to everyone anyway.

**Consequences, stated plainly:** any signed-in user can become any owner and can read all stored data. `/dashboard`,
`/logs`, the config-health gate, usage scoping and telemetry redaction stop *accidental* and *casual* access; they do
**not** stop an adversarial user. Per-user isolation (grain keys, workspaces) is only as strong as the shell's restraint.
The one remaining switch is `Copilot:BuiltInToolsEnabled` (global, chats only, restart needed); **fleet Copilot agents
have no switch** (`FleetCopilotHost` is hard-coded to `CopilotCli` mode). Do not describe the owner tier as isolation
anywhere.

## Alternatives considered
- **External ID (CIAM) tenant** — every user id changes (data migration) and Android's hard-coded `/.auth/login/aad`
  path changes.
- **B2B guests** — rejected on 2026-11-09 as "not everybody"; **chosen on 2026-11-14** as the access mechanism (see the
  amendment).
- **Owner-only Copilot kind, and a loopback guard on user MCP/provider endpoints (A3, A3b in the plan)** — dropped by
  the user, not deferred: a guard on endpoints is meaningless while the shell reaches the same prize, and an owner-only
  Copilot is the opposite of the decision. Reintroducing either is a one-step revert (the plan keeps the text).
- **Keep the usage key as the email** — a foreign-tenant admin with a spoofed UPN could read someone's usage.
- **Redact only in the live buffer** — would leave the OTLP collector carrying strangers' prompts.
- **Owners in Bicep (`authAllowedUserObjectIds`)** — a Bicep and deploy change per owner; and the list is going away.
- **Owner check that also compares the tenant id** — needs a `tid` claim spelling that is a prod-only fact (gotchas);
  a wrong spelling locks the owner out of owner features. Revisit after the Phase B spike (B1(d)).

## Consequences
- **Open risks, not mitigated here** (deferred to a follow-up plan by the user): no per-user rate limit or storage
  quota exists anywhere, so Cosmos/Blob growth is unmetered; operator-keyed services strangers can now drive are an
  **ADR 0013 egress extension**: Hindsight (ADR 0056), context.dev (ADR 0027), Google geocoding (ADR 0012) and Mailgun
  (ADR 0096); the SearXNG proxy (ADR 0059) is an open, operator-IP-billed search surface; and the **global** fleet
  `MaxConcurrentTasks = 2` lets one user block every other.
- **Telemetry residual:** redaction covers span tags only (not span events, which an Activity cannot drop; the current chat client emits content as tags) and not log records. `ILogger` records and http-client span URLs can still
  carry model-chosen search queries and URLs; production logs are Warning-level, which bounds it. The promise is "no
  prompt/completion text", not "no stranger content in `/logs`".
- A non-owner who opens the Config tab sees an empty list (the endpoint answers 404); hiding it needs an owner flag on
  a bootstrap response, left as follow-up. There is no link to `/logs` in the UI.
- The recommended follow-up (own plan): move the shell out of the backend into a sandbox with no managed identity, no
  route to the app's localhost and no shared workspaces mount.
- The legal text (`terms`/`privacy` say "invite-only", which stays true) ships with the Phase B deploy and must disclose
  that a signed-in user's chats can run commands in the service's own environment. It must not promise erasure (Hindsight
  has no delete path, ADR 0056/0057).
- **Prod checks confirmed by the user, 2026-11-14 (after #234 deployed):** owners reach `/dashboard`, `/logs` and Copilot; an invited
  guest gets 404 on both and sees only their own usage; an unassigned account is refused at Entra; `appRoleAssignmentRequired`
  prints `True`; Android still signs in. The drift command stays the standing check after any portal change to the app.
