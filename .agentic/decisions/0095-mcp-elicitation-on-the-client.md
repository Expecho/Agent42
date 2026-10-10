---
id: 0095
date: 2026-10-10
status: accepted
tags: [mcp, client-tools, elicitation, egress, security, frontend, no-migration]
supersedes: []
amends: [0024, 0076, 0089, 0013]
---

# MCP elicitation on the client: form and URL mode, routed per call, answered `cancel` when nobody can be asked

## Context
Agent42 connected to MCP servers with `McpClient.CreateAsync(transport, loggerFactory)` and no options, so it
declared no elicitation capability. A server that needs input mid-call (`elicitation/create`) failed with
"Client does not support elicitation requests." (measured on SDK 2.2.0). ADR 0089 had built the machinery to pause a
turn on the browser: a per-run `ClientToolBroker`, an interleaved result endpoint, recovery through
`pending-tool-calls`, and capped waits. Elicitation is the protocol's own human-in-the-loop channel.

The hard part is routing, not rendering. `UserMcpToolProvider` caches one live connection per **user**, shared by
every conversation of that user, so the SDK's handler cannot know which chat asked. The run's broker is reached only
through an `AsyncLocal` that `PullAsync` sets around the run's own pulls.

**Measured (spike, SDK 2.2.0, 2026-10-10).** A stateful streamable-HTTP server (the only HTTP shape where 2.2.0's
server-side `ElicitAsync` works; a stateless one throws "Elicitation is not supported in stateless mode.")
negotiates protocol **2025-11-25**, even when the client defaults to 2026-07-28. Elicitation then arrives as a
server-to-client request, and **the handler does not see the caller's `AsyncLocal`** (`handlerSaw=<null>`). Over a
stream transport (the local bridge, ADR 0076) the session negotiates 2026-07-28, and the `AsyncLocal` **does**
flow (`handlerSaw=SCOPE`). A -32042 surfaces from `McpClientTool.InvokeAsync` as
`ModelContextProtocol.UrlElicitationRequiredException`, with `Elicitations` carrying the URLs.

## Decision
Both connection factories build their client options with one helper, `McpElicitationClient.Options(router)`. It
declares form and URL elicitation and answers each request through the connection's `McpElicitationRouter`, which
travels to the grain on `McpServerTools.Elicitation`.

- **Routing.** `BuildAgent` wraps each selected MCP tool in `ElicitingMcpTool`, the **innermost** wrapper:
  `WithReauthNotice(WithUrlSeeding(WithElicitation(t)))`, so the reauth notice stays outermost (ADR 0081). Around
  each call the wrapper opens a router scope with the run's sink (built from `CurrentClientToolBroker` inside the
  call), the model's call id and the server's display name. The handler resolves in this order: (a) the router's
  own `AsyncLocal` scope, which works on the local bridge; (b) otherwise the **only** scope in flight on that
  connection, which is the remote-HTTP path; (c) otherwise `cancel`. Any overlapping call to the same server makes
  an HTTP elicitation `cancel`: another chat, a background run, or a parallel tool call in the same turn. This is
  honest, and a guessed routing could hand one chat's form to another chat's tab.
- **Who can be asked.** Only an interactive run whose browser advertised `mcp_elicitation` has a sink. A background
  run, a reminder, the agent-MCP surface (`interactive: false`), a Copilot chat (its tools run outside the run's
  async local, ADR 0089) and a browser that did not advertise all answer **`cancel`**. It is `cancel` and not
  `decline`, because the spec reserves `decline` for an explicit refusal by the user.
- **Wire.** The broker owns a **locked** `McpElicitationQueue`, because the handler enqueues from the SDK's thread.
  The tool calls keep their no-lock, grain-scheduler invariant. `StreamModelReplyAsync` starts each pull once
  (`PullAsync(...).AsTask()`) and races it against the queue's `Arrived` signal. Queued elicitations stream as a
  live-only `mcpElicitation` event: `toolCallId` is the waiting call, `text` is the server name, and `elicitation`
  is `{id, mode, server, message, schema?, url?, host?}`. `id` is a minted `elicit_<guid>` and never the server's
  own id. The answer is `{action, content?}` posted to the existing `tool-result` endpoint. Recovery lists a
  waiting elicitation as a `PendingClientToolCall` named `mcp_elicitation`. `ClientToolLimits.AdvertisableCapabilities`
  (`ToolNames` ∪ `mcp_elicitation`) is what the endpoint intersects with. `ToolNames` stays the set of model tools
  that Copilot and the tool catalog read.
- **Limits.** The wait is `ClientToolTimeouts.For("mcp_elicitation")` (the 120 s confirm wait). Timeout, Stop and
  the end of the turn each answer `cancel`. A closed queue refuses a new elicitation at once, so an SDK-thread wait
  never hangs. An answer over `MaxResultChars` becomes `cancel` and is never truncated, because cut JSON is corrupt.
  Form content is checked against the requested schema before anything is sent: declared keys only, types, ranges,
  lengths, enum members and `required`. A mismatch is `cancel` and a logged warning.
- **-32042.** The Backend's `TruncatingMcpTool` translates the SDK exception into the Agents layer's
  `McpUrlElicitationRequiredException`. `ElicitingMcpTool` shows each URL through the sink and returns a short
  result to the model that names the **host only**.

**Security.** URLs are accepted only as absolute `https:` (or `http:` to a loopback host) and are refused
server-side before any event. The page shows the host prominently and the full URL in monospace. It opens the URL
only on the user's click, with `noopener,noreferrer`, and never prefetches it. All server text is set with
`textContent`. The form shows who is asking and tells the user not to enter passwords or secrets. The model never
sees an elicitation URL.

**Egress (ADR 0013 extension).** On an explicit Submit, the typed form values go to an MCP server that **the user
configured**. It is neither always-on nor ungated: nothing is sent unless a human answers a specific request.

## Alternatives considered
- **A per-conversation `McpClient`.** Rejected. It costs one socket per chat, and the per-user cache exists to avoid
  that. Routing would be exact, but it would only move the problem: a reminder or a background run would still need
  a no-human answer.
- **Serialize calls per connection, so the fallback is never ambiguous.** Rejected by the user. Every parallel tool
  call and every second chat on the same server would pay latency to make a rare case exact.
- **Force protocol 2025-11-25 or 2026-07-28 per server.** Not available. The server picks the version, and on HTTP
  neither one carries the async local into the handler.
- **Elicitation as a model tool.** Rejected. The model would relay the user's answers, which may be secrets.
- **`decline` for headless runs.** Rejected, for the spec-semantics reason above.
- **A per-server "allow this server to ask me" toggle.** Deferred. It would be a new persisted `StoredMcpServer`
  field. Revisit when a server abuses elicitation.

## Consequences
- Interactive chats can answer servers that elicit. Local-bridge servers route exactly. On remote HTTP servers, a
  call that overlaps another call to the same server gets `cancel` (user-visible flakiness, accepted).
- **No migration.** Nothing is persisted: the event is live-only, and only the tool's eventual result enters the
  session, as before. `AgentStreamEvent` gains a trailing `[Id(18)] Elicitation`.
- `sampling/createMessage`, roots and `notifications/elicitation/complete` stay out of scope. Sampling is a
  model-egress decision of its own.
- Device check: a form and a URL request in DevHost against a local MCP server that elicits.
