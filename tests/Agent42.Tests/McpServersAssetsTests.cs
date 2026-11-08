namespace Agent42.Tests;

/// <summary>
/// Guards the remote-MCP-servers UI wiring in the frontend (config-driven remote MCP servers). Serves the
/// real Backend <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts on the
/// key source strings — the same shape as <see cref="ModelPickerAssetsTests"/>.
///
/// <para><b>Source-text heuristics.</b> The repo has no JS test runner by design, so these prove the code
/// <i>says</i> the right thing, not that a browser renders the dialog. Exercise it by hand in DevHost
/// (Preferences → MCP servers).</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class McpServersAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public McpServersAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // The Chrome-extension ADR: the bridge-key panel offers the extension as the second client, inside the one-time
    // key reveal (the key is pasted into the extension's options), and says plainly what the extension may do.
    [Fact]
    public async Task BridgeKeyPanel_MentionsTheChromeExtension()
    {
        var html = await _client.GetStringAsync("/index.html");

        var reveal = html.IndexOf("id=\"mcpBridgeKeyReveal\"", StringComparison.Ordinal);
        var note = html.IndexOf("id=\"mcpBridgeExtensionNote\"", StringComparison.Ordinal);
        var end = html.IndexOf("</section>", reveal, StringComparison.Ordinal);
        Assert.True(reveal > 0 && note > reveal && note < end, "the extension note must sit inside the key reveal");
        Assert.Contains("https://github.com/Expecho/Agent42/releases?q=chrome-extension", html, StringComparison.Ordinal);
        Assert.Contains("runs page scripts without asking", html, StringComparison.Ordinal);
    }

    // C9 — the duplicate-create trap, guarded by SOURCE TEXT only (there is no JS runner). POST always
    // INSERTS because the id is server-minted, and the dialog now stays OPEN after a save, so the dialog must
    // adopt the response id and flip into edit mode. Without this line a second Save silently creates a
    // duplicate server, and no server-side guard exists. MUTATION-CHECKED: removing the assignment from
    // mcp.js fails this test.
    [Fact]
    public async Task SaveFlow_AdoptsTheResponseId_SoASecondSaveDoesNotDuplicate()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("editingMcpId = saved.id ?? editingMcpId;", app);
    }

    // The save-path auto-test must NOT dial a DISABLED server. The chat path connects only to enabled
    // servers (UserMcpToolProvider's Where(s => s.Enabled)), so auto-dialling one from a SAVE would send its
    // decrypted credentials somewhere they have never been — new egress from a save rather than from an
    // explicit press. Pressing Test still dials it, deliberately. Source-text only, like the rules above.
    [Fact]
    public async Task SaveFlow_DoesNotAutoTestADisabledServer()
    {
        var app = await _fixture.GetAppScriptAsync();

        var saveStart = app.IndexOf("async function saveMcpServer()", StringComparison.Ordinal);
        var saveBody = app[saveStart..app.IndexOf("async function deleteMcpServer", StringComparison.Ordinal)];

        var gate = saveBody.IndexOf("if (!mcpEnabledInput.checked)", StringComparison.Ordinal);
        var autoTest = saveBody.IndexOf("await testMcpServer(editingMcpId, mcpTestResult)", StringComparison.Ordinal);
        Assert.True(gate >= 0, "the save-path auto-test must be gated on the Enabled checkbox.");
        Assert.True(autoTest > gate, "the enabled gate must precede (and short-circuit) the auto-test.");
    }

    // A verdict about a server must not outlive the dialog it was rendered from, nor the server itself.
    [Fact]
    public async Task TabResultLine_IsClearedOnPreferencesOpenAndOnDelete()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("export function clearMcpTabResult()", app);
        // Called synchronously from openProfileDialog, so it cannot race the un-awaited loadMcpServers().
        // The whole function body, not a fixed-width window off its declaration: a window silently turns
        // "the second call is missing" and "the function grew past N characters" into the same failure.
        var profileBody = StaticAssetsServerFixture.Body(app, "export async function openProfileDialog()");
        Assert.True(
            profileBody.IndexOf("clearMcpTabResult()", StringComparison.Ordinal)
            < profileBody.IndexOf("loadMcpServers()", StringComparison.Ordinal),
            "clearMcpTabResult() must run before loadMcpServers() in openProfileDialog.");
    }

    // The save-path auto-test must NOT dial an interactive-OAuth server that has never signed in. Save does
    // not perform the sign-in, so the dial can only return "not connected (no OAuth token yet)" — an accurate
    // diagnosis rendered as a failure, at the one moment the user cannot act on it (the dialog is modal, so
    // the list's Connect glyph is inert underneath). Source-text only, like the enabled gate it follows.
    [Fact]
    public async Task SaveFlow_DoesNotAutoTestAnOAuthServerThatHasNoTokenYet()
    {
        var app = await _fixture.GetAppScriptAsync();

        var saveStart = app.IndexOf("async function saveMcpServer()", StringComparison.Ordinal);
        var saveBody = app[saveStart..app.IndexOf("async function deleteMcpServer", StringComparison.Ordinal)];

        var gate = saveBody.IndexOf("mcpAuthSelect.value === \"InteractiveOAuth\" && !storedMcpServer(editingMcpId)?.hasToken", StringComparison.Ordinal);
        var autoTest = saveBody.IndexOf("await testMcpServer(editingMcpId, mcpTestResult)", StringComparison.Ordinal);
        Assert.True(gate >= 0, "the save-path auto-test must be gated on a stored OAuth token.");
        Assert.True(autoTest > gate, "the OAuth gate must precede (and short-circuit) the auto-test.");

        // A save IS a success; rendering the sign-in prompt as a failure is the bug being fixed.
        var verdict = saveBody[gate..autoTest];
        Assert.Contains("ok: true", verdict);
        Assert.Contains("Press Connect", verdict);
    }

    // The Connect control must be reachable from the dialog itself. Before this it lived ONLY on the list
    // row, which showModal() renders inert — so the save-time "not connected" verdict pointed at a button
    // the user could not press. Both entry points share one navigation helper.
    [Fact]
    public async Task McpDialog_OffersConnectForAStoredOAuthServer()
    {
        var html = await _client.GetStringAsync("/index.html");
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("id=\"mcpConnectBtn\"", html);

        // type="button": inside a <form> the default is "submit", so pressing Connect would also save.
        var buttonStart = html.LastIndexOf("<button", html.IndexOf("id=\"mcpConnectBtn\"", StringComparison.Ordinal), StringComparison.Ordinal);
        var buttonTag = html[buttonStart..(html.IndexOf('>', buttonStart) + 1)];
        Assert.Contains("type=\"button\"", buttonTag);

        // One navigation helper, used by the row glyph and the dialog button alike.
        Assert.Contains("function startMcpOAuth(serverId)", app);
        Assert.Contains("startMcpOAuth(editingMcpId)", app);
        Assert.Contains("startMcpOAuth(server.id)", app);

        // Shown only for a STORED OAuth server — the flow returns into this user's overlay by id.
        Assert.Contains("mcpConnectBtn.hidden = !(isOAuth && editingMcpId);", app);
    }

    // M3 — the save path's refreshToolCatalog() must stay UNCONDITIONAL (ADR 0026): a save whose auto-test
    // then fails must still update the pickers, because the server's own checkbox is driven by the save, not
    // by reachability. Pinned by source text and MUTATION-CHECKED: moving the call inside the test's success
    // branch fails this test.
    [Fact]
    public async Task SaveFlow_RefreshesTheToolCatalogUnconditionally()
    {
        var app = await _fixture.GetAppScriptAsync();

        var saveStart = app.IndexOf("async function saveMcpServer()", StringComparison.Ordinal);
        Assert.True(saveStart >= 0, "saveMcpServer() not found in the module graph.");
        var saveBody = app[saveStart..app.IndexOf("async function deleteMcpServer", StringComparison.Ordinal)];

        // The refresh sits at statement level in saveMcpServer, before the auto-test — never guarded by the
        // test's outcome.
        var refreshIndex = saveBody.IndexOf("await refreshToolCatalog();", StringComparison.Ordinal);
        var autoTestIndex = saveBody.IndexOf("testMcpServer(editingMcpId, mcpTestResult)", StringComparison.Ordinal);
        Assert.True(refreshIndex >= 0, "saveMcpServer() must still refresh the tool catalog (ADR 0026).");
        Assert.True(autoTestIndex > refreshIndex,
            "The unconditional refreshToolCatalog() must run BEFORE the auto-test, never inside its success branch.");
    }

    private static string SaveBody(string app)
    {
        var start = app.IndexOf("async function saveMcpServer()", StringComparison.Ordinal);
        return app[start..app.IndexOf("async function deleteMcpServer", start, StringComparison.Ordinal)];
    }
}
