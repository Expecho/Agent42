using System.Text;

namespace Agent42.Agents;

/// <summary>
/// Joins consecutive <see cref="DocumentChunk"/>s back into readable text for <c>read_document</c>
/// (ADR 0098). <see cref="DocumentChunker"/> windows each page or block with
/// <see cref="DocumentChunker.ChunkOverlap"/> characters shared between neighbours, so naive
/// concatenation repeats about a fifth of the text. Within one page or block the overlap is
/// <b>exact</b> (both windows are <c>Trim()</c>med cuts of the same string), so this strips the longest
/// suffix of one chunk that is a prefix of the next, searching no further than the overlap itself.
/// A page change starts a new block with no overlap, and is joined with a paragraph break.
///
/// <para>Accepted residual: on genuinely periodic text (identical repeated rows) the longest exact match
/// can be longer than the real overlap, so some repeated text is dropped. It never invents text and
/// never drops a non-repeated passage.</para>
/// </summary>
public static class DocumentStitcher
{
    /// <summary>
    /// The shortest suffix/prefix match treated as a real overlap. Below it a coincidental match ("." at
    /// the end of one chunk and the start of the next) would silently delete text; keeping a few repeated
    /// characters is the safer failure for a sequential read.
    /// </summary>
    public const int MinOverlap = 10;

    private const string BlockBreak = "\n\n";

    /// <summary>
    /// Stitches <paramref name="chunks"/> (in chunk order) into one text. <paramref name="previous"/> is the
    /// chunk just before the range, when there is one: it is used only to strip its overlap from the first
    /// chunk and is never emitted, so consecutive parts concatenate without repetition.
    /// </summary>
    public static string Stitch(DocumentChunk? previous, IReadOnlyList<DocumentChunk> chunks)
    {
        var builder = new StringBuilder();
        var prior = previous;

        foreach (var chunk in chunks)
        {
            var overlap = prior is null ? 0 : OverlapLength(prior, chunk);
            if (builder.Length == 0)
            {
                builder.Append(chunk.Text.AsSpan(overlap).TrimStart());
            }
            else if (overlap > 0)
            {
                builder.Append(chunk.Text.AsSpan(overlap));
            }
            else
            {
                builder.Append(BlockBreak).Append(chunk.Text);
            }

            prior = chunk;
        }

        return builder.ToString();
    }

    // The longest suffix of `before` that is a prefix of `after`, within one page or block, bounded by the
    // chunker's overlap; 0 when the chunks are on different pages or share less than MinOverlap.
    private static int OverlapLength(DocumentChunk before, DocumentChunk after)
    {
        if (before.Page != after.Page)
        {
            return 0;
        }

        var max = Math.Min(DocumentChunker.ChunkOverlap, Math.Min(before.Text.Length, after.Text.Length));
        for (var length = max; length >= MinOverlap; length--)
        {
            if (before.Text.AsSpan(before.Text.Length - length).SequenceEqual(after.Text.AsSpan(0, length)))
            {
                return length;
            }
        }

        return 0;
    }
}
