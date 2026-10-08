using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Where <see cref="ArtifactTheme"/> puts the app's look in a framed artifact: first inside its head, never before a
/// doctype (that drops the page into quirks mode), and at the start of a bare fragment.
/// </summary>
public sealed class ArtifactThemeTests
{
    private static readonly string Block = ArtifactTheme.Block("dark");

    [Theory]
    [InlineData("<html><HEAD lang=\"x\"><title>t</title></HEAD></html>", "<html><HEAD lang=\"x\">")]
    [InlineData("<!doctype html><html lang=\"en\"><body>x</body></html>", "<!doctype html><html lang=\"en\">")]
    [InlineData("  <!DOCTYPE html>\n<p>x</p>", "  <!DOCTYPE html>")]
    [InlineData("<p>x</p>", "")]
    public void Apply_InsertsAfterTheFirstAnchor(string html, string before)
        => Assert.Equal(before + Block + html[before.Length..], ArtifactTheme.Apply(html, "dark"));

    // <header> is not <head>.
    [Fact]
    public void Apply_IgnoresHeaderTag()
        => Assert.Equal(Block + "<header>x</header>", ArtifactTheme.Apply("<header>x</header>", null));

    [Theory]
    [InlineData("light", "--bg:#f4efe4", "color-scheme:light")]
    [InlineData("dark", "--bg:#100f0d", "color-scheme:dark")]
    [InlineData(null, "--bg:#100f0d", "color-scheme:dark")]
    [InlineData("<script>", "--bg:#100f0d", "color-scheme:dark")]
    public void Block_PicksTheTheme_DarkByDefault(string? theme, string bg, string scheme)
    {
        var block = ArtifactTheme.Block(theme);
        Assert.Contains(bg, block, StringComparison.Ordinal);
        Assert.Contains(scheme, block, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", block, StringComparison.Ordinal);
    }

    // The element defaults must lose to any CSS the artifact writes, so every one of them is zero-specificity.
    [Fact]
    public void Block_ElementRules_HaveZeroSpecificity()
    {
        var css = ArtifactTheme.Block("dark").Split("<style data-agent42-theme>")[1];
        var selectors = css.Split('}').Where(r => r.Contains('{')).Select(r => r.Split('{')[0]).ToList();
        Assert.Equal(":root", selectors[0]);
        Assert.All(selectors.Skip(1), s => Assert.Matches(@"^(:where\(|::selection$)", s));
    }
}
