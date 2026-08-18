using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

namespace Agent42.Agents.Documents;

/// <summary>
/// Local-library <see cref="IDocumentTextExtractor"/>: Docnet.Core (PDFium) for PDF with real
/// per-page attribution, OpenXml for docx/pptx/xlsx (no true pages), and a plain UTF-8 read for
/// any <c>text/*</c> type. No cloud OCR, so a scanned/image-only PDF simply yields no text.
/// </summary>
public sealed class DocumentTextExtractor : IDocumentTextExtractor
{
    // Docnet's DocLib.Instance wraps a process-global native PDFium handle that is not safe for
    // concurrent use, so every PDF extraction is serialized through this lock. Fine at
    // per-conversation, synchronous-ingestion scale (ADR 0007).
    private static readonly Lock PdfLock = new();

    public IReadOnlyList<DocumentSegment> Extract(string fileName, string mediaType, byte[] content)
    {
        var type = (mediaType ?? "").Split(';', 2)[0].Trim();
        return type switch
        {
            DocumentMediaTypes.Pdf => ExtractPdf(content),
            DocumentMediaTypes.Docx => ExtractDocx(content),
            DocumentMediaTypes.Pptx => ExtractPptx(content),
            DocumentMediaTypes.Xlsx => ExtractXlsx(content),
            _ when type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) => ExtractText(content),
            _ => throw new NotSupportedException($"Cannot extract text from media type '{mediaType}'."),
        };
    }

    // PDFium exposes real pages, so each non-empty page becomes a segment tagged with its 1-based
    // page number — the citation the retrieval tool surfaces.
    private static IReadOnlyList<DocumentSegment> ExtractPdf(byte[] content)
    {
        var segments = new List<DocumentSegment>();
        lock (PdfLock)
        {
            var library = DocLib.Instance;
            using var reader = library.GetDocReader(content, new PageDimensions(1080, 1920));
            var pageCount = reader.GetPageCount();
            for (var i = 0; i < pageCount; i++)
            {
                using var page = reader.GetPageReader(i);
                var text = page.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    segments.Add(new DocumentSegment(text, i + 1));
                }
            }
        }

        return segments;
    }

    // Word: one segment per non-empty paragraph, in document order. No page concept in the XML,
    // so pages are null and the chunk index locates the text.
    private static IReadOnlyList<DocumentSegment> ExtractDocx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return [];
        }

        return [.. body
            .Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Select(p => p.InnerText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => new DocumentSegment(t, null))];
    }

    // PowerPoint: one segment per slide (its concatenated text runs), in slide order.
    private static IReadOnlyList<DocumentSegment> ExtractPptx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var doc = PresentationDocument.Open(stream, false);
        var slideParts = doc.PresentationPart?.SlideParts;
        if (slideParts is null)
        {
            return [];
        }

        var segments = new List<DocumentSegment>();
        foreach (var slide in slideParts)
        {
            var runs = slide.Slide?.Descendants<A.Text>().Select(t => t.Text) ?? [];
            var text = string.Join(" ", runs);
            if (!string.IsNullOrWhiteSpace(text))
            {
                segments.Add(new DocumentSegment(text, null));
            }
        }

        return segments;
    }

    // Excel: one segment per worksheet, cells joined by spaces. Shared-string cells are resolved
    // through the workbook's shared string table.
    private static IReadOnlyList<DocumentSegment> ExtractXlsx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var doc = SpreadsheetDocument.Open(stream, false);
        var workbookPart = doc.WorkbookPart;
        if (workbookPart is null)
        {
            return [];
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var segments = new List<DocumentSegment>();
        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            var cells = worksheetPart.Worksheet?.Descendants<Cell>() ?? [];
            var values = cells
                .Select(cell => CellText(cell, sharedStrings))
                .Where(v => !string.IsNullOrWhiteSpace(v));
            var text = string.Join(" ", values);
            if (!string.IsNullOrWhiteSpace(text))
            {
                segments.Add(new DocumentSegment(text, null));
            }
        }

        return segments;
    }

    private static string CellText(Cell cell, SharedStringTable? sharedStrings)
    {
        var value = cell.CellValue?.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.SharedString
            && sharedStrings is not null
            && int.TryParse(value, out var index)
            && index >= 0 && index < sharedStrings.ChildElements.Count)
        {
            return sharedStrings.ChildElements[index].InnerText;
        }

        return value;
    }

    // txt/md and any other text/* type: decode as UTF-8, one segment for the whole document
    // (the chunker splits it). Pages are null.
    private static IReadOnlyList<DocumentSegment> ExtractText(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        return string.IsNullOrWhiteSpace(text) ? [] : [new DocumentSegment(text, null)];
    }
}
