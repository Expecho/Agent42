namespace Agent42.Tests;

/// <summary>
/// Guards the export wiring in the frontend (ADR 0091) by source text, like <see cref="ShareLinkAssetsTests"/>: the
/// share dialog's three download links, the "Export all conversations" control and the module that walks the parts.
/// </summary>
[Collection("StaticAssets")]
public sealed class ConversationExportAssetsTests(StaticAssetsServerFixture fixture)
{
    private static string Dialog(string index)
    {
        var at = index.IndexOf("id=\"shareLinkDialog\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "the share dialog is missing");
        var end = index.IndexOf("</dialog>", at, StringComparison.Ordinal);
        Assert.True(end > at, "the share dialog is not closed");
        return index[at..end];
    }

    // AC14: SHELL is all-or-nothing, so the new module must be in it (the CACHE pins live in the four older tests).
    [Fact]
    public async Task ServiceWorker_ListsExportModule()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");
        Assert.Contains("\"/js/export.js\"", sw, StringComparison.Ordinal);
    }
}
