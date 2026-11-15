namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of MCP Apps (ADR 0133). The repo has no JS test runner, so these assert on the source of the
/// real <c>wwwroot</c> module, as <see cref="McpElicitationAssetsTests"/> does; the behaviour in a browser is checked by
/// hand in DevHost. Each guard pins a line that, removed, turns a third-party view into a hole.
/// </summary>
[Collection("StaticAssets")]
public sealed class McpAppsAssetsTests(StaticAssetsServerFixture fixture)
{
    private async Task<string> ModuleAsync() => CodeOnly(await fixture.Client.GetStringAsync("/js/mcpapps.js"));

    private static string CodeOnly(string source) =>
        string.Join('\n', System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Split('\n').Select(line =>
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment >= 0 ? line[..comment] : line;
        }));

    // AC12: reachable from main.js (it is configured there), listed in the shell, and the capability is advertised.
    [Fact]
    public async Task Module_IsReachable_InTheShell_AndAdvertised()
    {
        var main = await fixture.Client.GetStringAsync("/js/main.js");
        var sw = await fixture.Client.GetStringAsync("/sw.js");
        var clientTools = await fixture.Client.GetStringAsync("/js/clienttools.js");

        Assert.Contains("configureMcpApps(", main, StringComparison.Ordinal);
        Assert.Contains("\"/js/mcpapps.js\"", sw, StringComparison.Ordinal);
        Assert.Contains("\"mcp_app\"", clientTools, StringComparison.Ordinal);
    }

    // AC11: the sandbox attribute is exactly allow-scripts. The same-origin token never appears in code.
    [Fact]
    public async Task TheFrame_IsSandboxedWithoutSameOrigin()
    {
        var code = await ModuleAsync();

        Assert.Contains("setAttribute(\"sandbox\", \"allow-scripts\")", code, StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", code, StringComparison.Ordinal);
        Assert.DoesNotContain("srcdoc", code, StringComparison.Ordinal);
        Assert.Contains("referrerPolicy = \"no-referrer\"", code, StringComparison.Ordinal);
    }

    // AC11: a message is authenticated by the window it came from, never by origin, and replies use targetOrigin "*".
    [Fact]
    public async Task Messages_AreAuthenticatedBySource_AndShapeChecked()
    {
        var code = await ModuleAsync();

        Assert.Contains("iframe.contentWindow === source", code, StringComparison.Ordinal);
        Assert.Contains("cardFor(event.source)", code, StringComparison.Ordinal);
        Assert.Contains("data.jsonrpc !== \"2.0\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("event.origin", code, StringComparison.Ordinal);
        Assert.DoesNotContain("postMessage(", code.Replace("?.postMessage({ jsonrpc: \"2.0\"", "").Replace("win.postMessage({ jsonrpc: \"2.0\"", ""),
            StringComparison.Ordinal);
    }

    // AC11: nothing goes to the view before it says initialized, and the protocol constants are the spec's.
    [Fact]
    public async Task NothingIsSentBeforeInitialized_AndTheProtocolIsTheSpecs()
    {
        var code = await ModuleAsync();

        Assert.Contains("if (!card.initialized) return;", code, StringComparison.Ordinal);
        Assert.Contains("const PROTOCOL_VERSION = \"2026-01-26\";", code, StringComparison.Ordinal);
        foreach (var method in new[]
        {
            "ui/initialize", "ui/notifications/initialized", "ui/notifications/tool-input", "ui/notifications/tool-result",
            "ui/notifications/tool-cancelled", "ui/notifications/host-context-changed", "ui/notifications/size-changed",
            "ui/open-link", "ui/message", "ui/request-display-mode", "ui/update-model-context", "ui/resource-teardown",
            "tools/call", "resources/read",
        })
        {
            Assert.Contains($"\"{method}\"", code, StringComparison.Ordinal);
        }

        // tool-input is sent before tool-result, in start().
        Assert.True(
            code.IndexOf("\"ui/notifications/tool-input\", {", StringComparison.Ordinal)
                < code.IndexOf("\"ui/notifications/tool-result\", result", StringComparison.Ordinal),
            "tool-input must be sent before tool-result");
    }

    // AC11: both injection methods sit behind the confirm gate, and the gate is the shared confirm dialog.
    [Fact]
    public async Task MessageAndModelContext_AreConfirmGated()
    {
        var code = await ModuleAsync();
        var send = Slice(code, "async function sendMessage", "async function updateContext");
        var context = Slice(code, "async function updateContext", "function textOf");

        Assert.Contains("askUserConfirm(", send, StringComparison.Ordinal);
        Assert.Contains("askUserConfirm(", context, StringComparison.Ordinal);
        // The message is sent only after approval, and the context stored only after approval.
        Assert.True(send.IndexOf("askUserConfirm(", StringComparison.Ordinal) < send.IndexOf("host.send(", StringComparison.Ordinal));
        Assert.True(context.IndexOf("askUserConfirm(", StringComparison.Ordinal) < context.IndexOf("card.context =", StringComparison.Ordinal));
        Assert.Contains("import { askUserConfirm } from \"./clienttools.js\"", code, StringComparison.Ordinal);
        // All open cards together stay under the server cap, or a request would silently lose its context.
        Assert.Contains("otherContextLength(card) + text.length > MAX_TOTAL_CONTEXT_CHARS", context, StringComparison.Ordinal);

        var clientTools = await fixture.Client.GetStringAsync("/js/clienttools.js");
        Assert.Contains("export async function askUserConfirm", clientTools, StringComparison.Ordinal);
    }

    // AC16: a stored card is inert until activated, and the three acting requests are guarded; a live card is active.
    [Fact]
    public async Task AReplayedCard_IsInertUntilActivated()
    {
        var code = await ModuleAsync();

        Assert.Contains("active: live", code, StringComparison.Ordinal);
        Assert.Contains("if (!live) bar.append(activateButton(card));", code, StringComparison.Ordinal);
        Assert.Contains("card.active = true", code, StringComparison.Ordinal);
        Assert.Contains("card.active ? run() : fail(", code, StringComparison.Ordinal);
        foreach (var method in new[] { "\"ui/message\"", "\"ui/update-model-context\"", "\"tools/call\"" })
        {
            var arm = code[code.IndexOf($"case {method}", StringComparison.Ordinal)..];
            Assert.StartsWith($"case {method}:\n      return guarded(", arm.Replace("\r", ""), StringComparison.Ordinal);
        }

        // resources/read reads a view and acts on nothing, so it is the one request not guarded.
        Assert.DoesNotContain("guarded(card, id, () => proxy(card, id, \"resources/read\"", code, StringComparison.Ordinal);
    }

    // The view tears down properly: told before its frame goes, and the live card receives the result as a notification.
    [Fact]
    public async Task TheFrame_IsTornDownAfterTheViewIsTold()
    {
        var code = await ModuleAsync();
        var remove = Slice(code, "function removeCard", "function activateButton");

        Assert.True(
            remove.IndexOf("ui/resource-teardown", StringComparison.Ordinal) < remove.IndexOf("card.root.remove()", StringComparison.Ordinal));
        Assert.Contains("teardownAppCards();", await fixture.Client.GetStringAsync("/js/thread.js"), StringComparison.Ordinal);
    }

    // A link opens only for http(s), and only on a real click.
    [Fact]
    public async Task OpenLink_IsHttpOnly_AndNeedsAUserGesture()
    {
        var code = await ModuleAsync();

        Assert.Contains("url.protocol !== \"https:\" && url.protocol !== \"http:\"", code, StringComparison.Ordinal);
        Assert.Contains("navigator.userActivation?.isActive", code, StringComparison.Ordinal);
        Assert.Contains("\"noopener,noreferrer\"", code, StringComparison.Ordinal);
    }

    // The stream and history both render the card, and a stop reaches the views.
    [Fact]
    public async Task SendAndHistory_RenderTheCard()
    {
        var send = await fixture.Client.GetStringAsync("/js/send.js");
        var conversations = await fixture.Client.GetStringAsync("/js/conversations.js");

        Assert.Contains("renderMcpAppCard(agentMsg, convId", send, StringComparison.Ordinal);
        Assert.Contains("{ live: true }", send, StringComparison.Ordinal);
        Assert.Contains("renderMcpAppCards(agentMsg, convId, last.toolCalls)", send, StringComparison.Ordinal);
        Assert.Contains("cancelAppCards()", send, StringComparison.Ordinal);
        Assert.Contains("renderMcpAppCards(msg, id, turn.toolCalls)", conversations, StringComparison.Ordinal);
    }

    private static string Slice(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        var end = source.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"could not slice {from}..{to}");
        return source[start..end];
    }
}
