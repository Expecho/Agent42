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
        Assert.Contains("renderMcpReauth(agentMsg, chunk.mcpServerId, chunk.text, turnWatch)", send);

        Assert.Contains("import { mcpOAuthStartUrl } from \"./mcp.js\";", thread);
        Assert.Contains("export function renderMcpReauth(msg, serverId, name", thread);
        Assert.Contains("window.open(mcpOAuthStartUrl(serverId), \"_blank\", \"noopener\")", thread);
        Assert.DoesNotContain("location.href = mcpOAuthStartUrl", thread);

        Assert.Contains(".msg__reauth {", css);
    }

    // ADR 0087 — once the user has signed back in, the turn is re-sent by itself. The OAuth return tab
    // wakes the chat tab over a BroadcastChannel, a visibility change re-checks for the PWA case (a system-browser
    // tab cannot reach it), and the SERVER list is the only judge of "signed in". One watch per turn, armed only by
    // the click, cancelled by the next send, and the replay keeps the turn's attachments and leaves the composer.
    [Fact]
    public async Task Reconnect_ResendsTheTurn_OnceSignedInAgain()
    {
        var send = await _client.GetStringAsync("/js/send.js");
        var thread = await _client.GetStringAsync("/js/thread.js");
        var mcp = await _client.GetStringAsync("/js/mcp.js");

        Assert.Contains("export function watchMcpReconnect(onReconnected)", mcp);
        Assert.Contains("const MCP_OAUTH_CHANNEL = \"agent42-mcp-oauth\";", mcp);
        Assert.Contains("channel.postMessage({ result, serverId });", mcp);
        Assert.Contains("document.addEventListener(\"visibilitychange\", onVisible);", mcp);
        Assert.Contains("s.hasToken && !s.needsReauth", mcp);

        Assert.Contains("renderMcpReauth(msg, serverId, name, watch = null)", thread);
        Assert.Contains("watch?.add(serverId);", thread);
        Assert.Contains("watch?.arm();", thread);

        Assert.Contains("turnWatch ??= resendAfterReconnect(prompt, turnAttachments, convId, agentMsg);", send);
        Assert.Contains("export async function send(prompt, replay = null)", send);
        Assert.Contains("reconnectWatch?.cancel();", send);
        Assert.Contains("send(prompt, { attachments });", send);
        Assert.Contains("const turnAttachments = replay ? replay.attachments : pendingAttachments;", send);
        Assert.Contains("if (!replay) clearPendingAttachments();", send);
    }
}
