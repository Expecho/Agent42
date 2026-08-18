using Agent42.Agents;
using Agent42.Agents.Documents;

namespace Agent42.Tests;

/// <summary>
/// AC3: extracted segments are chunked with overlap, and each chunk carries its source metadata
/// (filename, page where known, document-global chunk index).
/// </summary>
public sealed class DocumentChunkerTests
{
    private readonly DocumentChunker _chunker = new();

    // A long, whitespace-free string so window boundaries aren't shifted by trimming.
    private static string LongText(int length)
        => string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + (i % 26))));

    [Fact]
    public void Chunk_ShortSegment_ProducesOneChunkCarryingMetadata()
    {
        var chunks = _chunker.Chunk("guide.txt", [new DocumentSegment("The answer is 42.", null)]);

        var chunk = Assert.Single(chunks);
        Assert.Equal("guide.txt", chunk.FileName);
        Assert.Null(chunk.Page);
        Assert.Equal(0, chunk.ChunkIndex);
        Assert.Equal("The answer is 42.", chunk.Text);
    }

    [Fact]
    public void Chunk_LongText_ProducesOverlappingWindows()
    {
        var text = LongText(2500);

        var chunks = _chunker.Chunk("big.txt", [new DocumentSegment(text, null)]);

        // step = 1000 - 200 = 800 → windows [0,1000], [800,1800], [1600,2500].
        Assert.Equal(3, chunks.Count);
        Assert.Equal(text[0..1000], chunks[0].Text);
        Assert.Equal(text[800..1800], chunks[1].Text);
        Assert.Equal(text[1600..2500], chunks[2].Text);
        Assert.Equal([0, 1, 2], chunks.Select(c => c.ChunkIndex));

        // Adjacent chunks share exactly ChunkOverlap characters.
        Assert.Equal(
            chunks[0].Text[^DocumentChunker.ChunkOverlap..],
            chunks[1].Text[..DocumentChunker.ChunkOverlap]);
    }

    [Fact]
    public void Chunk_PdfPages_AttributesEachChunkToItsPage()
    {
        var chunks = _chunker.Chunk("book.pdf",
        [
            new DocumentSegment("page one body", 1),
            new DocumentSegment("page two body", 2),
        ]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(1, chunks[0].Page);
        Assert.Equal(2, chunks[1].Page);
        Assert.Equal([0, 1], chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public void Chunk_MergesContiguousSamePageSegments_ButSplitsOnPageChange()
    {
        var chunks = _chunker.Chunk("book.pdf",
        [
            new DocumentSegment("alpha", 1),
            new DocumentSegment("beta", 1),
            new DocumentSegment("gamma", 2),
        ]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("alpha\nbeta", chunks[0].Text);
        Assert.Equal(1, chunks[0].Page);
        Assert.Equal("gamma", chunks[1].Text);
        Assert.Equal(2, chunks[1].Page);
    }

    [Fact]
    public void Chunk_NoSegments_ProducesNoChunks()
        => Assert.Empty(_chunker.Chunk("empty.txt", []));
}
