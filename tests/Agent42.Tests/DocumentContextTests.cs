using Agent42.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The read-only document RAG half (plan cc-12; ADR 0066): the instruction sentences keep uploaded and web-fetched
/// documents apart, because merging them would assert user provenance for attacker-influenced content.
/// </summary>
public sealed class DocumentContextTests
{
    private static DocumentContext Make(List<string> uploaded, List<string> web)
        => new(new InMemoryDocumentIndex(), () => throw new InvalidOperationException("no factory needed"),
            () => uploaded, () => web, NullLogger.Instance, "u/c");

    [Fact]
    public void WithAwareness_NoDocuments_ReturnsTheBaseInstructionsUnchanged()
    {
        var context = Make([], []);

        Assert.Equal("base", context.WithAwareness("base"));
    }

    [Fact]
    public void WithAwareness_UploadedAndWebDocuments_GetSeparateSentences()
    {
        var context = Make(["mine.pdf"], ["theirs.pdf (web: example.com)"]);

        var text = context.WithAwareness("base");

        Assert.Contains("The user has uploaded the following document(s) to this conversation: mine.pdf.", text, StringComparison.Ordinal);
        Assert.Contains("NOT uploaded by the user: theirs.pdf (web: example.com).", text, StringComparison.Ordinal);
        Assert.Contains("UNTRUSTED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("uploaded the following document(s) to this conversation: mine.pdf, theirs.pdf", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithAwareness_ReadsTheListsLive()
    {
        var uploaded = new List<string>();
        var context = Make(uploaded, []);
        Assert.Equal("base", context.WithAwareness("base"));

        uploaded.Add("late.pdf");

        Assert.Contains("late.pdf", context.WithAwareness("base"), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSearchProvider_ReturnsAProvider()
        => Assert.NotNull(Make(["a.pdf"], []).BuildSearchProvider());
}
