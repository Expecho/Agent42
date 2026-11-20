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
    /// The <c>error</c> event (a provider failure that arrived inside a 200 stream, or a model that said nothing) reaches
    /// the user, and does so WITHOUT being treated as something the assistant said: it must not be appended to the reply
    /// the copy button captures, and it must not be spoken by hands-free voice (ADR 0037). Scoped to the arm, and to
    /// code, for the reason <see cref="CodeOnly"/> gives.
    /// </summary>
    [Fact]
    public async Task TheErrorEvent_IsShown_AndNeverSpokenOrCopiedAsTheReply()
    {
        var app = await fixture.GetAppScriptAsync();

        var arm = CodeOnly(Section(app, "chunk.type === \"error\"", "chunk.type === \"reasoning\""));
        Assert.Contains("streamError", arm);
        Assert.DoesNotContain("voice.onDelta", arm);
        Assert.DoesNotContain("answer +=", arm);
        // ... and it is painted after the loop, as an error bubble.
        Assert.Contains("agentMsg.classList.add(\"is-error\")", CodeOnly(Section(app, "if (streamError) {", "addCopyButton(agentMsg, answer)")));
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
