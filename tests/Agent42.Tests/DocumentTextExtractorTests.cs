using System.Text;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Agent42.Agents.Documents;

namespace Agent42.Tests;

/// <summary>
/// AC2: text is extracted per document type, with real page attribution where the format has
/// pages (PDF) and null pages where it doesn't (Office/text). Office fixtures are built in-memory
/// with OpenXml; the PDF is a tiny two-page fixture embedded as base64 (exact bytes, so PDFium
/// parses it identically on every OS).
/// </summary>
public sealed class DocumentTextExtractorTests
{
    private readonly DocumentTextExtractor _extractor = new();

    [Fact]
    public void Extractor_Pdf_ReturnsPerPageSegmentsWithRealPageNumbers()
    {
        var segments = _extractor.Extract("book.pdf", DocumentMediaTypes.Pdf, TestDocuments.SampleTwoPagePdf());

        Assert.Equal(2, segments.Count);
        Assert.Equal(1, segments[0].Page);
        Assert.Contains("Hello PDF World", segments[0].Text);
        Assert.Equal(2, segments[1].Page);
        Assert.Contains("magrathea", segments[1].Text);
    }

    [Fact]
    public void Extractor_Docx_ReturnsParagraphSegmentsWithNullPages()
    {
        var docx = BuildDocx("The answer is 42.", "So long and thanks for all the fish.");

        var segments = _extractor.Extract("towel.docx", DocumentMediaTypes.Docx, docx);

        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Null(s.Page));
        Assert.Equal("The answer is 42.", segments[0].Text);
        Assert.Contains("fish", segments[1].Text);
    }

    [Fact]
    public void Extractor_Xlsx_ResolvesSharedStringsIntoOneSegmentPerSheet()
    {
        var xlsx = BuildXlsxWithSharedString("Magrathea");

        var segments = _extractor.Extract("planets.xlsx", DocumentMediaTypes.Xlsx, xlsx);

        var segment = Assert.Single(segments);
        Assert.Null(segment.Page);
        Assert.Contains("Magrathea", segment.Text);
    }

    [Fact]
    public void Extractor_Pptx_ReturnsSlideTextWithNullPage()
    {
        var pptx = BuildPptx("Deep Thought computes.");

        var segments = _extractor.Extract("deck.pptx", DocumentMediaTypes.Pptx, pptx);

        var segment = Assert.Single(segments);
        Assert.Null(segment.Page);
        Assert.Contains("Deep Thought", segment.Text);
    }

    [Fact]
    public void Extractor_PlainText_ReturnsWholeContentAsOneNullPageSegment()
    {
        var bytes = Encoding.UTF8.GetBytes("Don't panic. Bring a towel.");

        var segments = _extractor.Extract("guide.txt", DocumentMediaTypes.PlainText, bytes);

        var segment = Assert.Single(segments);
        Assert.Null(segment.Page);
        Assert.Equal("Don't panic. Bring a towel.", segment.Text);
    }

    [Fact]
    public void Extractor_UnknownType_Throws()
    {
        Assert.Throws<NotSupportedException>(
            () => _extractor.Extract("x.bin", "application/octet-stream", [1, 2, 3]));
    }

    [Theory]
    [InlineData(DocumentMediaTypes.Pdf, true)]
    [InlineData(DocumentMediaTypes.Docx, true)]
    [InlineData("text/markdown", true)]
    [InlineData("text/x-markdown; charset=utf-8", true)]
    [InlineData("image/png", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData(null, false)]
    public void DocumentMediaTypes_IsSupported_ClassifiesTypes(string? mediaType, bool expected)
        => Assert.Equal(expected, DocumentMediaTypes.IsSupported(mediaType));

    private static byte[] BuildDocx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                paragraphs.Select(p => new W.Paragraph(new W.Run(new W.Text(p))))));
            main.Document.Save();
        }

        return ms.ToArray();
    }

    private static byte[] BuildXlsxWithSharedString(string value)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();

            var sstPart = workbookPart.AddNewPart<SharedStringTablePart>();
            sstPart.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text(value)));

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(new S.Cell
                {
                    CellReference = "A1",
                    DataType = S.CellValues.SharedString,
                    CellValue = new S.CellValue("0"),
                })));

            var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
            sheets.Append(new S.Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1U, Name = "Sheet1" });
            workbookPart.Workbook.Save();
        }

        return ms.ToArray();
    }

    private static byte[] BuildPptx(string text)
    {
        using var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, DocumentFormat.OpenXml.PresentationDocumentType.Presentation))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();

            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(
                new P.CommonSlideData(
                    new P.ShapeTree(
                        new P.Shape(
                            new P.TextBody(
                                new A.BodyProperties(),
                                new A.Paragraph(new A.Run(new A.Text(text))))))));

            presentationPart.Presentation.Append(
                new P.SlideIdList(new P.SlideId { Id = 256U, RelationshipId = presentationPart.GetIdOfPart(slidePart) }));
            presentationPart.Presentation.Save();
        }

        return ms.ToArray();
    }
}
