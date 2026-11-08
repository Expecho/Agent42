namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of the outbound webhook (ADR 0094). The repo has no JS test runner, so these are
/// source-text checks on the served assets; the behaviour is checked by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class AgentWebhookAssetsTests(StaticAssetsServerFixture fixture)
{
    // SHELL is all-or-nothing: a module missing from it breaks the offline load.
    [Fact]
    public async Task Shell_ListsWebhooksModule()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/webhooks.js\"", sw, StringComparison.Ordinal);
    }

}
