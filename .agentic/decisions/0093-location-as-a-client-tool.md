---
id: 0093
date: 2026-10-09
status: accepted
tags: [location, tools, client-tools, privacy, pii, geocoding, frontend, copilot, no-migration]
supersedes: []
amends: [0012, 0089]
---

# `get_current_location` becomes a client tool; the position no longer rides the chat request

## Context
ADR 0012 sent the browser's cached fix on **every** chat request (`ChatRequest.Location`) and parked it in the
turn-scoped `_turnLocation` field. The reason was that a server-side tool could not ask the browser for anything
mid-turn. ADR 0089 (2026-10-09) removed that limit: a client tool pauses the turn, the browser runs it, and the
turn resumes on the posted result. `_turnLocation` was also the most hazardous member of the activation-scoped
field family: three clears (`ChatAsync` finally, `EnqueuePromptAsync`, `ReceiveReminder`), any one of which,
if missed, would leak a stale position into another turn.

## Decision
`get_current_location` is a client tool. Its name joins `ClientToolLimits.ToolNames`, so it gets the endpoint
intersection and the Copilot exclusion for free. The browser answers it through `locationForTool()` in
`location.js`, and only when the model calls it. `ChatRequest.Location`, the `location` parameter of
`IAgentGrain.ChatAsync` and `_turnLocation` are deleted.

- **Server.** The tool calls `AwaitClientResultAsync`. `ParseBrowserFix` reads the answer as JSON numbers and
  range-checks them with `UserLocation.IsValid` before the geocoder sees anything. Anything else is "no usable
  fix" and never reaches the geocoder: a non-number, out of range, garbage, or a truncated prefix. The tool still
  exposes **zero** model-visible parameters. Each status maps to plain language for the model, and the raw status
  JSON is never the result:
  - `not_shared`: unchanged text from 0012 ("has not shared their location… Preferences").
  - `no_fix` and `timeout`: the device could not get a position, don't guess.
  - `unavailable`: no browser is connected to this chat, don't guess.
- **Browser.** It checks `isCaptureAuthorized()` first and answers `{status:"not_shared"}` without any capture.
  A cached fix up to `LOCATION_MAX_AGE_MS` (30 min) old is answered at once. Otherwise the browser takes **one**
  capture bounded by `LOCATION_TOOL_TIMEOUT_MS` (10 s), which is below the server's 15 s
  `ClientToolLimits.AutomaticTimeout`. A stale fix is never returned. The background refresh (on grant, on
  visibility, every 5 min while visible) stays, so most calls are answered from the cache.
- **Selection.** The tool keeps its own `location` selectable key. It does **not** move under `browser_tools`,
  which would turn location OFF for everyone who ever pressed Save (ADR 0021).

## Alternatives considered
- **Keep the request-borne path.** Rejected. The position leaves the device on every send, including "hi", and
  `_turnLocation` with its three clears stays.
- **Keep the request path for Copilot chats only.** Rejected. Copilot chats cannot run client tools
  (`IsExcludedFromCopilot`), but keeping this path for them keeps `ChatRequest.Location`, `_turnLocation` and all
  three clears, which is most of what this change removes.
- **Make client tools work under Copilot first.** Out of scope. The 120 s confirm wait under Copilot's RPC timeout
  is unmeasured (ADR 0089) and needs its own spike.
- **Never wait for a GPS fix on the tool path either** (0012's original rule). Rejected. That rule existed because
  the read sat on every send. The tool path only runs when asked, its turn is already paused, and the capture is
  bounded below the server's wait.

## Consequences
- **Privacy gain, no egress change.** The position leaves the browser only when the model asks for it. Google
  receives coordinates exactly when the tool runs, as before.
- **Copilot chats have no location.** Their tool picker still shows a Location checkbox, which does nothing there.
  This is accepted until client tools work under Copilot.
- **The tool path may take one bounded GPS capture.** On the Permissions-API leg, that capture only happens under a
  `granted` state, so it never prompts. On the WebKit leg the grant is only a stored flag, so if the site permission
  was reset to "ask", the capture can prompt mid-turn. This is accepted and can only be verified on a device.
- **The SSRF input moved.** The coordinates now arrive through `POST /api/chat/{id}/tool-result` instead of the chat
  request. The 0012 argument still holds: no parameters, values parsed as doubles and range-checked, and a
  config-pinned host.
- **The turn-scoped field family shrinks** to `_ephemeral`, `_interactiveTurn` and `_webDocumentIngested`, with
  `_pushesThisTurn` reset on its own pattern. `_ephemeral` is now the family's archetype.
- **Transition.** A PWA still running the old shell posts `location` and does not advertise the tool. The unknown
  member is ignored, so the request still binds, and the tool answers `unavailable` until the `CACHE` bump swaps
  the shell. Pinned by `Chat_OldClientBodyWithLocation_StillBinds`.
- **No migration.** Location was never persisted. Older `get_current_location` results already in chat histories
  stay as they are.
- Device-only: on iOS Safari and Android Chrome, check that "where am i" answers when location is granted, and
  answers "not shared" when it is revoked.
