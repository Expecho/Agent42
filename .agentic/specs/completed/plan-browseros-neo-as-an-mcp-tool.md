> **Superseded by `.agentic/specs/completed/plan-local-browser-bridge.md` (ADR 0076, 2026-09-17).** This research
> closed the direct routes; the local bridge is what shipped.

# Plan — drive BrowserOS neo from Agent42 as a remote MCP server

planned-against: main @ c6fecd9 · sibling repo `Expecho/mcpserver` @ 6508396 · researched 2026-09-13

> **This is a research handoff, not an executable plan.** No option has been chosen and no code has
> been written. It exists so the verified facts below do not have to be rediscovered. The open
> decision is in **Decision needed** — answer that first, then this becomes a `/plan` input.

## Overview

**Goal:** let an Agent42 conversation drive [BrowserOS neo](https://browseros.com/) — a local,
open-source agentic browser (Chromium fork, AGPL-3.0) that exposes 50+ browser automation tools
(navigate, click, type, extract) over MCP, acting with the user's already-signed-in sessions.

**Direction:** Agent42 is the MCP **client**, BrowserOS neo the server. (The inverse already exists
and is free — ADR 0070 lets BrowserOS neo call stored agents via `/api/agent-mcp`. Not this work.)

**The finding that makes this cheap:** it needs **zero code in Agent42**. The blocker is entirely
network reachability and auth, and it lives outside this repo.

## Verified facts

### BrowserOS neo (from its own docs)

- Exposes a **streamable-HTTP MCP server** at `http://127.0.0.1:9239/mcp`; URL shown under
  `chrome://browseros/mcp`. *(Measured 2026-09-17 on 0.0.53: it listened on `127.0.0.1:9210` — read
  the URL from `chrome://browseros/mcp`, never assume the port.)*
- **No authentication of any kind** on that endpoint.
- **Binds `127.0.0.1` only.** Its docs: *"The BrowserOS MCP server only listens on `127.0.0.1`, so
  by default only programs on the same computer can reach it."*
- Its own warning, which governs every option below: *"Anyone who can reach this URL has full
  control of your browser, including every account you are signed into."*
- Documented remote options: `tailscale serve` (tailnet-only), `cloudflared`, `ngrok`.
- Sources: <https://docs.browseros.com/features/use-with-claude-code>, <https://browseros.com/>

### Agent42 (verified in this repo @ c6fecd9)

- `McpConnectionFactory` uses `HttpClientTransport` over the `ModelContextProtocol` SDK — **the same
  streamable HTTP BrowserOS serves**. No transport work needed.
- `ValidateMcpServer` (`src/Agent42.Backend/Mcp/AgentApi.Mcp.cs:107`) requires only an absolute
  http/https URL. **Localhost is deliberately accepted** — `.agentic/gotchas.md:1623`, *"config, not
  a model/request"*.
- ⇒ In DevHost this is a Preferences → MCP servers row with auth `None`. Nothing else.

### The blocker

Agent42's backend runs in ACA. `127.0.0.1:9239` **from that container** is the container's own
loopback — where the SearXNG sidecar lives on 8888 — not the user's machine. The URL from BrowserOS's
install board is useless to production as-is.

### `Mcp.Gateway` — why the obvious fix does not fit (verified @ 6508396)

The first suggestion in conversation was "put it behind the gateway". Reading the source, that was
too glib on two counts:

1. **It is not a general reverse proxy.** `src/Mcp.Gateway/BackendRoute.cs`:
   `public string Address => $"http://127.0.0.1:{Port}/";` — every destination is loopback by
   construction, because the backends are ASP.NET apps the gateway process itself hosts (ports
   5101/5102/5103 in `GatewayBackends.cs`). **There is no field for a remote upstream.**
2. **It enforces no auth.** `GatewayRoutes.Build` emits a bare `Host` → cluster table and nothing
   else. Auth lives in each *backend* via `Mcp.Hosting`'s `McpAuth` — `ApiKey()` (blanket
   middleware, `X-API-Key` or `Bearer`, constant-time compare in `ApiKey.cs`) or `Bearer()` (OAuth).
   BrowserOS neo is not an `McpHost` app, so it inherits **neither**.

Compounding both: ADR 0063 records the gateway runs *"on the operator's own bare-metal server,
directly on the internet with no reverse proxy in front"*. A naive route would therefore publish
unauthenticated full-browser-control to the internet.

Also relevant: `GatewayRoutes` routes **by `Host`, not by path**, so any new backend needs its own
subdomain regardless of auth mode.

## Options

### A — Run BrowserOS neo on the gateway box *(recommended if this ships at all)*

The only option that fits the existing design: `127.0.0.1:9239` is genuinely correct there, so
`new BackendRoute("browser", browserHost, 9239)` works unchanged, and the LettuceEncrypt SAN comes
free (`BackendRoute` is the single source of truth for routing *and* certificates).

- **Still needs:** an auth check on that route, since BrowserOS has none and the gateway adds none.
  Smallest honest version — a route-scoped middleware reusing `Mcp.Hosting.ApiKey.IsAuthorized` for
  backends the gateway does not itself host. Agent42 then adds the server with auth `Header`,
  `X-API-Key`.
- **Cost:** BrowserOS neo is a GUI Chromium fork — on bare-metal Linux it needs a display (Xvfb or
  equivalent). Unresearched; this is the main unknown in option A.
- **Identity trade, cuts both ways:** the browser's signed-in accounts become whatever is logged in
  *on that server*, not the laptop's. A loss (not your real sessions) and a gain (a contained agent
  identity whose blast radius is not your personal Google account). Given ADR 0024 has no
  confirmation gate on writes, the containment is arguably the feature.

### B — Keep it on the laptop and tunnel

Requires `BackendRoute` to express a non-loopback address — a change to the record whose own doc
comment calls it the single source of truth for two subsystems that must never disagree — plus a
tailnet hop (`tailscale serve` is tailnet-only, so the gateway must be on the tailnet), plus the
laptop being awake, plus the same auth gap as A. **More moving parts, worse availability, no gain
over A.** Recorded so it is not re-proposed.

### C — Skip the gateway: DevHost only

`dotnet run --project tools/Agent42.DevHost` runs on the same machine as the browser, so
`http://127.0.0.1:9239/mcp` with auth `None` works directly. **No infra, no exposure, no code
anywhere.**

## Decision needed

**Is the tool set worth standing up infrastructure for?** Unanswered — nobody has yet seen what
BrowserOS neo's 50+ tools actually do in an Agent42 turn.

Recommended sequencing: **C first** (costs nothing, answers exactly that question), then A only if
it earns its place. Do not start A before C.

## Constraints that apply whichever option is chosen

- **ADR 0024's inbound prompt-injection channel is sharper here than anywhere else in the app.** No
  confirmation gate on writes, and this server's tools include *click* and *type*. An injected page
  can make Agent42 act as the signed-in user. This is strictly worse than the Gmail warning in the
  ADR 0067 bullet — that one concerned attacker-authored URLs; this one hands over actions.
- **`SeedsFetchableUrls` must stay OFF** (ADR 0067). A browser tool returns page URLs by the
  hundred; seeding them makes them ingestible by `read_web_document` into memory with **no delete
  path** (ADR 0056). Same reasoning as the Gmail bullet, at higher volume.
- **Narrow the tools** (ADR 0058). `null == all`, and 50+ tool schemas would ride every turn's JSON
  (token cost; note the measured GitHub Models 16k input cap). Use per-tool `mcp:{id}:{tool}` keys.
- **Results persist into the 2 MB session** (ADR 0004), truncated by `TruncatingMcpTool` at
  `McpToolLimits.MaxToolResultLength` = **8 KiB**. Page extracts are large — do not point a
  **continuous** agent (ADR 0016) at this.
- **Silent degradation when the machine is asleep:** connect fails after
  `McpConnectionFactory.ConnectionTimeout` (10 s) and `UserMcpToolProvider` is best-effort, so the
  tools vanish mid-conversation with no user-visible error. Press **Test** (ADR 0064) to see truth.

## Next steps

1. **Option C**, ~15 minutes: install BrowserOS neo, run DevHost, add
   `http://127.0.0.1:9239/mcp` (auth `None`) in Preferences → MCP servers, press **Test**, then
   narrow to a handful of read-shaped tools per ADR 0058 and run a real task. **Record which tools
   are worth keeping** — that list is the input to every later decision.
2. If it earns its place: research whether BrowserOS neo runs headless/Xvfb on bare metal. **This is
   option A's one real unknown** and it can kill A on its own.
3. Only then `/plan` the sibling-repo work (the `BackendRoute` entry + the API-key middleware) and
   an ADR here if a stored MCP server for it becomes a standing part of the setup.

## Option D — relay tool calls through the open Agent42 tab *(measured 2026-09-17: BLOCKED)*

Idea: the backend declares neo's tools as declaration-only `AIFunction`s, the tab executes each call
against loopback and posts the result back. **Neo refuses it by design.** Measured against
BrowserOS neo 0.0.53 (server process `browseros-claw-server`, listening on **`127.0.0.1:9210`**, not
the documented 9239):

- CORS preflight passes (`access-control-allow-origin: *`, `mcp-session-id` allowed) — misleading.
- `POST /mcp` `initialize` with **no** `Origin` → `200`, SSE, `serverInfo.name = browseros-neo`.
- The same request with **any** `Origin` (`https://chat.agent42.nl`, `http://localhost:61326`,
  `null`, its own `http://127.0.0.1:9210`, `chrome-extension://…`) → **`403 {"error":"unsupported request"}`**.
- No `Origin` but `Sec-Fetch-Site: cross-site` → also `403`. `GET /mcp` with an `Origin` → `403`.

A browser always attaches `Origin`/`Sec-Fetch-*` to a cross-origin `fetch` and page script cannot
remove them, so **no web page — and no extension — can drive neo**. This is neo's DNS-rebinding /
drive-by protection working as intended; do not look for a workaround. Also noted: the preflight
does not expose `mcp-session-id` (`Access-Control-Expose-Headers` absent), which would have blocked
session reuse from JS even if the request were accepted.

⇒ Anything that reaches neo must be a **non-browser process on the user's machine**. The shape that
follows is a small local companion that dials **out** to Agent42 (authenticated) and relays MCP to
loopback — which also covers Vercel's `agent-browser` (a CLI, not an HTTP server).

## Status

- **Nothing has been written or committed.** The session was research only; the repo is untouched
  apart from this file, which is untracked on `main` (hard rule 1 — no commits to a protected
  branch; branch when work starts).
- **Unverified:** whether BrowserOS neo can run without a desktop session; whether its tool set is
  useful enough to justify A. Both are step 1 and 2 above.
