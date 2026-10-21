---
id: 0096
date: 2026-10-10
status: accepted
tags: [agents, stored-agents, email, mailgun, inbound, excluded-paths, security, credentials, prompt-injection, orleans, no-migration]
supersedes: []
amends: [0013, 0022]
---

> **Amended by [ADR 0111](0111-past-chats-as-a-model-tool.md) (2026-10-21).** A new key, `past_chats`, lets the model search
> and read the owner's other conversations, and it is ON for an agent bound with `null == all`. Add it to the
> mitigation below: point email at an agent whose `EnabledTools` excludes `past_chats` as well as `memory` and the fetch
> tools, or an emailed instruction can make the agent search the owner's chats and send them out through those
> channels. The editor note now names it. There is no code gate on this door (0111 records that choice).

# Inbound email triggers a stored agent (Mailgun Routes → `POST /api/email-in`)

> **Renumbered from 0095 on 2026-10-13.** It merged (PR #185) as a second ADR 0095 beside
> [0095](0095-mcp-elicitation-on-the-client.md) (MCP elicitation, which merged first and keeps the number).
> Commit messages and PR #185 still say "ADR 0095" for this decision.

## Context
The owner wants to send or auto-forward an email to an agent and have it run, attachments included, with no browser
and no login. ADR 0022 already built this door for HTTP (key → directory grain → owner, `excludedPaths`, multipart,
background turn). This is a second instance of that shape with the credential carried in the address. The app has
one HTTPS ingress and cannot run SMTP, so a provider must turn mail into a webhook. Untrusted text now reaches an agent
from anyone who learns an address, which is a new **inbound** vector for ADR 0013's egress set.

## Decision
Each stored agent gets one address, `<token>@<Email:InboundDomain>` (`in.agent42.nl`). A Mailgun Route on that
domain forwards each message to `POST /api/email-in` (exact `excludedPaths` entry), which verifies the Mailgun HMAC,
resolves the token to `(userId, agentId)` and runs that agent in a **new** listed conversation. Nothing is emailed back.

- **D1: inbound only.** The reply lands in the UI and the ADR 0028 push. If replies are ever added they go only to the
  owner's Easy-Auth-verified address, never to the inbound `From`: replying to the sender would make the app an
  attacker-steered relay.
- **D2: Mailgun Routes**, because Mailgun HMAC-signs the forward, which is a check independent of the token.
- **D3: the address token is the only credential.** No `From` allowlist: auto-forwarding keeps the original `From` and
  fails SPF, so an allowlist would reject exactly the hands-free case.
- **D4: one address per agent, a new conversation per email.** Mail would reach ADR 0016's 2 MB grain faster than typing.
- **A separate directory grain** (`IEmailAddressDirectoryGrain`), never `IAgentKeyDirectoryGrain`. Both resolve by hash
  alone, so a shared directory would make every address a live `/api/agent-run` and `/api/agent-mcp` key.
- **A case-insensitive token:** `agent-` + 32 lowercase base32 characters (160 bits). `EmailAddress.Hash` lowercases
  before hashing. Local parts are not case-preserving in transit, so base64url (ADR 0022's alphabet) would 401 every
  message while the UI looked right.
- **The token is stored raw** in `AgentDefinition [Id(12)] EmailToken` (nullable, no migration). The owner must be able
  to read the address again, which is ADR 0035's share-token reasoning. It is a cleartext credential in grain state, so
  **regenerate is the only revocation**. The registry syncs the email directory on create, regenerate (old hash
  deregistered first) and delete, in the ADR 0022 orders.
- **Signature check (`MailgunWebhook`):** HMAC-SHA256 over `timestamp+token`, hex, compared with
  `CryptographicOperations.FixedTimeEquals`. This is unlike `ApiKey`, whose hash-keyed lookup has no timing channel.
  The HMAC does **not** cover the body, so an in-process seen-token cache (`MailgunReplayGuard`) refuses replays. The
  timestamp window is 8h15m: Mailgun retries for 8 hours and does not document whether retries are re-signed.
- **Status codes follow Mailgun's retry rule** (only 200 is success, 406 is never retried, anything else is retried
  for 8h):
  - **200** accepted, never 202, or every email would run up to seven times.
  - **503** with no signing key: never "skip verification", and a retry delivers once the key is set.
  - **401** for a bad, stale or replayed signature: only a key rotation produces one genuinely, and a retry can fix it.
  - **406** for every rejection of a signed message: unknown or deleted address (identical answers), or nothing
    readable. Saying 406 rather than 401 there leaks nothing, because only a signing-key holder gets that far.
- **Prompt:** `From`, `Subject`, then `body-plain` (`stripped-text` only as a fallback; stripping removes the forwarded
  thread). An HTML-only body is noted, not read (no HTML converter, ADR 0066). The total is capped at 32,000 characters.
  The recipient address is never in the prompt.
- **An unusable attachment is skipped and noted in the prompt**, not a whole-batch rejection as on `/api/agent-run`.
  Diverges from the plan's AC7: the sender gets no reply, so a rejected email would vanish without trace. Both doors
  share one per-file rule (`AgentRunFileError`).

## Alternatives considered
- **SendGrid Inbound Parse.** No signature on the forward, so the token would be the endpoint's only check.
- **Cloudflare Email Workers.** Puts feature code in another repo and deploy pipeline.
- **Azure Communication Services inbound.** The least-proven receive path of the four.
- **Reusing `IAgentKeyDirectoryGrain`.** No new grain type, but an exposed address becomes a run/MCP key (above).
- **Storing only the hash (ADR 0022).** The address could then be shown once only, which is useless for setting up a
  forwarding rule months later.
- **A 5-minute timestamp window.** A tighter replay bound, but an hour of 503 before the key is set would lose mail
  permanently if retries are not re-signed. The seen-token cache covers replay instead.
- **Rejecting a bad attachment with the whole email (agent-run's rule).** Automation can retry; a mail sender never
  learns, so the owner would lose the message silently.

## Consequences
- **A permanently exposed inbound door.** An address ends up in headers, logs and contact lists, and whoever holds it
  can push instructions and documents into the agent. That text composes with:
  - **ADR 0056 memory** (no delete path, so an injection retained once is recalled into every later conversation),
  - the ADR 0011/0027 fetch and search exfiltration channel and ADR 0067's seeded fetch allowlist,
  - **ADR 0094's outbound webhook**: an emailed turn's reply is POSTed to the agent's webhook with no human in the loop.

  The mitigations are rotation and pointing email at an agent whose ADR 0021 `EnabledTools` excludes `memory` and the
  fetch tools. The editor note says so. The Mailgun HMAC does **not** mitigate any of this: it authenticates the
  transport, not the sender.
- **The body is read before anything is authenticated**, because the signature is inside the form. The request is capped
  at 30 MB (`EmailLimits.MaxRequestBytes`), by a `Content-Length` pre-check and by Kestrel and form-reader limits on the
  route, so an unauthenticated caller cannot make the app buffer more than that.
- **A 500 midway through starting the run can duplicate the email.** The replay token is released on an unexpected
  exception so Mailgun's retry is accepted. If the throw came after the conversation was listed, the retry creates a
  second conversation. Losing the email instead would be worse.
- The replay guard is in-process (rule 12, single replica). It is empty after a restart, which leaves the token as the gate.
- A document that fails ingestion (no embedding model) 406s the whole email, because files are already stored by then.
  The only trace is a warning log.
- Agents created before this have no address until the owner regenerates (no backfill, as with ADR 0022 keys).
- **Manual prerequisites, outside this repo:**
  - an MX record for `in.agent42.nl` pointing at Mailgun, on a subdomain because the apex may carry real mail. DNS is
    at the registrar; `infra/main.bicep` only binds the ACA custom domain;
  - a Mailgun Route matching that domain and forwarding to `https://chat.agent42.nl/api/email-in`. The URL must not end
    in `mime` or `json`, which change the payload;
  - the `MAILGUN_SIGNING_KEY` GitHub secret (the HTTP webhook signing key).
- Measured only against Mailgun's docs and a DevHost curl run, not a real delivery. The first live delivery should
  confirm the field names and whether `recipient` ever carries more than one address (the handler tries up to five).
