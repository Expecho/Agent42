namespace Agent42.Tests;

/// <summary>
/// Guards the image-generation frontend wiring (ADR 0020). Serves the real Backend <c>wwwroot</c> through
/// the shared <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same
/// source-text-heuristic shape as <see cref="LocationAssetsTests"/> (the repo has no JS test runner by
/// design, so these prove the code <i>says</i> the right thing; the browser flow is the Step-7 <c>/run</c>).
/// </summary>
[Collection("StaticAssets")]
public sealed class ImageGenerationAssetsTests
{
    private readonly HttpClient _client;

    public ImageGenerationAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

    // AC11 — an assistant-generated image renders through the SAME first-party display proxy (by id) as a
    // user image, so a live stream event and a reloaded turn share one path and the page never holds a blob URL.
    [Fact]
    public async Task AssistantImage_RendersThroughTheAttachmentProxy()
    {
        var app = await _client.GetStringAsync("/app.js");

        var addAgentImage = Body(app, "function addAgentImage(");
        Assert.Contains("attachmentUrl(convId, attachmentId)", addAgentImage);
    }

    // AC11 — the live Image stream event is handled: image gen is not token-streamed, so a single "image"
    // chunk drives the assistant-bubble render.
    [Fact]
    public async Task Send_HandlesTheImageStreamEvent()
    {
        var app = await _client.GetStringAsync("/app.js");

        var send = Body(app, "async function send(prompt)");
        Assert.Contains("chunk.type === \"image\"", send);
        Assert.Contains("addAgentImage(agentMsg, activeId, chunk.attachmentId)", send);
    }

    // AC11 — a reloaded assistant turn re-renders its generated image from history (turn.attachments).
    // The render loop was extracted into renderTurns (shared by the live path and the offline-cache
    // fallback, ADR 0032); selectConversation drives it, so assert the delegation AND the render.
    [Fact]
    public async Task SelectConversation_RendersAssistantImagesFromHistory()
    {
        var app = await _client.GetStringAsync("/app.js");

        var select = Body(app, "async function selectConversation(id)");
        Assert.Contains("renderTurns(id, turns)", select);

        var renderTurns = Body(app, "function renderTurns(id, turns)");
        Assert.Contains("addAgentImage(msg, id, att.id", renderTurns);
    }

    // AC11 — the providers UI lets a user flag a model as image-generation, and it is sent on save.
    [Fact]
    public async Task ProvidersForm_HasImageGenerationFlag()
    {
        var app = await _client.GetStringAsync("/app.js");

        // The editable model row offers the checkbox…
        var addModelRow = Body(app, "function addModelRow(model)");
        Assert.Contains("model?.supportsImageGeneration", addModelRow);
        Assert.Contains("image gen", addModelRow);

        // …and its state is sent on save.
        var collectModels = Body(app, "function collectModels()");
        Assert.Contains("supportsImageGeneration: image.checked", collectModels);
    }

    // The source text of one function, declaration to its matching closing brace — so an assertion can say
    // "this function does X" rather than "the file contains X somewhere". Copied from LocationAssetsTests.
    private static string Body(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' not found in app.js — did it get renamed?");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{declaration}'");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail($"unbalanced braces after '{declaration}'");
        return string.Empty;
    }
}
