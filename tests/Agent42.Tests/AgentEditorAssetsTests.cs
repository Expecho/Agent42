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

    // AC19 (ADR 0082). The research agent is per-chat only: the editor disables the continuous box and says why,
    // and the list marks the agent. The server forces the flag off regardless (AgentRegistryGrainTests), so this
    // guards the half a user sees — a box they can tick that silently does nothing would be worse than a lock.
    [Fact]
    public async Task AgentEditor_LocksContinuousForResearch_AndBadgesTheAgent()
    {
        var html = await _client.GetStringAsync("/index.html");
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("id=\"agentContinuousNote\"", html);
        var open = StaticAssetsServerFixture.Body(app, "function openAgentDialog");
        Assert.Contains("applyKindLock(agent?.kind", open);
        var lockBody = StaticAssetsServerFixture.Body(app, "function applyKindLock");
        Assert.Contains("kind === \"research\"", lockBody);
        Assert.Contains("agentContinuousInput.disabled = perChatOnly", lockBody);
        Assert.Contains("agentContinuousNote.textContent", lockBody);
        Assert.Contains("agent__badge", app);
    }

    // AC16 (copilot kind). The type is offered on CREATE only and rides the POST; choosing copilot locks the continuous
    // box like research; a copilot agent is badged; its chat locks incognito off; and the composer refuses a document
    // in a copilot chat before uploading it. The server enforces each of these too (CopilotAgentTests, AgentApiTests).
    [Fact]
    public async Task AgentEditor_OffersCopilotOnCreateOnly_AndGatesItsChat()
    {
        var html = await _client.GetStringAsync("/index.html");
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("id=\"agentKindSelect\"", html);
        Assert.Contains("<option value=\"copilot\">", html);
        var open = StaticAssetsServerFixture.Body(app, "function openAgentDialog");
        Assert.Contains("agentKindField.hidden = !!agent", open);
        var save = StaticAssetsServerFixture.Body(app, "async function saveAgent");
        Assert.Contains("if (!editingAgentId && agentKindSelect.value) payload.kind = agentKindSelect.value", save);
        Assert.Contains("kind === \"copilot\"", StaticAssetsServerFixture.Body(app, "function applyKindLock"));
        Assert.Contains("agentKindSelect.addEventListener(\"change\"", app);
        Assert.Contains("agent.kind === \"copilot\"", app);

        var start = StaticAssetsServerFixture.Body(app, "export function startAgentChat");
        Assert.Contains("setActiveAgentKind(agent.kind", start);
        Assert.Contains("setIncognito(false, false)", start);
        var files = StaticAssetsServerFixture.Body(app, "export async function handleFiles");
        Assert.Contains("activeAgentKind === \"copilot\"", files);
        var select = StaticAssetsServerFixture.Body(app, "export async function selectConversation");
        Assert.Contains("activeAgentKind = binding.kind", select);
    }

    // AC17 (ADR 0070). The MCP-exposure toggle, and — the half worth guarding by source text — the
    // regenerate OFFER on the OFF -> ON transition.
    //
    // That offer is the only place in the UI that says out loud what turning the flag on does: the ADR 0022
    // key was minted under a write-only contract, and exposure turns it into a read credential that can
    // return document excerpts, the user's street address and prior turns. Delete the condition and every
    // other test here still passes, because nothing else observes it.
    [Fact]
    public async Task AgentEditor_HasMcpExposureToggle_AndOffersAKeyRotationWhenItIsTurnedOn()
    {
        var html = await _client.GetStringAsync("/index.html");
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("id=\"agentMcpExposed\"", html);
        Assert.Contains("id=\"agentMcpUrl\"", html);

        Assert.Contains("agentMcpExposedInput.checked = agent?.mcpExposed ?? false;", app);
        Assert.Contains("mcpExposed: agentMcpExposedInput.checked", app);

        // The transition, not merely the current value: `checked && !wasChecked`, and only for a SAVED
        // agent (a create mints a fresh key anyway, so there is nothing to re-consent to).
        Assert.Contains("editingAgentId && agentMcpExposedInput.checked && !mcpExposedWhenOpened", app);
        Assert.Contains("mcpExposedWhenOpened = agentMcpExposedInput.checked;", app);
        // And the save adopts the new state, so a dialog left open does not re-prompt on every Save.
        Assert.Contains("mcpExposedWhenOpened = payload.mcpExposed;", app);
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
