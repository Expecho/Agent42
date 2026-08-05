namespace Agent42.Tests;

/// <summary>
/// Guards the model picker's multi-provider wiring in the frontend (ADR 0013). Serves the real Backend
/// <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts on the key
/// source strings — the same shape as <see cref="LocationAssetsTests"/>.
///
/// <para><b>Source-text heuristics.</b> The repo has no JS test runner by design, so these prove the
/// code <i>says</i> the right thing, not that a browser renders the grouped pickers. There are three of
/// them — the composer, the agent dialog, and the preferences default-model select — and all three are fed
/// by the same <c>fillModelOptions</c>/<c>selectModel</c> pair. Exercise them by hand in DevHost: its
/// static catalog spans three providers (foundry/github/images) on purpose.</para>
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

    [Fact] // The optgroup is grouped by provider KEY but LABELLED with the provider's display name when it
           // has one, falling back to the key. One shared helper feeds every provider-grouped select.
    public async Task ModelPicker_LabelsGroupWithProviderDisplayName_FallingBackToKey()
    {
        var app = await _client.GetStringAsync("/app.js");

        // The single source of the group label: display name ?? key, then "Models" for the unkeyed default.
        Assert.Contains("function providerGroupLabel(m)", app);
        Assert.Contains("(m.providerDisplayName ?? m.provider) || \"Models\"", app);

        // Both provider-grouped selects use the shared helper rather than the bare key, and neither
        // hand-rolls `provider || "Models"` any more.
        Assert.Contains("group.label = providerGroupLabel(m);", app);
        Assert.DoesNotContain("group.label = provider || \"Models\";", app);
    }

    [Fact] // AC14 — one append-if-missing helper, not one per picker (duplication_pct_max: 3).
    public async Task BothPickers_ShareTheSameSelectHelper()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("function selectModel(select, id)", app);
        Assert.Contains("selectModel(agentModelSelect,", app);
        Assert.Contains("selectModel(modelSelect, id)", app);
    }

    // AC7: the preferences default-model select PUTs to the endpoint and reuses the shared append-if-missing
    // helper rather than hand-rolling a third picker (duplication_pct_max: 3).
    [Fact]
    public async Task DefaultModelPicker_PutsToTheEndpoint_AndSharesTheSelectHelper()
    {
        var app = await _client.GetStringAsync("/app.js");
        var html = await _client.GetStringAsync("/index.html");

        Assert.Contains("id=\"defaultModelSelect\"", html);
        // The verb must be bound to THIS fetch: `method: "PUT"` alone appears on other calls too, so a
        // bare Contains would pass whatever verb the default-model call actually used.
        var fetchAt = app.IndexOf("fetch(\"/api/default-model\"", StringComparison.Ordinal);
        Assert.True(fetchAt >= 0, "no fetch of /api/default-model in app.js");
        Assert.Contains("method: \"PUT\"", app[fetchAt..(fetchAt + 120)]);
        // The catalog fill and the append-if-missing selection are the SHARED helpers, not copies.
        Assert.Contains("fillModelOptions(defaultModelSelect);", app);
        Assert.Contains("selectModel(defaultModelSelect, userDefaultModel);", app);
    }

    // AC8: "New chat" re-points the composer picker at the default, so a new chat does not inherit the
    // model the previously-opened conversation was pinned to.
    [Fact]
    public async Task StartNewConversation_ResetsPickerToTheDefault()
    {
        var app = await _client.GetStringAsync("/app.js");

        var start = app.IndexOf("function startNewConversation()", StringComparison.Ordinal);
        Assert.True(start >= 0, "startNewConversation() not found in app.js");
        var body = app[start..app.IndexOf("\n  }", start, StringComparison.Ordinal)];

        Assert.Contains("if (defaultModel) setModel(defaultModel);", body);
    }

    // AC10: changing the preference must NOT retarget the conversation the user has open — send()
    // transmits modelSelect.value, so the post-PUT catalog refresh takes the pin:false opt-out.
    [Fact]
    public async Task DefaultModelChange_DoesNotRepinTheComposer()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("async function loadModels({ pin = true } = {})", app);
        Assert.Contains("if (pin && defaultModel) setModel(defaultModel);", app);
        Assert.Contains("loadModels({ pin: false });", app);
        // One fetch path, not a cloned catalog request for the preference case.
        Assert.Equal(1, app.Split("fetch(\"/api/models\")").Length - 1);
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
