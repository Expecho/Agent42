namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of reasoning output (ADR 0071): the collapsed disclosure a reasoning model's
/// thinking is rendered into, and the two things that arm must NOT do. Serves the real Backend
/// <c>wwwroot</c> through the shared <see cref="StaticAssetsServerFixture"/> and asserts on key source
/// strings — the repo has no JS test runner by design, so these prove the wiring exists rather than that it
/// paints. The rendered result was verified manually in DevHost over CDP.
/// </summary>
[Collection("StaticAssets")]
public sealed class ReasoningAssetsTests(StaticAssetsServerFixture fixture)
{
    /// <summary>
    /// The renderer and its closer both exist and are reachable from <c>main.js</c>. A module unreachable
    /// from there is invisible to this fixture, so this also proves the import graph carries them.
    /// </summary>
    [Fact]
    public async Task Frontend_ShipsReasoningDisclosure()
    {
        var app = await fixture.GetAppScriptAsync();

        Assert.Contains("renderReasoning", app);
        Assert.Contains("finishReasoning", app);
        // A <details> element, so it is collapsed until the user opens it.
        Assert.Contains("msg__thinking", app);
        // Dispatched on the wire type the endpoint actually emits — pinned server-side by
        // AgentApiTests.ChatEndpoint_StreamsReasoningAsNdjson.
        Assert.Contains("\"reasoning\"", app);
    }

    /// <summary>
    /// Reasoning is rendered as TEXT, never as markup.
    ///
    /// <para>The reply goes through the markdown renderer and the sanitizer; reasoning deliberately does
    /// not, so the only safe sink is <c>textContent</c>. This pins that the thinking body is filled that
    /// way — an <c>innerHTML</c> here would put unsanitized model output into the DOM.</para>
    /// </summary>
    [Fact]
    public async Task Reasoning_IsRenderedAsTextNotMarkup()
    {
        var app = await fixture.GetAppScriptAsync();

        var render = CodeOnly(StaticAssetsServerFixture.Body(app, "export function renderReasoning"));
        Assert.Contains("textContent", render);
        Assert.DoesNotContain("innerHTML", render);
    }

    /// <summary>
    /// The reasoning arm must not speak (ADR 0037/0038).
    ///
    /// <para>The <c>textDelta</c> arm one branch above calls <c>voice.onDelta(chunk.text)</c>, and copying
    /// that line literally is the obvious mistake: hands-free voice would read the model's deliberation
    /// aloud ahead of its answer, and with a speech model configured it would pay a third party per
    /// character to do it. Scoped to the arm itself, because <c>voice.onDelta</c> legitimately appears
    /// elsewhere in the same file.</para>
    /// </summary>
    [Fact]
    public async Task ReasoningArm_DoesNotSpeakTheThinking()
    {
        var app = await fixture.GetAppScriptAsync();

        var arm = CodeOnly(Section(app, "chunk.type === \"reasoning\"", "chunk.type === \"toolCallStart\""));
        Assert.DoesNotContain("voice.", arm);
        Assert.Contains("renderReasoning", arm);

        // Not vacuous: the textDelta arm DOES speak, so the assertion above is a real difference between
        // the two arms rather than a claim about a file that never mentions voice.
        Assert.Contains("voice.onDelta", app);
    }

    /// <summary>
    /// Reasoning is scoped to its own turn: the renderer looks the disclosure up INSIDE the message it was
    /// handed, so a new turn starts a fresh one instead of appending to the previous turn's thinking.
    /// </summary>
    [Fact]
    public async Task Reasoning_IsScopedToItsOwnTurn()
    {
        var app = await fixture.GetAppScriptAsync();

        var render = CodeOnly(StaticAssetsServerFixture.Body(app, "export function renderReasoning"));
        // msg.querySelector, never document.querySelector — the latter would find the FIRST disclosure in
        // the whole thread and stream every later turn's thinking into the earliest reply.
        Assert.Contains("msg.querySelector", render);
        Assert.DoesNotContain("document.querySelector", render);
    }

    /// <summary>
    /// Every tail settles the disclosure, not just the happy one.
    ///
    /// <para><c>done</c> closes it when the turn completes normally, but three other tails never see a
    /// <c>done</c>: a voice barge-in (<c>AbortError</c>, ADR 0037), a stream that died and was recovered
    /// (ADR 0046), and an outright failure. Each would otherwise leave a pulsing "Thinking…" above a reply
    /// that is finished, interrupted or errored — permanently, since reasoning is never persisted and so
    /// never replayed.</para>
    ///
    /// <para>The <c>finally</c> is the only construct that covers all four, so that is what this pins —
    /// scoped to the block, because <c>finishReasoning</c> is legitimately called from the done arm too and
    /// an unscoped assertion would pass on that one alone.</para>
    /// </summary>
    [Fact]
    public async Task EveryStreamTail_SettlesTheReasoningDisclosure()
    {
        var app = await fixture.GetAppScriptAsync();

        var tail = CodeOnly(Section(app, "  } finally {", "export function wireSend"));
        Assert.Contains("finishReasoning", tail);
    }

    /// <summary>
    /// The providers UI lets a user flag a model as reasoning, and it is sent on save — copying the
    /// <c>ProvidersForm_HasImageGenerationFlag</c> shape.
    ///
    /// <para><b>The second half is the one that matters.</b> <c>collectModels</c> reads the checkboxes by
    /// POSITIONAL destructuring of <c>querySelectorAll(".agent-form__checkbox")</c>, paired by ORDER with
    /// the <c>capDefs</c> array that renders them. Add a flag to one and not the other, or at a different
    /// index, and every flag after the insertion point silently reads the wrong box — a model saved as
    /// "speech" comes back as "embeddings". Nothing else in the suite pins that pairing; until now it was
    /// covered only by a manual browser pass.</para>
    /// </summary>
    [Fact]
    public async Task ProvidersForm_HasReasoningFlag_AndSendsIt()
    {
        var app = await fixture.GetAppScriptAsync();

        var addModelRow = StaticAssetsServerFixture.Body(app, "function addModelRow(model)");
        Assert.Contains("model?.supportsReasoning", addModelRow);
        Assert.Contains("reasoning", addModelRow);

        var collectModels = StaticAssetsServerFixture.Body(app, "function collectModels()");
        Assert.Contains("supportsReasoning: reasoning.checked", collectModels);

        // The pairing itself: `reasoning` must be the SIXTH name (transcription, ADR 0111, is the seventh) destructured, matching capDefs' sixth
        // entry. A name bound at the wrong index compiles, renders and saves — just wrongly.
        Assert.Contains(
            "const [tools, vision, image, embed, speech, reasoning, transcription] =",
            CodeOnly(collectModels),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The disclosure has a style of its own, so it is not silently inheriting <c>.tool-call</c>'s.
    /// </summary>
    [Fact]
    public async Task Styles_ShipTheThinkingDisclosure()
    {
        var css = await fixture.Client.GetStringAsync("/styles.css");

        Assert.Contains(".msg__thinking", css);
        Assert.Contains(".msg__thinking-body", css);
    }

    /// <summary>
    /// <paramref name="source"/> with its <c>//</c> line comments removed.
    ///
    /// <para>Every negative assertion here MUST go through this. The comments in the code under test
    /// explain precisely what it must not do — naming <c>voice.onDelta</c> and <c>innerHTML</c> to say
    /// "not these" — so a raw <c>DoesNotContain</c> is tripped by the explanation rather than by the
    /// defect, and the only way to make it pass would be to delete the comment that documents the rule.
    /// That is the same shape as the comment-blind <c>IndexOf</c> guards ADR 0065 records.</para>
    ///
    /// <para>Line comments only: the bundled module source has no block comments inside a function body,
    /// and a half-clever block-comment stripper would be more likely to eat real code than to earn its
    /// keep. String literals are left alone, which is why the positive assertions still work.</para>
    /// </summary>
    private static string CodeOnly(string source) =>
        string.Join('\n', source
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));

    /// <summary>
    /// The slice of <paramref name="source"/> between two markers.
    ///
    /// <para>A last resort, used only for the two regions <see cref="StaticAssetsServerFixture.Body"/>
    /// cannot express — a single arm of an <c>if</c>/<c>else</c> chain, and a <c>finally</c> block. Both
    /// are sub-regions of a function body rather than a braced declaration, so the canonical brace-depth
    /// extractor has nothing to anchor on. Every WHOLE-function assertion here uses <c>Body</c>, which is
    /// strictly stronger: this one depends on the two markers still being adjacent in the concatenated
    /// corpus, and silently widens if something is inserted between them.</para>
    ///
    /// <para>Asserts both markers are found, so a rename fails loudly instead of silently searching "".</para>
    /// </summary>
    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker not found in the bundled source: {from}");
        var end = source.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Marker not found after '{from}': {to}");
        return source[start..end];
    }
}
