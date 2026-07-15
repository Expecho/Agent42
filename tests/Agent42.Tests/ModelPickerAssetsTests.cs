namespace Agent42.Tests;

/// <summary>
/// Guards the model picker's multi-provider wiring in the frontend (ADR 0013). Serves the real Backend
/// <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts on the key
/// source strings — the same shape as <see cref="LocationAssetsTests"/>.
///
/// <para><b>Source-text heuristics.</b> The repo has no JS test runner by design, so these prove the
/// code <i>says</i> the right thing, not that a browser renders two grouped pickers. Exercise both
/// pickers by hand in DevHost — its static catalog spans two providers on purpose.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class ModelPickerAssetsTests
{
    private readonly HttpClient _client;

    public ModelPickerAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

    [Fact] // AC14
    public async Task ModelPicker_ReadsQualifiedId_AndGroupsByProvider()
    {
        var app = await _client.GetStringAsync("/app.js");

        // The catalog's qualified id, not the old bare deployment name.
        Assert.Contains("opt.value = m.id;", app);
        Assert.DoesNotContain("m.deployment", app);

        // Grouped under one <optgroup> per provider.
        Assert.Contains("createElement(\"optgroup\")", app);
        Assert.Contains("m.provider", app);
    }

    [Fact] // AC14 — one append-if-missing helper, not one per picker (duplication_pct_max: 3).
    public async Task BothPickers_ShareTheSameSelectHelper()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("function selectModel(select, id)", app);
        Assert.Contains("selectModel(agentModelSelect,", app);
        Assert.Contains("selectModel(modelSelect, id)", app);
    }

    [Fact] // A raw "github:openai/gpt-4o-mini" is not a label a user should ever see.
    public async Task AgentChip_RendersADisplayName_NotARawQualifiedId()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("function modelLabel(id)", app);
        Assert.Contains("model.textContent = modelLabel(agent.model);", app);
        Assert.DoesNotContain("model.textContent = agent.model;", app);
    }
}
