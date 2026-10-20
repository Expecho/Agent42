planned-against: main @ 38a3ffb

# Plan: an Agent42 Chrome extension as a second local-bridge client

## Overview

Agent42 conversations can already drive a browser on the user's machine through the local bridge (ADR 0076).
Today that means the `Agent42.LocalBridge` console helper relaying to BrowserOS neo, a separate browser with
its own profile. The user wants something that works like Claude in Chrome: an MV3 **Chrome extension** that
connects *out* to `/api/local-bridge` itself and acts as the local MCP server, driving the user's **real
Chrome profile**, with no helper process and no neo. To the backend it is just another bridge helper on the
same per-server bridge key, so almost all of 0076 (registry, channels, the MCP client path, gating,
truncation, revocation) is reused unchanged.

**Second part (user request, 2026-10-20): MCP image results reach the model.** Claude in Chrome's
screenshot tool returns an MCP `image` content block, and Claude Code hands it to the model as a real image.
Agent42 today stringifies every MCP result and cuts it at 8 KiB, so an image becomes garbage. This plan adds
a backend path that pulls images out of any MCP tool result and shows them to a vision model **for the
current turn only**. That serves the extension's `screenshot` tool and every other MCP server that returns
images. Neo's `screenshot` is included only if its images fit the caps, which Step 0(g1) measures.

## Affected areas (verified against the tree at 38a3ffb)

- **New:** `tools/Agent42.ChromeExtension/`: manifest, service worker, options/popup/approval pages, `lib/`
  pure modules, `test/` node tests. It is never deployed, only released.
- **Backend (small):** `src/Agent42.Backend/Mcp/LocalBridgeEnvelope.cs` (control frames),
  `src/Agent42.Backend/Mcp/LocalBridgeSession.cs` (`RouteAsync`: answer ping), `src/Agent42.Backend/Mcp/LocalBridgeEndpoint.cs`
  (Origin rule; subprotocol key only on auth path B), `src/Agent42.Backend/Mcp/LocalBridgeLimits.cs` (constants).
- **Helper copy of the envelope:** `tools/Agent42.LocalBridge/BridgeEnvelope.cs` (must keep matching:
  `Envelope_WireFormatsMatch`), `tools/Agent42.LocalBridge/BridgeClient.cs` (ignore control frames).
- **UI:** `src/Agent42.Backend/wwwroot/js/mcp.js` (`showBridgeKey` panel: mention the extension),
  `src/Agent42.Backend/wwwroot/index.html` if the panel markup lives there, `src/Agent42.Backend/wwwroot/sw.js` (`CACHE` bump, currently `agent42-shell-v67`).
- **MCP image results (backend):** `src/Agent42.Backend/Mcp/McpConnectionFactory.cs` (`TruncatingMcpTool`:
  extract images before truncating), `src/Agent42.Agents/Mcp/McpToolLimits.cs` (image caps),
  `src/Agent42.Agents/Conversations/TurnContext.cs` (the per-run image list, cross-cutting rule 6), a new
  `DelegatingChatClient` in `src/Agent42.Backend/Models/` (exemplars: `PromptCacheKeyChatClient.cs`,
  `AnthropicCompatibilityChatClient.cs`), `src/Agent42.Backend/Models/ChatClientFactory.cs` (where those are
  layered), and `src/Agent42.Agents/Models/IChatClientFactory.cs` (`ModelCapabilities.SupportsVision`).
- **CI:** new `.github/workflows/release-chrome-extension.yml`; `.github/workflows/pr-checks.yml` (`setup-node` in `build-test`).
- **Tests:** `tests/Agent42.Tests/LocalBridgeContractTests.cs`, `LocalBridgeEndpointTests.cs`,
  `LocalBridgeKestrelTests.cs` / `LocalBridgeTestSupport.cs`, `McpServersAssetsTests.cs`, and new
  `ChromeExtensionScriptTests.cs` + `ChromeExtensionBridgeTests.cs` + `ChromeExtensionReleaseTests.cs`.
- **Docs:** `docs/features/remote-mcp-servers.md` (local bridge section), `.agentic/gotchas.md`, a new ADR,
  and the CLAUDE.md feature-map row "MCP client …" (add the new ADR number).

## Context & constraints (judgment the executor cannot cheaply re-derive)

### Governing ADRs and gotchas

ADR 0076 (read all of it, including both amendments), 0024, 0042, 0058, 0067, 0013 (egress). Gotchas:
"The bridge key is accepted from the `X-Api-Key` HEADER only", "A local-bridge key hash is GRAIN-OWNED",
"Nothing in a browser tab can relay to BrowserOS neo", and the Easy Auth `excludedPaths` entry. Exemplars:
the helper (`tools/Agent42.LocalBridge/`) for every behaviour the extension mirrors, and
`tests/Agent42.Tests/LocalBridgeTestSupport.cs` (`LocalBridgeTestHost`) for real-Kestrel tests.

### Investigation findings

1. **Auth is the crux.** `LocalBridgeEndpoint.HandleAsync` reads the key from `X-Api-Key` only, and the
   gotcha says why: browsers cannot set headers on a WebSocket, which keeps every web page out. An
   extension's `new WebSocket()` cannot set headers either. Two paths:
   - **Path A (preferred, no auth change):** the extension adds the header with a `declarativeNetRequest`
     **session** rule (`modifyHeaders`, `set X-Api-Key`, `resourceTypes:["websocket"]`, URL-filtered to
     `<server>/api/local-bridge`). **The rule must match only the extension's own requests**
     (`tabIds:[-1]`, plus `initiatorDomains:[chrome.runtime.id]` if Chrome honours it for a service-worker
     request). Otherwise *any web page* that opens a socket to that URL in the user's Chrome would get the
     key attached, which is precisely the hole the header-only rule closes. Step 0 must prove both halves.
   - **Path B (user-approved fallback):** the key travels as a `Sec-WebSocket-Protocol` value, accepted
     **only** when `Origin` is exactly `chrome-extension://<pinned id>`. Web pages cannot forge `Origin`.
     The server must echo a chosen subprotocol on accept, or the browser fails the handshake, **so never
     echo the key** (critic M3): the extension offers `["agent42-bridge.v1", <key>]`, and the server reads
     the key from the second value and echoes only `agent42-bridge.v1`. A test asserts the key appears in
     no response header. This amends 0076 and its gotcha, recorded in the new ADR.
   - **The Origin pin is not authentication** (critic L4). The manifest `key`, and so the extension id, is
     public. Any non-browser client can send that Origin. The pin only keeps *web pages* out, and the bridge
     key remains the credential. The ADR says so.
2. **A stable extension id is needed** (Path B's pinned Origin and the Origin rule below). An unpacked
   extension's id derives from its folder path unless `manifest.json` carries a `key`. Commit a generated
   public key in `key` and never commit the private key (unpacked loading does not need it). Record the
   resulting id as a constant in the backend (`LocalBridgeLimits`) and in a test.
3. **Origin rule (both paths, defence in depth).** The helper sends no `Origin`, and the extension sends
   `chrome-extension://<id>` (Step 0 confirms the exact value). The endpoint should therefore refuse any
   request whose `Origin` is present and is not that extension origin: a web page's socket is refused even
   with a valid key. It stays the same uniform 401 and `RejectionBody`, decided **before** the upgrade
   check, as today.
4. **The browser cannot see a 401.** A failed WebSocket handshake surfaces as close code 1006 with no
   status. The helper's "401 → stop, never retry" rule (0076) therefore needs a **preflight**: a plain
   `fetch` GET to `/api/local-bridge` with `X-Api-Key` set by the extension (fetch can set headers). 401 →
   stop and show "key rejected". 400 ("only accepts a WebSocket upgrade") → the key is good, so connect.
   **Anything else** (5xx or 502 during an ACA revision swap, a network error, a redirect) → backoff retry
   (critic L3).
   The `replaced by a newer connection` close reason **is** visible in `onclose.reason`: stop on it, as
   `BridgeExit.Replaced`. Measure in Step 0 whether the preflight's 400 body is what the endpoint returns
   today.
5. **MV3 service-worker lifetime.** The worker is suspended after ~30 s idle. Since Chrome 116, WebSocket
   *message* traffic extends its lifetime. Control frames (the server's ping/pong) do not count, and the
   page cannot see them either. So the extension sends an **app-level** heartbeat every 20 s. Backend
   channels are numbered from 1 (`++_nextChannel`), so **channel 0 is free for control**: the extension
   sends `{"ch":0,"type":"ping"}` and the backend answers `{"ch":0,"type":"pong"}`. A missing pong within
   20 s → the extension closes and reconnects (dead-path detection the browser otherwise lacks). Today an
   unknown type is a "malformed frame" logged at Information, so the envelope must learn the two types,
   or the log fills at one line per 20 s. A `chrome.alarms` alarm (1 min) restarts the loop if the worker
   was killed anyway.
6. **8 KiB result cap.** `TruncatingMcpTool` (`src/Agent42.Backend/Mcp/McpConnectionFactory.cs`) cuts every MCP
   result to `McpToolLimits.MaxToolResultLength` (8 × 1024 **characters**). It measures `raw.ToString()`,
   which is the compact JSON of the **whole result element** (`{"content":[{"type":"text","text":…}],…}`
   with escaping), not the content text. **Today a screenshot can never reach the model** through this
   path. Step 4b fixes that by extracting `image` blocks **before** the text measure, so only the *text*
   part of a result is subject to the cap below. The extension caps text results on
   `JSON.stringify(result).length` (target ≤ 7,500,
   leaving margin for `System.Text.Json` escaping non-ASCII and HTML-sensitive characters as `\uXXXX`,
   which `JSON.stringify` does not do). Step 0(f) measures the inflation, and the cap logic must count the
   escaping the backend will do, not trust a raw-text budget. Every capped result ends with an explicit
   `offset`/"N more characters" paging hint, so truncation never silently eats the tail.
7. **SDK handshake.** MCP SDK 2.2.0 opens with a `server/discover` probe and waits 5 s for it unless
   answered (0076 Step 0). The extension's hand-written JSON-RPC server must answer `-32601`, then serve
   `initialize` (echo the client's `protocolVersion` if supported, else the newest it supports),
   `notifications/initialized`, `ping`, `tools/list` (with `annotations.readOnlyHint`), and `tools/call`.
   Step 0 confirms SDK 2.2.0 accepts the `initialize` answer.
7b. **How the image reaches the model (design for Step 4b, the spike decides the mechanism).** Agent42
   already keeps image bytes out of the 2 MB session (ADR 0004/0020): attachments live in Blob storage, and
   the session holds a `UriContent` SAS link that `AgentGrain.Attachments.cs` re-mints and strips for no-vision
   models. Tool screenshots take a **simpler** route that needs no Blob, no retention and no re-minting:
   - **One vision rule, owned by the grain (critic-2 H1/M1).** At run entry the grain sets
     `TurnContext.ToolImagesVisible` from its existing `CanSeeImages()` (`AgentGrain.Copilot.cs`:
     `!_copilotChat && …SupportsVision`). It is false for Copilot chats and agents, no-vision models,
     `Closed`, background contexts without a model decision, and fleet runs (which have no `TurnContext`).
   - `TruncatingMcpTool` removes `image` blocks from the result. When `ToolImagesVisible` is true **and**
     the call id (`FunctionInvokingChatClient.CurrentContext?.CallContent.CallId`, the `ElicitingMcpTool.cs`
     precedent) is non-empty, it records the bytes in the run's `TurnContext` keyed by that id, with the
     placeholder `[image N: image/jpeg 1280×720, attached for this turn only; only the latest 3 are sent]`.
     Otherwise it drops them, and the placeholder reads `[image omitted: this model cannot be shown
     images; use snapshot/read_text]`. The persisted placeholder is therefore never a false claim. An empty
     call id (the Copilot path, where there is no function-invoking client) is always "omitted". **Never a
     grain field** (rule 6). Two runs can share an activation. The list is capped **at record time** at
     `MaxImagesPerRun`, evicting the oldest (critic-2 L6).
   - A new `ToolImageChatClient : DelegatingChatClient` sits **below** the Agent Framework's function-invoking
     client **and below `UseOpenTelemetry`**. OTel runs with `EnableSensitiveData = true` and exports to
     App Insights and `/logs` (ADR 0077), so above it, up to 3 MiB of base64 would land in every span
     (critic-2 L1). It is therefore factory-side: in `MultiProviderChatClientFactory.Build`, directly
     around the provider client (outside the Anthropic shim, which keeps `image/*` `DataContent`), **and**
     in the single-client factory's `GetClient` (`src/Agent42.Agents/Models/IChatClientFactory.cs`). Test silos and DevHost then run it too,
     so the grain tests are not vacuous (critic-2 H1). It inserts nothing unless images were recorded,
     so the Copilot twin and every non-vision run are no-ops by construction.
   - **It always clones (critic-2 H2).** The messages it is handed are the grain's live session objects
     (the `AnthropicCompatibilityChatClient` doc comment and its gotcha). It builds a **new** list, and for
     option (a) new `ChatMessage`/`FunctionResultContent` objects. It never edits `Contents` or `Result` in
     place, or base64 would be persisted into the 2 MB Cosmos item.
   - On each model call it finds tool-result messages whose call ids have recorded images and inserts the
     image as `DataContent` (inline base64). Option (b) goes **after the whole tool-result group** of that
     iteration, never between two tool results (Chat Completions needs them contiguous after `tool_calls`,
     and Anthropic needs `tool_result` blocks first; critic-2 L2).
     Option (a) is inside the `FunctionResultContent`, where the provider supports images in tool results
     (Responses `function_call_output` → `input_image`, Anthropic `tool_result` image blocks). Option (b) is
     a synthetic user message (`"Screenshot returned by <tool>:"` + `DataContent`), which works on every
     vision provider, Chat Completions included. Step 0(g) picks per provider. (b) everywhere is
     acceptable if (a) needs adapter work.
   - **Current turn only.** The inserted content lives in the request, never in the session. The function
     loop's own message list holds the placeholder text, and only that is persisted. The bound: at most
     `McpToolLimits.MaxImagesPerRun` (proposed 3), the **latest** ones, each ≤ `MaxImageBytes` (proposed
     1 MiB decoded), and only `image/png|jpeg|webp|gif`. Anything else is dropped with a note in the
     placeholder. A later turn sees the placeholder text only, which is accepted: the model can screenshot
     again. It also means incognito (ADR 0034) needs nothing special, since nothing is stored.
   - **No-vision model**: `ToolImagesVisible` is false, so the image is dropped at tool time with the
     honest placeholder.
   - **Foundry caveat (critic-2 M2):** `GetCapabilities` returns `ModelCapabilities.Full` for every
     `AzureOpenAI` deployment (`ChatClientFactory.cs`), so a Foundry deployment without vision would get
     an image it triggered itself, and the turn may fail with a provider 400. Step 0(g2) measures that
     failure. The ADR records the limitation, and the no-vision manual check uses a non-Foundry provider.
     `SupportsVision`'s doc says "an image supplied as a URL", but this path sends inline base64; widen
     the doc comment.
   - **Copilot agents/chats (0085/0107)** do receive MCP tools (`AgentGrain.Copilot.cs` →
     `CopilotFunctions`), but with no function-invoking client, so the call id is empty. The image is
     omitted with the honest placeholder, which a test pins. **Fleet runs (0097)** have no `TurnContext`,
     so the image is likewise omitted.
   - **Prompt-prefix caching (ADR 0104), an honest account (critic-2 M3):** the instructions/system prefix
     is unaffected, but the *history* prefix is not. Turn N's request holds `[tool result][image]`, while
     turn N+1 replays the history without the image, so the cache diverges at the first screenshot. Every
     screenshot turn costs a guaranteed history-cache miss on the next turn, and on Anthropic an automatic
     cache write through the image that is never read back. Evicting the oldest image mid-loop shifts the
     prefix within a turn too. The new ADR records this as an **amendment to 0104**. The test is named for
     what it proves: `InstructionsPrefix_IsUnchangedByAnImage`.
   - **Egress (rule 13, critic-2 L4):** **any** MCP server's image results now reach the model provider,
     not just the extension's screenshots of signed-in pages. The new ADR records this as an ADR 0013
     extension in those generic terms.
8. **JS test harness.** There is no `package.json` in the repo, and `wwwroot` JS is guarded by C# source-text
   tests. But C# tests already spawn `node` (`ProtectMainHookTests`, `DocPathCheckerTests`). **However,
   the `build-test` job in `.github/workflows/pr-checks.yml` has no `setup-node`** (only the coverage and
   docs jobs pin 22), so Step 2 adds `actions/setup-node` with node-version 22 to `build-test`, and
   `ChromeExtensionScriptTests` asserts the Node major version is ≥ 22 with a clear message. So the
   extension keeps its logic in **chrome-free ES modules under `lib/`**, tested with `node --test` and run
   from an xunit test, so `dotnet test` (and CI) runs them. No npm dependencies, matching the no-build
   frontend (ADR 0041). The 80 % new-code coverage gate measures C# only (coverlet), so it covers the
   backend changes. JS coverage is not gated and must not be claimed.

### User decisions (interview, 2026-10-20)

- **Tab scope: any tab.** The agent may list and act on every open tab, not just a tab group it owns.
- **`evaluate` (arbitrary JS in a page) ships in v1 and always runs WITHOUT prompting.** This is a
  deliberate exception to the 0076 gate (an unhinted tool prompts). Combined with "any tab", a
  prompt-injected page can run script in any signed-in tab with no confirmation. **The reach is wider than
  one interactive chat** (critic H2): 0076 amended ADR 0070 on the premise that "each write still meets the
  helper's prompt" for `/api/agent-run` and `/api/agent-mcp` key holders and reminder turns, and ADR 0096
  added an untrusted inbound-email door into agent turns. With `evaluate` unprompted, an emailed instruction,
  an agent-run key holder or an unwatched reminder turn can run arbitrary JS in the owner's real Chrome
  profile. Under 0076 that needed a person to type `--yolo`; here it is on by default. The ADR must state
  this as the headline consequence, name the broken 0070/0076 guarantee and the 0096 composition, and give
  the mitigation: narrow the bridge server's tools (ADR 0058) for agents that are reachable by email or key.
  The executor must **not** quietly add a prompt back. If a reviewer objects, surface it to the user.
- **Hard floor that the user decisions do not cover (critic H1):** no tool, `evaluate` included and yolo
  included, may act on `chrome-extension://<own id>/*`, `chrome://*`, `devtools://*`,
  `chrome-untrusted://*` or the Chrome Web Store. The extension's own `approve.html` and `options.html` run
  with full `chrome.*` and hold the pending prompt, the key and yolo, so script there would let the model
  approve itself. This refusal is **hard-coded, not user-editable**, runs before the gate, and is re-checked
  against the tab's URL at **execution** time (a tab can navigate between review and execution).
- **Auth fallback:** Path B (Origin-pinned subprotocol) if Path A fails Step 0.
- **Distribution:** a zip on a GitHub Release, loaded with "Load unpacked". There is no Web Store.

### Executor's choices recorded here (redirectable)

- **Gate semantics port 1:1 from `tools/Agent42.LocalBridge/ConfirmationGate.cs`.** A read-only tool
  passes only if no string argument looks like a URL. Every other tool prompts, except `evaluate` (user
  decision). Answers are **Allow once / Always for this tool (this browser session) / Deny**. Prompts are
  serialized with a 60 s timeout → deny, the set is re-checked after the prompt lock, and the **raw** tool
  name is recorded. Arguments are shown in full, and anything over 20,000 characters is denied. C0/C1, DEL
  and Cf characters are stripped, and the text is rendered via `textContent` only. A deny is a JSON-RPC
  **result** `{content:[…], isError:true}`, never sent to Chrome.
- **Approval UI** = a `chrome.windows.create({type:"popup"})` window (`approve.html`) that the service
  worker answers through `chrome.runtime` messaging. It is not the chat UI (0076 rejected that).
- **"Yolo"** = an options-page checkbox, off by default, stored in `chrome.storage.local`, so it is
  **persisted**. That diverges from the 0076 amendment's "launch-time flag only, never persisted"
  (critic M5), and the ADR records the divergence: an extension has no launch. Nothing arriving over the
  socket can set it, because the hard floor above keeps every tool out of the options page. While it is on,
  the toolbar badge reads `!`.
- **"Always for this tool" set** lives in `chrome.storage.session`. It survives MV3 worker suspension, dies
  with the browser session, and content scripts cannot read it (the default access level).
- **Content scripts carry nothing sensitive.** `chrome.storage.local` is readable from the extension's
  content scripts, and `scripting.executeScript` injects into every page, so injected functions take only
  their explicit arguments and never touch storage.
- **Blocked sites** = an options-page list of origins on which **every** tool, `evaluate` included, refuses
  with an `isError` result before the gate. This is the cheap counterweight to "any tab" + auto-run
  `evaluate` (banking, the Agent42 origin itself by default).
- **Key storage:** the bridge key and the server URL are in `chrome.storage.local` (needed across restarts)
  and are never logged. The session DNR rule is rebuilt from storage on each worker start.
- **v1 tools:** `tabs` (list/new/close/activate), `navigate`, `snapshot` (compact DOM/accessibility text
  with `ref` ids, paged), `find` (text/role query → refs), `read_text` (page text, paged), `click`
  (ref → CDP `Input.dispatchMouseEvent` at the element centre), `type` (focus ref + CDP `Input.insertText`),
  `key` (CDP `Input.dispatchKeyEvent`), `scroll`, `console` (recent console messages via CDP
  `Runtime.consoleAPICalled`, paged), `evaluate`, **`screenshot`** (CDP `Page.captureScreenshot`, JPEG
  quality ~60, the viewport scaled so the longest side ≤ 1280 px, returned as an MCP `image` block plus a
  text block with the CSS-pixel size and scale; base64 ≤ 700 KB so it fits the 1 MiB frame), and
  **`click_at`** (x, y in screenshot pixels → CSS pixels via the returned scale, then CDP mouse events), so
  a screenshot is actionable. `screenshot` is read-only, and `click_at` prompts. Read-only (`readOnlyHint:true`): `tabs` with
  `action:"list"` is not expressible per-action, so **split** it into `list_tabs` (read-only) and
  `manage_tabs` (prompts). Also read-only: `snapshot`, `find`, `read_text`, `console`.
- **`chrome.debugger`** attaches per tab on first use and detaches on tab close. If the user cancels the
  yellow banner, `onDetach` makes the next call return `isError: "debugger detached by the user"`. That is
  accepted and documented, not worked around.

## Steps

### Step 0: Spike (throwaway, outside the repo, in the scratchpad). BLOCKS everything

Load a minimal unpacked MV3 extension. Branded Chrome ≥137 ignores `--load-extension`, so either use
Chrome for Testing (`npx @puppeteer/browsers install chrome@stable` into the scratchpad) or ask the user to
load it via chrome://extensions. Run it against a tiny local WebSocket server that logs request headers. Measure and
record in the ADR's Verification section:

- (a) A DNR session `modifyHeaders` rule adds `X-Api-Key` to the **extension service worker's** WebSocket.
- (b) The same rule does **not** add it to a WebSocket opened by an ordinary page (http://localhost page) to
  the same URL. **If (a) holds but (b) fails, Path A is unsafe → Path B.**
- (b2) Nor to a socket from a page's **service worker** or **SharedWorker**. `tabIds:[-1]` matches those
  too, and whether `initiatorDomains:[runtime.id]` matches an extension origin is unknown. If either gets
  the header, Path A is acceptable **only** because Step 1's Origin rule refuses it, and the ADR must say
  so. Otherwise choose Path B.
- (b3) Whether `chrome.debugger.attach` / `scripting.executeScript` can reach the extension's own pages
  and `chrome://` pages. Either way the hard floor ships; this records whether Chrome also blocks it.
- (c) The exact `Origin` the extension's socket sends, and what a page's socket sends.
- (d) The worker survives >5 min idle with a 20 s app-level message heartbeat, and the socket stays up.
- (e) Node 22's global `WebSocket` can send a custom header (`new WebSocket(url, {headers})`) for the test
  harness. If not, Step 6 uses Path B's subprotocol in tests or a Node-side `ws`-free raw upgrade helper.
- (f) A hand-written `initialize`/`tools/list` server (the Step 2 module) is accepted by `McpClient` from
  SDK 2.2.0 over the existing `LocalBridgeChannelTransport`. Also measure how much `raw.ToString()` of a
  returned result inflates for non-ASCII and markup-heavy text relative to `JSON.stringify`. A
  `dotnet run app.cs` file-based spike is fine (skill `spike-dotnet`).
- (g) **Images in the tool loop** (Microsoft.Extensions.AI.OpenAI 10.10.1, Anthropic 12.58.0, as pinned in
  `Directory.Packages.props`):
  - (g1) Whether `McpClientTool` (SDK 2.2.0) hands back `image` blocks as raw JSON inside the
    `JsonElement`, or as `DataContent`. The extraction in Step 4b reads whichever it is. If neo is
    running, also measure the size of its `screenshot` output against `MaxImageBytes` and the bridge's
    1 MiB frame. Otherwise mark neo support unverified.
  - (g2) Per provider kind in `ChatClientFactory` (Foundry/Azure Responses, OpenAI-compatible Chat
    Completions such as GitHub Models, Anthropic through `AnthropicCompatibilityChatClient`): capture the
    outgoing request JSON with a recording `HttpMessageHandler` for option (a), `DataContent` inside
    `FunctionResultContent`, and option (b), a user message after the tool-result group. Record which
    shape each adapter emits, and what a Foundry deployment **without** vision answers to an inline image.
  - (g3) Live, opt-in, with the user's own keys: does the model actually describe a test image under the
    chosen option? Ask the user to run it if no key is available in the session, and never assume it.
  - (g4) That a message inserted below the function-invoking client is **not** written back into the AF
    session: run one tool-loop turn on a test grain and inspect the stored session JSON.

  Choose (a) or (b) per provider, and record the table in the ADR.

Verify: a written result table. Choose Path A or B, and say which in the ADR draft.

### Step 1: Backend: control frames and the Origin rule (+ Path B if chosen)

- `LocalBridgeEnvelope` (and the helper's `BridgeEnvelope` copy) learn `ping`/`pong`. `TryParse` accepts
  them, and `LocalBridgeSession.RouteAsync` answers a `ch:0 ping` with `ch:0 pong` **before** the channel
  lookup. A `ping` on any channel other than 0 is ignored. The helper needs **no** `BridgeClient` change:
  `DispatchAsync`'s switch already ignores parsed types without a case. Only `BridgeEnvelope.cs` changes,
  to keep `Envelope_WireFormatsMatch` honest. **Accepted consequence (critic L1):** that edit touches
  `tools/Agent42.LocalBridge/**`, so the merge to main cuts an automatic, immutable helper patch release
  (`release-local-bridge.yml`), which is functionally identical. Say so in the MR description.
- `LocalBridgeEndpoint`: reject a present `Origin` that is not `LocalBridgeLimits.ExtensionOrigin` with the
  same 401 + `RejectionBody`, before the upgrade check. On Path B: read the key from `Sec-WebSocket-Protocol`
  **only** when `Origin == ExtensionOrigin`, and accept with that subprotocol echoed
  (`WebSocketAcceptContext.SubProtocol`). Keep `X-Api-Key` as is, and the query string stays refused.
  Keep `HandleAsync` under S3776 15 (split helpers if needed).
- Tests (`LocalBridgeContractTests`: `Envelope_WireFormatsMatch` rows for ping/pong, a
  `ControlFrames_ParseOnBothSides` fact; `LocalBridgeEndpointTests`: `Endpoint_RejectsAWebPageOrigin_EvenWithAValidKey`,
  `Endpoint_AcceptsTheExtensionOrigin`, `Endpoint_AcceptsNoOrigin` (the helper), plus on Path B:
  `Endpoint_SubprotocolKey_OnlyFromTheExtensionOrigin`, `Endpoint_NeverEchoesTheKeyInResponseHeaders`, and
  `Endpoint_FailsClosed("query-string")` still 401;
  `LocalBridgeKestrelTests`: `Ping_IsAnsweredWithPong_OnChannelZero`, `Ping_OnANonZeroChannel_IsIgnored`;
  the existing bogus-type row of `Envelope_RejectsMalformedFrames` keeps unknown types rejected).
- Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~LocalBridge"`.

### Step 2: Extension scaffold + pure core modules (parallelizable with Step 1 after Step 0)

- `tools/Agent42.ChromeExtension/manifest.json`: MV3, `key`, `background.service_worker: "background.js"`
  (`type: module`), permissions `debugger, tabs, scripting, storage, alarms, declarativeNetRequestWithHostAccess`
  (drop DNR on Path B), `host_permissions: ["<all_urls>"]`, `version` (stamped by the release workflow).
- `lib/envelope.js` (the wire shape; must match the C# pair), `lib/mcp-server.js` (JSON-RPC dispatch per
  channel: `server/discover`→-32601, `initialize`, `ping`, `tools/list`, `tools/call`, unknown method → -32601),
  `lib/bridge.js` (preflight → connect → channel map ≤16 → heartbeat → backoff 1 s→60 s; stop on preflight 401
  and on the `replaced` reason; the socket factory is **injected** so node tests drive it), `lib/limits.js`
  (frame 1 MiB, 7,000-char result cap, timeouts).
- `test/*.test.mjs` with `node --test`. `tests/Agent42.Tests/ChromeExtensionScriptTests.cs` has
  `ExtensionNodeTests_Pass`, which runs `node --test "tools/Agent42.ChromeExtension/test/**/*.test.mjs"`.
  Since Node 21 the arguments are globs, so do not pass a bare directory. Exemplar:
  `ProtectMainHookTests`' `ProcessStartInfo("node", …)`. The test asserts exit 0, with the output in the
  failure message, and asserts Node ≥ 22 first.
- `.github/workflows/pr-checks.yml`: add `actions/setup-node` (node-version 22) to the `build-test` job.
- `lib/bridge.js` node tests: a missing pong within the timeout → close + reconnect; preflight
  401 → stop; preflight 400 → connect; preflight 5xx/network error → backoff retry; `replaced` → stop.
- Pin cross-language constants in C# source-text tests: the `replaced` close reason, the extension id ↔ the
  manifest `key`, the envelope literal `{"ch":…,"type":…`, and the result cap below 8 KiB.
- Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ChromeExtension"`.

### Step 3: The gate + approval window

- `lib/gate.js`: the 1:1 port described above (`review(name, args, toolMeta)` → allow/deny + text,
  `allowForSession`, the post-lock re-check, sanitize, the 20,000 limit, URL-argument detection, the
  `evaluate` exemption, yolo, and the blocked-sites check, which runs **first** and also binds `evaluate`).
  The prompt is an injected async function, so node tests answer it.
- `approve.html`/`approve.js`: shows the tool name alone on its line plus the full args, with three buttons.
  A closed window counts as a deny. `textContent` only.
- Node tests mirror the helper's: `allowForSession` stops asking for that tool only, allow-once does not
  carry over, queued calls are released after one approval, the raw name is recorded, a typo-free
  three-answer contract, read-only + URL arg prompts, `evaluate` never prompts, a blocked site refuses even
  `evaluate`, yolo passes everything except blocked sites, and the node test "no tool acts on the extension's own pages or browser-internal pages"
  (plus `chrome://`, `devtools://`, `chrome-untrusted://`, the Web Store) under yolo too, re-checked when a
  tab's URL changes between review and execution. **Mutation check:** break the post-lock
  re-check, see exactly that test fail, then revert.
- Verify: same filter as Step 2.

### Step 4: Tools over a browser adapter

- `lib/tools.js`: definitions (name, description, JSON schema, `readOnlyHint`) + argument validation +
  result paging/capping, over an injected `browser` adapter. `browser.js` is the real adapter
  (`chrome.tabs`, `chrome.scripting.executeScript` for snapshot/find/read_text/ref resolution with
  `data-agent42-ref` attributes, and `chrome.debugger` CDP for input, console and `evaluate` via
  `Runtime.evaluate` with `returnByValue`). Tool descriptions are what the model reads (cross-cutting
  rule 11), so say "refs come from the latest `snapshot`/`find` of that tab" and "results are paged; pass
  `offset`".
- Node tests with a fake adapter: validation errors are `isError` results (not exceptions), paging never
  exceeds the cap and says how much is left, unknown ref → a clear `isError`, `list_tabs` is the only tabs
  tool marked read-only.
- The real adapter is **not** unit-testable. It is covered by Step 8's manual check, and that must be stated.

### Step 4b: MCP image results reach the model (backend; parallelizable with Steps 2–5 after Step 0)

- `McpToolLimits`: `MaxImagesPerRun`, `MaxImageBytes`, the allowed media types, and the placeholder format.
- `TurnContext`: `ToolImagesVisible` (set by the grain at run entry from `CanSeeImages()`, false on
  `Closed`/background), plus a per-run, thread-safe image list keyed by call id and capped at record time
  (`RecordToolImage`, `ToolImagesFor(callId)`). `Closed` drops everything, so a missing context fails
  closed: no image, and the placeholder says "omitted".
- `TruncatingMcpTool`: extract and validate the images (decode the base64, check the size and the magic
  bytes against the declared type), record them, and replace them with placeholders. Then apply the
  existing 8 KiB text truncation to what remains. A malformed image is dropped with a note and never throws.
- `ToolImageChatClient`: put it in `src/Agent42.Agents/Models/`, because the single-client factory there
  must use it too, which is a project-reference constraint (verify that `Agents` can host it). It inserts
  per Step 0(g)'s per-provider choice, cloning always. It is layered **innermost** in `Build` (around the
  provider client, below `UseOpenTelemetry`) and in the single-client factory's `GetClient`.
- Grain: set `ToolImagesVisible` where the run's `TurnContext` is created (`ForChat`/the Copilot path),
  from `CanSeeImages()`.
- Tests (new `McpToolImageTests.cs` + additions to the existing chat-client tests):
  `ImageBlock_IsExtracted_AndNeverReachesTheTextResult` (no base64 in the returned string),
  `OversizeOrWrongTypeImage_IsDroppedWithANote`, `NoTurnContext_DropsTheImage`,
  `EmptyCallId_IsNeverKeyed_AndSaysOmitted`, `ImagesNotVisible_PlaceholderSaysOmitted`,
  `ToolImageChatClient_InsertsTheImageAfterItsToolResult` (one test per chosen shape),
  `ToolImageChatClient_InsertsAfterTheWholeParallelToolResultGroup`,
  `ToolImageChatClient_NeverMutatesTheCallersMessages` (assert the handed list and every message and
  content are unchanged by reference and value), `ToolImageChatClient_SitsBelowOpenTelemetry` (assert the
  composition order in `Build`), `NoVisionModel_GetsNoImage_AndThePlaceholderSaysSo`,
  `RecordedImages_AreCappedAtRecordTime`, `TwoRunsOnOneActivation_DoNotSeeEachOthersImages`,
  `StoredSession_HoldsNoImageBytes_AfterAScreenshotTurn` (grain-level, through the single-client factory
  path so the wrapper really runs: the session JSON has the placeholder and no base64), and
  `InstructionsPrefix_IsUnchangedByAnImage` (ADR 0104).
- Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~McpToolImage|FullyQualifiedName~ChatClient"`.

### Step 5: Options page, popup, service-worker composition root

- `background.js` wires storage → DNR rule (Path A) → bridge → mcp-server → gate → tools → adapter. It
  reconnects on `onStartup`/`onInstalled`/the alarm, and the badge shows connected / disconnected / key
  rejected / `!` yolo.
- `options.html`: server URL (default `https://chat.agent42.nl`), bridge key (password field), yolo,
  blocked sites (default: the server's own origin). `popup.html`: status + "Reconnect".
- Verify: node tests for the storage→config parsing (URL normalisation, blocked-origin matching).

### Step 6: End-to-end over a real Kestrel bridge (C#)

- `tests/Agent42.Tests/ChromeExtensionBridgeTests.cs` uses `LocalBridgeTestHost` and starts
  `node test/harness/run-bridge.mjs` (the real `lib/bridge.js` + `lib/mcp-server.js` + gate + tools over
  a fake adapter, with the prompt scripted via env). Cases: the backend's `McpClient` lists the extension's
  tools with annotations; a read-only call returns the fake result; a large non-ASCII/markup page result
  passes through the real `TruncatingMcpTool` measure without a `TruncationMarker`
  (`ExtensionCore_LargeResult_IsNeverTruncatedByTheBackend`); a `screenshot` call with a fake JPEG comes
  back through the real bridge and `TruncatingMcpTool` as a placeholder text plus a recorded image
  (`ExtensionCore_Screenshot_ReachesTheTurnAsAnImage`); a denied call returns `isError` and the
  adapter saw nothing; `evaluate` runs unprompted; ping→pong keeps a 60 s idle socket open (use a shortened
  interval via env); a regenerate (`MintBridgeKeyAsync`) evicts the socket and the harness's next preflight
  sees 401 and exits non-zero without retrying; a second harness on the same key → the first exits on
  `replaced`.
- Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ChromeExtensionBridge"`.

### Step 7: UI pointer, release workflow, docs, ADR

- `mcp.js` `showBridgeKey`: add "or paste this key into the Agent42 Chrome extension's options" + a link to
  the extension's release page. Bump `CACHE` in `sw.js`. Add an `McpServersAssetsTests` assertion
  (`BridgeKeyPanel_MentionsTheChromeExtension`).
- `.github/workflows/release-chrome-extension.yml`, modelled on `release-local-bridge.yml`: tag
  `chrome-extension-v*`, an automatic patch release on a push to main touching `tools/Agent42.ChromeExtension/**`,
  draft → upload `agent42-chrome-extension-<v>.zip` + SHA256SUMS → publish last (immutable releases, the
  0076 amendment), the version stamped into `manifest.json` (Chrome needs 1–4 dot-separated integers, so
  strip any pre-release suffix into `version_name`), and `deploy.yml` untouched.
  `ChromeExtensionReleaseTests` copies `LocalBridgeReleaseTests`' shape.
- `/decide`: a new ADR (next free number, check `.agentic/decisions/` and the `adr-numbering.yml` workflow)
  that **extends 0076** and amends it (and 0042's bridge-auth wording if Path B). Content: the extension as a
  second bridge client; control frames; the Origin rule (not authentication); Path A/B with Step 0's
  measurements; the preflight; any-tab + auto-run `evaluate` as **user decisions** with the headline
  injection consequence, **including the broken 0070/0076 "each write meets the prompt" guarantee and the
  0096 email-in composition**, plus the ADR 0058 narrowing mitigation; the hard floor on extension and
  browser-internal pages; persisted yolo as a divergence from the 0076 amendment; and egress: page text,
  tab titles, URLs **and screenshots** reach the cloud model (an ADR 0013 extension, same as 0076). The
  MCP-image-results mechanism is part of the same ADR, as its own Decision subsection, **amending ADR 0004**
  (tool images are request-only, never in the session or Blob). Record the Step 0(g) per-provider table,
  the caps, current-turn-only and its trade-off, no-vision behaviour, and Copilot staying text-only. Add the number to the CLAUDE.md "MCP client" feature-map row.
- `/gotcha`: (1) the DNR rule must be initiator/tab-scoped, or every page gets the key; (2) a browser cannot
  see a 401, hence the preflight; (3) the MV3 worker needs message traffic, because control frames don't
  count; (4) the 8 KiB cap measures the whole escaped JSON, not the text, and images must be extracted
  **before** it; tool images are request-only, so never "fix" a later turn not seeing an old screenshot by
  persisting it;
  (5) the hard floor on the extension's own pages. Update the existing header-only gotcha if Path B, and
  qualify the existing bridge-confirmation-gate gotcha (yolo is "launch-time only" for the helper, but
  persisted in the extension).
- `docs/features/remote-mcp-servers.md`: a short "Chrome extension" subsection (install, paste key, badge
  states, the `evaluate`/any-tab warning).
- Verify: `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, and
  `node .agentic/scripts/check-doc-paths.mjs`.

### Step 8: Manual live check (record results in the ADR; unverified items listed as such)

Against DevHost (`dotnet run --project tools/Agent42.DevHost`): create a Local-bridge MCP server, paste the
key into the extension, Test → `✓ ok — N tools`, Regenerate → the badge says "key rejected" and there are no
reconnect attempts in the service-worker console. DevHost cannot run a real chat tool call
(`CannedChatClient`, per 0076), so **post-deploy** on chat.agent42.nl: one chat that runs `list_tabs`,
`snapshot`, a prompted `click` (approved) and a denied `navigate`, plus a `screenshot` the model describes
correctly, followed by a `click_at` on something it saw. Do this on one vision model per provider kind the
user has configured, and on one no-vision model (it must say it can't see the image, not hallucinate). Also confirm the 101 upgrade through ACA +
Easy Auth with the extension's `Origin`, since 0076 lists the ingress pass-through as unverified.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | Ping on ch 0 is answered with pong; unknown types still ignored | `Ping_IsAnsweredWithPong_OnChannelZero`, `ControlFrames_ParseOnBothSides`, `Envelope_WireFormatsMatch` rows | `LocalBridgeKestrelTests.cs`, `LocalBridgeContractTests.cs` |
| 2 | A web page's Origin is refused even with a valid key; the helper (no Origin) and the extension Origin are accepted | `Endpoint_RejectsAWebPageOrigin_EvenWithAValidKey`, `Endpoint_AcceptsNoOrigin`, `Endpoint_AcceptsTheExtensionOrigin` | `LocalBridgeEndpointTests.cs` |
| 3 | (Path B only) the subprotocol key is accepted only from the extension Origin and echoed; the query string stays 401 | `Endpoint_SubprotocolKey_OnlyFromTheExtensionOrigin`, `Endpoint_FailsClosed` | `LocalBridgeEndpointTests.cs` |
| 4 | (Path A only) the DNR rule is scoped to the extension's own requests | Step 0 measurement (b) + source-text test `DnrRule_IsScopedToTheExtension` (asserts `tabIds:[-1]` / initiator scoping in `background.js`) | ADR Verification; `ChromeExtensionScriptTests.cs` |
| 5 | The backend's real `McpClient` lists and calls the extension's tools over the bridge | `ExtensionCore_ListsAndCallsToolsThroughTheBridge` | `ChromeExtensionBridgeTests.cs` |
| 6 | Gate: read-only passes, writes prompt, deny never reaches the adapter, allow-for-session per tool, queued calls released, raw name recorded, >20,000 denied, sanitized | node `gate.test.mjs` (run by `ExtensionNodeTests_Pass`) + `ExtensionCore_DeniedCallNeverReachesTheBrowser` | `ChromeExtensionScriptTests.cs`, `ChromeExtensionBridgeTests.cs` |
| 7 | `evaluate` runs without a prompt; a blocked site refuses every tool including `evaluate` | node `gate.test.mjs` + `ExtensionCore_EvaluateRunsUnprompted` | same |
| 7b | No tool (yolo or not) acts on the extension's own pages or browser-internal pages, re-checked at execution | node "no tool acts on the extension's own pages or browser-internal pages" in `tools.test.mjs` (run by `ExtensionNodeTests_Pass`; + Step 0 b3 measurement) | `ChromeExtensionScriptTests.cs` |
| 8 | Preflight 401 stops without retry; `replaced` stops; 5xx/network → backoff; missing pong → reconnect | `ExtensionCore_StopsOnRevokedKey`, `ExtensionCore_StopsWhenReplaced` + node `bridge.test.mjs` | `ChromeExtensionBridgeTests.cs`, `ChromeExtensionScriptTests.cs` |
| 9 | No tool result is ever cut by `TruncatingMcpTool`; every capped result carries a paging hint | node `tools.test.mjs` + `ExtensionCore_LargeResult_IsNeverTruncatedByTheBackend` | `ChromeExtensionScriptTests.cs`, `ChromeExtensionBridgeTests.cs` |
| 9b | (Path B) the key never appears in a response header | `Endpoint_NeverEchoesTheKeyInResponseHeaders` | `LocalBridgeEndpointTests.cs` |
| 9c | Options parsing: URL normalisation, blocked-origin matching | node `config.test.mjs` | `ChromeExtensionScriptTests.cs` |
| 9d | An MCP `image` block is extracted before truncation; its base64 never reaches the text result | `ImageBlock_IsExtracted_AndNeverReachesTheTextResult`, `OversizeOrWrongTypeImage_IsDroppedWithANote`, `NoTurnContext_DropsTheImage` | `McpToolImageTests.cs` |
| 9e | A vision model receives the image in the same turn, in the per-provider shape from Step 0(g); after the whole tool-result group; capped at record time; runs isolated | `ToolImageChatClient_InsertsTheImageAfterItsToolResult`, `ToolImageChatClient_InsertsAfterTheWholeParallelToolResultGroup`, `RecordedImages_AreCappedAtRecordTime`, `TwoRunsOnOneActivation_DoNotSeeEachOthersImages` | `McpToolImageTests.cs` |
| 9f | A no-vision model, a Copilot chat or an empty call id gets no image, and the persisted placeholder says "omitted" | `NoVisionModel_GetsNoImage_AndThePlaceholderSaysSo`, `ImagesNotVisible_PlaceholderSaysOmitted` (the Copilot and no-vision case: visible=false), `EmptyCallId_IsNeverKeyed_AndSaysOmitted` | `McpToolImageTests.cs` |
| 9g | The stored session holds no image bytes (the wrapper never mutates); images never reach OTel spans; the instructions prefix is unchanged | `StoredSession_HoldsNoImageBytes_AfterAScreenshotTurn`, `ToolImageChatClient_NeverMutatesTheCallersMessages`, `ToolImageChatClient_SitsBelowOpenTelemetry`, `InstructionsPrefix_IsUnchangedByAnImage` | `McpToolImageTests.cs` |
| 9h | The extension's screenshot crosses the real bridge into the turn as an image | `ExtensionCore_Screenshot_ReachesTheTurnAsAnImage` + node `tools.test.mjs` (`click_at` scale mapping, base64 cap) | `ChromeExtensionBridgeTests.cs`, `ChromeExtensionScriptTests.cs` |
| 9i | A real model describes a real screenshot | Step 0(g3) + manual Step 8 | ADR Verification |
| 10 | The extension id matches the manifest `key` and `LocalBridgeLimits.ExtensionOrigin` | `ExtensionId_MatchesManifestKey` | `ChromeExtensionScriptTests.cs` |
| 11 | The UI points at the extension; `sw.js` CACHE bumped | `BridgeKeyPanel_MentionsTheChromeExtension` + the existing CACHE guards | `McpServersAssetsTests.cs` |
| 12 | Release: tag-driven, draft-then-publish, version stamped, deploy untouched | `ChromeExtensionReleaseTests.*` | `ChromeExtensionReleaseTests.cs` |
| 13 | Real Chrome drives a real page | Manual Step 8 (DevHost + post-deploy) | ADR Verification |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

- [ ] `coverage_new_code_pct_min: 80`: `node .agentic/scripts/check-new-code-coverage.mjs` (C# lines only; JS is not measured, so do not claim it)
- [ ] `cognitive_complexity_max: 15`: S3776 is a build error, so `dotnet build Agent42.slnx` passes
- [ ] `vulnerable_packages: 0`: no new packages expected (the extension has zero npm deps)
- [ ] Build, tests, `dotnet format --verify-no-changes`, doc-path integrity (CI hard blocks)

## Out of scope

- Showing tool screenshots to the **user** in the chat UI (the tool-call fold shows the placeholder text only).
- Persisting tool images across turns (they are request-only by design, see Context 7b).
- Images from **Agent42's own** (non-MCP) tools, images on the Copilot SDK path (0085/0107), and images
  in **fleet** runs (0097). These are omitted with the honest placeholder.
- Fixing Foundry's blanket `ModelCapabilities.Full` (recorded as a limitation only).
- Chrome Web Store publishing, CRX signing, Firefox/Safari, and auto-update.
- Client tools under Copilot chats, and bridged tools for Copilot agents beyond what 0076 already gives.
- Replacing or deprecating the console helper / neo path, which keeps working functionally unchanged. Its
  `BridgeEnvelope.cs` gains the two control types only, which cuts one automatic patch release.
- Approving actions in the chat UI (rejected in 0076).
- Multi-replica bridge registry (single replica stays load-bearing, cross-cutting rule 12).

## Step 0 results (measured 2026-10-20, Chrome for Testing 155.0.8059.84 headless=new, Node 22.22.2)

Spike files are in the session scratchpad (`spike/`, `spike-g/`). They are throwaway and not committed.

| # | Question | Result |
|---|---|---|
| a | DNR session `modifyHeaders` adds `X-Api-Key` to the extension's own WebSocket | **NO.** Never, under any of 5 rule variants, including a rule with **no conditions** (`/d`). The rules read back fine (6 session rules). The extension's own `fetch` **is** modified (`/d`, `/f` with `tabIds:[-1]`). DNR does not touch an extension's own WebSocket handshake |
| b | …and not a web page's | **It does reach pages.** The broad rules (`/d` no conditions, `/e` scheme-less URL filter) put the key on the **page's** socket. The `tabIds:[-1]` and `initiatorDomains` variants matched nothing at all |
| b2 | Page service worker / SharedWorker | Not modified by the scoped rules (a/b/c). Moot given (a) |
| → | **Path decision** | **Path B** (Origin-pinned subprotocol). Path A cannot carry the key, and a broad rule would leak it to every page |
| b3 | Can the extension reach its own pages and `chrome://`? | `chrome.debugger` **attaches to the extension's own page and evaluates there, with `typeof chrome.storage === "object"`**, so critic H1 is real and the hard floor is mandatory. `scripting.executeScript` is refused on its own page ("must request permission"). Both are refused on `chrome://` ("Cannot access a chrome:// URL") |
| c | Origin | The service worker and the extension page both send `Origin: chrome-extension://<id>`. A page, its service worker and its SharedWorker send `http://127.0.0.1:9333` |
| — | Preflight | The extension's `fetch` sets `X-Api-Key` itself (no DNR needed) and gets the 400 body `This endpoint only accepts a WebSocket upgrade.` |
| — | CDP screenshot | `Page.captureScreenshot` jpeg q60 works from the service worker (a small page: 4,404 base64 chars) |
| d | Worker lifetime with a 20 s app-level heartbeat | **Holds.** One worker instance (a single `startedAt`) sent pings `n=0…18` every 20 s for 6 min 40 s with nothing else keeping it alive. The socket closed only when the run was killed (13:59:00 → 14:05:40) |
| e | Node 22 `WebSocket` headers | `new WebSocket(url, {headers, protocols})` sends custom headers **and a forged `Origin: chrome-extension://…`** (confirms L4: the pin is not authentication). The subprotocol list `["agent42-bridge.v1", "sk_bridge_…"]` is sent as one header, and the server can echo just the first. Bridge keys are unpadded base64url, so they are valid tokens |
| f | Hand-written MCP server vs SDK 2.2.0 | Deferred to Step 6's `ExtensionCore_ListsAndCallsToolsThroughTheBridge`, which is the same check against the real transport |
| g1 | What `McpClientTool.InvokeAsync` returns | Text-only → `TextContent` (`ToString()` = the text). Mixed → **`AIContent[]`** (`TextContent` + `DataContent image/jpeg`), whose `ToString()` is the type name. **Pre-existing bug:** `TruncatingMcpTool` measures `ToString()`, so a multi-part result is **never truncated**, and an image passes through as `DataContent` that the session then persists as a base64 data URI. Its "results surface as a JsonElement" comment is stale (it was true for an older SDK). Step 4b fixes both |
| g2 | Request shapes (OpenAI 2.14.0, MEAI.OpenAI 10.10.1, AF 1.23.0, Anthropic 12.58.0) | **Responses:** (a) → `function_call_output.output:[input_text,input_image]` ✓; (b) ✓. **Chat Completions:** (a) → the image is serialized as JSON **text** inside the tool message ✗; (b) → `image_url` user part ✓. **Anthropic** (raw adapter): (a) → an `image` block inside `tool_result` ✓; (b) → two consecutive user messages (Anthropic merges consecutive turns, so the live check must confirm it) |
| → | **Insert decision** | **Option (b) everywhere**: one code path, and the wrapper needs no provider kind. (a) is recorded as a measured alternative for Responses/Anthropic |
| g3 | Live model sees the image | Not run (no provider key in this session). It is a manual check in Step 8, and the user must run it |
| g4 | Inserted message not persisted | Proven by Step 4b's `StoredSession_HoldsNoImageBytes_AfterAScreenshotTurn` (through the single-client factory) |

## Implementation divergences from the plan (2026-10-20)

- **Path B, not A.** Step 0 showed declarativeNetRequest never touches the extension's own socket, and broad rules
  leak the key to pages. AC 4 (the DNR scoping test) became `Manifest_GivesWebPagesNoWayIn`, which asserts no
  `declarativeNetRequest` permission at all.
- **Option (b) everywhere** for tool images, measured in Step 0(g2): one code path, so the wrapper needs no provider
  kind.
- **Step 0(f) and (g4)** were proven by tests instead of throwaway spikes: `ExtensionCore_ListsAndCallsToolsThroughTheBridge`
  (the hand-written server against SDK 2.2.0 over real Kestrel) and `StoredSession_HoldsNoImageBytes_AfterAScreenshotTurn`.
- **A pre-existing bug fixed in passing.** Step 0(g1) showed a multi-part MCP result (`AIContent[]`) was never
  truncated, and its images were persisted. `McpToolResultShaper` fixes both, and `MultiPartText_IsTruncatedLikeAnyOtherResult`
  pins it. A single `TextContent` keeps its old raw-when-it-fits behaviour.
- **Result cap:** `MAX_RESULT_CHARS = 6000` on the *text*, not ≤ 7,500 on `JSON.stringify(result)`. g1 measured that a
  text-only result reaches the backend as a `TextContent` whose measured length is the text itself.
  `ExtensionCore_LargeResult_IsNeverTruncatedByTheBackend` proves it with non-ASCII and markup.
- **`scroll` is read-only**, beside the reading tools and `screenshot`. It changes no page state and carries no data out.
- **Background runs see tool images too.** `ToolImagesVisible` is set from `CanSeeImages()` in the chat core (which
  agent-run drains also take) and in the reminder path, so the rule is the model's vision, not the turn kind. Fleet
  runs have no `TurnContext`, so they stay "omitted".
- **Ping tests** live in `LocalBridgeEndpointTests` (TestServer), not `LocalBridgeKestrelTests`. A ping needs no real
  transport. The key-never-echoed test is on Kestrel, because it reads the real 101 headers.
- **The release workflow** is one job, not three. There is one artifact and no platform matrix, and the order is
  still draft → upload → sums → publish.
- **Live-check fix:** form fields are named from `<label for>` and submit-button values. The first live run showed an
  unlabeled `text ""`.
- **Not done:** the live model checks (g3, Step 8 post-deploy) and the branded headful-Chrome banner behaviour. Both are
  listed under the ADR's Unverified.

## Review

Critic pass on 2026-10-20 (fresh context). It verdicted "fix first", with all findings accepted and
folded in above:

- **H1 (accepted):** a self-approval hole. `evaluate`/CDP could reach `approve.html`/`options.html` and
  approve prompts or flip yolo. Fixed with the hard-coded floor (Context, Step 3, AC 7b, Step 0 b3).
- **H2 (accepted, surfaced to the user):** auto-run `evaluate` also reaches agent-run, agent-mcp, reminder and
  email-in (0096) turns, which breaks 0076/0070's "each write meets the prompt". The user decision stands
  unless the user changes it. The ADR must state it and the 0058 mitigation.
- **M1 (accepted):** the backend measures the escaped whole-result JSON in characters. Cap on serialized
  length, measure in Step 0(f), and an end-to-end no-truncation test.
- **M2 (accepted):** `build-test` has no `setup-node`. Add it, and assert Node ≥ 22. Use a glob for `node --test`.
- **M3 (accepted):** never echo the key as the subprotocol. Echo a fixed `agent42-bridge.v1`.
- **M4 (accepted):** Step 0 (b2) covers page service workers and SharedWorkers.
- **M5 (accepted):** persisted yolo recorded as a divergence; the always-set lives in `chrome.storage.session`;
  content scripts carry nothing sensitive; the gate gotcha is qualified.
- **L1 (accepted):** no `BridgeClient` change, and the helper patch release is stated.
- **L2 (accepted):** the missing tests are named.
- **L3 (accepted):** non-401/400 preflight → backoff.
- **L4 (accepted):** the Origin pin is not authentication.

Rejected: none.

**Scope change after the critic pass (user, 2026-10-20):** MCP image results (Context 7b, Step 0(g), Step
4b, AC 9d–9i) were added after the review above.

**Second critic pass (image part only, 2026-10-20), verdict "fix first":** all findings accepted and folded in.

- **H1 (accepted, re-shaped):** the critic recommended grain-side composition so test hosts run the
  wrapper. Taken partly. The wrapper stays **factory-side** because of L1: a grain-side wrapper sits
  above `UseOpenTelemetry`, which records sensitive data. It is also added to the single-client
  factory's `GetClient`, so test silos and DevHost run it. The vision rule is grain-owned through
  `TurnContext.ToolImagesVisible` from `CanSeeImages()`, which is one rule and excludes Copilot.
- **H2 (accepted):** always clone; `ToolImageChatClient_NeverMutatesTheCallersMessages`.
- **M1 (accepted):** the placeholder is worded from `ToolImagesVisible`; an empty call id is never keyed;
  the Copilot and fleet cases are named.
- **M2 (accepted):** the Foundry `Full` limitation is measured in 0(g2) and recorded; the no-vision
  check is on a non-Foundry provider.
- **M3 (accepted):** the history-cache miss is recorded as a 0104 amendment; the test is renamed.
- **L1–L6 (accepted):** below OTel; after the whole tool-result group; fleet out of scope; generic egress;
  the neo claim softened and measured; capped at record time.

Rejected: none.

**Third critic pass (the full implementation diff, 2026-10-20), verdict "fix first, nothing blocking":** the backend
auth (`ResolveKey`, the Origin rule) and the image path were found clean. All findings were accepted and fixed
before the MR:

- **M1:** "dismissing the banner stops the extension" was not true in code. A dismissal (`canceled_by_user`) now
  refuses every tool but `manage_tabs` until the tab is closed, not until it reloads, which a page could do itself.
- **M2:** blocked sites failed open on `blob:` and on origin-inheriting `about:`. The floor now fails closed (http(s)
  plus a bare `about:blank` only), and a `blob:` is judged by its creator. A blank tab allows only
  navigate/manage_tabs.
- **M3:** the approval window now shows the target tab (title and address) and the navigation destination.
- **M4:** a closed channel or socket now cancels a pending prompt (the helper's channel token, ported), and the
  call never runs.
- **L1:** a timeout aborts the prompt's signal, which closes its window.
- **L2:** both `url` and `pendingUrl` are checked.
- **L3:** the AC table names are corrected below.
- **L4:** a multi-part `UriContent` keeps its address. Other content is serialized, never reduced to a type name.
- **L5:** images are inserted only after this turn's tool results (after the last user message).
- **L6:** the default blocked host follows the address being configured.
