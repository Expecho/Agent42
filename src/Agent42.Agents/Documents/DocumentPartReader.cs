using System.Text;

namespace Agent42.Agents;

/// <summary>
/// The fetch → stitch → format core of <c>read_document</c> (ADR 0098), shared by the chat grain's tool and the
/// agent fleet's (ADR 0100). It reads part <c>p</c> of one file under one owner key in chunk order and renders it
/// with the fixed header and footer compaction relies on. The callers own everything around it: which names are
/// readable, any per-turn cap, and the catch that keeps a failure from bricking the turn or run.
/// </summary>
public static class DocumentPartReader
{
    /// <summary>
    /// Reads <paramref name="part"/> (1-based) of <paramref name="fileName"/> under <paramref name="ownerKey"/>.
    /// <see cref="DocumentPartResult.IsPart"/> is <c>true</c> only for a successful part; an empty document or an
    /// out-of-range part returns a refusal text instead. <paramref name="untrusted"/> adds ADR 0066's web-document
    /// marker to the header.
    /// </summary>
    public static async Task<DocumentPartResult> ReadAsync(
        IDocumentIndex index, string ownerKey, string fileName, int part, bool untrusted,
        CancellationToken cancellationToken = default)
    {
        var start = (Math.Max(part, 1) - 1) * ReadDocumentLimits.ChunksPerPart;

        // One chunk BEFORE the part, used only to strip its overlap from the part's first chunk, never emitted.
        var readFrom = Math.Max(start - 1, 0);
        var range = await index.ReadChunksAsync(
            ownerKey, fileName, readFrom, start - readFrom + ReadDocumentLimits.ChunksPerPart, cancellationToken);

        if (range.TotalChunks == 0)
        {
            return new($"'{fileName}' has no readable text (it may be a scanned or image-only document), so there is "
                + "nothing to read.", IsPart: false);
        }

        var totalParts = (range.TotalChunks + ReadDocumentLimits.ChunksPerPart - 1) / ReadDocumentLimits.ChunksPerPart;
        if (part < 1 || part > totalParts)
        {
            return new($"'{fileName}' has {totalParts} part(s); part {part} does not exist. Parts are numbered 1 to {totalParts}.",
                IsPart: false);
        }

        var previous = range.Chunks.FirstOrDefault(c => c.ChunkIndex < start);
        var chunks = range.Chunks.Where(c => c.ChunkIndex >= start).ToList();
        var text = DocumentStitcher.Stitch(previous, chunks);
        if (text.Length > ReadDocumentLimits.MaxPartTextLength)
        {
            text = text[..ReadDocumentLimits.MaxPartTextLength] + ReadDocumentLimits.TruncationMarker;
        }

        return new(FormatPart(fileName, part, totalParts, chunks, text, untrusted), IsPart: true);
    }

    /// <summary>The refusal for a name outside <paramref name="names"/>, listing the readable ones.</summary>
    public static string UnknownName(string fileName, IReadOnlyCollection<string> names, string scope)
        => names.Count == 0
            ? $"This {scope} has no documents to read."
            : $"There is no document named '{fileName}' in this {scope}. Use one of these names exactly: "
                + string.Join(", ", names.Select(n => $"'{n}'")) + ".";

    private static string FormatPart(
        string fileName, int part, int totalParts, List<DocumentChunk> chunks, string text, bool untrusted)
    {
        var header = new StringBuilder($"{ReadDocumentLimits.PartHeaderPrefix}{fileName}' — part {part} of {totalParts}");
        var pages = chunks.Where(c => c.Page is not null).Select(c => c.Page!.Value).ToList();
        if (pages.Count > 0)
        {
            header.Append(pages.Min() == pages.Max() ? $" (page {pages.Min()})" : $" (pages {pages.Min()}–{pages.Max()})");
        }

        // ADR 0066's provenance split: a web document's text was authored by whoever controls that URL.
        if (untrusted)
        {
            header.Append(" — ").Append(ReadDocumentLimits.UntrustedMarker);
        }

        var footer = part < totalParts
            ? $"Call read_document with part {part + 1} to continue."
            : "This is the last part.";

        return $"{header}\n\n{text}\n\n{footer}";
    }
}

/// <summary>One <see cref="DocumentPartReader.ReadAsync"/> outcome: the tool text, and whether it is a real part.</summary>
public readonly record struct DocumentPartResult(string Text, bool IsPart);
