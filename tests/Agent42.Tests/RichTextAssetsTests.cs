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

    // Mermaid runs strict, HTML labels are off (they are <foreignObject>, which the svg profile strips), and
    // what reaches innerHTML is the DOMPurify-sanitized SVG — never mermaid's raw output.
    [Fact]
    public async Task Mermaid_IsStrictAndSanitized()
    {
        var diagram = StaticAssetsServerFixture.Body(await GetRendererAsync(), "async function renderDiagram(");

        Assert.Contains("securityLevel: \"strict\"", diagram);
        Assert.Contains("htmlLabels: false", diagram);
        Assert.Contains(
            "figure.innerHTML = DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true } })",
            diagram);
        Assert.Single(Regex.Matches(diagram, @"\.innerHTML\s*="));
    }

    // An inline SVG <style> is page-global and survives the svg profile, so diagram text must not be able to
    // reach a network URL through CSS: themeCSS/fontFamily directives are locked via mermaid's `secure` list,
    // and every non-fragment url() and @import is removed from the sanitized SVG before it is inserted.
    [Fact]
    public async Task Mermaid_CannotInjectExternalCss()
    {
        var renderer = await GetRendererAsync();
        var diagram = StaticAssetsServerFixture.Body(renderer, "async function renderDiagram(");

        Assert.Contains("\"themeCSS\"", diagram);
        Assert.Contains("\"fontFamily\"", diagram);
        var sanitize = diagram.IndexOf("figure.innerHTML = DOMPurify.sanitize(", StringComparison.Ordinal);
        var neutralize = diagram.IndexOf("neutralizeCss(figure)", StringComparison.Ordinal);
        var insert = diagram.IndexOf("wrap.before(figure)", StringComparison.Ordinal);
        Assert.True(sanitize >= 0 && sanitize < neutralize && neutralize < insert,
            "the SVG must be sanitized, then neutralized, then inserted");

        Assert.Contains(@"const EXTERNAL_CSS = /@import[^;]*;?|url\(\s*(?!['""]?#)[^)]*\)/gi;", renderer);
        var neutralizeBody = StaticAssetsServerFixture.Body(renderer, "function neutralizeCss(");
        Assert.Contains("querySelectorAll(\"style\")", neutralizeBody);
        Assert.Contains("querySelectorAll(\"[style]\")", neutralizeBody);
    }

    // KaTeX never trusts \href/\url-style commands; a chart is data only — JSON.parse, bounded, and nothing in
    // the renderer can turn a string into code.
    [Fact]
    public async Task Math_And_Chart_AreSafe()
    {
        var renderer = await GetRendererAsync();

        var math = StaticAssetsServerFixture.Body(renderer, "async function renderMath(");
        Assert.Contains("trust: false", math);
        Assert.Contains("throwOnError: false", math);

        var chart = StaticAssetsServerFixture.Body(renderer, "function parseChart(");
        Assert.Contains("JSON.parse(source)", chart);
        Assert.Contains("MAX_CHART_SOURCE", chart);
        Assert.Contains("MAX_CHART_POINTS", chart);

        Assert.DoesNotContain("eval(", renderer);
        Assert.DoesNotContain("Function(", renderer);
    }

    // No new egress: every lib is vendored and loaded from the same origin's /shared/lib/.
    [Fact]
    public async Task Renderer_HasNoExternalUrls()
    {
        var renderer = await GetRendererAsync();

        Assert.Contains("const LIB = \"/shared/lib/\"", renderer);
        Assert.DoesNotContain("http://", renderer);
        Assert.DoesNotContain("https://", renderer);
        Assert.DoesNotContain("//cdn", renderer);
    }

    // One bad block must not stop the rest: every renderer runs through guard(), which catches both a sync
    // throw and an async rejection, and math is guarded per formula.
    [Fact]
    public async Task Enhance_IsolatesBlockFailures()
    {
        var renderer = await GetRendererAsync();

        var guard = StaticAssetsServerFixture.Body(renderer, "function guard(");
        Assert.Contains("try {", guard);
        Assert.Contains("} catch (err) {", guard);
        Assert.Contains(".catch((err) => markError(", guard);

        var code = StaticAssetsServerFixture.Body(renderer, "function enhanceCode(");
        Assert.Contains("guard(wrap, \"diagram\"", code);
        Assert.Contains("guard(wrap, \"chart\"", code);
        Assert.Contains("guard(wrap, \"code highlighting\"", code);

        var math = StaticAssetsServerFixture.Body(renderer, "async function renderMath(");
        Assert.Contains("guard(el, \"formula\"", math);
    }

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

        var send = StaticAssetsServerFixture.Body(app, "export async function send(prompt)");
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

    // A 404 or a skewed cache on richtext.js must degrade to plain markdown, not a blank app: every consumer
    // reaches the global through a guard, never a bare top-level reference.
    [Fact]
    public async Task Renderer_IsOptional()
    {
        var format = await _client.GetStringAsync("/js/format.js");
        Assert.Contains(
            "if (typeof Agent42Rich !== \"undefined\") marked.use({ extensions: Agent42Rich.mathExtensions })",
            format);

        var thread = await _client.GetStringAsync("/js/thread.js");
        Assert.Contains("globalThis.Agent42Rich?.enhance(", thread);
        Assert.DoesNotContain(" Agent42Rich.enhance(", thread);

        var viewer = await _client.GetStringAsync("/shared/viewer.js");
        Assert.Contains("const rich = typeof Agent42Rich !== \"undefined\" ? Agent42Rich : null;", viewer);
    }

    // The chat app loads the classic renderer from the public /shared/lib/ — after purify (it sanitizes with it)
    // and before the module (format.js registers its extensions at evaluation).
    [Fact]
    public async Task Index_LoadsRendererBetweenPurifyAndTheModule()
    {
        var index = await _client.GetStringAsync("/");

        var purify = index.IndexOf("<script src=\"lib/purify.min.js\"></script>", StringComparison.Ordinal);
        var highlight = index.IndexOf("<script src=\"/shared/lib/highlight/highlight.min.js\"></script>", StringComparison.Ordinal);
        var rich = index.IndexOf("<script src=\"/shared/lib/richtext.js\"></script>", StringComparison.Ordinal);
        var module = index.IndexOf("<script type=\"module\" src=\"js/main.js\"></script>", StringComparison.Ordinal);
        Assert.True(purify >= 0 && highlight >= 0 && rich >= 0 && module >= 0, "a script tag is missing");
        Assert.True(purify < highlight && highlight < rich && rich < module, "script order is wrong");

        Assert.Contains("data-hljs-theme=\"light\"", index);
        Assert.Contains("data-hljs-theme=\"dark\"", index);
    }

    // D6: a block AND an inline extension, both with start() (without it marked's text tokenizer runs past the
    // delimiter and its escape tokenizer eats \( and \[). The single-$ rule refuses whitespace inside the
    // delimiters and a digit after the closing $, so "$5 and $10" stays text.
    [Fact]
    public async Task MathExtensions_AreBlockAndInlineWithStart()
    {
        var renderer = await GetRendererAsync();

        var block = StaticAssetsServerFixture.Body(renderer, "const mathBlock = {");
        Assert.Contains("level: \"block\"", block);
        Assert.Contains("start(src)", block);

        var inline = StaticAssetsServerFixture.Body(renderer, "const mathInline = {");
        Assert.Contains("level: \"inline\"", inline);
        Assert.Contains("start(src)", inline);

        Assert.Contains(@"{ re: /^\$(?!\s)((?:\\.|[^\\$\n])+?)(?<!\s)\$(?!\d)/, display: false }", renderer);
        Assert.Contains("mathExtensions: [mathBlock, mathInline]", renderer);
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
