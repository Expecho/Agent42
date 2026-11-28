namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of per-agent message retention (ADR 0140): the editor select, its option sets per history
/// mode, the per-chat-only lock, and the payload field. Same discipline as <see cref="ResearchAssetsTests"/> — the repo
/// has no JS test runner, so these assert on source strings of the real <c>wwwroot</c>; the rendered dialog is checked
/// manually in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class AgentRetentionAssetsTests(StaticAssetsServerFixture fixture)
{
    // AC13: the select and its note exist, and dom.js exports them for agents.js.
    [Fact]
    public async Task Editor_HasTheRetentionSelect()
    {
        var html = await fixture.Client.GetStringAsync("/index.html");
        var dom = await fixture.Client.GetStringAsync("/js/dom.js");

        Assert.Contains("id=\"agentRetention\"", html);
        Assert.Contains("id=\"agentRetentionNote\"", html);
        Assert.Contains("export const agentRetentionSelect = document.getElementById(\"agentRetention\")", dom);
    }

    // AC13: per-chat offers Never, continuous cannot (brick protection, ADR 0016/0068) and offers the legacy default
    // instead; the checkbox swaps the set; per-chat-only kinds lock the select; the payload carries the field.
    [Fact]
    public async Task Agents_OffersPerModeOptions_LocksPerChatOnlyKinds_AndSendsTheField()
    {
        var js = CodeOnly(await fixture.Client.GetStringAsync("/js/agents.js"));

        Assert.Contains("const RETENTION_PRESETS = [[60, \"1 hour\"], [1440, \"24 hours\"], [10080, \"7 days\"], [43200, \"30 days\"]]", js);
        Assert.Contains("continuous ? \"Default (24 hours)\" : \"Never\"", js);
        Assert.Contains("agentContinuousInput.addEventListener(\"change\"", js);
        Assert.Contains("agentRetentionSelect.disabled = perChatOnly", js);
        Assert.Contains("retentionMinutes: agentRetentionSelect.value ? Number(agentRetentionSelect.value) : null", js);
        Assert.Contains("agent?.retentionMinutes", js);
    }

    // AC13: a change under wwwroot/js/ bumps the cache name (CLAUDE.md rule 3).
    [Fact]
    public async Task ServiceWorker_CacheVersionMovedOn()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");

        Assert.Contains("agent42-shell-v96", sw);
    }

    private static string CodeOnly(string source) =>
        string.Join('\n', source
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));
}
