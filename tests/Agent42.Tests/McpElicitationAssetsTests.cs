namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of MCP elicitation (ADR 0095). The repo has no JS test runner, so these assert on the
/// source of the real <c>wwwroot</c> module graph (everything reachable from <c>main.js</c>), as
/// <see cref="ClientToolsAssetsTests"/> does. The behaviour in a browser is checked by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class McpElicitationAssetsTests(StaticAssetsServerFixture fixture)
{
    [Fact]
    public async Task ElicitationModule_IsReachableFromMain()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("export function showElicitation", app, StringComparison.Ordinal);
        Assert.Contains("mcp_elicitation: (input) => showElicitation(input)", app, StringComparison.Ordinal);
    }

    // Everything the MCP server sends is untrusted: the module has no markup sink at all.
    [Fact]
    public async Task ServerText_IsSetWithTextContent_NeverInnerHtml()
    {
        var module = await ModuleAsync();

        Assert.DoesNotContain("innerHTML", module, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", module, StringComparison.Ordinal);
        Assert.Contains("mcpElicitationMessage.textContent", module, StringComparison.Ordinal);
        Assert.Contains("mcpElicitationUrl.textContent", module, StringComparison.Ordinal);
    }

    // The URL opens in exactly one place: the accept answer built for the Open click, with noopener + noreferrer.
    [Fact]
    public async Task UrlMode_OpensOnlyOnClick_WithNoopener()
    {
        var module = await ModuleAsync();

        Assert.Single(module.Split("window.open(").Skip(1));
        var renderUrl = CodeOnly(StaticAssetsServerFixture.Body(module, "function renderUrl"));
        var accept = renderUrl.IndexOf("acceptAnswer = () =>", StringComparison.Ordinal);
        Assert.True(accept >= 0 && renderUrl.IndexOf("window.open(", StringComparison.Ordinal) > accept,
            "window.open must sit inside the click-time accept answer, never in render");
        Assert.Contains("window.open(url.href, \"_blank\", \"noopener,noreferrer\")", renderUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UrlMode_NeverPrefetches()
    {
        var module = await ModuleAsync();

        Assert.DoesNotContain("fetch(", module, StringComparison.Ordinal);
        Assert.DoesNotContain("<link", module, StringComparison.Ordinal);
        Assert.DoesNotContain("new Image", module, StringComparison.Ordinal);
    }

    // Esc closes a dialog WITHOUT setting returnValue; a stale "accept" would make Esc submit the previous answer.
    [Fact]
    public async Task Dialog_ResetsReturnValueBeforeShowModal()
    {
        var show = CodeOnly(StaticAssetsServerFixture.Body(await ModuleAsync(), "export function showElicitation"));

        var reset = show.IndexOf("mcpElicitationDialog.returnValue = \"\"", StringComparison.Ordinal);
        Assert.True(reset >= 0 && reset < show.IndexOf("showModal()", StringComparison.Ordinal),
            "returnValue must be reset before the dialog opens");
    }

    // The request advertises the capability, and the stream line is dispatched as a client tool without awaiting it.
    [Fact]
    public async Task ClientTools_AdvertisesMcpElicitation()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("\"mcp_elicitation\",", app, StringComparison.Ordinal);
        var start = app.IndexOf("chunk.type === \"mcpElicitation\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "send.js does not handle the mcpElicitation line");
        var arm = CodeOnly(app[start..app.IndexOf("} else if", start, StringComparison.Ordinal)]);
        Assert.Contains("runClientTool(convId, {", arm, StringComparison.Ordinal);
        Assert.DoesNotContain("await runClientTool", arm, StringComparison.Ordinal);
    }

    // Enter in a one-field form submits implicitly, and method="dialog" would close with an empty returnValue: a
    // cancel that discards the typed value. The submit event is routed through the Submit button instead.
    [Fact]
    public async Task ImplicitSubmit_IsRoutedThroughTheSubmitButton()
    {
        var wire = CodeOnly(StaticAssetsServerFixture.Body(await ModuleAsync(), "export function wireElicitation"));

        Assert.Contains("addEventListener(\"submit\"", wire, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault()", wire, StringComparison.Ordinal);
        Assert.Contains("mcpElicitationSubmitBtn.click()", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellListsElicitationModule()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/elicitation.js\"", sw, StringComparison.Ordinal);
    }

    private async Task<string> ModuleAsync() => await fixture.Client.GetStringAsync("/js/elicitation.js");

    private static string CodeOnly(string source) =>
        string.Join('\n', source
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));
}
