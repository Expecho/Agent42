using System.Globalization;
using System.Text.RegularExpressions;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The drift guard for ADR 0129: <c>wwwroot/shared/themes.css</c> is the one palette source, so these tests parse
/// it and prove (a) ten palettes x dark/light exist, (b) every contract token is there, (c) the WCAG floors hold,
/// (d) amber is byte-identical to the pre-change values and (e) <see cref="ArtifactTheme"/>'s C# table equals it.
/// </summary>
[Collection("StaticAssets")]
public sealed partial class ThemePaletteTests
{
    private static readonly string[] Contract =
    [
        "--bg", "--bg-raised", "--bg-inset", "--line", "--line-soft", "--ink", "--ink-dim", "--ink-faint", "--amber",
        "--amber-deep", "--amber-glow", "--green", "--red", "--backdrop", "--shadow", "--chip-btn-bg", "--warn",
        "--agent-s", "--agent-l", "color-scheme",
    ];

    private readonly StaticAssetsServerFixture _fixture;

    public ThemePaletteTests(StaticAssetsServerFixture fixture) => _fixture = fixture;

    private async Task<Dictionary<(string Palette, string Mode), Dictionary<string, string>>> ParseAsync()
    {
        var css = await _fixture.Client.GetStringAsync("/shared/themes.css");
        var sets = new Dictionary<(string, string), Dictionary<string, string>>();
        foreach (Match m in SetRule().Matches(css))
        {
            var tokens = new Dictionary<string, string>();
            foreach (Match d in Declaration().Matches(m.Groups["body"].Value))
            {
                tokens[d.Groups["name"].Value] = d.Groups["value"].Value.Trim();
            }

            sets[(m.Groups["palette"].Value, m.Groups["mode"].Value)] = tokens;
        }

        return sets;
    }

    [Fact]
    public async Task ThemesCss_HasTenPalettes_EachWithDarkAndLight()
    {
        var sets = await ParseAsync();

        Assert.Equal(10, ThemePalettes.Ids.Count);
        Assert.Equal(ThemePalettes.Ids.OrderBy(x => x), sets.Keys.Select(k => k.Palette).Distinct().OrderBy(x => x));
        foreach (var id in ThemePalettes.Ids)
        {
            Assert.True(sets.ContainsKey((id, "dark")), $"{id}/dark missing");
            Assert.True(sets.ContainsKey((id, "light")), $"{id}/light missing");
        }
    }

    [Fact]
    public async Task EverySet_CarriesEveryContractToken()
    {
        foreach (var (key, tokens) in await ParseAsync())
        {
            foreach (var name in Contract)
            {
                Assert.True(tokens.ContainsKey(name), $"{key} lacks {name}");
            }

            Assert.Equal(key.Mode, tokens["color-scheme"]);
        }
    }

    // The floors the plan names. Amber's light accent is the one documented exception: it is pinned to the
    // pre-change #a86611 (4.00:1 on --bg) so existing users see no change; every new palette meets 4.5:1.
    [Fact]
    public async Task EverySet_MeetsTheContrastFloors()
    {
        foreach (var (key, t) in await ParseAsync())
        {
            var amberLight = key == ("amber", "light");
            var label = $"{key.Palette}/{key.Mode}";
            Floor(label, "ink/bg", t["--ink"], t["--bg"], 4.5);
            Floor(label, "ink/raised", t["--ink"], t["--bg-raised"], 4.5);
            Floor(label, "ink/inset", t["--ink"], t["--bg-inset"], 4.5);
            Floor(label, "accent/bg", t["--amber"], t["--bg"], amberLight ? 3.9 : 4.5);
            Floor(label, "accent/raised", t["--amber"], t["--bg-raised"], amberLight ? 4.2 : 4.5);
            Floor(label, "dim/bg", t["--ink-dim"], t["--bg"], 4.5);
            Floor(label, "faint/bg", t["--ink-faint"], t["--bg"], 3.0);
            Floor(label, "red/bg", t["--red"], t["--bg"], 3.0);
            Floor(label, "green/bg", t["--green"], t["--bg"], 3.0);
        }
    }

    [Fact]
    public async Task Amber_IsByteIdenticalToThePreChangeValues()
    {
        var sets = await ParseAsync();
        var dark = sets[("amber", "dark")];
        var light = sets[("amber", "light")];

        Assert.Equal("#100f0d", dark["--bg"]);
        Assert.Equal("#171512", dark["--bg-raised"]);
        Assert.Equal("#0b0a09", dark["--bg-inset"]);
        Assert.Equal("#ffb13d", dark["--amber"]);
        Assert.Equal("#c97f1b", dark["--amber-deep"]);
        Assert.Equal("rgba(255, 177, 61, 0.14)", dark["--amber-glow"]);
        Assert.Equal("#9ac06d", dark["--green"]);
        Assert.Equal("#e06c5f", dark["--red"]);
        Assert.Equal("#f4efe4", light["--bg"]);
        Assert.Equal("#fbf7ee", light["--bg-raised"]);
        Assert.Equal("#e9e2d2", light["--bg-inset"]);
        Assert.Equal("#a86611", light["--amber"]);
        Assert.Equal("#7f4e0a", light["--amber-deep"]);
        Assert.Equal("rgba(168, 102, 17, 0.12)", light["--amber-glow"]);
        Assert.Equal("#5a7d3a", light["--green"]);
        Assert.Equal("#bf3c30", light["--red"]);
    }

    // Pages that set only data-theme (the public /shared pages) or nothing must stay readable and amber.
    [Fact]
    public async Task ThemesCss_HasAmberFallbacks_ForPagesWithoutADataPalette()
    {
        var css = await _fixture.Client.GetStringAsync("/shared/themes.css");

        Assert.Matches(@"(?m)^:root \{", css);
        Assert.Matches("(?m)^:root\\[data-theme=\"light\"\\] \\{", css);
    }

    [Fact]
    public async Task ArtifactTheme_TableEqualsTheCss()
    {
        var sets = await ParseAsync();
        Assert.Equal(20, ArtifactTheme.AllPaletteTokens.Count);

        foreach (var ((palette, mode), css) in sets)
        {
            var cs = ArtifactTheme.AllPaletteTokens[(palette, mode)];
            var parsed = cs.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(':', 2)).ToDictionary(p => p[0], p => p[1]);

            Assert.Equal(css["color-scheme"], parsed["color-scheme"]);
            foreach (var (name, value) in parsed.Where(p => p.Key != "color-scheme"))
            {
                Assert.Equal(Canonical(css[name]), Canonical(value));
            }
        }
    }

    [Fact]
    public void ArtifactTheme_Block_SelectsThePalette_AndFallsBackToAmber()
    {
        Assert.Contains("--amber:#3d99f5", ArtifactTheme.Block("dark", "ocean"), StringComparison.Ordinal);
        Assert.Contains("--amber:#ffb13d", ArtifactTheme.Block("dark", "nonsense"), StringComparison.Ordinal);
        Assert.Contains("--amber:#ffb13d", ArtifactTheme.Block("dark", null), StringComparison.Ordinal);
        Assert.Contains("color-scheme:light", ArtifactTheme.Block("light", "ocean"), StringComparison.Ordinal);
    }

    private static string Canonical(string v) => v.Replace(" ", "").Replace("0.", ".");

    private static void Floor(string label, string pair, string fg, string bg, double min)
    {
        var ratio = Contrast(fg, bg);
        Assert.True(ratio >= min, $"{label} {pair} = {ratio:0.00}:1, floor {min}:1");
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (hi < lo)
        {
            (hi, lo) = (lo, hi);
        }

        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var c = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    [GeneratedRegex("""
        :root\[data-palette="(?<palette>[a-z]+)"\]\[data-theme="(?<mode>dark|light)"\]\s*\{(?<body>[^}]*)\}
        """)]
    private static partial Regex SetRule();

    [GeneratedRegex(@"(?<name>[a-z-]+)\s*:\s*(?<value>[^;]+);")]
    private static partial Regex Declaration();
}
