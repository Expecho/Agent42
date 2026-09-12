# Plan — Gmail MCP server in the sibling repo

planned-against: **Agent42** `main @ e42acd6` (this spec's home, plus one docs MR) and **mcpserver**
`main @ aed18d8` (where every code change lands). Both were read at plan time.

> **Read the base line above twice.** All code lands in `Z:\Sources\mcpserver` (GitHub
> `Expecho/mcpserver`), branched from **`main`**, not from the in-flight `feature/toolforge-mcp`.
> Agent42 gets **no code change** — the server is consumed through ADR 0024's Preferences → MCP
> servers — but it does get **one documentation MR** (step 9), because the single durable control
> this feature depends on is an Agent42 checkbox.

## Overview

**Goal:** a `Gmail.Mcp` server built the same way `Calendar.Mcp` is (ADR 0063) — bearer auth over a
stateless Google OAuth front, its own subdomain behind `Mcp.Gateway`, curated failure messages,
capped returns — exposing read, triage, drafting and send, the last of those split across three
tools so that an operator can choose how much of it to grant.

**Why now:** the calendar proved the shape and the operator wants the same agent reach over mail.
Everything expensive about this design (the OAuth front, the audience check, the gateway, the
certificate story) already exists and is deployed; this is the second tenant of it.

**Shape:**

```
MCP client --bearer--> gmail.<domain>/mcp --> GmailTools --> gmail.googleapis.com/gmail/v1
                        |                                    (Authorization: the caller's own
                        |                                     Google access token, never stored)
                        |-- /.well-known/oauth-protected-resource
                        |-- /.well-known/oauth-authorization-server
                        |-- /oauth/authorize  -> Google, + access_type=offline & prompt=consent
                        +-- /oauth/token      -> Google, + client_secret, - resource
```

## Decisions taken in the planning interview

The user's explicit calls. Do not relitigate; a change here is a new planning round. D7 was added
after the adversarial review surfaced a deployment break — see the Review section.

- **D1 — Read, triage and send, on exactly ONE scope:
  `https://www.googleapis.com/auth/gmail.modify`.** It already covers read, compose, send and
  labels, so `gmail.send` would be redundant. `https://mail.google.com/` is **forbidden** here: it
  is the only scope that permits permanent deletion, and not holding it is what makes
  "delete everything" reach the trash at worst — recoverable for about 30 days. This mirrors the
  calendar's existing narrow-pair scope discipline.
- **D2 — A SEPARATE Google OAuth client from the calendar's**, with its own environment variables.
  `gmail.modify` is a **restricted** scope; putting it on the calendar's client would drag the
  calendar into restricted-scope verification and an annual CASA Tier 2 assessment. Two clients
  means a verification problem on one cannot take the other down.
- **D3 — Its own subdomain** behind the gateway. ADR 0063 records why: a bearer backend must be the
  root of an origin, because RFC 8414 metadata at a non-root issuer uses a path-inserted well-known
  URI that real clients (Agent42's included) do not follow.
- **D4 — The Google OAuth consent screen is in Testing publishing status.** Consequence, recorded
  honestly rather than designed around: restricted scopes work with **no CASA**, but refresh tokens
  issued to an app in Testing **expire after 7 days**, so the connection needs re-consenting weekly.
  The calendar already lives with this. Moving to Production later is what removes it, and for a
  restricted scope that is where CASA starts.
- **D5 — Three send-shaped tools: draft, send-a-draft, and direct send.** `draft_email` writes a
  Gmail draft and sends nothing. `send_draft` sends an existing draft by id. `send_email` composes
  and sends in one call. **The user added direct send after the adversarial review** (Review
  section, "Change after the review"), so the draft path is an option the model may take, never a
  gate it must pass. Read "What the three send tools buy, and what they do not" before repeating any
  security claim about this, because the obvious version of that claim is wrong.
- **D6 — Extract the shared Google OAuth plumbing** into a new `Mcp.Google` project rather than
  copying it into `Gmail.Mcp`. Rationale in "Extraction, not duplication".
- **D7 — The Gmail backend is OPTIONAL in the gateway.** When `MCP_GATEWAY_GMAIL_HOST` or the Gmail
  client pair is absent, the gateway logs one line and starts **without** the Gmail backend, rather
  than refusing to start. The obvious alternative — a third `RequireHost` plus a third
  `IsConfigured` refusal, exactly "shaped like the calendar one" — was **rejected**: it would make
  an existing gateway that restarts without Gmail configuration exit 2 and take the calendar and
  the scraper down with it, and it would break the existing CI gateway step, which supplies only
  the scraper and calendar host variables. Skipping an unconfigured backend is not a weakening of
  the repo's fail-closed rule: that rule exists to prevent an **unprotected listener**, and an
  absent backend is not a listener at all.

## Affected areas

Verified against `mcpserver` `origin/main` (`aed18d8`) at plan time, not from memory. The entries
marked **(review)** were missed by the first draft and added after the adversarial pass.

**New — `src/Mcp.Google/`** (namespace `Mcp.Google`, sibling of `Mcp.Hosting`):
- `GoogleOptions.cs` — the `GoogleOptions` record only, moved from `Calendar.Mcp`
- `GoogleScopeSet.cs` — new: the scopes, the resource name, the stdio token variable
- `GoogleOAuthFront.cs` — moved, parameterised by a `GoogleScopeSet`
- `GoogleTokenValidator.cs` — moved, parameterised by a `GoogleScopeSet`
- `GoogleCaller.cs` — moved from `Calendar.Mcp/CalendarCaller.cs`, parameterised
- `Mcp.Google.csproj` — `Microsoft.NET.Sdk`, references `Mcp.Hosting` (no `FrameworkReference`
  needed; `Mcp.Hosting.csproj` already carries it)

**New — `src/Gmail.Core/`** (namespace `Gmail.Core`):
- `GmailClient.cs`, `GmailLimits.cs`, `GmailModel.cs` (records, `GmailMessages`, `GmailException`)
- `MimePlainText.cs` — the payload-tree walk, base64url decode, charset decode
- `MimeCompose.cs` — RFC 2822 composition for drafts
- `Gmail.Core.csproj` — empty `Microsoft.NET.Sdk`, everything from `Directory.Build.props`

**New — `src/Gmail.Mcp/`** (namespace `Gmail.Mcp`):
- `GmailStartup.cs` (including `GmailScopes`), `GmailTools.cs`, `Program.cs`
- `Gmail.Mcp.csproj` — `Microsoft.NET.Sdk.Web`, references `Gmail.Core` and `Mcp.Google`

**New — `tests/Gmail.Mcp.Tests/`** — mirrors `tests/Calendar.Mcp.Tests/` file for file, and
**references `Calendar.Mcp` as well** (review), because AC4 asserts rejection in both directions.

**Modified — `src/Calendar.Mcp/`:** `Program.cs`, `CalendarStartup.cs`, the rename of
`GoogleScopes` to `CalendarScopes`, and the deletion of the four moved files.

**Modified — `tests/Calendar.Mcp.Tests/`** (review): `CalendarServer.cs` at the two call sites named
in step 1, plus identifier renames in `FakeGoogle.cs`, `AuthorizeHopTests.cs` and
`DiscoveryTests.cs`. No assertion's **meaning** changes.

**Modified — infra and docs, all on `main`'s versions:**
- `src/Mcp.Gateway/Program.cs` — the optional Gmail backend per D7, `gmailPort`, a `BackendRoute`
  entry, and the certificate domain list that derives from it.
- `src/Mcp.Gateway/Mcp.Gateway.csproj` **(review)** — `ProjectReference` entries for `Gmail.Core`
  and `Gmail.Mcp`. The file lists one per backend project; without this, step 5 does not compile.
- `McpServer.slnx` — three `<Project>` lines in `/src/` and one in `/tests/`.
- `Dockerfile` — three `COPY ...csproj` lines in the restore-layer block (lines 12–18 on `main`),
  and the run-example comment block at lines 57–64 **(review)**.
- `docker-compose.yml` — a `gmail-mcp` service beside `calendar-mcp` publishing **5104:8080**
  (deliberately not 5101–5103, which are the gateway's in-container loopback ports; the two
  numbering spaces are unrelated and a reader should not have to work that out), plus the new
  variables on `mcp-gateway`.
- `.env.example` — the Gmail client pair and `MCP_GATEWAY_GMAIL_HOST`.
- `README.md` — a "Gmail MCP server" section after the calendar one (`main` line 219), **and
  (review)** the Gateway `### Setup` step 2 and its `docker run` example, and the
  "Adding another MCP server" section.
- `.github/workflows/ci.yml` — a new Gmail image build and smoke step, **and (review)** the
  existing gateway step at lines 126–140, which starts the gateway with only the scraper and
  calendar host variables and asserts it becomes healthy. Under D7 that step keeps passing
  unchanged; a test must prove it, because the naive implementation breaks it silently.

**Agent42:** no code. One docs MR — step 9.

## Context & constraints

Judgment and pointers only. Re-read the code for code facts; this section is what cannot be
re-derived cheaply.

### Extraction, not duplication

`GoogleOAuthFront`, `GoogleOptions`, `GoogleTokenValidator` and `CalendarCaller` live in
`Calendar.Mcp` today and hardcode calendar scopes, the calendar resource name and a
`urn:calendar-mcp:` claim namespace. Copying them into `Gmail.Mcp` would put **two versions of the
audience check** in the repo — the one piece of code whose own remarks say it *is* the security of
the server. The repo already made this call once when it extracted `Mcp.Hosting`.

Five traps in the move, the last three found by the adversarial pass:

1. **`GoogleOptions.ApiBaseAddress` currently defaults to `CalendarClient.DefaultBaseAddress`.**
   That default is what would drag a Calendar dependency into the shared project. Each server must
   pass its own API root.
2. **Removing that default alone does not compile.** `GoogleOptions` is a positional record whose
   last four parameters are all optional; making the last one required leaves a required parameter
   after optional ones, which is **CS1737**. `ApiBaseAddress` must be **reordered** into position 3,
   before the three endpoint parameters. That is a source-breaking change to a public record and
   every construction site must be revisited.
3. **`GoogleScopes` holds calendar-specific constants and must not move.** The file being split
   contains two public types. Only `GoogleOptions` moves; the scope constants stay in
   `Calendar.Mcp`, **renamed to `CalendarScopes`**, and gain a `GoogleScopeSet` built from them.
   `Gmail.Mcp` gets its own `GmailScopes` in the same shape.
4. **The claim type becomes one shared constant** (`urn:google-mcp:access-token`). Safe because a
   claim is request-scoped and never persisted, and because the gateway builds a **separate
   `WebApplication` per backend**, each with its own DI container and its own validator instance —
   verified in the review against the two `McpHost.Build` calls. The constraint that keeps it safe:
   **never host two Google backends in one `WebApplication`.** AC17 pins it, because it is the only
   thing standing between two mailbox-scoped credentials.
5. **`McpAuth.Bearer` takes `Func<string, ProtectedResourceMetadata>`.** With scopes parameterised,
   `GoogleOAuthFront.ResourceMetadata` stops being a usable method group and becomes a closure over
   the scope set **at every call site, including the one in `tests/`**. This is why step 1's
   verification gate is worded the way it is.

### What the three send tools buy, and what they do not (D5)

**Write this section's conclusion into the README, not the intuitive one.** The intuitive claim —
"drafting first means an injection has to take two steps" — was already weak before direct send
existed, because chaining tool calls in a single turn is the normal mode and a sent draft leaves the
same Sent Mail trace an unsplit tool would. With `send_email` present it is not merely weak, it is
**false**: there is a one-call path from an injected instruction to delivered mail, and nothing in
this design stands in front of it.

What the three tools buy is **granularity in what can be withheld.** Agent42's per-MCP-tool
selection (ADR 0058) treats each as its own key, so an operator can choose a posture:

| posture | tools enabled | effect |
|---|---|---|
| read and triage only | neither send tool, nor `draft_email` | no outbound anything |
| propose only | `draft_email` | the model writes, the human presses send in Gmail |
| supervised send | `draft_email` + `send_draft` | two-call path, artefact visible in Drafts in between |
| unsupervised send | `send_email` | one call from instruction to delivered mail |

That is a real control, and an unsplit design could not offer the middle two rows. It is also the
**only** control here.

Three honest qualifications, all of which belong in the README:

- **Every posture above is opt-in, and the default is the last one.** `EnabledTools` uses
  `null == all` (ADR 0021), so a freshly added server grants **every** tool including `send_email`
  until the user narrows it. A Gmail server added and left alone is an unsupervised-send server.
- **The control is not immediately available.** Per-tool checkboxes do not exist until that user's
  next chat activation or a press of **Test** (ADR 0058's cold start, narrowed by ADR 0064). Step
  8.4 handles this operationally, and it is the reason step 8.4 is not optional.
- **There is no confirmation gate**, exactly as ADR 0063 records for calendar writes. This plan does
  not add one, and with direct send enabled that position is doing more work than it was for the
  calendar: a rewritten meeting is recoverable, a sent email is not.

`send_email` is the **only** tool permitted to reach `users.messages.send`, and `messages.delete` is
never reached at all. AC11 pins both. Note that a narrow `send_draft` is a property of **our tool
signature**, not of the Gmail API: `users.drafts.send` takes a Draft resource whose `message` field
is optional, so the API itself permits compose-and-send on that endpoint too. Re-verify that against
the reference before writing any README sentence that attributes narrowness to Google.

### Untrusted text, and the one control that lives in the other repo

Every Gmail tool result is attacker-authored text — a subject line, a body, a sender's display
name — arriving on ADR 0024's inbound prompt-injection channel, and it reaches whatever agent
called the tool.

**Do not enable `SeedsFetchableUrls` on this server** (ADR 0067, `StoredMcpServer [Id(13)]`, default
false). Ticking it would make every URL in every email fetchable by `fetch_web_page` and ingestible
by `read_web_document`, which writes durable vector chunks and, through ADR 0056, can be retained
into a memory bank with no delete path. Mail is the highest-volume source of attacker-authored URLs
imaginable.

**That checkbox is in Agent42, not mcpserver**, so a warning in mcpserver's README reaches nobody
who is about to tick it. Step 9 puts it where it will be read.

Separately: tool results are persisted and count against the 2 MB session cap
(`.agentic/gotchas.md`, "An agent tool's RETURN value is persisted").

### The return budget is 8 KB, not "small"

**Agent42 truncates every MCP tool result at `McpToolLimits.MaxToolResultLength` = 8 KB**, applied
to the **serialized** string by plain substring — so an oversized result reaches the model as JSON
chopped mid-token, not as a shorter list. `WebToolLimits.MaxPageTextLength` is the same 8 KB and the
calendar's worst case is about 12 KB.

`GmailLimits` must therefore be sized against a **serialized** ceiling under 8,192 characters, not
against a vague sense of smallness. The first draft of this plan proposed values worth roughly 80 KB
for `read_thread`; they are corrected below, and AC7 now asserts the ceiling directly rather than
asserting that truncation happened.

mcpserver cannot reference Agent42's constant, so the number is duplicated with a comment naming its
source. That duplication is deliberate and should be called out in the code, because the two can
drift.

### Quota is a design input

Gmail bills in **quota units**, 6,000 per user per minute. Listing is cheap (`messages.list` = 5);
hydrating is not (`messages.get` = 20 each, `threads.get` = 40 for a whole thread, `drafts.send` =
100). A naive "summarise my last 50 unread" costs 1,005 units, so six such calls exhaust the minute.
The caps below are a quota control as much as a context-window control, and `threads.get` is
preferred over N message fetches for thread-shaped reads.

### Gmail API facts that cost a rewrite if missed

Sources are the official reference under `developers.google.com/workspace/gmail/api`. The ones with
an AC are marked; the rest are implementation notes the executor must simply not get wrong.

- **(AC8) Base64URL, both directions.** `body.data` and `raw` use the URL alphabet and Gmail's
  output is frequently **unpadded**. `Convert.FromBase64String` throws on both counts.
- **(AC8) There is no body field.** For any `multipart/*` message the top-level `payload.body` is
  empty and the text is two levels down. The walk must prefer `text/plain` inside
  `multipart/alternative`, recurse through `multipart/mixed`, and **skip any part with a non-empty
  `filename`** — an attached `.txt` or forwarded `.eml` is `text/plain` and would otherwise be
  concatenated into the body.
- **(AC12) `message.id` is not the RFC 822 `Message-ID`.** A reply built with the API id in
  `In-Reply-To` sends fine, returns 200 and silently never threads. Threading needs all four:
  `threadId`, an `In-Reply-To` and `References` built from the parent's **`Message-ID` header**, and
  a matching `Subject`.
- **(AC9) `labelIds` is a repeated parameter**, not a comma list. `?labelIds=INBOX,UNREAD` matches
  nothing and returns an empty list with no error.
- **(AC9) There is no archive.** Archive is `modify` with `removeLabelIds: ["INBOX"]`; mark-read is
  `removeLabelIds: ["UNREAD"]`.
- **(AC18) Charset.** Decode per the part's `Content-Type charset`.
  `Encoding.GetEncoding("windows-1252")` **throws** on .NET without the CodePages provider, and this
  repo adds no packages — catch and fall back to `Encoding.Latin1` (built in), then UTF-8.
- **(AC19) `internalDate` and `historyId` are integers serialized as JSON strings.** `sizeEstimate`
  is a real number. A plain `long` property throws without
  `JsonNumberHandling.AllowReadingFromString`.
- **(AC20) 403 is overloaded.** `rateLimitExceeded` and `userRateLimitExceeded` are retryable;
  `insufficientPermissions`, `dailyLimitExceeded` and `domainPolicy` are not. Branch on
  `error.errors[0].reason`, never the status alone.
- **Labels live on messages, not threads.** Archiving one message of a five-message thread leaves the
  thread in the inbox. Each tool description must say which it does. No AC — this is a wording
  obligation, not a behaviour.
- **`format=raw` is rejected by `threads.get`.** No AC; it is a 400 the first time it is tried.

### Curated messages are not optional here

A Gmail error body echoes back the **entire search query you sent**, **recipient addresses**,
**subject lines** from RFC 2822 parse failures, message, thread, draft and label ids, and the
authenticated user's own address. The calendar server established this discipline for exactly the
same reason and `CuratedMessageTests` pins it. Map upstream failures onto a closed set of our own
constants; put the raw body nowhere the model can read it.

### Why the plan does not add MimeKit

Composing a draft is one fixed shape — headers, a UTF-8 body, no attachments — so a small
`MimeCompose` with RFC 2047 encoded-words for non-ASCII subjects and CRLF line endings is about
forty lines and one test. The repo has no third-party dependency on this path today (raw
`HttpClient`, no Google SDK), and attachments are out of scope, which removes the usual argument for
a MIME library. If attachments are ever added, revisit this — hand-rolling multipart with
attachments is where the trade flips.

### Governing prior art

- `.agentic/decisions/0063-calendar-extracted-to-standalone-mcp-server.md` — the shape being copied,
  the origin-root requirement, the no-confirmation-gate position, the stateless OAuth front and its
  one-console-edit-per-client cost.
- `.agentic/decisions/0024-config-driven-remote-mcp-servers.md` — how Agent42 consumes this.
- `.agentic/decisions/0058-per-mcp-tool-selectable-tools.md` and `0064` — per-tool selection and its
  cold start.
- `.agentic/decisions/0067-mcp-results-seed-the-fetch-allowlist.md` — the setting to leave off.
- `.agentic/specs/plan-toolforge-mcp-slice1.md` — the precedent for an mcpserver feature planned
  from this repo, including its explicit decision that an ADR is out of scope.
- `.agentic/gotchas.md` — "An agent tool's RETURN value is persisted"; and the OTel path-redaction
  rule, which does not transfer today because mcpserver emits no OpenTelemetry at all, but which is
  exactly what a Gmail search query in a query string and message ids in paths would need if that
  ever changes.

## Steps

Steps 2 and 5 are independent of each other once step 1 lands and may be parallelized **only with
worktree isolation**; everything else is sequential.

**Step 0 — Branch.** In `Z:\Sources\mcpserver`, `git checkout main && git pull`, then
`git checkout -b feature/gmail-mcp`. *Verify:* `git log --oneline -1` shows `aed18d8` or later on
`main`'s line.

**Step 1 — Extract `Mcp.Google`.** Move `GoogleOptions`, `GoogleOAuthFront`, `GoogleTokenValidator`
and `CalendarCaller` out of `Calendar.Mcp`; introduce `GoogleScopeSet`; reorder `ApiBaseAddress`
into position 3 and drop its default; rename `GoogleScopes` to `CalendarScopes` and leave it in
`Calendar.Mcp`; update the `Calendar.Mcp`, `Mcp.Gateway` and `tests/Calendar.Mcp.Tests` call sites.
**A pure refactor: no behaviour change.**

*Verify:* `dotnet build McpServer.slnx` and `dotnet test McpServer.slnx` green, **and** the diff to
`tests/Calendar.Mcp.Tests/` contains only: identifier renames (`GoogleScopes` → `CalendarScopes`),
`using` lines, and the two call sites in `CalendarServer.cs` — the `GoogleTokenValidator`
construction and the `McpAuth.Bearer` argument. **If the meaning of any `Assert` changes, stop: the
move was not pure.** (The first draft said "unmodified except `using` lines", which trap 5 makes
impossible to satisfy.)

**Step 2 — `Gmail.Core`.** `GmailModel` (records, `GmailMessages`, `GmailException`), `GmailLimits`,
`MimePlainText` (the walk, base64url, charset), `MimeCompose`, then `GmailClient` over raw
`HttpClient`, mirroring `CalendarClient`'s single `SendAsync` and curated-exception shape, with the
403 reason branch of AC20. *Verify:* the pure unit tests that need no server — `GmailBodyTests`,
`GmailLimitsTests`, `MimeComposeTests`, `GmailCharsetTests`, `GmailJsonShapeTests` — written with
this step.

**Step 3 — `Gmail.Mcp`.** `GmailScopes`, `GmailStartup` (reads `GMAIL_OAUTH_CLIENT_ID` and
`GMAIL_OAUTH_CLIENT_SECRET`, falling back to `Gmail:ClientId` and `Gmail:ClientSecret`),
`GmailTools`, and a `Program.cs` shaped like `Calendar.Mcp/Program.cs` including its fail-closed
refusal when `--http` is passed unconfigured — that refusal is correct for the **standalone** server
and is not what D7 changes. **Delete the `appsettings.json` that `dotnet new web` scaffolds**: once
`Mcp.Gateway` references a second project shipping one, two same-named files in a single publish
output are a hard `NETSDK1152` failure, as the README already warns. The stdio token variable is
**`GMAIL_ACCESS_TOKEN`**. *Verify:* `dotnet build`, then a manual `--http --urls
http://127.0.0.1:5103` start with a dummy client pair and a `/health` request.

**Step 4 — `tests/Gmail.Mcp.Tests`.** `FakeGoogle` (an ephemeral loopback listener faking `/token`,
`/tokeninfo`, `/authorize` and `/gmail/v1/{**rest}`, recording what actually left the process),
`GmailServer`, `McpCall`, and the remaining test files. Reference `Calendar.Mcp` for AC4's second
direction. Add to `McpServer.slnx`. *Verify:* `dotnet test McpServer.slnx`, all green.

**Step 5 — Gateway, solution, Dockerfile.** Per D7: read the Gmail host and client pair, and build
the backend **only if both are present**. Extract that selection into a **pure, testable function**
returning the backend list, so AC21 can assert that an unconfigured Gmail leaves scraper and
calendar intact. Add `const int gmailPort = 5103`, the `Mcp.Gateway.csproj` project references, and
three `COPY` lines in the Dockerfile restore block. *Verify:* `dotnet test tests/Mcp.Gateway.Tests`,
then `docker build -t gmail-mcp --build-arg SERVER=src/Gmail.Mcp .` succeeds and the container
answers `/health`.

**Step 6 — Compose, env, README.** A `gmail-mcp` service publishing 5104, the new variables on
`mcp-gateway`, `.env.example` entries in the same non-blocking `${VAR}` shape the file already uses
(never the required-variable form — compose interpolates the whole file on load and would break
`docker compose up calendar-mcp` for anyone without a Gmail client), and README updates covering:
the tool table, the single `gmail.modify` scope and why not the full-access one, the Google Cloud
prerequisites including **a second OAuth client** and per-client redirect URI registration, the
Testing-status 7-day refresh token consequence, D7's optional-backend behaviour, and **the four
postures table** from "What the three send tools buy" including the fact that the default posture is
unsupervised send. Also update the Gateway `### Setup` step 2 and its `docker run`
example, "Adding another MCP server", and the Dockerfile run-example comment block. *Verify:*
`docker compose config` parses; README paths resolve under the repo's doc-path checker if it has
one.

**Step 7 — CI.** A new image build and smoke step in the docker job, mirroring the calendar's
"check it is the calendar, not the scraper": assert the Gmail server's discovery document declares
`gmail.modify`. **Leave the existing gateway step's environment unchanged** — that it still reaches
healthy without any Gmail variable is the CI-level proof of D7. *Verify:* the workflow runs green on
the PR, including the untouched gateway step.

**Step 8 — Manual, operator-side.** Not code, and it cannot be automated:
1. Create a **second** Google Cloud OAuth client (type Web application) with the **Gmail API**
   enabled, left in Testing.
2. Register each MCP client's redirect URI on it by hand — at minimum Agent42's
   `https://chat.agent42.nl/api/mcp/oauth/callback`, and `http://localhost:33418` for local testing.
3. Point DNS for the new subdomain at the gateway host **before** the gateway restarts — the
   certificate list derives from the backends array, and ACME fails for a name that does not
   resolve.
4. In Agent42: Preferences → MCP servers → add, connect, press **Test** (which is what makes the
   per-tool checkboxes appear at all), **then choose a posture from the table in "What the three
   send tools buy" and deselect the tools it excludes.** This step is not optional and not
   cosmetic: until it is done the server sits in the unsupervised-send posture, because
   `null == all`. Leave "seeds fetchable URLs" **off**.

**Step 9 — One Agent42 docs MR.** Add a `.agentic/gotchas.md` entry, or extend the ADR 0067 entry,
naming the Gmail MCP server as one whose seeding flag must stay off and saying why mail is the worst
case for it. This is the only place the warning reaches the person standing in front of the
checkbox. Separate branch, separate MR from the mcpserver work. *Verify:* the repo's doc-path check
passes.

## Tools

| tool | arguments | Gmail call(s) | units |
|---|---|---|---|
| `search_email` | `query?`, `label?`, `maxResults?` | `messages.list` plus capped `messages.get?format=metadata` | 5 + 20n |
| `read_email` | `messageId` | `messages.get?format=full` | 20 |
| `read_thread` | `threadId` | `threads.get?format=full` | 40 |
| `list_labels` | — | `labels.list` | 1 |
| `label_email` | `messageId`, `addLabels?`, `removeLabels?` | `messages.modify` | 5 |
| `archive_email` | `messageId` | `messages.modify` removing `INBOX` | 5 |
| `trash_email` | `messageId` | `messages.trash` | 20 |
| `draft_email` | `to`, `subject`, `body`, `cc?`, `bcc?`, `replyToMessageId?` | `messages.get?format=metadata` when replying, then `drafts.create` | 10–30 |
| `send_draft` | `draftId` | `drafts.send` | 100 |
| `send_email` | `to`, `subject`, `body`, `cc?`, `bcc?`, `replyToMessageId?` | `messages.get?format=metadata` when replying, then `messages.send` | 100–120 |

`draft_email` and `send_email` take the same arguments and share one `MimeCompose` call and one
reply-header lookup; they differ only in the endpoint they post the composed message to. Keep that
shared path shared — two composers would be two places for the RFC 2047 and CRLF rules to drift, and
AC12 asserts threading on both.

Every tool returns `Task<string>` of serialized JSON through a `Run<T>` wrapper that turns a
`GmailException` into `{ notice: "<curated message>" }`, exactly as `CalendarTools` does. Optional
parameters are nullable with `null` defaults and carry `[Description]`; `CancellationToken` is last.

`GmailLimits` — sized so the **serialized** result of every tool stays under 8,192 characters, and
truncated **at parse time** the way `CalendarLimits.Truncate` is applied inside `CalendarClient`:

| constant | value | worst-case serialized |
|---|---|---|
| `MaxMessages` (search) | 10 | ~6 KB at 10 × (150 subject + 200 snippet + ids and dates) |
| `MaxThreadMessages` | 8 | shares the body budget below |
| `MaxBodyLength` (single `read_email`) | 4,000 | ~4.5 KB |
| `MaxTotalBodyLength` (across a thread) | 5,000 | ~6 KB with 8 headers |
| `MaxSubjectLength` | 150 | |
| `MaxSnippetLength` | 200 | |
| `MaxLabels` | 60 | ~4 KB |
| `MaxRecipients` (per draft) | 10 | inbound guard |
| `MaxDraftBodyLength` | 20,000 | inbound guard, never returned |

`MaxTotalBodyLength` is the `WebToolLimits.MaxTotalPageTextLength` shape: a budget spent across the
thread rather than a per-message cap that multiplies.

## Acceptance criteria to test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | Protected resource metadata is served at the **root** well-known path and declares `gmail.modify` and a Gmail resource name | `ResourceMetadata_IsServedAtTheRootPath_AndDeclaresGmailModify` | `tests/Gmail.Mcp.Tests/GmailDiscoveryTests.cs` |
| AC2 | The authorize hop adds `access_type=offline` and `prompt=consent`, strips `resource`, and pins `client_id` to the **Gmail** client | `AuthorizeHop_AddsOfflineAndConsent_AndPinsTheGmailClientId` | `GmailAuthorizeHopTests.cs` |
| AC3 | The token proxy adds the **Gmail** `client_secret` and strips `resource` on both grants | `TokenProxy_AddsTheGmailSecret_AndStripsResource` | `GmailTokenProxyTests.cs` |
| AC4 | A token whose audience is the **calendar** client id is rejected by Gmail with 401, and the converse | `ATokenMintedForTheOtherGoogleClient_IsRejected` | `CrossClientTokenTests.cs` |
| AC5 | A token carrying only calendar scopes is rejected | `ATokenWithoutAGmailScope_IsRejected` | `GmailBearerValidationTests.cs` |
| AC6 | A Gmail error body echoing the search query, a recipient address and a subject never reaches the tool result | `AGoogleErrorBody_NeverReachesTheToolResult` | `GmailCuratedMessageTests.cs` |
| AC7 | **The serialized result of every tool, on a worst-case fixture, is under 8,192 characters** — the ceiling Agent42 applies by substring | `EveryToolResult_StaysUnderTheConsumerCeiling` | `GmailLimitsTests.cs` |
| AC8 | The plain-text walk prefers `text/plain` in `multipart/alternative`, skips parts with a filename, and decodes unpadded base64url | `PlainText_PrefersTextPlain_SkipsAttachments_AndDecodesUnpaddedBase64Url` | `GmailBodyTests.cs` |
| AC9 | `archive_email` sends `removeLabelIds: ["INBOX"]`, and `labelIds` goes on the wire as a **repeated** parameter | `Archive_RemovesInbox_AndLabelIdsAreRepeatedNotCommaJoined` | `GmailRequestTests.cs` |
| AC10 | `draft_email` calls `drafts.create` and makes **no** call to `drafts/send` or `messages/send` | `DraftEmail_CreatesADraft_AndSendsNothing` | `GmailDraftTests.cs` |
| AC11 | `send_draft` posts only the draft id; **`send_email` is the only tool that ever reaches `messages.send`**; and across the whole suite `FakeGoogle` records **no** request to `messages.delete` | `SendDraft_PostsOnlyTheId`, `SendEmail_IsTheOnlyPathToMessagesSend`, `NoRequestToMessagesDelete_WasEverRecorded` | `GmailSendTests.cs` |
| AC12 | A reply from **either** `draft_email` or `send_email` sets `threadId`, builds `In-Reply-To` and `References` from the parent's **`Message-ID` header** rather than the API id, and preserves the Subject | `Reply_ThreadsOnTheMessageIdHeader_NotTheApiId` (theory over both tools) | `GmailReplyTests.cs` |
| AC13 | Behind `X-Forwarded-Proto: https` the discovery documents mint `https://` URLs | `DiscoveryDocuments_UseHttpsBehindAProxy` | `GmailPublicOriginTests.cs` |
| AC14 | The `Mcp.Google` extraction changes no calendar behaviour | the existing suite green, with the diff limited as step 1 states | `tests/Calendar.Mcp.Tests/**` |
| AC15 | **The Gmail host is routed and certificated** — asserted at the container level, since the real backends array is a local in top-level statements and no unit test can reach it | the CI gateway smoke step | `.github/workflows/ci.yml` |
| AC16 | The full-access scope appears in **neither** discovery document **nor** the authorize hop's `scope` parameter — a runtime assertion, not a grep | `TheDeleteScope_IsNeverAdvertisedOrRequested` | `GmailScopeTests.cs` |
| AC17 | Two Google backends are never hosted in one `WebApplication` | `EachGoogleBackend_GetsItsOwnApplication` | `tests/Mcp.Gateway.Tests/BackendIsolationTests.cs` |
| AC18 | A `windows-1252` part decodes without throwing, falling back rather than failing | `AnUnknownCharset_FallsBackInsteadOfThrowing` | `GmailCharsetTests.cs` |
| AC19 | `internalDate` and `historyId` deserialize from JSON strings | `IntegerFieldsSerializedAsStrings_Deserialize` | `GmailJsonShapeTests.cs` |
| AC20 | A 403 branches on `error.errors[0].reason`: rate limits are retryable, `insufficientPermissions` is not | `A403_BranchesOnTheReason_NotTheStatus` | `GmailErrorTests.cs` |
| AC21 | **With no Gmail configuration the gateway still starts with the scraper and calendar backends** — D7 | `AnUnconfiguredGmail_LeavesTheOtherBackendsIntact` | `tests/Mcp.Gateway.Tests/BackendSelectionTests.cs` |
| AC22 | `send_email` posts a base64url `raw` to `messages.send`, and the recipients on the wire are the ones passed to the tool — not any value read from message content | `SendEmail_PostsBase64UrlRaw_WithTheGivenRecipients` | `GmailSendTests.cs` |

AC10, AC11 and AC21 are the three worth **mutation checking**: break the guard and confirm the test
actually fails. AC10 and AC11 are what keeps the three send tools distinguishable, which is the only
thing making the ADR 0058 postures selectable; AC21 is the whole of D7's.

`tests/Gmail.Mcp.Tests/GlobalUsings.cs` carries
`[assembly: CollectionBehavior(DisableTestParallelization = true)]`, as the calendar's does, because
`MCP_PUBLIC_ORIGIN` is process-global.

## Quality gates

Thresholds copied from `.agentic/quality-gates.yaml` at plan time. **Read the applicability column
before filling in a result — `mcpserver` is not Agent42 and does not carry the same tooling.**

| gate | threshold | measuring tool in `mcpserver`? |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | **No.** `check-new-code-coverage.mjs` and coverlet are Agent42's; mcpserver's CI runs `dotnet test McpServer.slnx` with no coverage collection. Report **SKIPPED**, never an estimate. |
| `cognitive_complexity_max` | 15 | **No.** `SonarAnalyzer.CSharp` is an Agent42 `GlobalPackageReference`; mcpserver's `Directory.Build.props` registers no analyzers at all. Report **SKIPPED**. |
| `vulnerable_packages` | 0 | Yes — `dotnet list package --vulnerable --include-transitive` runs locally. This plan adds **no package**, so the expected delta is none. |
| build, test, format | hard block | mcpserver CI runs `dotnet restore`, `dotnet build --configuration Release`, `dotnet test`, plus per-image docker smoke tests. |

Step 9's Agent42 docs MR is gated by Agent42's own CI, where the doc-path checker applies.

Two gates measure nothing in mcpserver. That is a fact about the repo, not a licence to estimate:
per hard rule 3, report SKIPPED with this reason. Whether mcpserver should adopt either is a
separate decision and is **out of scope here**.

## Out of scope

- **Attachments**, read or sent. `has:attachment` and `filename:` still work as search terms and
  filenames are listed, but bytes are never fetched or attached. This is what keeps `MimeCompose`
  small enough not to need a MIME library.
- **`messages.delete`.** It needs the full-access scope, is irreversible, and Google's own reference
  says to prefer trash. AC11 and AC16 pin its absence.
- **Push, `users.watch`, Pub/Sub, and any form of inbound trigger.** Agent42 already has a separate
  plan for inbound email (`.agentic/specs/plan-inbound-email-triggers-an-agent.md`) that
  deliberately does not use the Gmail API; the two must not be merged without a fresh round.
- **Gmail settings, filters, forwarding and delegation.** The `gmail.settings.*` scopes are not
  requested.
- **Multiple Google accounts per user.** One token, one mailbox, as the calendar does.
- **Moving the OAuth app out of Testing, verification, and CASA.** D4 accepts the weekly re-consent.
- **An ADR.** The precedent is ToolForge, also an mcpserver feature, planned from this repo with no
  ADR and with "an ADR" explicitly out of scope in its own spec. ADR 0063 exists because it deleted
  Agent42 code. Doc debt is paid by step 9 instead.
- **Any Agent42 code change.** Step 9 is documentation only. If code turns out to be needed, that is
  a separate MR with its own ADR consideration.
- **A confirmation gate on writes.** ADR 0063 records that the calendar has none and did not revisit
  it. D5's three tools are a **granularity** mechanism, explicitly not a gate: with `send_email`
  enabled there is nothing between an injected instruction and delivered mail. Adding a gate is a
  legitimate future ask and would be its own round, most likely an ADR, since it would be the first
  confirmation gate in this estate.
- **Adopting coverage or complexity tooling in mcpserver.**

## Change after the review

**The user added direct send on 2026-09-12, after the critic pass had been folded in.** D5 was
"draft by default with an explicit send tool"; it is now three tools, `send_email` among them. This
is a deliberate user decision, not drift, and the plan is updated rather than annotated — but the
security position moved with it and the executor must not carry the old framing forward:

- The old text said `users.messages.send` **must never be called**. It is now called, by exactly one
  tool. AC11 inverted accordingly: it no longer asserts absence, it asserts **exclusivity**.
- The old text leaned on a two-step path as though it were friction. With direct send there is a
  **one-call path from an injected instruction to delivered mail**, and the plan now says so
  plainly rather than implying a barrier that is not there.
- What survives is the ADR 0058 posture table. It is now the only control, which raises the stakes
  on step 8.4 from housekeeping to the thing that decides whether the server can send unsupervised.
- AC12 became a theory over both composing tools, and AC22 was added for `send_email`'s wire shape.
  `messages.delete` stays unreachable; that half of AC11 is unchanged.

Nothing else moved. The scope is still `gmail.modify` alone (D1 already covered sending), no new
package, no new endpoint, and the critic's three high-severity fixes are untouched by this change.

## Review

A `critic` pass ran against the first draft on 2026-09-12, reading both repositories. It returned
three high-severity findings, eight medium and six low. **All were accepted**; none were rejected.
What it could not break is listed at the end, because that is also evidence.

**Accepted and fixed:**

- **H1 — adding Gmail to the gateway would have made Gmail configuration mandatory for the whole
  gateway**, and would have broken the existing CI gateway step, which supplies only the scraper and
  calendar host variables. "Shaped like the calendar one" implies a third `RequireHost` and a third
  `IsConfigured` refusal, so an existing deployment restarting without Gmail variables would exit 2
  and take the calendar and scraper down. This was the most serious finding: it contradicted the
  plan's own step-6 reasoning about keeping compose non-blocking. Fixed by **D7**, with AC21 and an
  explicit instruction in step 7 to leave the existing CI gateway step's environment alone.
- **H2 — step 1's verification gate was unsatisfiable.** "Calendar tests unmodified except `using`
  lines" contradicted the plan's own trap about `McpAuth.Bearer`'s method group becoming a closure
  at every call site, one of which is in `tests/`. Fixed by naming the two permitted call-site edits
  and re-anchoring the gate on assertion *meaning*.
- **H3 — the proposed limits were roughly ten times the ceiling the consumer enforces.** Agent42
  truncates every MCP tool result at 8 KB of **serialized** output, by plain substring, so
  `read_thread` at 20 × 4,000 characters would have reached the model as JSON chopped mid-token. The
  first draft cited the persistence gotcha but never the binding limit. Fixed by resizing every
  constant against a serialized budget and by rewriting AC7 to assert the ceiling directly.
- **M1** `Mcp.Gateway.csproj` needs project references — a hard build failure, now an affected area.
- **M2** `GoogleScopes` had no disposition; it now stays in `Calendar.Mcp` as `CalendarScopes`.
- **M3** removing `ApiBaseAddress`'s default alone is **CS1737**; the record parameter must be
  reordered, now stated as trap 2.
- **M4** AC15 was unfalsifiable, because the real backends array is a local in top-level statements
  and the routing test fabricates its own. Restated honestly as container-level coverage, with the
  genuinely testable part (D7's selection) promoted to AC21.
- **M5** five of eleven API facts had no acceptance criterion. AC18, AC19 and AC20 added; the two
  that are wording obligations rather than behaviour are now marked as such.
- **M6** the send split was oversold. Chaining two tool calls is normal, so the split is not a speed
  bump on the injection path. Rewritten: what it buys is the ability to **withhold** send, with both
  honest qualifications (`null == all` grants it by default; the checkbox needs an activation or a
  Test press first).
- **M7** the claim that `users.drafts.send` takes only an id is probably wrong — its body is a Draft
  resource whose `message` is optional, so the API does permit compose-and-send. The narrowness is a
  property of our tool signature. Reworded, with an instruction to re-verify before it reaches a
  README.
- **M8** the `SeedsFetchableUrls` warning was being written into mcpserver's README while the
  checkbox lives in Agent42. Fixed by **step 9**, a small Agent42 docs MR.
- **Low** — the `dotnet new web` `appsettings.json` / `NETSDK1152` trap, the under-specified README
  and Dockerfile surfaces, the missing published compose port, AC11's structural half being better
  expressed as a recorded-requests assertion, AC16 being better as a runtime assertion than a grep,
  and AC4's second direction needing a `Calendar.Mcp` reference from the Gmail test project. All
  folded in.

**Added on the critic's prompting:** AC17, pinning the separate-`WebApplication` constraint that
makes the shared claim constant safe. The critic verified that separation holds today; it had no
test behind it.

**Confirmed, not changed:** the ADR question. The precedent is ToolForge — an mcpserver feature
planned from this repo, no ADR, "an ADR" explicitly out of scope in its own spec. ADR 0063 is not a
counter-example; it exists because it deleted Agent42 code. The critic's distinction between "no
ADR" and "no doc debt" is what step 9 answers.

**What the critic checked and could not break:** the `BackendRoute` shape on `main`, the Dockerfile
restore block line range, the README calendar section boundary, `.dockerignore` needing no change,
the `McpServer.slnx` line count, `Mcp.Google.csproj` working as a plain SDK project because
`Mcp.Hosting` already carries the framework reference, `HealthProbe` and `PublicOrigin` being
untouched, the `DisableTestParallelization` rationale transferring, the quality-gate numbers
matching `.agentic/quality-gates.yaml` verbatim with correct SKIPPED reasoning, ADR 0063's
origin-root requirement, and **no contradiction with any active ADR**.

## Where the implementation diverged from the plan

Executed 2026-09-12 against **Agent42** `main @ e42acd6` and **mcpserver** `main @ aed18d8` — both
exactly as the header names them, no drift. Steps 0-7 and 9 are done; step 8 is manual and is handed
to the operator. Every divergence below is deliberate and small; nothing in the Decisions section was
relitigated.

**Step 1's verification gate permits three call sites in `CalendarServer.cs`, not two.** The plan
named the `GoogleTokenValidator` construction and the `McpAuth.Bearer` argument. `MapOAuthFront` is a
third, because the `GoogleScopeSet` is passed to it **explicitly** rather than resolved from the
container. The alternative — reading it from `IEndpointRouteBuilder.ServiceProvider` — would have kept
the gate's letter and cost the repo's house style: `GoogleOptions` is passed explicitly at every call
site precisely so a composition root can be read without following a registration, and what a server
asks Google for is the last thing that should be invisible there. The gate's *substance* holds: the
calendar test diff is identifier renames, one `using` line, and three mechanical call-site edits, and
**no `Assert`'s meaning changed**.

**`CalendarTools.cs` was not in the affected-areas list but had to change.** It names `CalendarCaller`
six times, and that type moved and was renamed to `GoogleCaller`. Mechanical.

**Three constants exist that the sizing table does not list**, and AC7 is not satisfiable without
them: `MaxHeaderLength` (150), `MaxLabelNameLength` (60) and `MaxAttachmentNames` (10). The table
sized the large fields and folded the rest into "ids and dates", but a `From` display name is written
by whoever sent the mail and a label name is user-created, so both are unbounded in practice. The
plan's own worked figure for `list_labels` ("~4 KB at 60 labels") implies about 66 characters per
label, which is what `MaxLabelNameLength` makes true rather than assumed.

**A serialized backstop was added, and AC7 asserts both halves.** The caps count characters of text
while the ceiling counts characters of JSON, and the two are not the same number: a subject made
entirely of quotation marks doubles through the serializer, and the default encoder turns every
non-ASCII character into a six-character escape. Two things follow. `GmailJson.Options` uses relaxed
escaping, without which one line of ordinary Dutch costs six times its length; and
`GmailJson.Serialize` replaces a result that still would not fit with a curated notice, because a
document chopped mid-token is exactly what the ceiling exists to prevent. Measured worst cases:

| tool | serialized characters | ceiling |
|---|---|---|
| `read_thread` | 7,037 | 8,192 |
| `list_labels` | 6,792 | 8,192 |
| `search_email` | 6,604 | 8,192 |
| `read_email` | 6,208 | 8,192 |

**Tool results serialize camelCase.** Unplanned, and found by writing the tests: a search result
carried `ThreadId` while `read_thread` takes `threadId`, so every call chain asked the model to
translate between the two for nothing.

**Two files sit where the plan did not put them.** `GmailScopes` is its own file rather than part of
`GmailStartup.cs`, mirroring `CalendarScopes.cs`; and `Gmail.Core` has a sixth file, `GmailJson.cs`,
holding the shared JSON readers (AC19 needs them public to test), the serializer options and the
backstop.

**`search_email`'s `label` argument splits on commas.** The plan's tool table has a singular `label?`,
which alone could never demonstrate AC9's "repeated, not comma joined". Splitting is also the right
behaviour: a model writes `INBOX,UNREAD` readily, and forwarding that verbatim matches nothing and
returns an empty list **with no error** — the exact failure AC9 exists to catch.

**`MimePlainText` falls back to stripped HTML.** Not in the plan, and not optional in practice: a
great deal of real mail carries no `text/plain` alternative, and without the fallback those messages
read as having no body, which is indistinguishable from a broken tool. The `text/plain` preference and
the attachment skip run first and are unchanged.

**Two additions to `Gmail.Core` that the plan did not ask for, both budget- or safety-driven.**
Control characters are stripped from every returned text field (they cost six characters each when
serialized, and they carry terminal escape sequences written by a stranger); and the date returned is
`internalDate`, never the `Date` header, which is written by the sender and can say anything. The
second is what makes AC19 load-bearing rather than decorative.

**Step 9 also touched `CLAUDE.md`, not only `gotchas.md`.** One sentence on the ADR 0067 bullet. The
plan's reasoning for step 9 is that the warning has to reach the person standing in front of the
checkbox, and `CLAUDE.md` is read every session while a gotcha is read before touching an area.

### What was measured rather than assumed

- The mutation checks the plan asked for: AC10 (route `draft_email` to `SendMessageAsync` — fails),
  AC11 (route `send_draft` to `messages.send` — fails), AC21 (make the Gmail backend unconditional —
  five of seven selection tests fail). AC12 was mutation checked too, unasked: threading on the thread
  id instead of the parent's `Message-ID` header fails both halves of the theory.
- The standalone server was started live and its discovery documents, root well-known path, scope and
  authorize hop read off the wire; then the same against the built container.
- The gateway image was run with **the CI step's exact environment** — no Gmail variables — and
  reached `(healthy)` while logging `starting without the Gmail backend: …`. Run again with Gmail
  configured it binds 5101, 5102 and 5103 and logs nothing. This is D7's whole claim, and it is the
  one the naive implementation breaks silently.
- Every assertion in the new CI Gmail step was replayed locally against the running container.
- `docker compose config` parses with the `.env.example` values and with a `.env` holding none of
  them, which is the non-blocking `${VAR}` shape the plan insisted on.

### Still open

- **Step 8 is not done and cannot be automated.** The second Google Cloud **project** and its OAuth
  client, its redirect URIs, its test users, DNS for the new subdomain before the gateway restarts,
  and — the step that is neither optional nor cosmetic — adding the server in Agent42, pressing
  **Test**, and *choosing a posture* from the table in "What the three send tools buy". Until that is
  done the server sits in the unsupervised-send posture, because `null == all`.
- **Step 8.1 as written is not sufficient, and the README now says so.** The plan said "a second
  Google Cloud OAuth client"; a client is not the unit that carries this. The consent screen declares
  the scopes and holds the verification status, and it is **project-level**, shared by every client in
  the project — so a second client inside the calendar's project would declare the restricted
  `gmail.modify` scope for the calendar too, which is precisely the coupling D2 exists to prevent. It
  needs a second **project**. Test users are per project as well, and the plan never mentioned them at
  all: with a consent screen in Testing, an account that is not listed is refused without being told
  why, which reads as a broken server. Both are operator instructions; no code, scope, client pair or
  environment variable changes either way.
- **Nothing here has ever talked to the real Gmail API.** The suite runs against a fake Google, the
  same honestly-recorded gap the calendar has had since it shipped.
