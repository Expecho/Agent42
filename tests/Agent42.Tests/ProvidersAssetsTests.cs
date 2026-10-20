namespace Agent42.Tests;

/// <summary>
/// Guards the provider dialog's Anthropic half (ADR 0103). The repo has no JS test runner, so these assert on source
/// strings of the real <c>wwwroot</c>; saving an Anthropic provider is checked manually in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class ProvidersAssetsTests(StaticAssetsServerFixture fixture)
{
    // AC15: the kind is offered, and the endpoint is pre-filled only when it is empty (never over a typed or stored one).
    [Fact]
    public async Task ProvidersUi_OffersAnthropic_PrefillsEmptyEndpoint()
    {
        var html = await fixture.Client.GetStringAsync("/index.html");
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("<option value=\"Anthropic\">", html);
        Assert.Contains("id=\"providerAnthropicNote\"", html);
        Assert.Contains("const ANTHROPIC_ENDPOINT = \"https://api.anthropic.com\";", app);
        Assert.Contains("if (kind === \"Anthropic\" && !providerEndpointInput.value.trim())", app);
    }

    // ADR 0108 AC4/AC10: "Sign in with GitHub" shows only when the server can run it, says the models are an add-on for
    // Copilot agents, the connected row has no edit button (the API refuses edits), and the callback return is handled.
    [Fact]
    public async Task ProvidersUi_GitHubSignIn_GatedAndAddOn()
    {
        var html = await fixture.Client.GetStringAsync("/index.html");
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("id=\"githubSignInBtn\" hidden>Sign in with GitHub</button>", html);
        Assert.Contains("you still need an ordinary provider for plain chats", html);
        Assert.Contains("githubSignInBtn.hidden = !gitHubSignIn || connected;", app);
        Assert.Contains("location.href = \"/api/providers/github/start\";", app);
        Assert.Contains("if (github) li.append(meta, del);", app);
        Assert.Contains("const result = params.get(\"github\");", app);
        Assert.Contains("initGitHubSignInReturn();", app);
    }

    // ADR 0108 AC10: every picker leaves GitHub Copilot models out unless it asks for them, and only the agent editor
    // asks, for the copilot kind.
    [Fact]
    public async Task Pickers_HideCopilotOnlyModels_ExceptCopilotAgents()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("export function fillModelOptions(select, { copilot = false } = {})", app);
        Assert.Contains("if (m.copilotOnly && !copilot) continue;", app);
        // A GitHub-only user is still gated (decision 5, critic F2 on the diff): only ordinary models open the composer.
        Assert.Contains("const hasModel = (modelsData.models ?? []).some((m) => !m.copilotOnly);", app);
        Assert.Contains("fillModelOptions(agentModelSelect, { copilot: agent?.kind === \"copilot\" });", app);
        Assert.Contains("fillModelOptions(agentModelSelect, { copilot: agentKindSelect.value === \"copilot\" });", app);
        Assert.DoesNotContain("fillModelOptions(modelSelect, { copilot", app);
        Assert.DoesNotContain("fillModelOptions(defaultModelSelect, { copilot", app);
    }
}
