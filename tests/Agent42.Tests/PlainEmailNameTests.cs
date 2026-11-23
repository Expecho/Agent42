using Agent42.Agents;

namespace Agent42.Tests;

public class PlainEmailNameTests
{
    [Theory]
    [InlineData("Calendar Agent", "calendar-agent")]
    [InlineData("  Calendar   Agent  ", "calendar-agent")]
    [InlineData("Café Ünïcode", "cafe-unicode")]
    [InlineData("UPPER_case!!name", "upper-case-name")]
    [InlineData("--a--", "a")]
    [InlineData("R2-D2", "r2-d2")]
    public void Slugify_NormalisesName(string name, string expected)
        => Assert.Equal(expected, PlainEmailName.Slugify(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("日本語")]
    [InlineData("!!! ???")]
    public void Slugify_NothingLeft_ReturnsNull(string name)
        => Assert.Null(PlainEmailName.Slugify(name));

    [Fact]
    public void Slugify_Null_ReturnsNull() => Assert.Null(PlainEmailName.Slugify(null));

    [Fact]
    public void Slugify_CutsAtMaxLength_WithoutTrailingDash()
    {
        var name = new string('a', PlainEmailName.MaxLength - 1) + " b";
        var slug = PlainEmailName.Slugify(name);

        Assert.Equal(new string('a', PlainEmailName.MaxLength - 1), slug);
        Assert.False(slug!.EndsWith('-'));

        var long50 = PlainEmailName.Slugify(new string('x', 50));
        Assert.Equal(PlainEmailName.MaxLength, long50!.Length);
    }

    [Theory]
    [InlineData("postmaster")]
    [InlineData("admin")]
    [InlineData("agent42")]
    [InlineData("no-reply")]
    public void IsReserved_RoleNames(string slug) => Assert.True(PlainEmailName.IsReserved(slug));

    [Fact]
    public void IsReserved_RandomTokenShape_IsRefused()
    {
        var (token, _) = EmailAddress.Generate();
        Assert.True(PlainEmailName.IsReserved(token));
    }

    [Theory]
    [InlineData("calendar-agent")]
    [InlineData("agent-smith")]
    [InlineData("agent-0123456789012345678901234567890a")] // right length, but 0/1/8/9 are not base32
    public void IsReserved_OrdinaryNames_AreAllowed(string slug) => Assert.False(PlainEmailName.IsReserved(slug));
}
