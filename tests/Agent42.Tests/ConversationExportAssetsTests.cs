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

    // AC12: the links live INSIDE the share dialog, so they inherit its gate rather than re-deriving it.
    [Fact]
    public async Task ShareDialog_HasExportLinks()
    {
        var dialog = Dialog(await fixture.Client.GetStringAsync("/"));

        Assert.Contains("Share or export", dialog, StringComparison.Ordinal);
        foreach (var id in new[] { "exportMarkdownLink", "exportJsonLink", "exportZipLink" })
        {
            Assert.Contains($"id=\"{id}\"", dialog, StringComparison.Ordinal);
        }
    }

    // AC12: the hrefs are set for the conversation the dialog opened for, and only past the null guard.
    [Fact]
    public async Task OpenShareLink_SetsExportHrefsAfterNullGuard()
    {
        var body = StaticAssetsServerFixture.Body(await fixture.GetAppScriptAsync(), "export async function openShareLink");

        var guard = body.IndexOf("if (!convId) return;", StringComparison.Ordinal);
        var href = body.IndexOf("exportZipLink.href", StringComparison.Ordinal);
        Assert.True(guard >= 0, "openShareLink lost its null guard");
        Assert.True(href > guard, "the export hrefs must be set after the null guard");
        Assert.Contains("/api/conversations/${encodeURIComponent(convId)}/export?format=", body, StringComparison.Ordinal);
        Assert.Contains("exportMarkdownLink.href = exportBase + \"markdown\"", body, StringComparison.Ordinal);
        Assert.Contains("exportJsonLink.href = exportBase + \"json\"", body, StringComparison.Ordinal);
        Assert.Contains("exportZipLink.href = exportBase + \"zip\"", body, StringComparison.Ordinal);
    }

    // AC13: the control is in Preferences → General and main.js wires the module that drives it.
    [Fact]
    public async Task ExportAll_IsWiredFromMain()
    {
        var index = await fixture.Client.GetStringAsync("/");
        var general = index[index.IndexOf("id=\"panelGeneral\"", StringComparison.Ordinal)..index.IndexOf("id=\"panelTools\"", StringComparison.Ordinal)];
        Assert.Contains("id=\"exportAllBtn\"", general, StringComparison.Ordinal);
        Assert.Contains("id=\"exportAllStatus\"", general, StringComparison.Ordinal);

        var main = await fixture.Client.GetStringAsync("/js/main.js");
        Assert.Contains("import { wireExport } from \"./export.js\";", main, StringComparison.Ordinal);
        Assert.Contains("wireExport();", main, StringComparison.Ordinal);
    }

    // AC13: the loop follows the server's keyset cursor and reads the part count from the headers.
    [Fact]
    public async Task ExportAll_FollowsNextHeader()
    {
        var body = StaticAssetsServerFixture.Body(await fixture.GetAppScriptAsync(), "async function exportAll");

        Assert.Contains("fetch(\"/api/export\" + (next ? \"?after=\" + encodeURIComponent(next) : \"\"))", body, StringComparison.Ordinal);
        Assert.Contains("next = res.headers.get(\"X-Export-Next\");", body, StringComparison.Ordinal);
        Assert.Contains("} while (next);", body, StringComparison.Ordinal);
        Assert.Contains("res.headers.get(\"X-Export-Parts\")", body, StringComparison.Ordinal);
        Assert.Contains("exportAllBtn.disabled = true;", body, StringComparison.Ordinal);
    }

    // AC14: SHELL is all-or-nothing, so the new module must be in it (the CACHE pins live in the four older tests).
    [Fact]
    public async Task ServiceWorker_ListsExportModule()
    {
        var sw = await fixture.Client.GetStringAsync("/sw.js");
        Assert.Contains("\"/js/export.js\"", sw, StringComparison.Ordinal);
    }
}
