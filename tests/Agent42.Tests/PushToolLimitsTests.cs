using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The notification text policy (ADR 0075) — <see cref="PushToolLimits.SanitizeNotificationText"/> — plus
/// AC14's source-text guard over the tool itself.
///
/// <para>Pure unit tests: the sanitizer touches no grain, no cluster and no I/O, so these need none of the
/// <c>SharedClusterTests</c> machinery the behavioural push tests in <c>AgentGrainTests</c> use.</para>
/// </summary>
public sealed class PushToolLimitsTests
{
    // AC8: control and format characters are stripped. The format (Cf) half is the one that earns the
    // sanitizer its place — U+202E is a bidi override, which really can reorder rendered notification text
    // into something the model never wrote, and U+200B hides a word boundary.
    [Fact]
    public void Sanitize_StripsControlAndFormatCharacters()
    {
        var result = PushToolLimits.SanitizeNotificationText(
            "Build fin​ished‮", PushToolLimits.MaxTitleLength);

        Assert.Equal("Build finished", result);
    }

    // The bidi override on its own, asserted separately from the mixed case above so a regression that
    // strips only C0 controls (the easy half) cannot hide behind the BEL.
    [Fact]
    public void Sanitize_RemovesBidiOverride()
    {
        var result = PushToolLimits.SanitizeNotificationText(
            "‮latirpsoh ot oG", PushToolLimits.MaxBodyLength);

        Assert.DoesNotContain('‮', result);
        Assert.Equal("latirpsoh ot oG", result);
    }

    // AC8: whitespace runs — newlines included — collapse to a single space, and leading/trailing runs
    // vanish. A notification body is one paragraph; fake structure is not honoured by the shade anyway.
    [Fact]
    public void Sanitize_CollapsesWhitespaceRunsIncludingNewlines()
    {
        var result = PushToolLimits.SanitizeNotificationText(
            "  Build failed.\n\n\tSee the log.  ", PushToolLimits.MaxBodyLength);

        Assert.Equal("Build failed. See the log.", result);
    }

    // AC8: the hard length cap truncates rather than refusing — an over-long title is still usable.
    [Fact]
    public void Sanitize_CapsLength()
    {
        var result = PushToolLimits.SanitizeNotificationText(new string('x', 500), PushToolLimits.MaxTitleLength);

        Assert.Equal(PushToolLimits.MaxTitleLength, result.Length);
    }

    // The cap counts what SURVIVES, not what arrived: stripped characters must not consume budget, or a
    // title padded with zero-width spaces would arrive mostly empty.
    [Fact]
    public void Sanitize_CapCountsOnlySurvivingCharacters()
    {
        var padded = string.Concat(Enumerable.Repeat("a​", 60));

        var result = PushToolLimits.SanitizeNotificationText(padded, PushToolLimits.MaxTitleLength);

        Assert.Equal(new string('a', 60), result);
    }

    // AC9's unit half: nothing usable left returns empty, which the tool treats as a refusal. Covers the
    // three shapes the tool can actually receive — null, whitespace-only, and strippable-only.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t  ")]
    [InlineData("‮​")]
    public void Sanitize_ReturnsEmptyWhenNothingUsableRemains(string? raw)
    {
        Assert.Equal("", PushToolLimits.SanitizeNotificationText(raw, PushToolLimits.MaxTitleLength));
    }

    // Ordinary non-ASCII text and emoji SURVIVE, and this is the assertion that pins the deliberate
    // divergence from WebDocumentLimits.SanitizeFileName's allowlist (ADR 0066). That one allowlists ASCII
    // because its sink is the system prompt; this sink is showNotification's plain text, where an ASCII
    // allowlist would destroy ordinary Dutch and every emoji for no security gain. If someone "fixes" this
    // into an allowlist for consistency, this test is what says no.
    [Fact]
    public void Sanitize_KeepsAccentedTextAndEmoji()
    {
        var result = PushToolLimits.SanitizeNotificationText(
            "Vergadering om 9 uur ✅", PushToolLimits.MaxTitleLength);

        Assert.Equal("Vergadering om 9 uur ✅", result);
    }

    // A surrogate pair must never be cut in half by the cap — a lone surrogate is not text and breaks any
    // consumer that re-encodes the string. The sanitizer iterates RUNES for exactly this reason, so the
    // emoji is dropped whole when it does not fit.
    [Fact]
    public void Sanitize_NeverSplitsASurrogatePair()
    {
        // 9 'a's + a 2-unit emoji against a 10-character cap: the emoji cannot fit, so it is dropped.
        var result = PushToolLimits.SanitizeNotificationText(new string('a', 9) + "😀", maxLength: 10);

        Assert.Equal(new string('a', 9), result);
        Assert.DoesNotContain(result.ToCharArray(), char.IsSurrogate);
    }

    // AC14: the tool must never log the notification title or body — they are model-authored text about
    // whatever this user is doing, and this repo stamps user identity onto every span (ADR 0010). The same
    // rule GetCurrentLocationAsync carries for the coordinates.
    //
    // A SOURCE-TEXT guard, because there is no log-capture seam in this project (no ILoggerProvider or
    // fake-logger implementation exists to assert on). The precedent for reading C# source in a test is
    // McpUrlSeedingTests. It is scoped to the ONE _logger call in the file and asserts on that call's
    // argument list, which is what keeps it from going vacuous — ADR 0072's lesson is that a loosely
    // anchored source-text assertion goes vacuous rather than red. Mutation-checked: adding cleanTitle to
    // the logger call makes this fail.
    [Fact]
    public void PushTool_DoesNotLogNotificationText()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Agent42.Agents", "Push", "AgentGrain.PushTool.cs"));

        // Exactly one logging call, so "the logger call" below is unambiguous. A second one added later
        // fails here rather than slipping past the scoped assertion.
        Assert.Equal(1, CountOccurrences(source, "_logger."));

        var callStart = source.IndexOf("_logger.", StringComparison.Ordinal);
        Assert.True(callStart > 0, "the tool no longer logs at all; this guard is then anchored on nothing");
        var callEnd = source.IndexOf(");", callStart, StringComparison.Ordinal);
        Assert.True(callEnd > callStart, "could not find the end of the logger call");
        var loggerCall = source[callStart..callEnd];

        // The variables holding the sanitized text, and the raw parameters they came from. None may appear
        // anywhere in the logger call — neither as a format argument nor interpolated into the message.
        foreach (var forbidden in new[] { "cleanTitle", "cleanBody", "title", "body" })
        {
            Assert.DoesNotContain(forbidden, loggerCall, StringComparison.Ordinal);
        }

        // Non-vacuity: the call really does log something, and really is the one in the tool.
        Assert.Contains("devices.Count", loggerCall, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // Walks up from the test assembly to the repo root, the McpUrlSeedingTests shape.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
