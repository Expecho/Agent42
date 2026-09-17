# Remote MCP servers

> Connect your own remote HTTP MCP servers; their tools become available to the agent with no
> extra setup. ADRs 0024 · 0025 · 0026 · 0076.

## What it is

A signed-in user adds remote HTTP **MCP (Model Context Protocol)** servers in
**Preferences → MCP servers**. Their tools appear to the agent with **no per-server code** —
every MCP server speaks the same discovery protocol, and its tools convert to `AITool`. Auth
options: **none**, a **static header**, **client-credentials** (MSAL), or an **interactive
OAuth** sign-in flow — or a **local bridge** for a server that runs only on your own machine.
Secrets are stored encrypted. Each server can be turned on/off
individually in the tool picker.

## How it works

It is the MCP analogue of [user-configurable model providers](bring-your-own-models.md): a
per-user overlay grain holds the config, a singleton tool provider connects to enabled servers
(caching **live sockets** by version), and `AgentGrain` resolves the tools once on activation.

```mermaid
flowchart TD
    subgraph Config
      UI["Preferences → MCP servers"] -->|"encrypt secret"| API[AgentApi /api/mcp-servers]
      API -->|SaveAsync| GRAIN["UserMcpConfigGrain<br/>state: mcp-servers"]
    end
    subgraph Resolve["AgentGrain.OnActivateAsync (before first BuildAgent)"]
      PROV["UserMcpToolProvider (singleton)<br/>version-cached live sockets"]
      PROV -->|ConnectAllAsync 10s best-effort| SRV["each enabled server → McpClient.ListTools"]
      SRV --> WRAP["TruncatingMcpTool wraps each AITool"]
    end
    GRAIN --> PROV
    WRAP --> BUILD["BuildAgent: add per server if mcp / mcp:{id} enabled ∩ SupportsTools"]
```

### Tools merged per server

```csharp
if (capabilities.SupportsTools)
{
    foreach (var server in _mcpServers)
    {
        if (ToolCatalog.IsMcpServerEnabled(server.ServerId, enabled))
        {
            tools.AddRange(server.Tools);
        }
    }
}
```

Per-server gate — the bare `mcp` key is a **wildcard** ("all servers"), or an explicit
`mcp:{id}`:

```csharp
public static bool IsMcpServerEnabled(string serverId, ISet<string> enabled)
    => enabled.Contains(Mcp) || enabled.Contains(McpServerKey(serverId));
```

### Version-cached provider holding live sockets

```csharp
var version = await _grains.GetGrain<IUserMcpConfigGrain>(userId).GetVersionAsync();
var cache = _caches.GetOrAdd(userId, _ => new UserCache());
await cache.Gate.WaitAsync(cancellationToken);
try
{
    if (cache.TryGet(version, out var hit)) return hit;
    var config = await _grains.GetGrain<IUserMcpConfigGrain>(userId).GetAsync();
    var (servers, connections) = await ConnectAllAsync(userId, config, cancellationToken);
    var set = new McpToolSet(servers, version);
    cache.Store(version, set, connections);
    await cache.EvictBeyondAsync(RetainedVersionsPerUser, _logger);
    return set;
}
```

### Tools convert to `AITool` via the SDK

The MCP SDK returns each tool as an `AIFunction`, so they cast straight to `AITool` — no
adapter is needed to merge them:

```csharp
var client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
IReadOnlyList<AITool> raw = [.. tools.Cast<AITool>()];
return new McpServerConnection(raw, client);
```

Each tool is wrapped in a `TruncatingMcpTool : DelegatingAIFunction` — forwarding
Name/Description/JsonSchema unchanged so the model's view is identical, bounding only the
**return value** (it's persisted into the 2 MB session).

### Auth staging

- **Header** auth rides `AdditionalHeaders` (no `DelegatingHandler`, so it doesn't inherit the
  resilience pipeline):

```csharp
if (mode == McpAuthMode.Header && ...)
{
    options.AdditionalHeaders = new Dictionary<string, string>
    {
        [server.HeaderName] = secrets.HeaderValue,
    };
}
```

- **Interactive OAuth** (ADR 0025) is an app-driven authorization-code + PKCE flow with PRM
  discovery across `GET /api/mcp/oauth/start` + `/callback` (both **behind Easy Auth**); tokens
  are persisted encrypted and refreshed at connect. The SDK's own OAuth is not used (its
  redirect delegate blocks). Manual prereq: register the callback URI in each provider's app
  registration.

## Local bridge (a server on your own machine)

ADR 0076. Some MCP servers — BrowserOS neo, which drives your signed-in browser — listen only on
`127.0.0.1`, where production cannot reach them, and neo refuses any request that carries an `Origin`,
so a browser tab cannot relay either. Choose **Local bridge** as the auth mode instead:

1. Save the server (no endpoint). The dialog shows a **bridge key once** and the command to run.
2. Download the helper for your platform from the
   [GitHub Releases](https://github.com/Expecho/Agent42/releases?q=local-bridge&expanded=true)
   (`agent42-local-bridge-<version>-<platform>` — Windows, macOS and Linux, x64 and arm64). It is a single
   self-contained executable: **no .NET install needed**. It is not code-signed, so Windows SmartScreen
   asks you to confirm (*More info → Run anyway*) and on macOS you run
   `xattr -d com.apple.quarantine Agent42.LocalBridge` once. `SHA256SUMS.txt` sits next to the downloads.
3. On your computer, set `AGENT42_BRIDGE_KEY` to that key and run
   `./Agent42.LocalBridge --server https://chat.agent42.nl`
   (`--upstream <url>` if your server is not on `http://127.0.0.1:9210/mcp`; see
   `chrome://browseros/mcp`; `--auto-approve tool,tool` to skip prompts for named tools; `--version`).
   From a checkout, `dotnet run --project tools/Agent42.LocalBridge -- --server …` does the same.
4. Press **Test** — it reports the tool count, or "local bridge not connected" while the helper is off.

```mermaid
flowchart LR
    MODEL[cloud model] --> GRAIN[AgentGrain] --> CLIENT["McpClient over a bridge channel"]
    CLIENT -- "wss /api/local-bridge (X-Api-Key)" --- HELPER["Agent42.LocalBridge on your machine"]
    HELPER -->|"confirm unless readOnlyHint: true"| NEO["local MCP server (neo)"]
```

The helper dials **out**, so nothing on your machine is exposed. Before relaying any tool call that the
server has not declared read-only it **asks you in its console**, showing the full (sanitized) arguments;
declining, not answering within 60 s, or arguments too large to review all return an error result the
model can read. Read-only tools — page text, screenshots, **browsing history** — run without asking and
their output goes to the model, so narrow the server's tools and keep "let links from this server be
opened and read" off. **Regenerate key** rotates the key and disconnects the running helper, which then
stops. A chat that was already open when the helper connected picks the tools up on its next activation.

## How the Agent Framework fits

- MCP tools arrive from the SDK as `AIFunction`s and merge directly into the `List<AITool>` that
  `BuildAgent` hands to `ChatClientAgent`'s `ChatOptions.Tools`.
- `TruncatingMcpTool` is a `DelegatingAIFunction`, so the framework treats it exactly like any
  other function — only the tool result is bounded.

## How Orleans fits

- `IUserMcpConfigGrain` (state `mcp-servers`) and `IUserMcpOAuthPendingGrain` (state
  `mcp-oauth-pending`) ride the shared `agent-state` provider, keyed by user id — no new Cosmos
  container.
- `IUserMcpToolProvider` is a **singleton** (not a grain) holding live sockets, invalidated by
  the grain's monotonic `Version`; it retains the last two versions per user and disposes older
  connections on eviction.
- Because `AgentGrain` is non-reentrant, resolution happens **once in `OnActivateAsync`**, before
  the first `BuildAgent`.
- The endpoint is deliberately **not SSRF-guarded** — it's the user's own config (may be
  `localhost`), not a model-supplied request.

## Key files

| Concern | File |
|---------|------|
| Stored config + grain | `src/Agent42.Agents/Mcp/UserMcpConfig.cs`, `UserMcpConfigGrain.cs` |
| Tool provider | `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs`, `McpConnectionFactory.cs` |
| Encryption | `src/Agent42.Backend/Mcp/McpSecretProtector.cs` |
| Auth | `src/Agent42.Backend/Mcp/McpAuthHeader.cs`, `McpOAuth.cs` |
| Endpoints | `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` (`/api/mcp-servers`, `/api/mcp/oauth/*`) |
| Local bridge (backend) | `src/Agent42.Backend/Mcp/LocalBridgeEndpoint.cs`, `LocalBridgeSession.cs`, `LocalBridgeConnectionFactory.cs`, `LocalBridgeRegistry.cs` |
| Local bridge (helper) | `tools/Agent42.LocalBridge/BridgeClient.cs`, `ConfirmationGate.cs`, `UpstreamChannel.cs` |
| Local bridge release | `.github/workflows/release-local-bridge.yml` (automatic patch release when the helper changes on `main`; or tag `local-bridge-vX.Y.Z`) |
