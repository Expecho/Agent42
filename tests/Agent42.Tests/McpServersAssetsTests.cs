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
    private readonly HttpClient _client;

    public McpServersAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

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
    public async Task AppJs_WiresTheMcpCrudAgainstTheApi()
    {
        var app = await _client.GetStringAsync("/app.js");

        // CRUD against /api/mcp-servers, and the tab is loaded when Preferences opens.
        Assert.Contains("/api/mcp-servers", app);
        Assert.Contains("function loadMcpServers()", app);
        Assert.Contains("loadMcpServers();", app);
        // The secret is write-only: never prefilled, blank means keep.
        Assert.Contains("mcpHeaderValueInput.value = \"\";", app);
        // Header-only fields toggle with the auth mode.
        Assert.Contains("function syncMcpAuthUi()", app);
    }
}
