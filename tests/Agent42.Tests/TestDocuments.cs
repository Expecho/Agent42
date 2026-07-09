namespace Agent42.Tests;

/// <summary>
/// Shared document fixtures for the RAG tests. The PDF is a minimal, valid two-page document —
/// page 1 "Hello PDF World page one", page 2 "Second page magrathea" — embedded as base64 so its
/// exact bytes parse identically under PDFium on every OS (PDFium rebuilds the absent xref).
/// </summary>
public static class TestDocuments
{
    private const string TwoPagePdfBase64 =
        "JVBERi0xLjQKMSAwIG9iajw8L1R5cGUvQ2F0YWxvZy9QYWdlcyAyIDAgUj4+ZW5kb2JqCjIgMCBvYmo8" +
        "PC9UeXBlL1BhZ2VzL0tpZHNbMyAwIFIgNiAwIFJdL0NvdW50IDI+PmVuZG9iagozIDAgb2JqPDwvVHlw" +
        "ZS9QYWdlL1BhcmVudCAyIDAgUi9NZWRpYUJveFswIDAgNjEyIDc5Ml0vUmVzb3VyY2VzPDwvRm9udDw8" +
        "L0YxIDUgMCBSPj4+Pi9Db250ZW50cyA0IDAgUj4+ZW5kb2JqCjQgMCBvYmo8PC9MZW5ndGggNTI+PnN0" +
        "cmVhbQpCVCAvRjEgMjQgVGYgNzIgNzAwIFRkIChIZWxsbyBQREYgV29ybGQgcGFnZSBvbmUpIFRqIEVU" +
        "CmVuZHN0cmVhbSBlbmRvYmoKNSAwIG9iajw8L1R5cGUvRm9udC9TdWJ0eXBlL1R5cGUxL0Jhc2VGb250" +
        "L0hlbHZldGljYT4+ZW5kb2JqCjYgMCBvYmo8PC9UeXBlL1BhZ2UvUGFyZW50IDIgMCBSL01lZGlhQm94" +
        "WzAgMCA2MTIgNzkyXS9SZXNvdXJjZXM8PC9Gb250PDwvRjEgNSAwIFI+Pj4+L0NvbnRlbnRzIDcgMCBS" +
        "Pj5lbmRvYmoKNyAwIG9iajw8L0xlbmd0aCA0OD4+c3RyZWFtCkJUIC9GMSAyNCBUZiA3MiA3MDAgVGQg" +
        "KFNlY29uZCBwYWdlIG1hZ3JhdGhlYSkgVGogRVQKZW5kc3RyZWFtIGVuZG9iagp0cmFpbGVyPDwvUm9v" +
        "dCAxIDAgUi9TaXplIDg+PgolJUVPRg==";

    public static byte[] SampleTwoPagePdf() => Convert.FromBase64String(TwoPagePdfBase64);
}
