
using Agent42.Agents;

namespace Agent42.Tests;

public sealed class DocumentMediaTypesTests
{
    // A correct browser-supplied type is trusted as-is.
    [Theory]
    [InlineData("guide.pdf", DocumentMediaTypes.Pdf, DocumentMediaTypes.Pdf)]
    [InlineData("notes.txt", DocumentMediaTypes.PlainText, DocumentMediaTypes.PlainText)]
    [InlineData("photo.png", "image/png", "image/png")]
    [InlineData("readme.md", "text/x-markdown", "text/x-markdown")]
    public void ResolveMediaType_KeepsRecognizedProvidedType(string fileName, string provided, string expected) =>
        Assert.Equal(expected, DocumentMediaTypes.ResolveMediaType(fileName, provided));

    // The reported bug: Windows browsers send a generic (or empty) content type for documents, which
    // must fall back to the extension instead of being rejected as unsupported.
    [Theory]
    [InlineData("report.docx", "application/octet-stream", DocumentMediaTypes.Docx)]
    [InlineData("deck.pptx", "application/octet-stream", DocumentMediaTypes.Pptx)]
    [InlineData("sheet.xlsx", "application/zip", DocumentMediaTypes.Xlsx)]
    [InlineData("notes.md", "", DocumentMediaTypes.Markdown)]
    [InlineData("notes.markdown", null, DocumentMediaTypes.Markdown)]
    [InlineData("plan.txt", "application/octet-stream", DocumentMediaTypes.PlainText)]
    [InlineData("guide.PDF", "application/octet-stream", DocumentMediaTypes.Pdf)]
    public void ResolveMediaType_FallsBackToExtension_WhenProvidedTypeIsGeneric(
        string fileName, string? provided, string expected) =>
        Assert.Equal(expected, DocumentMediaTypes.ResolveMediaType(fileName, provided));

    // The resolved type must be one the extractor and IsSupported both accept.
    [Fact]
    public void ResolveMediaType_FallbackYieldsASupportedType()
    {
        var resolved = DocumentMediaTypes.ResolveMediaType("report.docx", "application/octet-stream");
        Assert.True(DocumentMediaTypes.IsSupported(resolved));
    }

    // An unknown extension with a generic type stays unrecognized — the handler still rejects it.
    [Fact]
    public void ResolveMediaType_UnknownExtension_ReturnsProvidedType()
    {
        var resolved = DocumentMediaTypes.ResolveMediaType("archive.rar", "application/octet-stream");
        Assert.Equal("application/octet-stream", resolved);
        Assert.False(DocumentMediaTypes.IsSupported(resolved));
    }
}
