using System.Text.RegularExpressions;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// AC4 (ADR 0098): <see cref="DocumentStitcher"/> turns real <see cref="DocumentChunker"/> output back into
/// text in which every source sentence appears exactly once, in order — within a part and across a part
/// boundary — and inserts a paragraph break where a page changes.
/// </summary>
public sealed class DocumentStitcherTests
{
    private static readonly DocumentChunker Chunker = new();

    private static string Sentences(int count, string prefix = "Sentence")
        => string.Join(" ", Enumerable.Range(1, count).Select(i => $"{prefix} {i:D4} says something unique here."));

    private static void AssertEverySentenceOnceInOrder(string stitched, int count, string prefix = "Sentence")
    {
        var found = Regex.Matches(stitched, prefix + @" (\d{4}) says something unique here\.")
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(Enumerable.Range(1, count), found);
    }

    private static string WithoutWhitespace(string text) => Regex.Replace(text, @"\s+", "");

    [Fact]
    public void Stitch_AllChunks_EverySentenceExactlyOnce_InOrder()
    {
        var source = Sentences(300);
        var chunks = Chunker.Chunk("a.txt", [new DocumentSegment(source, null)]);
        Assert.True(chunks.Count > 10, "the fixture must span many overlapping chunks");

        var stitched = DocumentStitcher.Stitch(null, chunks);

        AssertEverySentenceOnceInOrder(stitched, 300);
        Assert.Equal(source, stitched); // one block: the stitch reproduces the source exactly
    }

    [Fact]
    public void Stitch_AcrossAPartBoundary_UsesThePreviousChunkOnlyToStripOverlap()
    {
        var source = Sentences(300);
        var chunks = Chunker.Chunk("a.txt", [new DocumentSegment(source, null)]);

        var part1 = DocumentStitcher.Stitch(null, [.. chunks.Take(10)]);
        var part2 = DocumentStitcher.Stitch(chunks[9], [.. chunks.Skip(10)]);

        // The boundary may fall mid-word or on a space the next part trims, so compare ignoring whitespace:
        // nothing repeated, nothing lost.
        Assert.Equal(WithoutWhitespace(source), WithoutWhitespace(part1 + part2));
        Assert.DoesNotContain(chunks[9].Text, part2);
    }

    [Fact]
    public void Stitch_PageChange_InsertsAParagraphBreak()
    {
        var chunks = Chunker.Chunk("a.pdf",
        [
            new DocumentSegment(Sentences(40, "Alpha"), 1),
            new DocumentSegment(Sentences(40, "Beta"), 2),
        ]);

        var stitched = DocumentStitcher.Stitch(null, chunks);

        AssertEverySentenceOnceInOrder(stitched, 40, "Alpha");
        AssertEverySentenceOnceInOrder(stitched, 40, "Beta");
        Assert.Contains("here.\n\nBeta 0001", stitched);
    }

    [Fact]
    public void Stitch_UnrelatedChunks_AreJoinedWhole()
    {
        var stitched = DocumentStitcher.Stitch(null,
        [
            new DocumentChunk("a.txt", null, 0, "The first block ends with a period."),
            new DocumentChunk("a.txt", null, 1, ".NET is where the second one starts."),
        ]);

        Assert.Equal("The first block ends with a period.\n\n.NET is where the second one starts.", stitched);
    }

    // The accepted residual: periodic text may lose repeated rows, but never crashes and never invents text.
    [Fact]
    public void Stitch_PeriodicContent_OnlyEverLosesRepeatedText()
    {
        const string row = "id;name;value\n";
        var source = string.Concat(Enumerable.Repeat(row, 200)).TrimEnd();
        var chunks = Chunker.Chunk("sheet.xlsx", [new DocumentSegment(source, null)]);

        var stitched = DocumentStitcher.Stitch(null, chunks);

        Assert.NotEmpty(stitched);
        Assert.True(stitched.Length <= source.Length, "stitching must never add text");
        Assert.Empty(stitched.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line != "id;name;value"));
    }

    [Fact]
    public void Stitch_Empty_IsEmpty() => Assert.Equal("", DocumentStitcher.Stitch(null, []));
}
