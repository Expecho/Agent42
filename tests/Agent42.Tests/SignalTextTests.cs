using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>Signal's Markdown to text + <c>textStyle</c> conversion (ADR 0111, phase 3): offsets are UTF-16 units of the final text.</summary>
public class SignalTextTests
{
    [Fact]
    public void Bold_Italic_Strike_AndCode_BecomeStyleRuns_WithTheMarkersRemoved()
    {
        var (text, styles) = SignalText.FromMarkdown("**bold** and *it* and _al_ and ~~gone~~ and `code`");

        Assert.Equal("bold and it and al and gone and code", text);
        Assert.Equal(["0:4:BOLD", "9:2:ITALIC", "16:2:ITALIC", "23:4:STRIKETHROUGH", "32:4:MONOSPACE"], styles);
    }

    [Fact]
    public void Offsets_CountUtf16Units_AfterAnEmoji()
    {
        var (text, styles) = SignalText.FromMarkdown("\U0001F600 **hi**");

        Assert.Equal("\U0001F600 hi", text);
        Assert.Equal(["3:2:BOLD"], styles);
        Assert.Equal("hi", text.Substring(3, 2));
    }

    [Fact]
    public void Offsets_AccountForMarkersRemovedEarlier()
    {
        var (text, styles) = SignalText.FromMarkdown("**a** `b` **c**");

        Assert.Equal("a b c", text);
        Assert.Equal(["0:1:BOLD", "2:1:MONOSPACE", "4:1:BOLD"], styles);
    }

    [Fact]
    public void AFencedBlock_IsOneMonospaceRun_WithoutItsFenceLines_AndNoMarkdownInside()
    {
        var (text, styles) = SignalText.FromMarkdown("before\n```csharp\nvar **x** = 1;\nreturn;\n```\nafter");

        Assert.Equal("before\nvar **x** = 1;\nreturn;\nafter", text);
        Assert.Equal(["7:22:MONOSPACE"], styles);
    }

    [Fact]
    public void ALink_BecomesTextAndUrl_AndItsTextCanBeStyled()
    {
        var (text, styles) = SignalText.FromMarkdown("see [**the docs**](https://example.org/a?b=1) now");

        Assert.Equal("see the docs (https://example.org/a?b=1) now", text);
        Assert.Equal(["4:8:BOLD"], styles);
    }

    [Fact]
    public void AHeading_IsABoldLine()
    {
        var (text, styles) = SignalText.FromMarkdown("# Title\nbody\n## Sub *x*");

        Assert.Equal("Title\nbody\nSub x", text);
        Assert.Equal(["0:5:BOLD", "11:5:BOLD", "15:1:ITALIC"], styles);
    }

    [Fact]
    public void Nested_BoldContainingItalic_GivesBothRuns()
    {
        var (text, styles) = SignalText.FromMarkdown("**a *b* c**");

        Assert.Equal("a b c", text);
        Assert.Equal(["0:5:BOLD", "2:1:ITALIC"], styles);
    }

    [Theory]
    [InlineData("**unclosed bold")]
    [InlineData("*lonely")]
    [InlineData("a * b * c")]
    [InlineData("snake_case_name here")]
    [InlineData("`unclosed code")]
    [InlineData("```\nunclosed fence")]
    [InlineData("[text](no closing")]
    [InlineData("~~half")]
    [InlineData("**")]
    [InlineData("****")]
    [InlineData("`")]
    [InlineData("#")]
    [InlineData("")]
    public void UnbalancedInput_NeverThrows_AndKeepsTheMarkersLiteral(string markdown)
    {
        var (text, styles) = SignalText.FromMarkdown(markdown);

        Assert.Equal(markdown, text);
        Assert.Empty(styles);
    }

    [Fact]
    public void CrLf_IsNormalized()
    {
        var (text, styles) = SignalText.FromMarkdown("**a**\r\nb\r\n");

        Assert.Equal("a\nb\n", text);
        Assert.Equal(["0:1:BOLD"], styles);
    }

    [Fact]
    public void Plain_PassesTheTextThroughUntouched()
    {
        var (text, styles) = SignalText.Plain("**not bold**");

        Assert.Equal("**not bold**", text);
        Assert.Empty(styles);
    }
}
