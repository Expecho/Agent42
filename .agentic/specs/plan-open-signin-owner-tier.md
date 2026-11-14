planned-against: main @ 69c3fb2

# Open sign-in to everybody, with an owner tier

## Overview

Today only two Entra accounts can reach the app (Easy Auth `allowedPrincipals` + "assignment required" on the
enterprise app), and ~everything is built on the premise "every signed-in user is trusted" (ADR 0031, 0054, 0029,
0059, 0107 all say so in words). The goal: **anyone with a Microsoft account can sign in**, while the current two
accounts keep a privileged **owner tier**. Three things must hold once sign-in is open (user's explicit asks):

1. `/dashboard` (Orleans) and `/logs` are owner-only (owners = today's `authAllowedUserObjectIds`).
2. The usage tab shows only the signed-in user's own usage.
3. (Added by the interview) non-owners' prompt/completion text is redacted from telemetry.

### ACCEPTED RISK (user decision, 2026-11-09): the Copilot shell is available to everyone, un-isolated

Investigation (scouts + critic) found that the Copilot built-in shell (ADR 0107:82-95, "becomes cross-user the day
a third principal is admitted") runs in the backend container and can (1) forge `X-MS-CLIENT-PRINCIPAL-ID` against
the backend's own localhost (every `X-MS-CLIENT-*` header is trusted once inside the app, `CurrentUser.cs:9-13`) and
(2) read the process environment for managed-identity tokens (Cosmos, Blob, the Data Protection key ring). The same
forging prize is also reachable **without a shell** through user-supplied MCP and provider endpoints, which are
deliberately not SSRF-guarded and may be localhost (`AgentApi.Mcp.cs:132-134`, ADR 0018/0024;
`McpAuthMode.Header` lets the user choose header name and value). The user was told this and chose to open the
shell to all signed-in users anyway, un-isolated. Consequences this plan must state plainly and not paper over:

- **The owner tier is advisory, not a security boundary.** Any signed-in user can become any owner (forge the
  header) and can read all stored data. `/dashboard`, `/logs`, config-health owner gating, usage scoping and
  telemetry redaction still ship and still stop *accidental* and *casual* access, but they do **not** stop an
  adversarial user. The ADR must say this in those words.
- Per-user data isolation everywhere (grain keys, workspaces) is likewise only as strong as the shell's
  restraint. The escape hatch that remains is the kill switch `Copilot:BuiltInToolsEnabled` (global, chat only,
  restart needed; fleet has no switch, see the false comment at `FleetCopilotAgentFactory.cs:15-16`).
- Because of that, **A3 (owner-only Copilot) and A3b (loopback guard) are dropped**, not deferred: a guard on
  endpoints is meaningless while the shell reaches the same prize, and an owner-only Copilot is the opposite of the
  decision. Re-introducing either is a one-step revert of this decision, listed under Out of scope.
- The recommended follow-up (own plan, not here): move the shell out of the backend (separate sandbox with no
  managed identity, no route to the app's localhost, no shared workspaces mount).

**Sequencing is load-bearing:** Phase A (app-side gating) ships first. It is *not* "no behaviour change":
`Dashboard:OwnerOid` is blank today so `/dashboard` denies everyone, and populating `Access:Owners` makes it newly
reachable by the two owners. Phase B (flip the identity provider) is infra, is run partly by the user, and goes
last; **the legal text, the only public statement that would turn false, ships with Phase B only, and must say
that a signed-in user's chats can run commands in the service's own environment, i.e. disclose the accepted risk.**

## Affected areas (verified against the tree at 69c3fb2)

| Area | Files |
|---|---|
| Owner policy (new) | new `src/Agent42.Backend/Access/` feature folder (flat `namespace Agent42.Backend;`, ADR 0039/0040); port interface in `src/Agent42.Agents/` for grain-side reads |
| Dashboard gate (generalise) | `src/Agent42.Backend/Orleans/DashboardOwnerGate.cs`, `DashboardRegistration.cs`, `Program.cs:564,584` (gate before static files), `appsettings.json:70-71`, `src/Agent42.AppHost/AppHost.cs:62`, `tools/Agent42.DevHost/Program.cs:254` |
| `/logs` + diagnostics | `src/Agent42.Backend/Telemetry/AgentApi.Diagnostics.cs:37,45,56` (3 routes), static page `src/Agent42.Backend/wwwroot/logs/` |
| Config-health billable probe | `src/Agent42.Backend/Configuration/AgentApi.Configuration.cs:26-29`, `ConfigHealth.cs:104,110` |
| Shell-capable features | `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs:36-50` (create), `src/Agent42.Backend/Copilot/CopilotAgentFactory.cs`, `src/Agent42.Backend/Fleet/FleetCopilotAgentFactory.cs:15-16,37,47`, `src/Agent42.Backend/Fleet/FleetTaskService.cs:343`, `src/Agent42.Backend/ChatGateway/ChatTurnPipeline.Turn.cs:99`, `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs:52,178`, `src/Agent42.Agents/StoredAgents/AgentKinds.cs:21-33` |
| Usage scoping | `src/Agent42.Backend/Telemetry/AgentApi.Telemetry.cs:21-42`, `src/Agent42.Agents/Telemetry/UsageRollupGrain.cs`, `IUsageRollupGrain`, `UsageRollupState.cs:115,121,180-187`, `UsageSpanProjector.cs:57-61`, `src/Agent42.Backend/wwwroot/js/usage.js:156-225`, `wwwroot/index.html:118` + usage markup, `wwwroot/sw.js:5` (`CACHE`) |
| Telemetry redaction | `src/Agent42.Backend/Telemetry/LiveTelemetryCaptureExtensions.cs:57-70`, `UserTelemetrySpanProcessor.cs`, `src/Agent42.ServiceDefaults/Extensions.cs:101-153`, `AzureMonitorTraceExport.cs:150`, `src/Agent42.Backend/Models/ChatClientFactory.cs:273` (`EnableSensitiveData = true`) |
| Identity infra (Phase B) | `infra/modules/container-app.bicep:402-495` (authConfig), `infra/main.bicep:125-145` (auth params), `.github/workflows/deploy.yml:90-109,98` |
| Identity tests | `tests/Agent42.Tests/EasyAuthSessionTests.cs:20-55` (pins `authLoginHint`), `UserIdAliasTests.cs:237-290` (reads `authAllowedUserObjectIds` from `main.bicep`), `DashboardOwnerGateTests.cs`, `UsageApiTests.cs:116` (`Usage_IsCrossUserVisible`), `EasyAuthExclusionTests.cs` |
| User-supplied endpoints (loopback hole) | `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs:132-137`, `src/Agent42.Agents/Mcp/UserMcpConfig.cs:98-100`, `src/Agent42.Backend/Models/AgentApi.Models.cs:158-163`, `src/Agent42.Backend/Web/PrivateAddressGuard.cs` (existing guard), `Webhooks/WebhookRegistration.cs:28` (connect-time precedent) |
| Public pages that become false (Phase B only) | `src/Agent42.Backend/wwwroot/legal/terms.html:24`, `legal/privacy.html:23` (both say "invite-only"); stale sign-out comment `wwwroot/index.html:125-127` |
| Docs | ADRs 0002, 0029, 0031, 0054, 0062, 0074, 0090, 0097, 0107 (amend banners) + new ADR 0121; `CLAUDE.md` Feature map "Identity" row and cross-cutting rules; `.agentic/gotchas.md` |

## Context & constraints

Decisions the user made (interview, 2026-11-09):

- **Identity = multi-tenant Entra**, keeping the existing `azureActiveDirectory` Easy Auth provider and app
  registration. Rejected: External ID tenant (every user id changes → data migration; Android's hard-coded
  `/.auth/login/aad` path changes) and B2B guests (not "everybody"). Because the owner's home-tenant object id
  is unchanged, **no data migration** is needed (to be confirmed by the Phase-B spike: that the id header still
  carries the same oid).
- **Abuse limits are deferred** to a follow-up plan. No per-user rate limiter exists anywhere (scout grep: no
  `AddRateLimiter`/`Quota`). The ADR records it as an open risk: unmetered Cosmos/Blob growth, operator keys
  (context.dev, Geocoding, Hindsight), and the *global* fleet `MaxConcurrentTasks = 2` that lets one user block
  every other (`FleetTaskService.cs:151-158`).
- **Non-owner prompt text is redacted from telemetry** (not merely owner-gated).

Decisions I took (the user did not specify; each is listed in the final message for redirection):

- **Owners = `Access:Owners`, a committed list in `appsettings.json`**, replacing `Dashboard:OwnerOid` and the
  Bicep `authAllowedUserObjectIds`. Same reasoning as ADR 0062: an object id is an identifier, not a credential,
  and committed config avoids a Bicep/deploy-workflow change per owner. The check runs on the **post-alias**
  `GetUserId()` (ADR 0074 amended in 0062), so an aliased account is the same *person* and is an owner. Fails
  closed: empty list → nobody is an owner. DevHost/AppHost set it to `local-dev` (the ADR 0062 pattern; **never**
  in `appsettings.json`; a pinned test already guards this for `Dashboard:OwnerOid`, retarget it).
- **Copilot shell stays available to everyone** (user decision, see ACCEPTED RISK). Not gated, not isolated.
  Fix the false comment at `FleetCopilotAgentFactory.cs:15-16` (it claims fleet follows
  `Copilot:BuiltInToolsEnabled`; `:47` hard-codes `CopilotCli`, per hard rule 2) and document that fleet has no
  kill switch.
- **Usage is own-data-only for everyone, owners included** (literal reading of "only show the usage for the
  signed in user"). The server derives the caller's identity itself, never from a query parameter. The
  `/api/usage/users` endpoint and the user dropdown are removed; the applications list is scoped to the caller.
  **Key choice (needs approval, see "Open decisions"):** today's rollup key is `user.email → user.name → user.id`
  (`UsageSpanProjector.cs:57-61`, inlined, no shared function). Under multi-tenant sign-in the email claims
  (`preferred_username`, `upn`, `email`, `CurrentUser.cs:42-50`) are **mutable and not unique across tenants**: a
  foreign-tenant admin could set a UPN equal to someone else's and read their usage. So the filter keys on the
  **object id**: add a trailing `[Id(n)]` `UserId` (default empty) to `TokenBucket`/`ToolBucket`
  (CLAUDE.md rule 2; prove on the Cosmos JSON path), stamped from `user.id`, and filter on it. Pre-change
  buckets have an empty `UserId` and are matched by the old email key **only when the caller is an owner**
  (every pre-change row belongs to an owner). Spans with no user identity (reminder, gateway, email-in, fleet
  runner may not carry `RequestContext`) land in the existing "unreported" bucket, which no one sees; this is a
  visible behaviour change from "all" and goes in the ADR. Alias note (ADR 0074): `/usage` stays per *account*.
- **`authLoginHint` is removed** (ADR 0090). It is currently `peter.bons@aceve.com` and would pre-fill the owner's
  email for every stranger. The cost is the Entra account picker returning for the owner's two accounts; accepted.
- **Sequencing:** gating code first, identity flip last.

Traps from the repo (read before executing):

- `.agentic/gotchas.md:105` (alias applied at ONE point, feeds four surfaces) and `:129` (Easy Auth claim `typ`
  spellings are a prod-only fact). The same function `CurrentUser.GetUserTelemetryTags` must feed both the span
  stamp and the usage-filter key, or the user sees an empty report.
- CLAUDE.md rule 4: an excluded endpoint is public and takes userId from a directory grain, never `GetUserId()`.
  Nothing here changes `excludedPaths`; `EasyAuthExclusionTests` must stay green. `/dashboard`, `/logs` and
  `/api/diagnostics/*` are gated by *absence* from `excludedPaths`, which stays true.
- CLAUDE.md rule 6 does **not** help the owner decision: `TurnContext` carries no user id and is `Closed` outside a
  run, while `BuildCopilotAgent` runs at composition (`AgentGrain.Copilot.cs:58-79`). Use the grain's own
  `_userId` (`AgentGrain.Copilot.cs:70,186`), fail closed when empty. Do not widen `TurnContext` to carry the user
  without an ADR line justifying it. Rule 7: a registration identical in every host goes in
  `AddAgent42Silo()`; one that varies is a port each host registers. The owner policy is a port (owners differ per
  host), so register it in Backend, DevHost and the test silos. `AddAgent42Silo()` does not catch a missing
  *transitive* dependency.
- CLAUDE.md rule 3: any `wwwroot/js/` change bumps `CACHE` in `sw.js` (`agent42-shell-v75` today).
- ADR 0062: the gate is **middleware over the path prefix, installed before static files**, 404 not 403. Reuse that
  shape for `/logs` and `/api/diagnostics`; a pinned source-text test asserts ordering.
- Complexity: S3776 ≤ 15 is a build error. Don't pile branches into `MapAgentApi`/`Program.cs`; new endpoint
  logic goes in its own partial/registration folder (ADR 0040/0050).
- Hard rule 5 + memory "IAM changes need explicit user execution": Phase B's `az` commands (app registration
  audience, enterprise-app assignment) are **prepared and handed to the user**, never run by the agent.
- Exemplars: `DashboardOwnerGate.cs` / `DashboardOwnerGateTests.cs` (rule + middleware + host test);
  `HindsightRegistration` (feature registration split); `/api/speech` (optional `GetService` endpoint dependency).

Governing ADRs: 0002, 0009/0010, 0029, 0031, 0042, 0054, 0059, 0062, 0074, 0085, 0090, 0097, 0107, 0108, 0110,
0114; ADR 0013 (egress: any third party receiving conversation content must be recorded as an extension).

## Steps

### Phase A: app-side gating (deployable now; no behaviour change for today's two users)

**A1. Owner policy.** Add `Access:Owners` (`string[]` of GUIDs) + an `IOwnerPolicy` port (Agents) and a Backend
implementation using `OrdinalIgnoreCase`, fail-closed on empty/blank, `local-dev` never in shipped config.
Register as a per-host port (Backend, DevHost, AppHost env `Access__Owners=local-dev`, test silos).
*Verify:* `OwnerPolicyTests` (empty denies, case-insensitive, alias resolves to owner, `local-dev` not an owner
in shipped `appsettings.json`). `dotnet test --filter OwnerPolicy`.

**A2. Generalise the dashboard gate and extend it.** `DashboardOwnerGate` reads `IOwnerPolicy` instead of
`Dashboard:OwnerOid`; add `/logs` and `/api/diagnostics` prefixes (same middleware shape, before
`UseDefaultFiles`/`UseStaticFiles`, 404 for non-owner). Retire `Dashboard:OwnerOid` (appsettings, AppHost,
DevHost) and retarget `ShippedConfiguration_NeverNamesTheOfflineFallbackUserAsOwner` to `Access:Owners`.
Make `/api/config-health` **owner-only** (404 otherwise): its payload (operator endpoints and key status,
`ConfigHealth.cs:69-150`, the alias row) is operator information and its `?probe=true` runs billable probes on
operator keys (`:104,110`); no operator model means strangers have no use for it. Hide the Config tab for
non-owners only if an owner flag is cheap (A3d). `sw.js` already bypasses `/dashboard`; add `/logs` if the SW would
otherwise paint the chat shell over the 404 (ADR 0062's service-worker paragraph) and bump `CACHE`.
*Verify:* extend `DashboardOwnerGateTests` (non-owner 404 on page + sub-paths for all three prefixes, owner 200,
empty list denies everyone, aliased owner passes); the `Program_GatesTheOrleansDashboardBeforeServingIt`-style
ordering test covers the new prefixes. `dotnet test --filter "DashboardOwnerGate|ProjectLayout"`.
Parallelizable with A3 once A1 lands.

**A3. DROPPED (user decision, ACCEPTED RISK above). Do not implement the text below; it is kept only so a revert is
cheap.** Only keep: correct the false fleet comment, and add the owner flag (A3d) for hiding `/logs` and Config
in the UI. Former text:

**A3 (former). Shell-capable features owner-only (fail closed at the point of use, not only at creation).**
(a) `AgentApi.StoredAgents.cs:36-50`: a non-owner creating a Copilot-kind agent → reject (4xx). (b) Run time:
`CopilotAgentFactory` and the grain's Copilot composition (`AgentGrain.Copilot.cs:52,178`) refuse for a
non-owner (covers existing stored agents, the chat gateway at `ChatTurnPipeline.Turn.cs:99`, email-in and
`/api/agent-run`), reading the grain's `_userId` (not `TurnContext`, see Context). (c) Fleet: `FleetCopilotAgentFactory`
availability and the planner's YAML validation refuse Copilot agents for non-owners; correct the false comment at
`:15-16`. (d) Hide the Copilot kind in the agent editor for non-owners (UX only; the server is the control) if an
owner flag is cheap to expose on an existing bootstrap/profile response, otherwise skip and list it as follow-up.
*Verify:* tests for each entry path above with a non-owner (denied, no shell session constructed) and an owner
(allowed); a test that the fleet Copilot factory is unavailable to a non-owner. `dotnet test --filter "Copilot|Fleet|StoredAgent"`.

**A3b. DROPPED (same reason: the shell reaches the same prize, so the guard would not protect anything). Do not
implement; kept only for a cheap revert.** Former text: **Close the loopback hole in user-supplied endpoints
(H1).** For MCP server endpoints
(`AgentApi.Mcp.cs:132-137`, header auth `UserMcpConfig.cs:98-100`) and user provider endpoints
(`AgentApi.Models.cs:158-163`): a **non-owner's** endpoint must pass the existing `PrivateAddressGuard`
(`Web/PrivateAddressGuard.cs`, applied at connect time as the webhooks do) so loopback/private/link-local targets are
refused; owners keep today's behaviour (a self-hosted MCP server on localhost stays valid). Also refuse a
non-owner `Header`-auth entry whose header name starts with `X-MS-CLIENT-` (defence in depth). The guard must act at
**connect time**, not only at save time (a hostname can re-resolve). Amends ADR 0018 and 0024; the "trusted,
self-directed action" rationale in both is recorded as no longer true.
*Verify:* tests that a non-owner's `http://localhost`, `127.0.0.1`, `169.254.169.254`, and a hostname resolving to
loopback are refused for MCP (save, `/test`, and tool call) and for providers; an owner's localhost is still allowed.
`dotnet test --filter "Mcp|Models"`.

**A4. Usage scoped to the caller.** Per Context: add trailing `UserId` (default empty) to the usage buckets, stamp it
from `user.id` in `UsageSpanProjector`, extract **one** `UsageUserKey` helper used by the projector and by the
endpoint (so write and filter cannot drift; today the logic is inlined). `/api/usage` ignores any `user` query
parameter and always filters by the caller; `/api/usage/applications` lists only the caller's applications;
`/api/usage/users` is removed. A caller with no identity gets an empty report (never "all"). Pre-change buckets
(empty `UserId`) match by old email key only for an owner. `QueryAsync` already takes a user filter; add the
caller-id filter additively on the grain interface. `usage.js`: drop the user `<select>` and its loader, remove it
from `index.html`; bump `sw.js` `CACHE`.
*Verify:* rewrite `Usage_IsCrossUserVisible` as an isolation test (user A's report excludes user B; `?user=<B>`
does not leak; a stranger whose email equals an owner's email does **not** see the owner's new rows or legacy rows);
`GrainStorageSerializerTests` proves the new field on the Cosmos JSON path with a pre-change golden bucket;
`UsageApiTests`, `UsageRollupGrainTests`, `UsageSpanProjectorTests`. `dotnet test --filter "Usage|GrainStorageSerializer"`.
Parallelizable with A2/A3/A3b after A1.

**A5. Redact non-owner content from telemetry, at the source.** A span processor removes content-bearing tags
(`gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`, tool-call argument/result
payloads) from spans unless the span's `user.id` **is** an owner. **A span with no `user.id` is treated as
non-owner (redact)**: reminder, gateway, email-in and fleet runs may not carry `RequestContext`
(`UserTelemetrySpanProcessor.cs:36-39`), and "has an id and it is not an owner" would leak exactly those.
Real content sinks (corrected): the **OTLP collector** and the **live buffer**. Application Insights already strips
all gen_ai content through the fan-out allow-list (`BackendTelemetryExtensions.cs:31-40`,
`AzureMonitorTraceExport.cs:168-180`), so it is not a sink to redact.
**Ordering is the hard part and the plan commits to one design:** `Program.cs:30` calls `AddServiceDefaults()`,
which registers the OTLP trace exporters as processors; anything added afterwards runs *after* OTLP's `OnEnd`, and
`BackendTelemetryExtensions.cs:13-17` documents that a batch worker can export while later `OnEnd` processors
still run. So the redactor must be registered **before** the exporters. Give `ServiceDefaults` a host hook
(an `Action<TracerProviderBuilder>`-style "pre-export processors" slot invoked before the exporters are added);
ServiceDefaults stays free of app types (`AzureMonitorTraceExport.cs:129-131`). This **contradicts the comment at
`Program.cs:32-35`** and the order pins in `AzureMonitorTraceExportTests.cs:231`, `OtelConfigurationTests.cs:287`
and `LiveTelemetryCaptureWiringTests.cs:32`; the executor updates all four together and records the new order in
the ADR and `gotchas.md`. If the hook turns out infeasible, **stop and report**; do not fall back to
buffer-only redaction, which would silently leave OTLP carrying strangers' prompts.
**Residual (state it, do not hide it):** this redacts *span content only*. `ILogger` records (live log buffer and
OTLP logs, `LiveLogProcessor`, `Extensions.cs:141`) and http-client span URLs can still carry model-chosen search
queries and URLs (`Extensions.cs:90`); prod logs are Warning-level, which bounds it. Do not promise "no stranger
content in /logs", promise "no prompt/completion text".
*Verify:* an in-memory-exporter test on the **real** composition (`OtelConfigurationTests` is the precedent):
non-owner content absent from the exporter **and** the live buffer; owner content present; **no-`user.id` span is
redacted**; a source-text guard pins the new order. Scope assertions to the source under test (the process-global
`ActivityListener` flake in `gotchas.md`).

**A6. Docs and ADRs (Phase A MR, hard rule 2). The legal pages are NOT in this MR** (a push to `main` deploys,
and `/legal/*` is in `excludedPaths`, so it would go public saying "open to everybody" while the allow-list is on).
`/decide` new ADR 0121 ("open sign-in and the owner tier"). Amend ADRs 0002, 0018, 0024, 0029, 0031, 0054, 0062,
0074, 0090, 0097, 0107 (banners) and update the "~2 trusted allow-listed users" sentences (0031:58-62,
0029:33-34, 0054:152-161, 0059:202-205, 0107:85-87, plus 0018:59-62 and 0024:68-70). ADR 0074:99 says
`allowedPrincipals` "would be the only tenant gate": add a note that it is removed and `Access:Owners` plus the
alias-⊆-owners test replace it. Record as **ADR 0013 egress extensions / open risks**: operator-keyed services that
strangers can now drive (Hindsight, context.dev, Google geocoding, Mailgun) and the SearXNG proxy (ADR 0059) as an
open, operator-IP-billed search surface (not gated here; the deferred limits plan owns it). Docs to update:
`CLAUDE.md` (Identity row, the `Dashboard:OwnerOid` Manual-prerequisites row, a cross-cutting rule for the owner
tier), `docs/features/usage-and-diagnostics.md`, `docs/features/configuration-health.md`,
`docs/features/pwa-and-sign-in.md`, `docs/features/long-term-memory.md`, `docs/features/custom-agents.md`
(state that Copilot kind is open to every signed-in user, un-isolated), `src/Agent42.Backend/Api/UserIdAliases.cs`
(doc comment). ADR 0018/0024/0107 get banners saying the "~2 trusted users" premise no longer holds and that the
resulting exposure (shell and localhost endpoints reach managed identity and the principal header) is **accepted**,
not mitigated. `/gotcha` for the owner-policy
exemplar, the "usage keys on object id, not email" trap, and the new telemetry processor order.
*Verify:* `node .agentic/scripts/check-doc-paths.mjs`, `/sync-docs`.

### Phase B: identity flip (infra; the user runs the commands)

**B1. Spike (needs the user, blocks the rest of Phase B).** Prepare and hand over the exact commands; record
results in this spec. Questions to settle with a real second-tenant account and a personal Microsoft account:
(a) set the app registration sign-in audience to `AzureADandPersonalMicrosoftAccount` (or
`AzureADMultipleOrgs` if personal accounts fail) and set `accessTokenAcceptedVersion` to 2; (b) turn
"assignment required" **off** on the home-tenant enterprise app (`agent42-easyauth`); (c) with issuer
`https://login.microsoftonline.com/common/v2.0` (or `/organizations`, `/consumers` as needed), what does Easy
Auth put in `X-MS-CLIENT-PRINCIPAL-ID`, and is it the **same** oid the owner has today; (d) which claim `typ`
carries email and **tenant id** in the principal blob; (e) known limitation to record, not solve: tenants that
block user consent will show "need admin approval". **No stranger can sign in until B2 removes
`allowedPrincipals`**, so the Phase A acceptance check is not done in B1: it is done in the tests (a non-owner
principal header against the real middleware and endpoints, as `DashboardOwnerGateTests` does) and in DevHost with
a different `X-MS-CLIENT-PRINCIPAL-ID`; the first real-stranger check is B3, after B2 is deployed. A narrow window
exists between B2's deploy and B3's check; that is acceptable only because Phase A is already live.
*Gate:* Phase B cannot proceed unless (c) shows the owner id is unchanged. If it is not, stop and re-plan; do
not remap ids ad hoc.

**B2. Infra change.** `container-app.bicep`: issuer → the multi-tenant authority proven in B1; remove
`validation.defaultAuthorizationPolicy.allowedPrincipals`; remove `authLoginHint` (`loginParameters`) and its param
in `main.bicep`; remove `authAllowedUserObjectIds` (owners now live in `Access:Owners`). Update the code that
referenced them: `EasyAuthSessionTests` (drop the login-hint pins, keep the lifetime pins), `UserIdAliasTests`
(`ShippedAliases_*` now checks alias keys and values ⊆ `Access:Owners`, so an alias can only ever link owners).
Fix the stale comments at `container-app.bicep:402-406,58`. Run a `what-if` before deploy; the deploy goes through
the normal `deploy.yml` (do not change its shape).
*Verify:* `dotnet test --filter "EasyAuth|UserIdAlias"`, `az deployment group what-if` output shown to the user.

**B3. Legal text + deploy + post-deploy checks (user-observed).** Ship the `terms`/`privacy` rewrite (replace
"invite-only", `terms.html:24`, `privacy.html:23`) with the Phase B MR, and fix the stale sign-out comment at
`wwwroot/index.html:125-127` (it describes `login_hint`). The privacy text must **not promise erasure**: Hindsight has
no delete path (ADR 0056/0057), so say what is stored and where; it must also name the operator-keyed egress above.
After deploy: sign in with the owner (still owner: `/dashboard` 200, `/logs` 200, Copilot agent works), with a
non-owner second-tenant account (404 on `/dashboard` and `/logs`, no Copilot kind, `/usage` shows only own,
`/logs` shows no strangers' prompt or completion text to the owner, see the A5 residual), and confirm the Android app still signs in. Prod checks are
**listed as open** in the ADR until the user confirms them (repo convention).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | Empty/blank `Access:Owners` denies everyone; case-insensitive; aliased account is an owner | `OwnerPolicy_*` | new `tests/Agent42.Tests/OwnerPolicyTests.cs` |
| 2 | Shipped `appsettings.json` never lists `local-dev` as an owner | `ShippedConfiguration_NeverNamesTheOfflineFallbackUserAsOwner` (retargeted) | `DashboardOwnerGateTests.cs` |
| 3 | Non-owner gets 404 on `/dashboard`, `/logs`, `/api/diagnostics/*` including sub-paths; owner passes | gate theory over all three prefixes | `DashboardOwnerGateTests.cs` |
| 4 | Gate is installed before static files for every gated prefix | ordering source-text guard | `ProjectLayoutTests.cs` |
| 5 | `/api/config-health` is 404 for a non-owner (incl. `?probe=true`, no billable probe runs); owner unchanged | `ConfigHealth_NonOwner_*` | `ConfigHealthApiTests.cs`, `ConfigHealthProbeTests.cs` |
| 6 | (dropped with A3) Copilot kind and fleet Copilot stay available to a non-owner; nothing in this plan gates them | existing Copilot/fleet tests stay green and unchanged | n/a |
| 7 | The false fleet kill-switch comment is corrected and the docs say fleet has no kill switch | `check-doc-paths.mjs`, `/sync-docs` | n/a |
| 8 | ADR 0121 and ADR 0107's amendment state the accepted risk in plain words (owner tier advisory, any user can read all data) | review of the ADR text; `/audit-docs` | `.agentic/decisions/` |
| 9 | (dropped with A3b) | n/a | n/a |
| 10 | `/api/usage` returns only the caller's rows; `?user=<other>` does not leak; no identity → empty report | `Usage_IsolatedPerUser` (replaces `Usage_IsCrossUserVisible`) | `UsageApiTests.cs` |
| 11 | A stranger whose email equals an owner's sees neither the owner's new rows nor legacy rows | `Usage_SpoofedEmail_SeesNothing` | `UsageApiTests.cs` |
| 12 | `/api/usage/users` removed; applications scoped to the caller | `Usage_Applications_ScopedToCaller` | `UsageApiTests.cs` |
| 13 | The projector and the endpoint share one `UsageUserKey` helper; new `UserId` field reads back from a pre-change golden bucket on the Cosmos JSON path | `UsageKey_SharedWithProjector`, golden read | `UsageSpanProjectorTests.cs`, `GrainStorageSerializerTests.cs` |
| 14 | Non-owner prompt/completion content absent from OTLP exporter **and** live buffer; owner content present; a span with no `user.id` is redacted | real-composition test with in-memory exporter | `OtelConfigurationTests.cs` / new `TelemetryRedactionTests.cs` |
| 15 | Redactor registered before every trace exporter (new ServiceDefaults hook); the four old order pins updated, not deleted | source-text order guards | `ProjectLayoutTests.cs`, `AzureMonitorTraceExportTests.cs`, `OtelConfigurationTests.cs`, `LiveTelemetryCaptureWiringTests.cs` |
| 16 | Alias keys and values must all be owners | `ShippedAliases_AreAllOwners` | `UserIdAliasTests.cs` |
| 17 | Bicep no longer pins `allowedPrincipals`/`login_hint`; lifetime pins kept | updated EasyAuth tests | `EasyAuthSessionTests.cs` |
| 18 | `excludedPaths` unchanged; `/logs`, `/api/diagnostics`, `/dashboard` stay un-excluded | existing + new rows | `EasyAuthExclusionTests.cs` |
| 19 | `sw.js` `CACHE` bumped; `/logs` bypass present if added; usage module still reachable from `main.js` | existing shell/SW guards (the 4 tests that pin `CACHE`, memory `fleet-from-chat`) | executor finds by `grep CACHE tests/` |
| 20 | Owner flag exposed to the client and used to hide `/logs` and Config (only if cheap; Copilot kind is no longer hidden) | API test on the bootstrap/profile response | executor names it |
| 21 | Docs match code | `check-doc-paths.mjs`, `/sync-docs` | n/a |

## Quality gate checklist (from `.agentic/quality-gates.yaml`, read 2026-11-09)

- [ ] `coverage_new_code_pct_min: 80`: measure with `node .agentic/scripts/check-new-code-coverage.mjs`
      (coverlet counts blank/comment lines inside lambdas; keep registrations terse, ADR 0062 verification)
- [ ] `cognitive_complexity_max: 15`: S3776 is a build error; `dotnet build Agent42.slnx` is the measurement
- [ ] `vulnerable_packages: 0`: no new packages are expected; if one is added, resolve the version with
      `dotnet add package`, never from memory (hard rule 4)
- [ ] `dotnet format Agent42.slnx --verify-no-changes`, `dotnet test Agent42.slnx`, doc-path integrity
- [ ] Never edit `quality-gates.yaml` as a side effect (memory: gate failures are fixed in-branch)

## Out of scope

- Per-user rate limits, storage quotas, the global fleet concurrency cap, operator-key cost controls
  (deferred by the user; recorded as open risks in ADR 0121). Includes the unauthenticated-sign-up abuse story
  (anyone can create storage/Cosmos documents).
- External ID / CIAM, Google/Apple social login, B2B guests, any data migration of user ids.
- A role system beyond the single owner tier.
- Isolating or sandboxing the Copilot shell (recommended follow-up plan), gating it to owners (A3), and guarding
  loopback MCP/provider endpoints (A3b): all three were considered and **declined by the user**. Reverting to the
  safe shape means reinstating A3 + A3b from the text kept above.
- Changing `excludedPaths`, the Android app, or the Blob-lease/chat-gateway design.
- Setting `Dashboard`'s owner via Bicep; owners stay committed config.

## Open decisions for approval

1. **Usage key = object id (new trailing `UserId` on the buckets).** Recommended. Alternative: keep the
   email-first key and accept that a foreign-tenant admin with a spoofed UPN can read someone's usage. Not
   recommended under open sign-in.
2. **Tenant check on owners.** The owner test compares only the object id. In a multi-tenant app an id is unique
   per home tenant; I could not verify from the repo that `X-MS-CLIENT-PRINCIPAL-ID` equals `oid` for every
   provider (no test exercises real `tid`/`oid`/`iss` claims). **Default: add `Access:OwnerTenantId` and require
   it, failing closed, if B1(d) shows a reliable `tid` claim** (cheap, and the critic recommended making it the
   default); skip it if the claim spelling cannot be pinned, because a wrong spelling locks the owner out of owner
   features (`gotchas.md:129`).
3. (Resolved by the user: shell open to all, accepted risk. No decision left here.)

## Review

Critic pass 1 (2026-11-09). Fixed in the plan above: **H1** (user-supplied MCP/provider endpoints reach localhost and
can forge the principal header; added A3b and the ADR 0018/0024 amendments), **H2** (redaction ordering infeasible
as first written, and App Insights already strips content; rewrote A5 around a ServiceDefaults hook, corrected the
sinks, specified no-`user.id` = redact, and stated the log/URL residual), **M1** (usage key can drift and can be
spoofed across tenants; extracted a shared helper, keyed on object id), **M2** (`TurnContext` carries no user id;
use the grain's `_userId`), **M3** (Phase A is not "no behaviour change"; stated, and the missing docs added),
**M4** (legal text moved to Phase B only; defined where the real-stranger check happens; no erasure promise because
Hindsight has no delete path; egress recorded under ADR 0013), **M5** (`/api/config-health` made owner-only;
SearXNG proxy recorded as an open surface), and the LOW items (stale `index.html:125-127` text, ADR 0074:99 note,
`ConfigHealthApiTests.cs` naming, vague test locations, missing ACs for the SW bypass and the owner flag).
Rejected: none. Not re-run: a second critic pass is cheap if you want one before `/execute-plan`.

**Decision change after the critic pass (user, 2026-11-09):** told that the shell and the loopback MCP/provider
endpoints reach the managed identity and the principal header, the user chose to **open the Copilot shell to all and
accept the risk**. A3 and A3b are therefore dropped (text kept for a cheap revert), and the plan's owner tier is
documented as advisory. The critic's H1 finding stands as an *accepted residual*, not a fix.

## Progress and divergences (executed 2026-11-09, branch `feature/open-signin-owner-tier`)

**Phase A is implemented; Phase B (B1 spike, B2 infra, B3 legal text + deploy) is NOT started and is the user's to run.** The
spec therefore stays in `.agentic/specs/` (not `completed/`) until Phase B lands.

Where the implementation diverged from the plan above:

- **`IOwnerPolicy` lives in `Agent42.Backend` (`Access/OwnerPolicy.cs`), not in `Agent42.Agents`.** The plan wanted a grain-side
  port, but A3 was dropped, so nothing in the grain layer asks who is an owner. Registered in `Program.cs` (from config)
  and `tools/Agent42.DevHost/Program.cs` (`local-dev`); the AppHost sets `Access__Owners__0=local-dev` and blanks `__1`. Test
  silos need no registration. It is resolved with `GetService` and fails closed when absent (CLAUDE.md rule 7).
- **`DashboardOwnerGate` is deleted, replaced by `OwnerGate`** (`Access/OwnerGate.cs`). `DashboardRegistration.RoutePrefix`
  keeps the `/dashboard` constant. `/api/config-health` is simply a fourth entry in `OwnerGate.OwnerOnlyPrefixes`, so there
  is no endpoint-level logic and the billable `?probe=true` is blocked by the same middleware. The method is
  `app.UseOwnerGate()` (the ordering pins in `ProjectLayoutTests` were retargeted, not deleted). `ProjectLayoutTests`'s
  feature-folder list gained `Access`.
- **A3d (owner flag to hide Config and `/logs` in the UI) was skipped.** AC20 said "only if cheap": it needs a new bootstrap
  field and JS. A non-owner sees an empty Config tab (the endpoint 404s and `confighealth.js` returns quietly) and there is no
  link to `/logs`. Recorded in ADR 0121 as follow-up.
- **`/sw.js`:** added a `/logs` bypass (before any `respondWith`) and bumped `CACHE` to `agent42-shell-v76`; five tests
  retargeted plus a new `ServiceWorker_BypassesTheLogsConsoleBeforeCaching`.
- **Usage grain interface:** `QueryAsync`/`ListApplicationsAsync`/`ListUsersAsync` were replaced by
  `QueryForUserAsync(range, application, userId, legacyUserKey)` and `ListApplicationsForUserAsync(userId, legacyUserKey)`
  (new `[Alias]`es, `ListUsersAsync` removed) rather than adding a parameter, so no overload can return everyone's rows. The
  id is part of the bucket key. The "Usage by user" table stays in the UI and shows the caller's own row labels.
  A side effect worth knowing: both accounts of one aliased person now share one report (ADR 0074's "`/usage` stays per
  account" no longer holds for scoping; the row label is still per account).
- **A5 hook:** `ConfigureOpenTelemetry`/`AddServiceDefaults` gained `Action<TracerProviderBuilder>? beforeTraceExporters`, invoked
  after the instrumentation and before `AddOpenTelemetryExporters()`; `Program.cs` passes `NonOwnerContentRedactor.Register`.
  The four order pins named in the plan did **not** need changing: they test `BuildBackendHost`, which does not use the
  hook (the existing "live console keeps content" tests rely on that). The new ordering guard is
  `TelemetryRedactionTests.RedactorHook_RunsBeforeTheExporters_AndProgramPassesIt`. Found while building it: the redactor's
  allowlist must also keep `agent42.provider.kind`, which `UsageSpanProcessor` reads after the redactor.
- **AC16 (`ShippedAliases_AreAllOwners`) was added in Phase A** (owners exist already). The Bicep-allowlist test stays until
  B2 removes `authAllowedUserObjectIds`. AC17 (EasyAuth test updates) is B2.
- **Open decision 2 (`Access:OwnerTenantId`) is deferred to B1(d)** as the plan's default said (skip unless a `tid` claim
  spelling can be pinned).
- ADR 0121 written; amend banners added to 0002, 0018, 0024, 0029, 0031, 0054, 0059, 0062, 0074, 0090, 0097, 0107; the
  "~2 trusted users" sentences in 0031 and 0029 were annotated. `CLAUDE.md`, `usage-and-diagnostics.md`,
  `configuration-health.md`, `custom-agents.md`, `pwa-and-sign-in.md` and three `gotchas.md` entries were updated.
  `docs/features/long-term-memory.md` needed no change (it makes no claim about who can sign in). The legal pages are
  untouched on purpose (Phase B only).

### Phase B handoff: commands for the USER to run (prepared, not executed; hard rule 5)

B1 is a spike with a real second-tenant account and a personal Microsoft account. App registration client id (from
`infra/main.bicep:126`): `e9a02aec-5368-4c9a-8211-d21222c4f30c`. Check the syntax with `--help` before running.

```powershell
# (a) read the current state first
az ad app show --id e9a02aec-5368-4c9a-8211-d21222c4f30c --query "{aud:signInAudience, tokenVersion:api.requestedAccessTokenVersion}"
# (a) widen the audience, and accept v2 tokens (use AzureADMultipleOrgs if personal accounts fail)
az ad app update --id e9a02aec-5368-4c9a-8211-d21222c4f30c --sign-in-audience AzureADandPersonalMicrosoftAccount
az ad app update --id e9a02aec-5368-4c9a-8211-d21222c4f30c --set api.requestedAccessTokenVersion=2
# (b) turn "assignment required" off on the home-tenant enterprise app
az ad sp update --id e9a02aec-5368-4c9a-8211-d21222c4f30c --set appRoleAssignmentRequired=false
```

Then settle, and write the answers here: (c) with issuer `https://login.microsoftonline.com/common/v2.0` (or `/organizations`,
`/consumers`), what `X-MS-CLIENT-PRINCIPAL-ID` carries and whether the owner's id is **unchanged** (hard gate: if not, stop
and re-plan); (d) which claim `typ` carries email and tenant id; (e) record that tenants that block user consent show "need
admin approval". No stranger can sign in until B2 removes `allowedPrincipals`.

Results: _(pending the user)_
