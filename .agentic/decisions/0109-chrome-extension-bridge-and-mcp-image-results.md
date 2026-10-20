---
id: 0109
date: 2026-10-20
status: accepted
tags: [mcp, local-bridge, chrome-extension, browser-automation, websocket, security, prompt-injection, egress, images, vision, no-migration]
supersedes: []
extends: [0076, 0024, 0013]
amends: [0076, 0070, 0004, 0104]
---

# A Chrome extension as a second local-bridge client, and MCP image results shown to the model for one turn

## Context
The user asked how Claude in Chrome works and whether Agent42 could do the same. ADR 0076 already lets a production
conversation drive a browser on the user's machine, through a console helper that relays to BrowserOS neo, a separate
browser with its own profile. What was missing is the user's **real Chrome**, with no helper process. Claude in
Chrome also shows the model **screenshots**, and Agent42 could not: every MCP result was stringified and cut at 8 KiB.

Measured on 2026-10-20 (Chrome for Testing 155.0.8059.84, Node 22.22.2, ModelContextProtocol 2.2.0, OpenAI 2.14.0 /
MEAI 10.10.1 / AF 1.23.0 / Anthropic 12.58.0). The full table is in
`.agentic/specs/completed/plan-chrome-extension-bridge.md`, "Step 0 results":
- **declarativeNetRequest never modifies an extension's own WebSocket handshake**, under five rule variants, including
  one with no conditions. It does modify the extension's `fetch`, and broad rules put the header on **web pages'**
  sockets. A browser WebSocket cannot set `X-Api-Key` either.
- The extension's service worker and its pages send `Origin: chrome-extension://<id>`. Pages and their workers send
  their own origin.
- `chrome.debugger` **attaches to the extension's own pages and evaluates there with full `chrome.*`**.
  `scripting.executeScript` is refused there, and both are refused on `chrome://`.
- An MV3 worker with a 20 s app-level message heartbeat stayed one instance for 6 min 40 s.
- `McpClientTool` returns a `TextContent` for a text-only result and an **`AIContent[]`** for a multi-part one, whose
  `ToString()` is the type name. So `TruncatingMcpTool`'s `ToString()` measure **never truncated a multi-part result**,
  and an image passed through as `DataContent` and was **persisted** as a base64 data URI. That bug predates this ADR
  and affected neo's `screenshot` through the helper.
- An image inside a tool result reaches Responses as `input_image` and Anthropic as a `tool_result` image block, but
  **Chat Completions serializes it as JSON text**. An image in a user message after the tool results works on all three.

## Decision

### The extension (`tools/Agent42.ChromeExtension`)
1. **A second bridge client, not a new subsystem.** An MV3 extension whose service worker dials `/api/local-bridge`
   with the same per-server bridge key and speaks the same envelope protocol. It **is** the local MCP server: a
   hand-written JSON-RPC server per channel answers `server/discover` with −32601, then `initialize`, `ping`,
   `tools/list` and `tools/call`. The backend side is the unchanged 0076 path (registry, channels, `McpClient`,
   per-server/per-tool selection, revocation). Logic lives in chrome-free modules under `lib/`, tested by
   `node --test`, and `ChromeExtensionScriptTests` runs that suite inside `dotnet test`. `build-test` in
   `pr-checks.yml` now sets up Node 22.
2. **Auth: the key rides `Sec-WebSocket-Protocol`, only under the extension's Origin.** The extension offers
   `["agent42-bridge.v1", <key>]`. The endpoint reads the key from there **only** when `Origin` is exactly
   `LocalBridgeLimits.ExtensionOrigin`, and it echoes only the fixed name, never the key, which must not land in
   response headers or ingress logs. The id is stable because `manifest.json` carries a committed public `key`
   (`ExtensionId_MatchesManifestKey`). **This amends 0076's header-only rule.** `X-Api-Key` still works for the helper,
   and the query string stays refused.
3. **The Origin rule, for every client.** A request whose `Origin` is present and is not the extension's is refused
   with the uniform 401, **even with a valid key**, before the upgrade check. Web pages cannot forge `Origin`, and the
   helper sends none. **The Origin is not a credential**: the manifest key is public, and any non-browser client can
   send that header (measured with Node). It only keeps web pages out. The bridge key is the credential.
4. **Control frames on channel 0.** The backend numbers MCP channels from 1. `{"ch":0,"type":"ping"}` is answered with
   `{"ch":0,"type":"pong"}`, a ping on another channel is ignored, and both envelope copies parse the two types. The
   extension pings every 20 s, which keeps the MV3 worker alive (control frames do not count, and a page cannot see
   them), and reconnects when a pong is missing.
5. **A preflight, because a browser cannot see a 401.** Before each connect, a plain `fetch` GET with `X-Api-Key`. On
   401 it stops for good, as the helper does. On 400 it connects. Anything else backs off (1 s → 60 s). The
   `replaced by a newer connection` close reason stops it too.
6. **The approval gate is ported rule for rule** from `ConfirmationGate`: read-only without URL-shaped arguments
   passes, and everything else prompts in a popup window (`approve.html`) with *Allow once* / *Always for this tool* /
   *Deny*. It is serialized, re-checks after the lock, records the raw name, denies over 20,000 characters, denies
   after 60 s or on a closed window, sanitizes the text and renders it with `textContent` only. Like the helper's
   channel token, a closed channel or socket **cancels** a pending prompt: the window closes and the call never runs.
   A timeout closes the window too. Unlike the helper, the window also shows **which tab** (title and address) the
   call targets, since a ref or a tab id alone says nothing. "Always" lives in `chrome.storage.session`. Only the
   extension's own approval page may answer (`sender` check). The manifest declares no content scripts, no
   `externally_connectable` and no `web_accessible_resources`.
7. **User decisions (2026-10-20):**
   - **Any tab.** The agent may act on every open tab.
   - **`evaluate` runs without prompting.**
   - **Yolo** is an options-page checkbox, off by default and **persisted**. That diverges from 0076's launch-time-only
     `--yolo`, because an extension has no launch. Nothing over the socket can set it, because of rule 8.
8. **A hard floor no setting reaches, and it fails closed.** No tool, `evaluate` and yolo included, acts on anything
   but an http(s) page (outside the Chrome Web Store) or a bare `about:blank`. That refuses `chrome-extension://*`,
   `chrome://`, `devtools://`, `file:`, `data:`, `about:srcdoc` and unreadable addresses. A `blob:` is judged by the
   site that created it: `blob:https://bank.example/…` runs as the bank. A blank tab can inherit an opener's
   origin, so on one only `navigate` and `manage_tabs` run. Both the committed and a pending address are checked,
   before the gate and again at execution, because a tab can navigate while a prompt is open. Without the floor the
   model could script `approve.html` or `options.html` and approve itself (measured reachable). On top of it sits a
   user-editable **blocked-sites** list (default: the Agent42 host, following the address being configured), which
   binds `evaluate` too. `list_tabs` shows refused tabs by id only. A tab whose "being debugged" banner the user
   **dismissed** is refused by every tool except `manage_tabs` **until it is closed**. Not until it reloads: a page
   could reload itself to undo the user's choice.
9. **Tools:** `list_tabs`, `manage_tabs`, `navigate`, `snapshot`, `find`, `read_text`, `screenshot`, `click`, `click_at`,
   `type`, `key`, `scroll`, `console`, `evaluate`. Read-only: the reading tools, `screenshot` and `scroll`. Refs are
   per-tab nonce-tagged DOM attributes. Text results are capped at 6,000 characters with an explicit `offset` hint.
   Injected functions take only their arguments, because content scripts can read `chrome.storage.local`, where the
   key lives (`InjectedFunctions_NeverTouchExtensionApis`).
10. **Distribution:** `release-chrome-extension.yml` builds a zip on a `chrome-extension-v*` release, the same three
    triggers as the helper, draft first and published last (immutable releases). The manifest `version` gets the
    numeric core and `version_name` the full SemVer. The user unzips it and uses "Load unpacked". There is no Web Store.

### MCP image results (any MCP server, not just the extension)
11. **`McpToolResultShaper`** (called by `TruncatingMcpTool`) turns a multi-part result into one bounded string, which
    fixes the truncation bug above. Each image block, if it is an allowed type whose magic bytes match and at most
    1 MiB, is recorded in the run's `TurnContext` under the tool call id. The persisted text keeps a placeholder worded
    from what will happen: "attached for this turn only", or "omitted" when the run cannot show images (no call id,
    a closed context, a copilot chat, a no-vision model, a fleet run). A single `TextContent` keeps its old
    raw-when-it-fits behaviour.
12. **One vision rule, grain-owned.** `TurnContext.ToolImagesVisible` is set where the grain already computes
    `CanSeeImages()` (the chat core and the reminder path). At most 3 images are kept per run, the oldest evicted when a
    new one is recorded.
13. **`ToolImageChatClient`** inserts the run's images into the **request only**, as one user message after the whole
    tool-result group of an iteration (option (b), the shape all three adapters send as an image). It always clones,
    because the handed messages are the live session. It sits **innermost**: in `ChatClientFactory.Build` below
    `UseOpenTelemetry` (which records message contents), and in the single-client factory, so the test silos and
    DevHost run it. **Amends ADR 0004**: tool images never enter the session or Blob storage.

## Alternatives considered
- **DNR header injection (Path A).** It was the preferred path, and measurement killed it: it never touches the
  extension's own socket, and a broad rule leaks the key to web pages.
- **Key in the query string, or the subprotocol from any Origin.** Rejected: either lets a page that learned the key
  open the socket.
- **Grain-side `ToolImageChatClient`** (the critic's first suggestion, so one path serves all hosts). Rejected: it
  would sit above OTel and put base64 into every span. Factory-side in both factories gives the same coverage.
- **Images inside the tool result (option (a)).** Native on Responses and Anthropic, but garbage on Chat Completions.
  One path everywhere won.
- **Persisting tool images (as Blob attachments, like ADR 0004 uploads).** Rejected: retention, session growth and
  re-minting for something the model can re-take in one call.
- **Prompting for `evaluate`, or acting only on the agent's own tab group.** Offered and declined by the user.

## Consequences
- **Headline: arbitrary JavaScript in the user's signed-in Chrome, with no prompt, from every turn kind.** 0076
  amended ADR 0070 on the premise that "each write still meets the helper's prompt". With `evaluate` unprompted, that
  no longer holds: an `/api/agent-run` or `/api/agent-mcp` key holder, an unwatched reminder turn, or an **inbound
  email (ADR 0096)** can steer a model into running script in any open tab. A prompt-injected page can do the same.
  Under 0076 this needed a person to type `--yolo`, and here it is the default. The mitigations are the hard floor,
  the blocked-sites list, and **narrowing the bridge server's tools (ADR 0058)** for agents others can reach. The
  options page, the release notes and the bridge-key panel all say so.
- **Egress (ADR 0013 extension):** page text, tab titles, URLs, console output and **screenshots of signed-in pages**
  reach the model provider. More generally, **any MCP server's image results** now reach it as images rather than as
  truncated text.
- **Prompt caching (amends ADR 0104):** the instructions prefix is unaffected, but the history prefix is not. Turn N's
  request holds `[tool result][image]`, while turn N+1 replays it without the image. So each screenshot turn costs a
  history-cache miss on the next turn, and on Anthropic a cache write through the image that is never read back.
- **Foundry caveat:** `GetCapabilities` reports every `AzureOpenAI` deployment as vision-capable, so a non-vision
  Foundry deployment would be sent a tool image and may 400. That is recorded, not fixed.
- **Copilot and fleet** runs stay text-only (honest "omitted" placeholders).
- The helper's `BridgeEnvelope.cs` gained the two control types, so the merge cuts one functionally identical helper
  patch release.
- **No new package, secret, Cosmos container, persisted field or migration.** `sw.js` `CACHE` → `v68`.

## Verification
- **Tests:**
  - `node --test`: 66 tests over the gate (including cancel and timeout closing the prompt), bridge loop, MCP
    server, tools (including blob, blank, pending-address and dismissed-banner refusals, and the target shown to the
    reviewer), options and JPEG size, run by `ExtensionNodeTests_Pass`.
  - `ChromeExtensionScriptTests`: constants, manifest surface, injected functions and approval page.
  - `ChromeExtensionBridgeTests`: the real core over a fake browser, run by node against **real Kestrel** and the
    real `McpClient`. It lists, denies, auto-runs `evaluate`, pages, crosses a screenshot as an image, stops on a
    revoked key after exactly one reconnect, stops when replaced, and keeps the heartbeat alive.
  - `LocalBridgeEndpointTests` / `LocalBridgeKestrelTests` / `LocalBridgeContractTests`: the Origin rule, the
    subprotocol key, a key never echoed (real 101 headers), ping/pong.
  - `McpToolImageTests` / `McpToolImageGrainTests`: the shaper, caps, no mutation, the group insert, the OTel order,
    a vision grain turn reaching the model with no image bytes in the stored session, and the no-vision placeholder.
  - `ChromeExtensionReleaseTests`, and `BridgeKeyPanel_MentionsTheChromeExtension`.
- **Mutation checks:**
  - Breaking the gate's post-lock re-check turns exactly `calls queued behind the prompt…` red (50/51).
  - Unwrapping the single-client factory turns exactly `StoredSession_HoldsNoImageBytes_AfterAScreenshotTurn` red.
- **Live, on real Chrome (Chrome for Testing, headless) against DevHost:**
  - The real extension loaded under the pinned id, connected (badge `on`), and DevHost's Test listed 14 tools.
  - Through the real adapter on a real form page: `snapshot` showed refs and labels, with the password shown only as
    "(filled)"; `type` then `click` gave "Hello Ada"; console capture, scroll, key, screenshot (743×482 JPEG) and
    `click_at` all ran.
  - `evaluate` on the extension's own options page was refused, `list_tabs` hid that tab, and a message from a
    non-approval page got no request back.

## Unverified
- **The approval window in a real chat turn**, and a **real model describing a real screenshot** (DevHost's model is
  canned). Post-deploy: one chat that runs `list_tabs`, `snapshot`, an approved `click`, a denied `navigate`, a
  `screenshot` the model describes and a `click_at` on it, on one vision model per configured provider kind and one
  non-Foundry no-vision model.
- **Anthropic with two consecutive user messages** (a tool result, then the image message): the adapter emits it, and
  Anthropic documents merging consecutive turns, but no live call was made.
- **The ACA + Easy Auth upgrade with the extension's Origin and subprotocol.** 0076 already lists the plain upgrade as
  a post-deploy check. Add: `wss://chat.agent42.nl/api/local-bridge` with the extension's Origin and
  `agent42-bridge.v1, <key>` gives 101 with `Sec-WebSocket-Protocol: agent42-bridge.v1`.
- **Branded, headful Chrome.** Everything here ran on Chrome for Testing headless. The "being debugged" banner and
  its dismissal (`canceled_by_user`) were not exercised. The refusal after a dismissal is unit-tested over a fake
  adapter, and the `onDetach` reason that sets it was not observed on a real banner.
- **The release workflow** has not run (it runs only on main or a tag).
