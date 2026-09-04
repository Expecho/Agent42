---
id: 0063
date: 2026-09-04
status: accepted
tags: [tools, calendar, oauth, google, mcp, egress, prompt-injection, deletion, no-migration]
supersedes: [0053]
extends: [0024, 0013]
amends: [0039, 0040]
---

# Google Calendar moves out of Agent42 into a standalone MCP server

## Context

ADR 0053 shipped six native Google Calendar tools here. It is now **totally superseded**: the code is
deleted from this repo and the calendar is reached the way any other remote MCP server is (ADR 0024),
by adding it in Preferences → MCP servers.

**This re-adopts an alternative ADR 0053 rejected twice and marked closed.** That must be quoted, not
paraphrased. 0053's Context says the user asked for a co-hosted first-party MCP server, reversed it in
the same round, and that a third-party remote MCP server through ADR 0024 "was the other option, and
was rejected with it. Both rejections are recorded below so the reversal stays legible later;
**neither is to be relitigated.**" Its Alternatives list gives the reasons:

> - **A co-hosted first-party MCP server** … It would have added a protocol, a transport and a
>   discovery step to reach code that already lives in this process.
> - **A third-party remote MCP server** added through ADR 0024. … It would also have put a third party
>   between the app and the user's calendar for no gain.

**What changed is the premise of the first reason, not the reasoning.** "Code that already lives in
this process" was true and is the whole argument; it is no longer true, because the calendar logic is
now wanted *outside* Agent42 — usable from Claude Code and other MCP clients. Reuse is the one
motivation 0053 never weighed, because it did not exist then. The second reason still stands on its
own terms and is now an accepted cost rather than a refutation: there **is** a party between the app
and the calendar, but it is the operator's own server on the operator's own hardware, not a third
party's. This was an explicit user decision at plan approval, not drift.

## Decision

Delete the native integration entirely — both `Calendar/` folders, the six `AIFunctionFactory`
registrations, the `calendar` selection key and its six `ToolNameToKey` rows, the four `/api/google/*`
endpoints, the two grain states, the Preferences → Calendar tab, the `GoogleCalendar:*` config and its
two Bicep parameters, secrets and env conditionals.

The replacement is `Calendar.Mcp` in the sibling repo `Expecho/mcpserver`, running at
`https://google.agent42.nl/mcp` behind `Mcp.Gateway` (host-based YARP routing, LettuceEncrypt
TLS-ALPN-01) **on the operator's own bare-metal server, directly on the internet with no reverse proxy
in front**. It fronts Google with a stateless federating OAuth server ("Design D"): RFC 8414 metadata,
an `/oauth/authorize` hop that adds `access_type=offline` + `prompt=consent`, and an `/oauth/token`
proxy that adds `client_secret` and strips `resource`. Two facts force that design — Google requires
`client_secret` at the token endpoint even with PKCE, while Agent42 is a strict public client; and
Agent42's authorize URL sends neither `access_type=offline` nor `prompt=consent`, without which no
refresh token is issued and the connection dies silently about an hour later.

## Consequences

- **Google tokens and the Google client secret now live outside Agent42**, on the operator's server.
  Agent42 holds only what ADR 0024 already holds for any OAuth MCP server.
- **The calendar is now reached through ADR 0024's inbound prompt-injection channel.** Tool results
  from a remote MCP server are untrusted text; that was already true of every other MCP server, and
  the calendar has joined them. ADR 0053's read-side egress consequence is otherwise unchanged: event
  titles, locations and shared-calendar ids (other people's email addresses) still enter the
  transcript, where ADR 0013 egress, ADR 0027's followed links, ADR 0035 share links, ADR 0032's
  IndexedDB mirror, ADR 0054's `/logs` and ADR 0061's message index all reach them.
- **No confirmation gate on writes, still.** ADR 0053 rejected one explicitly; nothing here revisits
  that. A prompt injection can still rewrite or delete entries — now over MCP.
- **`CalendarSpanSuppressingHandler` is deleted, and the rule it served is not.** Calendar REST v3 puts
  the calendar id (an email address, someone else's for a shared calendar) in the URL **path**, and
  OTel redacts a query string but never a path. The general rule stays in `gotchas.md`, with ADR 0056's
  Hindsight exception intact. Repo A emits no OpenTelemetry at all today, so the concern does not
  transfer — it is absent there, not solved there.
- **The curated-message discipline was re-established in Repo A, not lost in translation.** A Google
  error body echoes the request URL, the calendar id and event detail; Repo A's house style is
  exceptions, so its tools layer catches and maps to curated constants.
- **Design D serves one pre-registered redirect URI per MCP client.** Google supports no DCR and the
  authorize hop is a pass-through, so each additional client needs a manual Google Cloud console edit.
  Design B (a full authorization server with DCR and per-subject token storage) is the documented
  upgrade path, deliberately deferred rather than dismissed — revisit if the limit chafes.
- **The design requires an origin root.** PRM at `/.well-known/oauth-protected-resource` plus RFC 8414
  path insertion means `Calendar.Mcp` cannot be hosted at a subpath. A second OAuth-backed MCP server
  behind the same gateway needs its own subdomain.
- **Orphaned grain state.** `google-credentials` and `google-oauth-pending` documents stay in the
  shared `agent-state` container, unread, still holding DP-encrypted Google refresh tokens that the
  persisted key ring can still decrypt in principle. Cleanup is a manual operator step, not code —
  which is also why removing those two entries from `GrainStorageSerializerTests`' `$type` list was a
  deliberate edit rather than an incidental one.
- **A stored `"calendar"` selection is now silently dropped** by `Sanitize`. Harmless, no migration,
  one-way. `AgentGrainTests.LegacyAgentSnapshot_DoesNotEnableMemory` keeps such a key in its snapshot
  on purpose, because that is what real stored selections look like.
- **ADR 0058's cold start applies to the new server**, as narrowed by ADR 0064: per-tool checkboxes
  appear after the next chat activation *or* one press of Test. The master checkbox works throughout.
- **Existing reminders that assumed calendar access degrade.** Calendar tools were live on
  `ReceiveReminder` and `/api/agent-run`; such a reminder now runs on a model without them until the
  MCP server is added and enabled.
- Counts move: Agents feature folders 21 → 20, Backend 24 → 23, `AgentGrain` partials 13 → 12,
  endpoint groups 17 → 16. `excludedPaths` is **unchanged** — no calendar route was ever excluded, and
  the three `/api/google/*` rows removed from `EasyAuthExclusionTests` asserted gating, not exclusion.

## The `check-doc-paths.mjs` rule collision, decided explicitly

Deleting these paths puts two repo rules in conflict: `check-doc-paths.mjs` scans **every**
`.agentic/decisions/*.md` and hard-blocks CI on a missing path, while `decisions/README.md:5` says
*"Never edit an accepted ADR's decision content; supersede it."* Two ADRs cite deleted paths — 0053
(now superseded) and **0039** (which stays active). The plan required picking explicitly rather than
letting CI failure choose. Both halves were needed, because neither fixes the other:

1. **The checker now skips ADRs whose front matter says `status: superseded`.** A totally-superseded
   ADR is a historical record, and paths naming since-deleted code are *accurate history* — editing
   them would corrupt the record as well as breach the never-edit rule. The key is deliberately
   `status: superseded` and **not** `superseded_by:`: ADR 0007 carries `superseded_by: 0008 # partial`
   while remaining `status: accepted` and authoritative, and keying on that tag would have silently
   stopped checking it. Partial supersessions stay fully checked.
2. **ADR 0039's and ADR 0040's inventories are corrected in place** — the calendar folder removed from
   each project's folder list, and the calendar grain partial from 0039's partials list. This is not an
   exception to the never-edit rule: **both ADRs already prescribe it**, in the same words — "A folder
   list is a factual inventory, not a decision, so it is corrected in place" — and 0039 repeats the rule
   for its partials list. Hence `amends: [0039, 0040]` above.

   The same pass also repairs counts those two ADRs had already drifted on, because 0039 warns that
   correcting a heading's count without the list beneath it "is the one outcome that rule exists to
   prevent, and it has now happened twice": 0039's folder list gains the `Search/` that ADR 0061 never
   recorded there; 0040's gains `Search/` and `Orleans/` (ADR 0062) and loses `Calendar/`, taking its
   heading from "Twenty" to twenty-one; and 0040's endpoint-group count is restated as sixteen. 0040's
   "25 DTO records" figure is dropped rather than renumbered — nothing measures it, so per ADR 0050's
   own logic a number that cannot fail should not be written down as though it could.

## Alternatives rejected

- **Keeping the native tools and adding the MCP server alongside.** Two implementations of one
  capability, two credentials, two places for a bug — and the tools would keep their `calendar` key
  competing with the server's `mcp:{id}` key in the same picker.
- **Editing ADR 0053's body to remove the deleted paths.** Breaches the never-edit rule and destroys
  the record of what the code actually was.
- **Making `check-doc-paths.mjs` skip every ADR carrying `superseded_by:`.** Would have silently
  stopped checking ADR 0007, which is only partially superseded and still authoritative.
- **Design B now** (full authorization server, DCR, per-subject token storage). Deferred, not
  dismissed — see the Consequences.
- **A confirmation gate on writes**, and **widening the OAuth scope** beyond the `calendar.events` +
  `calendar.calendarlist.readonly` pair. Both were settled by 0053 and are unchanged.

## Unverified

- **The real-Google leg was never automated, in either repo.** Repo A's tests run against a fake Google
  only, and Agent42's never reach a real MCP server. What *was* confirmed before this deletion landed,
  by the operator at execution time: the server is added in Preferences → MCP servers as an
  InteractiveOAuth server, the Google consent flow completes through Agent42, and the calendar tools
  work end to end. That is the landing-order gate, and it is met.
- **The post-expiry refresh specifically has no recorded observation.** It is the one failure this
  design is most exposed to — a missing `access_type=offline`/`prompt=consent` yields no refresh token,
  and the connection then dies **silently, about an hour later**, invisible in any test. The operator's
  confirmation above does not distinguish "worked" from "worked within the first hour", and no observed
  values were recorded. Nothing in CI can close this; the way to close it is to use the connection, let
  an access token expire, and use it again.
- **The production redirect URI** `https://chat.agent42.nl/api/mcp/oauth/callback` must be registered
  on the server's Google OAuth client. A `http://localhost` test cannot detect its absence; that
  failure appears only at chat.agent42.nl, after this deletion has landed.
