using System.Text;

namespace Agent42.Agents.Documents;

/// <summary>
/// Splits extracted <see cref="DocumentSegment"/>s into overlapping <see cref="DocumentChunk"/>s
/// sized for embedding + retrieval. Consecutive segments sharing a page (all of a document's
/// null-page Office/text segments count as one run) are concatenated, then windowed into
/// <see cref="ChunkSize"/>-char chunks that overlap by <see cref="ChunkOverlap"/> so a passage
/// straddling a window boundary still lands whole in one chunk. Each chunk carries the source
/// filename, its page (when known), and a document-global sequential index for citation.
/// </summary>
public sealed class DocumentChunker
{
    /// <summary>Target chunk length in characters.</summary>
    public const int ChunkSize = 1000;

    /// <summary>Characters shared between adjacent chunks so context isn't cut at a boundary.</summary>
    public const int ChunkOverlap = 200;

    public IReadOnlyList<DocumentChunk> Chunk(string fileName, IReadOnlyList<DocumentSegment> segments)
    {
        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;

        foreach (var (page, text) in GroupByContiguousPage(segments))
        {
            foreach (var window in Window(text))
            {
                chunks.Add(new DocumentChunk(fileName, page, chunkIndex++, window));
            }
        }

        return chunks;
    }

    // Merges consecutive segments with the same page into one text block, so windowing spans
    // paragraphs/cells within a page rather than emitting a chunk per tiny segment. Page order is
    // preserved; a change in page value starts a new block.
    private static IEnumerable<(int? Page, string Text)> GroupByContiguousPage(
        IReadOnlyList<DocumentSegment> segments)
    {
        var builder = new StringBuilder();
        int? currentPage = null;
        var hasCurrent = false;

        foreach (var segment in segments)
        {
            if (hasCurrent && segment.Page != currentPage)
            {
                yield return (currentPage, builder.ToString());
                builder.Clear();
                hasCurrent = false;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(segment.Text);
            currentPage = segment.Page;
            hasCurrent = true;
        }

        if (hasCurrent)
        {
            yield return (currentPage, builder.ToString());
        }
    }

    // Sliding window over one page/block: fixed-size chunks stepping forward by
    // (ChunkSize - ChunkOverlap), the final window ending exactly at the text's end. Whitespace-only
    // windows are dropped.
    private static IEnumerable<string> Window(string text)
    {
        const int step = ChunkSize - ChunkOverlap;
        var length = text.Length;
        if (length == 0)
        {
            yield break;
        }

        for (var start = 0; start < length; start += step)
        {
            var end = Math.Min(start + ChunkSize, length);
            var window = text[start..end].Trim();
            if (window.Length > 0)
            {
                yield return window;
            }

            if (end == length)
            {
                yield break;
            }
        }
    }
}
