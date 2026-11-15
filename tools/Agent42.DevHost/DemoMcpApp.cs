using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;

namespace Agent42.DevHost;

/// <summary>
/// The offline MCP App fixture (ADR 0133): one model-visible tool that declares a <c>ui://</c> view and returns
/// <c>structuredContent</c>, one app-only tool the view calls, and a self-contained view that speaks the postMessage
/// protocol directly. It rides the same <see cref="IMcpAppClient"/> seam the real connection does, so the registry, the
/// frame route, the CSP and the browser host all run for real offline; only the MCP handshake is stood in for.
/// </summary>
internal static class DemoMcpApp
{
    public const string ToolName = "demo_show_app";

    public const string AppOnlyToolName = "demo_app_refresh";

    public const string ResourceUri = "ui://demo/chart.html";

    public static McpAppRef Ref(string serverId) => new(serverId, ToolName, ResourceUri);

    /// <summary>The model-visible tool, wrapped as the real provider wraps an app tool.</summary>
    public static AITool Tool(string serverId) =>
        new TruncatingMcpTool(
            AIFunctionFactory.Create(
                JsonElement (string? topic) => JsonDocument.Parse(Result(1)).RootElement,
                ToolName,
                "shows an interactive demo chart"),
            Ref(serverId));

    public static string Result(int round)
        => JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = $"Chart data, round {round}." } },
            structuredContent = new { round, values = new[] { 3 * round, 7, 5 + round, 2 } },
        });

    public static McpAppHandle Handle() => new(
        new FixtureClient(),
        new HashSet<string> { ToolName, AppOnlyToolName },
        new Dictionary<string, McpAppToolMeta>
        {
            [ToolName] = new(ResourceUri, ["model", "app"]),
            [AppOnlyToolName] = new(null, ["app"]),
        });

    private sealed class FixtureClient : IMcpAppClient
    {
        private int _round = 1;

        public Task<McpAppResource> ReadResourceAsync(string uri, CancellationToken cancellationToken)
            => Task.FromResult(uri == ResourceUri
                ? new McpAppResource(McpElicitationClient.AppsMimeType, ViewHtml, """{"csp":{"connectDomains":["https://api.example.com"]}}""")
                : new McpAppResource(null, null, null));

        public Task<JsonElement> CallToolAsync(string toolName, JsonElement? arguments, CancellationToken cancellationToken)
            => Task.FromResult(JsonDocument.Parse(Result(++_round)).RootElement);
    }

    /// <summary>The registry's handle source offline: one fixed server, for every user.</summary>
    internal sealed class HandleSource(string serverId) : IMcpAppHandleSource
    {
        private readonly McpAppHandle _handle = Handle();

        public Task<McpAppHandle?> GetAppHandleAsync(string userId, string id, CancellationToken cancellationToken)
            => Task.FromResult<McpAppHandle?>(id == serverId ? _handle : null);
    }

    private const string ViewHtml = """
        <!doctype html><html><head><meta charset="utf-8"><title>Demo chart</title>
        <style>
          body{font-family:var(--font-sans,system-ui);margin:0;padding:12px;background:var(--color-background-primary,#fff);color:var(--color-text-primary,#111)}
          .bar{display:inline-block;width:28px;margin-right:6px;background:var(--color-ring-primary,#c97f1b);vertical-align:bottom}
          button{margin:8px 6px 0 0;padding:4px 10px;border:1px solid var(--color-border-primary,#999);background:transparent;color:inherit;border-radius:6px;cursor:pointer}
          #log{font-size:12px;opacity:.8;margin-top:8px;white-space:pre-wrap}
        </style></head><body>
        <div id="chart" style="height:80px"></div>
        <div id="meta">waiting for data…</div>
        <button id="refresh">Refresh (tools/call)</button><button id="link">Open link</button>
        <button id="msg">Message</button><button id="ctx">Tell the assistant</button><button id="big">Fullscreen</button>
        <div id="log"></div>
        <script>
        let nextId = 1; const pending = new Map();
        const log = (t) => { document.getElementById('log').textContent += t + '\n'; };
        const send = (m) => parent.postMessage(m, '*');
        const request = (method, params) => new Promise((resolve) => { const id = nextId++; pending.set(id, resolve); send({jsonrpc:'2.0', id, method, params}); });
        const notify = (method, params) => send({jsonrpc:'2.0', method, params});
        function draw(result) {
          const v = result?.structuredContent?.values ?? [];
          document.getElementById('chart').innerHTML = v.map(n => '<span class="bar" style="height:' + (n*6) + 'px"></span>').join('');
          document.getElementById('meta').textContent = (result?.content?.[0]?.text ?? '') + ' round ' + (result?.structuredContent?.round ?? '?');
          notify('ui/notifications/size-changed', {width: document.body.scrollWidth, height: document.body.scrollHeight});
        }
        addEventListener('message', (e) => {
          const d = e.data; if (!d || d.jsonrpc !== '2.0') return;
          if (d.method === 'ui/notifications/tool-result') draw(d.params);
          else if (d.method === 'ui/resource-teardown') send({jsonrpc:'2.0', id: d.id, result: {}});
          else if (d.method) log('host: ' + d.method);
          else if (pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
        });
        document.getElementById('refresh').onclick = async () => { const r = await request('tools/call', {name:'demo_app_refresh', arguments:{}}); r.error ? log('refresh refused: ' + r.error.message) : draw(r.result); };
        document.getElementById('link').onclick = async () => { const r = await request('ui/open-link', {url:'https://modelcontextprotocol.io'}); log(r.error ? 'link refused: ' + r.error.message : 'link opened'); };
        document.getElementById('msg').onclick = async () => { const r = await request('ui/message', {role:'user', content:{type:'text', text:'Summarize the chart.'}}); log(r.error ? 'message refused: ' + r.error.message : 'message sent'); };
        document.getElementById('ctx').onclick = async () => { const r = await request('ui/update-model-context', {content:[{type:'text', text:'The user is looking at round ' + document.getElementById('meta').textContent}]}); log(r.error ? 'context refused: ' + r.error.message : 'context stored'); };
        document.getElementById('big').onclick = async () => { const r = await request('ui/request-display-mode', {mode:'fullscreen'}); log('mode: ' + r.result?.mode); };
        (async () => { await request('ui/initialize', {protocolVersion:'2026-01-26', appCapabilities:{}, clientInfo:{name:'demo', version:'1'}}); notify('ui/notifications/initialized', {}); })();
        </script></body></html>
        """;
}
