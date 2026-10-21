using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Splitting an over-long reply into platform-sized messages (ADR 0111). Each chunk must render on its own: a code
/// fence split across messages is closed and reopened with its language, an inline code span is never cut, markers
/// count the parts, nothing exceeds the limit, and a runaway reply stops at the platform's chunk cap.
/// </summary>
public class ChatMessageChunkerTests
{
    [Fact]
    public void ShortText_IsOneUnmarkedChunk()
        => Assert.Equal(["hello"], ChatMessageChunker.Split("hello", 100));

    [Fact]
    public void SplitsAtANewline_ThenMarksEveryChunk()
    {
        var text = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line number {i:00}"));

        var chunks = ChatMessageChunker.Split(text, 120);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 120, $"{c.Length} > 120"));
        Assert.All(chunks.Select((c, i) => (c, i)), x => Assert.EndsWith($"({x.i + 1}/{chunks.Count})", x.c));
        // No line is torn in half, and nothing is lost.
        var rejoined = string.Join('\n', chunks.Select(StripMarker));
        Assert.Equal(text, rejoined);
    }

    [Fact]
    public void WithoutNewlines_SplitsAtASpace()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 100));

        var chunks = ChatMessageChunker.Split(text, 80);

        Assert.All(chunks.Select(StripMarker), c => Assert.Matches("^(word ?)+$", c));
    }

    [Fact]
    public void AFenceSplitAcrossChunks_IsClosedAndReopenedWithItsLanguage()
    {
        var code = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"print({i})"));
        var text = "Here you go:\n```python\n" + code + "\n```\nDone.";

        var chunks = ChatMessageChunker.Split(text, 150);

        Assert.True(chunks.Count > 2);
        foreach (var chunk in chunks.Select(StripMarker))
        {
            // Every chunk has balanced fences, so each renders on its own.
            Assert.Equal(0, chunk.Split('\n').Count(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal)) % 2);
        }

        Assert.All(chunks.Skip(1).SkipLast(1), c => Assert.StartsWith("```python\n", c));
    }

    [Fact]
    public void AnInlineCodeSpan_IsNeverCut()
    {
        var text = new string('a', 60) + " `some_long_inline_code_identifier_here` " + new string('b', 60);

        var chunks = ChatMessageChunker.Split(text, 90);

        Assert.All(chunks.Select(StripMarker), c => Assert.Equal(0, c.Count(ch => ch == '`') % 2));
    }

    [Fact]
    public void ASurrogatePair_IsNeverSplit()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 100));

        var chunks = ChatMessageChunker.Split(text, 51);

        Assert.All(chunks.Select(StripMarker), c =>
        {
            Assert.False(char.IsLowSurrogate(c[0]));
            Assert.False(char.IsHighSurrogate(c[^1]));
        });
    }

    [Fact]
    public void ARunawayReply_StopsAtTheChunkCap_AndSaysSo()
    {
        var text = string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"line {i}"));

        var chunks = ChatMessageChunker.Split(text, 2000, maxChunks: 8);

        Assert.Equal(8, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 2000));
        Assert.Contains("…(truncated)", chunks[^1]);
        Assert.EndsWith("(8/8)", chunks[^1]);
    }

    private static string StripMarker(string chunk)
    {
        var at = chunk.LastIndexOf("\n\n(", StringComparison.Ordinal);
        return at < 0 ? chunk : chunk[..at];
    }
}
