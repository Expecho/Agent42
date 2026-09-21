namespace Agent42.Tests;

/// <summary>
/// The transcript's Reconnect button for an MCP server whose sign-in expired (ADR 0081), guarded on the source text
/// served from the real <c>wwwroot</c> — the <see cref="McpServersAssetsTests"/> shape. The repo has no JS test
/// runner by design, so these prove the code says the right thing; exercise it by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class McpReauthAssetsTests(StaticAssetsServerFixture fixture)
{
    private readonly HttpClient _client = fixture.Client;

    // AC19 — the stream's mcpReauthRequired event renders a Reconnect button in the bubble, and the button opens the
    // OAuth flow in a NEW tab from the one shared start-URL source. A full navigation here would drop the open chat.
    [Fact]
    public async Task Transcript_RendersReconnectButton_OpeningANewTab()
    {
        var send = await _client.GetStringAsync("/js/send.js");
        var thread = await _client.GetStringAsync("/js/thread.js");
        var css = await _client.GetStringAsync("/styles.css");

        Assert.Contains("chunk.type === \"mcpReauthRequired\"", send);
        Assert.Contains("renderMcpReauth(agentMsg, chunk.mcpServerId, chunk.text)", send);

        Assert.Contains("import { mcpOAuthStartUrl } from \"./mcp.js\";", thread);
        Assert.Contains("export function renderMcpReauth(msg, serverId, name)", thread);
        Assert.Contains("window.open(mcpOAuthStartUrl(serverId), \"_blank\", \"noopener\")", thread);
        Assert.DoesNotContain("location.href = mcpOAuthStartUrl", thread);

        Assert.Contains(".msg__reauth {", css);
    }
}
