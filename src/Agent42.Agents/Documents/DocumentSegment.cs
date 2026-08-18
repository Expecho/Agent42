namespace Agent42.Agents;

/// <summary>
/// One ordered piece of a document's extracted text, with the page it came from when the
/// format has real pages (PDF) — <c>null</c> for formats that don't (docx/pptx/xlsx/txt/md),
/// where the chunk index is the only locator. Never crosses a grain boundary (extractor →
/// chunker → ingestion, all in-process), so it is a plain record.
/// </summary>
public sealed record DocumentSegment(string Text, int? Page);
