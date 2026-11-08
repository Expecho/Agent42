using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the rich inline renderer (ADR 0083): mermaid diagrams, code highlighting + copy, KaTeX math and
/// JSON charts. <c>richtext.js</c> lives under the public <c>/shared/lib/</c> and is a classic script, so it
/// is NOT part of the <c>js/</c> module graph: it is fetched from the fixture client directly and never
/// through <see cref="StaticAssetsServerFixture.GetAppScriptAsync"/>. Source-text heuristics, like every
/// other asset guard (the repo has no JS runner by design); the visual check is manual.
/// </summary>
[Collection("StaticAssets")]
public sealed class RichTextAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public RichTextAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    private Task<string> GetRendererAsync() => _client.GetStringAsync("/shared/lib/richtext.js");

    // Enhancement runs on a FINAL render only. setAgentMarkdown enhances in its non-caret branch, and every
    // caret call is a streaming one: the initial empty bubble plus the calls inside the stream loop. A new
    // caret: true call anywhere else would silently skip enhancement on a final render.
    [Fact]
    public async Task Enhance_RunsOnFinalizeOnly()
    {
        var app = await _fixture.GetAppScriptAsync();

        // Body() takes the first '{' — the signature's destructuring default — so start at the body's ") {".
        var declared = app.IndexOf("export function setAgentMarkdown(", StringComparison.Ordinal);
        Assert.True(declared >= 0, "setAgentMarkdown not found — did it get renamed?");
        var set = StaticAssetsServerFixture.Body(app[declared..], ") {");
        var elseAt = set.IndexOf("} else {", StringComparison.Ordinal);
        var enhanceAt = set.IndexOf("globalThis.Agent42Rich?.enhance(prose)", StringComparison.Ordinal);
        Assert.True(elseAt >= 0, "setAgentMarkdown lost its non-caret branch");
        Assert.True(enhanceAt > elseAt, "enhance must run only in the non-caret (final) branch");

        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt, replay = null)");
        var loop = StaticAssetsServerFixture.Body(send, "for (;;)");
        var loopCalls = Count(loop, "setAgentMarkdown(");
        Assert.True(loopCalls >= 1, "the stream loop no longer renders chunks");
        Assert.Equal(loopCalls, Count(loop, "setAgentMarkdown(agentMsg, answer, { caret: true })"));

        // The stream end renders final (no caret) right after the loop.
        var afterLoop = send[(send.IndexOf(loop, StringComparison.Ordinal) + loop.Length)..];
        Assert.StartsWith("setAgentMarkdown(agentMsg, answer);", afterLoop.TrimStart());

        // Outside the loop, the only caret call in the whole app is the initial empty bubble.
        Assert.Contains("setAgentMarkdown(agentMsg, \"\", { caret: true })", send);
        Assert.Equal(loopCalls + 1, Count(app, "caret: true"));
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var at = source.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
