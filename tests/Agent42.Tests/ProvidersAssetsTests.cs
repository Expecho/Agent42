namespace Agent42.Tests;

/// <summary>
/// Guards the provider dialog's Anthropic half (ADR 0102). The repo has no JS test runner, so these assert on source
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
}
