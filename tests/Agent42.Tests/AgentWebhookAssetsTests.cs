namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of the outbound webhook (ADR 0094). The repo has no JS test runner, so these are
/// source-text checks on the served assets; the behaviour is checked by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class AgentWebhookAssetsTests(StaticAssetsServerFixture fixture)
{
    // A module unreachable from main.js is invisible to every other guard (ADR 0041): it must be imported by agents.js.
    [Fact]
    public async Task WebhooksModule_IsImportedByAgents()
    {
        var agents = await fixture.Client.GetStringAsync("/js/agents.js");

        Assert.Contains("from \"./webhooks.js\"", agents, StringComparison.Ordinal);
        Assert.Contains("openWebhookPanel(", agents, StringComparison.Ordinal);
    }

    // The delivery log and the secret are server strings: textContent only, never innerHTML.
    [Fact]
    public async Task WebhooksModule_NeverUsesInnerHtml()
    {
        var module = await fixture.Client.GetStringAsync("/js/webhooks.js");

        Assert.DoesNotContain("innerHTML", module, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", module, StringComparison.Ordinal);
        Assert.Contains("row.textContent", module, StringComparison.Ordinal);
    }

    // saveAgent selects the API-key tab by index 2, so the webhook tab must be appended last.
    [Fact]
    public async Task WebhookTab_IsTheLastAgentTab()
    {
        var dom = await fixture.Client.GetStringAsync("/js/dom.js");
        var start = dom.IndexOf("export const agentTabs = wireTabs([", StringComparison.Ordinal);
        var tabs = dom[start..dom.IndexOf("]);", start, StringComparison.Ordinal)];

        Assert.True(tabs.IndexOf("tabAgentApi", StringComparison.Ordinal) < tabs.IndexOf("tabAgentWebhook", StringComparison.Ordinal));
        Assert.Equal(4, tabs.Split("getElementById(\"tabAgent", StringSplitOptions.None).Length - 1);
    }

    // SHELL is all-or-nothing: a module missing from it breaks the offline load.
    [Fact]
    public async Task Shell_ListsWebhooksModule()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/webhooks.js\"", sw, StringComparison.Ordinal);
    }

    // The panel tells the owner plainly what leaves the app (rule 13).
    [Fact]
    public async Task Panel_SaysWhatIsSent()
    {
        var html = await fixture.Client.GetStringAsync("/index.html");

        Assert.Contains("id=\"panelAgentWebhook\"", html, StringComparison.Ordinal);
        Assert.Contains("its prompt and the full reply are sent to this URL", html, StringComparison.Ordinal);
    }
}
