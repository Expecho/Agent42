using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The export's pure parts (ADR 0091): the Markdown transcript and the names every file and entry is given.
/// </summary>
public sealed class ConversationMarkdownTests
{
    private static readonly DateTimeOffset Exported = new(2026, 10, 10, 12, 30, 0, TimeSpan.Zero);

    private static ConversationExport Export(IReadOnlyList<ChatTurn> turns, string id = "c1", AgentBinding? agent = null)
        => new(new ConversationInfo(id, "My chat", Exported), "gpt-test", agent, null, turns, []);

    // AC1: header, both roles, and the agent's name heading its turns.
    [Fact]
    public void Render_HasHeaderAndTurns()
    {
        var md = ConversationMarkdown.Render(
            Export([new ChatTurn("user", "Hi"), new ChatTurn("assistant", "Hello")], agent: new AgentBinding("a", "Helper", "be nice", "gpt-test")),
            Exported);

        Assert.StartsWith("# My chat", md, StringComparison.Ordinal);
        Assert.Contains("- Exported: 2026-10-10 12:30 UTC", md, StringComparison.Ordinal);
        Assert.Contains("- Agent: Helper", md, StringComparison.Ordinal);
        Assert.Contains("- Model: gpt-test", md, StringComparison.Ordinal);
        Assert.Contains("## You", md, StringComparison.Ordinal);
        Assert.Contains("## Helper", md, StringComparison.Ordinal);
        Assert.DoesNotContain("24 hours", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NamesAPlainChatsAssistantAgent42()
        => Assert.Contains("## Agent 42", ConversationMarkdown.Render(Export([new ChatTurn("assistant", "x")]), Exported), StringComparison.Ordinal);

    // A fence inside a tool result cannot close the block that holds it.
    [Fact]
    public void Fence_IsLongerThanTheLongestRun()
    {
        Assert.Equal("```", ConversationMarkdown.Fence("plain"));
        Assert.Equal("````", ConversationMarkdown.Fence("a ``` b"));
        Assert.Equal("``````", ConversationMarkdown.Fence("`````"));
    }

    // AC1: tool calls fold into <details>, with arguments and a result truncated to the limit.
    [Fact]
    public void Render_FoldsToolCallsAndTruncatesResults()
    {
        var result = "```" + new string('r', ExportLimits.MarkdownToolResultChars + 500);
        var md = ConversationMarkdown.Render(
            Export([new ChatTurn("assistant", "done", ToolCalls: [new ChatToolCall("t1", "web_search", "{\"q\":\"x\"}", result)])]),
            Exported);

        Assert.Contains("<details>", md, StringComparison.Ordinal);
        Assert.Contains("<summary>Tool call: web_search</summary>", md, StringComparison.Ordinal);
        Assert.Contains("```json", md, StringComparison.Ordinal);
        Assert.Contains("````" + Environment.NewLine + "```rrr", md, StringComparison.Ordinal);
        Assert.Contains("{\"q\":\"x\"}", md, StringComparison.Ordinal);
        Assert.Contains("(truncated: 2503 characters in all", md, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('r', ExportLimits.MarkdownToolResultChars), md, StringComparison.Ordinal);
        Assert.Contains("</details>", md, StringComparison.Ordinal);
    }

    // AC1: an image links into the archive in ZIP mode and is only named otherwise, as is one whose blob was missing.
    [Fact]
    public void Render_ReferencesImagesInBothModes()
    {
        var turn = new ChatTurn("user", "look", [new AttachmentRef("img1", "blob", "image/png", "cat.png")]);
        var plain = ConversationMarkdown.Render(Export([turn]), Exported);
        var zipped = ConversationMarkdown.Render(Export([turn]), Exported,
            new ExportFiles(new Dictionary<string, string> { ["img1"] = "attachments/img1-cat.png" }, new Dictionary<string, string>()));
        var missing = ConversationMarkdown.Render(Export([turn]), Exported,
            new ExportFiles(new Dictionary<string, string>(), new Dictionary<string, string>()));

        Assert.Contains("_[image: cat.png]_", plain, StringComparison.Ordinal);
        Assert.Contains("![cat.png](attachments/img1-cat.png)", zipped, StringComparison.Ordinal);
        Assert.Contains("_[image: cat.png]_", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ListsArtifactCardsWithTheirPath()
    {
        var turn = new ChatTurn("assistant", "made it", Artifacts: [new ArtifactInfo("art1", 2, "html", "Page")]);
        var md = ConversationMarkdown.Render(Export([turn]), Exported,
            new ExportFiles(new Dictionary<string, string>(), new Dictionary<string, string> { ["art1/v2"] = "artifacts/art1/v2.html" }));

        Assert.Contains("- Artifact: **Page** (html, v2) — [artifacts/art1/v2.html](artifacts/art1/v2.html)", md, StringComparison.Ordinal);
    }

    // ADR 0068: a continuous chat's export is the retained 24 h window, and says so.
    [Fact]
    public void Render_NotesAContinuousChatsRetentionWindow()
    {
        var export = Export([new ChatTurn("user", "x")], id: RetentionLimits.ContinuousConversationIdPrefix + "a1");
        Assert.True(export.IsContinuous);
        Assert.Contains("last 24 hours", ConversationMarkdown.Render(export, Exported), StringComparison.Ordinal);
        Assert.Equal("continuous", ConversationExportDocument.From(export, Exported, null).HistoryMode);
    }

    // AC7: no separator, no `..`, a fallback when nothing is left, and a length cap.
    [Theory]
    [InlineData("Hello, World!", "hello-world")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("a\\b/c..d", "a-b-c-d")]
    [InlineData("", "conversation")]
    [InlineData("///", "conversation")]
    [InlineData(null, "conversation")]
    public void FileNames_AreSanitized(string? title, string expected)
        => Assert.Equal(expected, ExportFileNames.Slug(title));

    [Fact]
    public void Slug_IsCapped()
        => Assert.Equal(ExportLimits.MaxSlugChars, ExportFileNames.Slug(new string('a', 300)).Length);

    [Theory]
    [InlineData("cat.png", "cat.png")]
    [InlineData("../../x.png", "x.png")]
    [InlineData("a/b\\c.png", "a_b_c.png")]
    [InlineData("..", "file")]
    [InlineData(".hidden", "hidden")]
    public void FileName_KeepsTheExtensionButNoTraversal(string name, string expected)
        => Assert.Equal(expected, ExportFileNames.FileName(name));

    [Fact]
    public void FileName_IsCappedFromTheStartSoTheExtensionSurvives()
    {
        var name = ExportFileNames.FileName(new string('a', 300) + ".png");
        Assert.Equal(ExportLimits.MaxFileNameChars, name.Length);
        Assert.EndsWith(".png", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("html", "html")]
    [InlineData("svg", "svg")]
    [InlineData("markdown", "md")]
    [InlineData("code", "txt")]
    public void ArtifactExtension_MapsFromKind(string kind, string extension)
        => Assert.Equal(extension, ExportFileNames.ArtifactExtension(kind));

    [Fact]
    public void EntryNames_AreBuiltFromSafeParts()
    {
        var info = new ConversationInfo("../abc/defghijk", "T/../x", Exported);
        Assert.Equal("2026-10-10-t-x.md", ExportFileNames.Download(info, "md"));
        Assert.Equal("conversations/2026-10-10-t-x-abc_defg/", ExportFileNames.Folder(info));
        Assert.Equal("attachments/a_b-c.png", ExportFileNames.Attachment(new AttachmentRef("a/b", "blob", "image/png", "../c.png")));
    }
}
