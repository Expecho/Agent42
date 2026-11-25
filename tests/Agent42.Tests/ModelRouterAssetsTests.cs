using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of the Auto model (ADR 0137). The repo has no JS test runner, so these assert on the source
/// of the real <c>wwwroot</c> and prove the wiring exists, in the style of <see cref="ResearchAssetsTests"/>. The rendered
/// picker and label are checked in DevHost, which ships a canned router.
/// </summary>
[Collection("StaticAssets")]
public sealed class ModelRouterAssetsTests(StaticAssetsServerFixture fixture)
{
    private Task<string> Js(string name) => fixture.Client.GetStringAsync($"/js/{name}");

    // The composer and default-model pickers offer Auto from the server's flag, never from a hardcoded one (ADR 0137/0139).
    [Fact]
    public async Task Picker_OffersAutoOnlyWhenTheServerSaysSo_InTheComposerAndTheDefault()
    {
        var models = await Js("models.js");

        Assert.Contains("export const AUTO_MODEL = \"auto\"", models);
        Assert.Contains("auto && modelsData.auto", models);
        Assert.Contains("fillModelOptions(modelSelect, { auto: true })", models);
        // The default-model preference may be Auto (ADR 0139); the agent dialog and the fleet still pin real models.
        Assert.Contains("fillModelOptions(defaultModelSelect, { auto: true })", await Js("providers.js"));
        Assert.DoesNotContain("auto: true", await Js("agents.js"));
    }

    // New chats start on Auto only while the server says the Auto default is live (a router still exists, ADR 0139).
    [Fact]
    public async Task LoadModels_PinsAuto_OnlyWhenTheServerSaysTheAutoDefaultIsLive()
    {
        var models = await Js("models.js");

        Assert.Contains("if (pin && modelsData.autoDefault && modelsData.auto) setModel(AUTO_MODEL);", models);
    }

    // Embedding, speech and transcription models can't chat, so no chat-model picker may list them.
    [Fact]
    public async Task Picker_HidesModelsThatCannotChat()
    {
        var models = await Js("models.js");

        Assert.Contains("m.supportsEmbeddings || m.supportsSpeech || m.supportsTranscription", models);
    }

    // A stale Auto must never turn into an "Unavailable" option: it is not a model.
    [Fact]
    public async Task SelectModel_NeverAddsARawAutoOption()
    {
        var models = await Js("models.js");

        Assert.Contains("id === AUTO_MODEL && ![...select.options].some((o) => o.value === id)) return;", models);
        Assert.Contains("if (id === AUTO_MODEL) return \"Auto\";", models);
    }

    // The refill resets the select to its first option, which is Auto: a refresh that must not retarget the open chat puts the choice back.
    [Fact]
    public async Task LoadModels_KeepsTheOpenChatsChoice_WhenNotPinning()
    {
        var models = await Js("models.js");

        Assert.Contains("const kept = modelSelect.value;", models);
        Assert.Contains("else if (kept) setModel(kept);", models);
    }

    // The picker restores through GET .../model, which answers `auto` for an Auto conversation.
    [Fact]
    public async Task ConversationRestore_GoesThroughSetModel()
    {
        Assert.Contains("setModel(await modelRes.json())", await Js("conversations.js"));
    }

    // The server answers a chat turn with the model's own id plus a routed event; the page renders the event as a label.
    [Fact]
    public async Task Stream_RendersTheRoutedLabel_AsLiveOnlyMetadata()
    {
        var send = await Js("send.js");
        var thread = await Js("thread.js");

        Assert.Contains("chunk.type === \"routed\"", send);
        Assert.Contains("renderRouted(agentMsg, chunk.text)", send);
        Assert.Contains("export function renderRouted(msg, name)", thread);
        // The label goes on the meta line, never into the reply's text.
        Assert.Contains("msg.querySelector(\".msg__meta\")", thread);
    }

    // Gotcha "The provider editor's capability checkboxes are positional": the save path destructures the checkboxes BY
    // ORDER, so capDefs and the destructure must list the flags in the same order, and a new flag goes LAST in both.
    [Fact]
    public async Task ProviderEditor_CapabilityCheckboxes_StayInOrder()
    {
        var providers = await Js("providers.js");

        var defs = Regex.Matches(providers[providers.IndexOf("const capDefs = [", StringComparison.Ordinal)..], @"checked: model\?\.(\w+) \?\? false")
            .Select(m => m.Groups[1].Value).Take(7).ToArray();
        var destructure = Regex.Match(providers, @"const \[([\w, ]+)\] =\s*row\.querySelectorAll\(""\.agent-form__checkbox""\)").Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            ["supportsTools", "supportsVision", "supportsImageGeneration", "supportsEmbeddings", "supportsSpeech", "supportsReasoning", "supportsTranscription"],
            defs);
        Assert.Equal(["tools", "vision", "image", "embed", "speech", "reasoning", "transcription"], destructure);
    }

    // The editor: a DecisionModel kind, the per-model hint read by class (never positionally, a flag added there would shift), and the router CSS.
    [Fact]
    public async Task ProviderEditor_HasTheRouterKind_AndTheHintField()
    {
        var providers = await Js("providers.js");
        var html = await fixture.Client.GetStringAsync("/");
        var css = await fixture.Client.GetStringAsync("/styles.css");

        Assert.Contains("<option value=\"DecisionModel\">", html);
        Assert.Contains("id=\"providerDecisionNote\"", html);
        Assert.Contains("hint: row.querySelector(\".provider-model-row__hint\").value.trim() || null", providers);
        Assert.Contains("hint.value = model?.hint ?? \"\";", providers);
        Assert.Contains(".provider-models--router .provider-model-row__hint", css);
        Assert.Contains(".msg__routed", css);
    }
}
