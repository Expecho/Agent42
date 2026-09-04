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

    [Fact]
    public async Task Preferences_HasAnMcpServersTabAndPanel()
    {
        var html = await _client.GetStringAsync("/index.html");

        Assert.Contains("id=\"tabMcp\"", html);
        Assert.Contains("id=\"panelMcp\"", html);
        Assert.Contains("id=\"mcpServerList\"", html);
        Assert.Contains("id=\"addMcpServerBtn\"", html);
    }

    [Fact]
    public async Task McpDialog_HasEndpointAuthAndWriteOnlyHeaderSecret()
    {
        var html = await _client.GetStringAsync("/index.html");

        Assert.Contains("id=\"mcpDialog\"", html);
        Assert.Contains("id=\"mcpEndpoint\"", html);
        Assert.Contains("id=\"mcpAuth\"", html);
        Assert.Contains("id=\"mcpEnabled\"", html);
        // The header secret is a write-only password field, blank-keeps on edit.
        Assert.Contains("id=\"mcpHeaderValue\"", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("leave blank to keep the stored value", html);
    }

    [Fact]
    public async Task McpDialog_HasClientCredentialsOptionAndFields()
    {
        var html = await _client.GetStringAsync("/index.html");

        // The ClientCredentials (MSAL) auth option and its fields (secret write-only).
        Assert.Contains("value=\"ClientCredentials\"", html);
        Assert.Contains("id=\"mcpAuthority\"", html);
        Assert.Contains("id=\"mcpClientId\"", html);
        Assert.Contains("id=\"mcpClientSecret\"", html);
        Assert.Contains("id=\"mcpScopes\"", html);
    }

    [Fact]
    public async Task McpDialog_HasInteractiveOAuthOptionAndConnectFlow()
    {
        var html = await _client.GetStringAsync("/index.html");
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("value=\"InteractiveOAuth\"", html);
        Assert.Contains("id=\"mcpOAuthNote\"", html);
        // The Connect button starts the sign-in flow, and the callback return is handled on load.
        Assert.Contains("/api/mcp/oauth/start?serverId=", app);
        Assert.Contains("function initMcpOAuthReturn()", app);
        Assert.Contains("mcp_oauth", app);
    }

    [Fact]
    public async Task AppJs_WiresTheMcpCrudAgainstTheApi()
    {
        var app = await _fixture.GetAppScriptAsync();

        // CRUD against /api/mcp-servers, and the tab is loaded when Preferences opens.
        Assert.Contains("/api/mcp-servers", app);
        Assert.Contains("function loadMcpServers()", app);
        Assert.Contains("loadMcpServers();", app);
        // The secret is write-only: never prefilled, blank means keep.
        Assert.Contains("mcpHeaderValueInput.value = \"\";", app);
        // Header-only fields toggle with the auth mode.
        Assert.Contains("function syncMcpAuthUi()", app);
    }

    // AC12: the dialog carries the Test button and both result lines, and the tab line sits OUTSIDE
    // #mcpServerList (renderMcpServers clears that list's innerHTML, and openProfileDialog calls
    // loadMcpServers() un-awaited, so a result inside it would be wiped by the in-flight render).
    [Fact]
    public async Task McpDialog_HasATestButtonAndBothResultLines()
    {
        var html = await _client.GetStringAsync("/index.html");

        Assert.Contains("id=\"mcpTestBtn\"", html);
        Assert.Contains("id=\"mcpTestResult\"", html);
        Assert.Contains("id=\"mcpTabResult\"", html);

        // The Test button must be type="button": inside a <form> the default is "submit", so a Test press
        // would silently save the server too.
        var buttonStart = html.LastIndexOf("<button", html.IndexOf("id=\"mcpTestBtn\"", StringComparison.Ordinal), StringComparison.Ordinal);
        var buttonTag = html[buttonStart..(html.IndexOf('>', buttonStart) + 1)];
        Assert.Contains("type=\"button\"", buttonTag);

        // The tab result line is a sibling of the list, not a child of it.
        var listIndex = html.IndexOf("id=\"mcpServerList\"", StringComparison.Ordinal);
        var listClose = html.IndexOf("</ul>", listIndex, StringComparison.Ordinal);
        Assert.True(html.IndexOf("id=\"mcpTabResult\"", StringComparison.Ordinal) > listClose,
            "#mcpTabResult must sit outside #mcpServerList, which renderMcpServers() clears.");
    }

    // AC12: the client actually calls the endpoint, and shares ONE renderer between the two targets.
    [Fact]
    public async Task AppJs_CallsTheConnectionTestEndpoint()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("/test`, { method: \"POST\" }", app);
        Assert.Contains("async function testMcpServer(id, target)", app);
        Assert.Contains("function renderTestResult(target, outcome)", app);
        // The OAuth return auto-tests the server the callback named, into the TAB's line.
        Assert.Contains("testMcpServer(serverId, mcpTabResult)", app);
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
        var profileStart = app.IndexOf("export async function openProfileDialog()", StringComparison.Ordinal);
        var profileBody = app[profileStart..(profileStart + 1200)];
        Assert.True(
            profileBody.IndexOf("clearMcpTabResult()", StringComparison.Ordinal)
            < profileBody.IndexOf("loadMcpServers()", StringComparison.Ordinal),
            "clearMcpTabResult() must run before loadMcpServers() in openProfileDialog.");
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
}
