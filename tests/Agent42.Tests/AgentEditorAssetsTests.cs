namespace Agent42.Tests;

/// <summary>
/// Guards the per-agent history-mode toggle in the frontend (ADR 0016). Serves the real Backend
/// <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts on the key
/// source strings — the same shape as <see cref="ModelPickerAssetsTests"/>.
///
/// <para><b>Source-text heuristics.</b> The repo has no JS test runner by design, so these prove the
/// code <i>says</i> the right thing, not that a browser renders the checkbox or resumes the thread.
/// Exercise the real toggle by hand in DevHost.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class AgentEditorAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public AgentEditorAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact] // AC4 — the editor exposes the toggle, reads it on open, and sends it on save.
    public async Task AgentEditor_HasContinuousToggle_AndRoundTripsIt()
    {
        var html = await _client.GetStringAsync("/index.html");
        var app = await _fixture.GetAppScriptAsync();

        // The checkbox exists in the agent form.
        Assert.Contains("id=\"agentContinuous\"", html);

        // openAgentDialog reads the stored flag (defaulting to false for the new-agent case).
        Assert.Contains("agentContinuousInput.checked = agent?.continuous ?? false;", app);

        // saveAgent sends it in the payload.
        Assert.Contains("continuous: agentContinuousInput.checked", app);
    }

    [Fact] // ADR 0022 — the editor exposes a regenerate control and reveals the once-shown key on save.
    public async Task AgentEditor_HasApiKeyRegenerateAndReveal()
    {
        var html = await _client.GetStringAsync("/index.html");
        var app = await _fixture.GetAppScriptAsync();

        // The key section, regenerate button and reveal target exist in the agent form.
        Assert.Contains("id=\"agentKeySection\"", html);
        Assert.Contains("id=\"agentRegenerateKeyBtn\"", html);
        Assert.Contains("id=\"agentKeyValue\"", html);

        // The regenerate control sends the regenerate flag, and saveAgent reveals a returned raw key once.
        Assert.Contains("payload.regenerateKey = true", app);
        Assert.Contains("body.apiKey", app);
    }

    [Fact] // AC1/AC2/AC3 — a dedicated API-key tab whose run-endpoint URL shows always, key reveal lands here.
    public async Task AgentEditor_HasApiKeyTab_WithAlwaysVisibleRunUrl()
    {
        var html = await _client.GetStringAsync("/index.html");
        var app = await _fixture.GetAppScriptAsync();

        // The third tab + its panel exist (AC1).
        Assert.Contains("id=\"tabAgentApi\"", html);
        Assert.Contains("id=\"panelAgentApi\"", html);

        // The always-visible run-endpoint URL element exists and is populated with the absolute URL (AC2).
        Assert.Contains("id=\"agentRunUrl\"", html);
        Assert.Contains("`${location.origin}/api/agent-run`", app);

        // The post-save reveal switches to the API-key tab (index 2), where the key now lives (AC3).
        Assert.Contains("agentTabs.select(2)", app);
    }

    [Fact] // AC5 — continuous starts route onto a deterministic id AND resume the existing history.
    public async Task AgentEditor_ContinuousRoutesToDeterministicConversationId_AndResumes()
    {
        var app = await _fixture.GetAppScriptAsync();

        // The deterministic `agent-<id>` conversation id (collapses every start onto one grain);
        // a per-chat agent still gets a random UUID.
        Assert.Contains("`agent-${agent.id}`", app);
        Assert.Contains("crypto.randomUUID()", app);

        // The continuous branch delegates to selectConversation to load the ongoing thread instead
        // of showing a blank one — the anti-blank-thread logic. Dropping this must fail here.
        Assert.Contains("selectConversation(convId)", app);
    }
}
