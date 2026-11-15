using Agent42.Agents;

namespace Agent42.Backend;

public static partial class ArtifactTheme
{
    // One token string per (palette, mode): the C# mirror of wwwroot/shared/themes.css (an artifact frame has an
    // opaque origin and cannot load it). ThemePaletteTests parses the CSS and fails when this table drifts from it.
    private static readonly Dictionary<(string Palette, string Mode), string> PaletteTokens = new()
    {
        [("amber", "dark")] =
            "color-scheme:dark;--bg:#100f0d;--bg-raised:#171512;--bg-inset:#0b0a09;--line:#2a2620;"
            + "--line-soft:#201d18;--ink:#e9e2d3;--ink-dim:#a89f8c;--ink-faint:#6d6555;--amber:#ffb13d;"
            + "--amber-deep:#c97f1b;--amber-glow:rgba(255,177,61,.14);--green:#9ac06d;--red:#e06c5f;",
        [("amber", "light")] =
            "color-scheme:light;--bg:#f4efe4;--bg-raised:#fbf7ee;--bg-inset:#e9e2d2;--line:#d6cdb8;"
            + "--line-soft:#e2dac7;--ink:#2a2620;--ink-dim:#5c5647;--ink-faint:#8a8270;--amber:#a86611;"
            + "--amber-deep:#7f4e0a;--amber-glow:rgba(168,102,17,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("ocean", "dark")] =
            "color-scheme:dark;--bg:#0b0f13;--bg-raised:#10161b;--bg-inset:#080a0d;--line:#1f2933;"
            + "--line-soft:#171f26;--ink:#dee6ed;--ink-dim:#798ca0;--ink-faint:#556372;--amber:#3d99f5;"
            + "--amber-deep:#156bc1;--amber-glow:rgba(61,153,245,.14);--green:#9ac06d;--red:#e06c5f;",
        [("ocean", "light")] =
            "color-scheme:light;--bg:#eef2f6;--bg-raised:#f8fafc;--bg-inset:#dee6ed;--line:#c2cfdb;"
            + "--line-soft:#d6dee6;--ink:#17212c;--ink-dim:#445769;--ink-faint:#708599;--amber:#1466b8;"
            + "--amber-deep:#083868;--amber-glow:rgba(20,102,184,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("indigo", "dark")] =
            "color-scheme:dark;--bg:#0c0c13;--bg-raised:#11121a;--bg-inset:#08080c;--line:#202132;"
            + "--line-soft:#181925;--ink:#dedfed;--ink-dim:#7c7f9c;--ink-faint:#5e6078;--amber:#6874f3;"
            + "--amber-deep:#1b28bb;--amber-glow:rgba(104,116,243,.14);--green:#9ac06d;--red:#e06c5f;",
        [("indigo", "light")] =
            "color-scheme:light;--bg:#eeeff6;--bg-raised:#f8f9fb;--bg-inset:#dee0ed;--line:#c4c6d9;"
            + "--line-soft:#d7d8e5;--ink:#17182c;--ink-dim:#474a66;--ink-faint:#737696;--amber:#1926b3;"
            + "--amber-deep:#0b1365;--amber-glow:rgba(25,38,179,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("violet", "dark")] =
            "color-scheme:dark;--bg:#0f0c12;--bg-raised:#15111a;--bg-inset:#0a080c;--line:#282131;"
            + "--line-soft:#1e1825;--ink:#e5deed;--ink-dim:#8b7e9a;--ink-faint:#685e73;--amber:#a35ff2;"
            + "--amber-deep:#661bbb;--amber-glow:rgba(163,95,242,.14);--green:#9ac06d;--red:#e06c5f;",
        [("violet", "light")] =
            "color-scheme:light;--bg:#f2eff6;--bg-raised:#faf8fb;--bg-inset:#e5dfec;--line:#cec5d8;"
            + "--line-soft:#ddd7e4;--ink:#20172b;--ink-dim:#564965;--ink-faint:#847693;--amber:#6119b3;"
            + "--amber-deep:#350b65;--amber-glow:rgba(97,25,179,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("rose", "dark")] =
            "color-scheme:dark;--bg:#120c0f;--bg-raised:#1a1115;--bg-inset:#0c080a;--line:#312127;"
            + "--line-soft:#25181e;--ink:#eddee4;--ink-dim:#9a7e8a;--ink-faint:#705c64;--amber:#f0428b;"
            + "--amber-deep:#bb1b5e;--amber-glow:rgba(240,66,139,.14);--green:#9ac06d;--red:#e06c5f;",
        [("rose", "light")] =
            "color-scheme:light;--bg:#f6eff2;--bg-raised:#fbf8fa;--bg-inset:#ecdfe4;--line:#d8c5cd;"
            + "--line-soft:#e4d7dd;--ink:#2b171f;--ink-dim:#654954;--ink-faint:#937682;--amber:#b31959;"
            + "--amber-deep:#650b31;--amber-glow:rgba(179,25,89,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("crimson", "dark")] =
            "color-scheme:dark;--bg:#130c0d;--bg-raised:#1a1112;--bg-inset:#0c0809;--line:#322022;"
            + "--line-soft:#25181a;--ink:#eddee0;--ink-dim:#9c7c80;--ink-faint:#755c5f;--amber:#eb475d;"
            + "--amber-deep:#b62034;--amber-glow:rgba(235,71,93,.14);--green:#9ac06d;--red:#e06c5f;",
        [("crimson", "light")] =
            "color-scheme:light;--bg:#f6eeef;--bg-raised:#fbf8f9;--bg-inset:#eddee0;--line:#d9c4c7;"
            + "--line-soft:#e5d7d9;--ink:#2c1719;--ink-dim:#66474b;--ink-faint:#967378;--amber:#ad1f32;"
            + "--amber-deep:#620e19;--amber-glow:rgba(173,31,50,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("coral", "dark")] =
            "color-scheme:dark;--bg:#130e0c;--bg-raised:#1a1311;--bg-inset:#0c0908;--line:#322420;"
            + "--line-soft:#251b18;--ink:#ede1de;--ink-dim:#9c847c;--ink-faint:#725f5a;--amber:#f7673b;"
            + "--amber-deep:#c33c13;--amber-glow:rgba(247,103,59,.14);--green:#9ac06d;--red:#e06c5f;",
        [("coral", "light")] =
            "color-scheme:light;--bg:#f6f0ee;--bg-raised:#fbf9f8;--bg-inset:#ede2de;--line:#d9c9c4;"
            + "--line-soft:#e5dad7;--ink:#2c1b17;--ink-dim:#664e47;--ink-faint:#967b73;--amber:#ba3912;"
            + "--amber-deep:#691e07;--amber-glow:rgba(186,57,18,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("forest", "dark")] =
            "color-scheme:dark;--bg:#0d120e;--bg-raised:#121915;--bg-inset:#090c0a;--line:#222f27;"
            + "--line-soft:#1a231d;--ink:#dfece3;--ink-dim:#839589;--ink-faint:#5b675f;--amber:#66cc88;"
            + "--amber-deep:#40965d;--amber-glow:rgba(102,204,136,.14);--green:#9ac06d;--red:#e06c5f;",
        [("forest", "light")] =
            "color-scheme:light;--bg:#eff5f1;--bg-raised:#f9fbf9;--bg-inset:#e0ebe4;--line:#c7d6cc;"
            + "--line-soft:#d9e3dc;--ink:#192a1e;--ink-dim:#4c6153;--ink-faint:#7b8e81;--amber:#34794b;"
            + "--amber-deep:#1f5130;--amber-glow:rgba(52,121,75,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("teal", "dark")] =
            "color-scheme:dark;--bg:#0c1312;--bg-raised:#111a1a;--bg-inset:#080c0c;--line:#203231;"
            + "--line-soft:#182524;--ink:#deedec;--ink-dim:#7c9c9a;--ink-faint:#516765;--amber:#4ce6db;"
            + "--amber-deep:#25b1a7;--amber-glow:rgba(76,230,219,.14);--green:#9ac06d;--red:#e06c5f;",
        [("teal", "light")] =
            "color-scheme:light;--bg:#eef6f6;--bg-raised:#f8fbfb;--bg-inset:#deedec;--line:#c4d9d8;"
            + "--line-soft:#d7e5e4;--ink:#172c2a;--ink-dim:#476664;--ink-faint:#6e918f;--amber:#197670;"
            + "--amber-deep:#115f5a;--amber-glow:rgba(25,118,112,.12);--green:#5a7d3a;--red:#bf3c30;",
        [("graphite", "dark")] =
            "color-scheme:dark;--bg:#0f0f0f;--bg-raised:#161616;--bg-inset:#0a0a0a;--line:#292929;"
            + "--line-soft:#1f1f1f;--ink:#e6e6e6;--ink-dim:#8c8c8c;--ink-faint:#636363;--amber:#f2f2f2;"
            + "--amber-deep:#bdbdbd;--amber-glow:rgba(242,242,242,.14);--green:#9ac06d;--red:#e06c5f;",
        [("graphite", "light")] =
            "color-scheme:light;--bg:#f2f2f2;--bg-raised:#fafafa;--bg-inset:#e6e6e6;--line:#cfcfcf;"
            + "--line-soft:#dedede;--ink:#212121;--ink-dim:#575757;--ink-faint:#858585;--amber:#1c1c1c;"
            + "--amber-deep:#000000;--amber-glow:rgba(28,28,28,.12);--green:#5a7d3a;--red:#bf3c30;",
    };

    /// <summary>The token string for a palette and mode; unknown ids fall back to amber, any mode but light to dark.</summary>
    public static string TokensFor(string? palette, string? theme)
        => PaletteTokens[(ThemePalettes.Normalize(palette), theme == "light" ? "light" : "dark")];

    /// <summary>The token sets, for the parity test.</summary>
    public static IReadOnlyDictionary<(string Palette, string Mode), string> AllPaletteTokens => PaletteTokens;
}
