namespace Agent42.Tests;

/// <summary>
/// Guards the redesigned Preferences dialog (ADR 0065): the rail/pane/footer shell, the ≤760px drill-down,
/// the two-places-in-one-stylesheet CSS ordering, and the switch treatment on tool rows.
///
/// <para>Source-text heuristics, the same shape as <see cref="ConfigHealthAssetsTests"/> — this repo has no
/// JS runner by design, so these prove the code <i>says</i> the right thing, not that a browser <i>does</i>
/// it. The visual half was verified manually against DevHost and recorded in ADR 0065.</para>
///
/// <para>The pre-existing markup contract (every tab/panel id, both ARIA roles, the
/// <c>panelTools</c> &lt; <c>profileToolChecks</c> &lt; <c>panelProviders</c> order, MCP at index 3) is
/// deliberately NOT re-asserted here: <see cref="PwaAssetsTests"/>, <see cref="ConfigHealthAssetsTests"/>
/// and <c>UserSkillPickerAssetsTests</c> already pin it, and they passed this redesign unedited.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class PreferencesLayoutAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public PreferencesLayoutAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // AC1 — the shell ships. Six new elements, all of which the JS below looks up by id: a miss here is a
    // null ref at module load, which takes the whole app down (dom.js runs at import time).
    [Fact]
    public async Task Preferences_ShipsTheRailShell()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"profileNav\"", index);
        Assert.Contains("id=\"prefsCloseBtn\"", index);
        Assert.Contains("id=\"prefsBackBtn\"", index);
        Assert.Contains("id=\"prefsDetailTitle\"", index);
        Assert.Contains("id=\"prefsStatus\"", index);
        Assert.Contains("id=\"toolsCount\"", index);

        // The rail is a vertical tablist — the same role="tab" buttons, re-oriented.
        Assert.Contains("aria-orientation=\"vertical\"", index);
        // The label lives in its own span so the drill-down title can read it without the count badge.
        Assert.Contains("prefs__navlabel", index);
        // .prefs is ADDED to .agent-dialog, never replacing it: the frame, radius and ::backdrop come from
        // the shared class and only width/height/padding are overridden.
        Assert.Contains("class=\"agent-dialog prefs\"", index);
    }

    // AC4 — the MCP OAuth return opens the mobile DETAIL view, not just the (invisible-on-a-phone) tab.
    // Asserted inside initMcpOAuthReturn's own body, so moving the call elsewhere fails rather than passing
    // on the string existing somewhere in the module graph.
    [Fact]
    public async Task McpOAuthReturn_EntersThePreferencesDetailView()
    {
        var app = await _fixture.GetAppScriptAsync();
        var body = StaticAssetsServerFixture.Body(app, "export async function initMcpOAuthReturn()");

        Assert.Contains("profileTabs.select(3)", body);   // unchanged — MCP is still index 3
        Assert.Contains("enterPrefsDetail(3)", body);
    }

    // AC5 — the drill-down is entered by a RAIL CLICK, never by tab selection.
    //
    // This is the load-bearing one. openProfileDialog() calls profileTabs.select(0) on every open, so if
    // enterPrefsDetail were driven from wireTabs' select, a phone would land in General's detail view on
    // every single open — a section the user never chose. wireTabs is also shared with the agent dialog and
    // must stay free of prefs concepts entirely.
    [Fact]
    public async Task DetailView_IsEnteredByRailClick_NotByProgrammaticSelect()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.DoesNotContain(
            "enterPrefsDetail",
            StaticAssetsServerFixture.Body(app, "export function wireTabs(tabPairs)"));
        Assert.DoesNotContain(
            "enterPrefsDetail",
            StaticAssetsServerFixture.Body(app, "export async function openProfileDialog()"));

        // ...and openProfileDialog LEAVES the detail view instead, so every open lands on the rail.
        Assert.Contains(
            "exitPrefsDetail()",
            StaticAssetsServerFixture.Body(app, "export async function openProfileDialog()"));

        // The rail listener is delegated and resolves the row with closest(): the buttons carry an inline
        // <svg> icon, so e.target is usually a <path> and a bare e.target check would miss most presses.
        var wire = StaticAssetsServerFixture.Body(app, "export function wireProfile()");
        Assert.Contains("profileNav.addEventListener(\"click\"", wire);
        Assert.Contains("e.target.closest('[role=\"tab\"]')", wire);
    }

    // AC4/AC5 support — the rail's DOM order must match the profileTabs array order.
    //
    // A new coupling this redesign introduces, and the only one nothing else covers. `enterPrefsDetail`
    // resolves its tab by DOM position (`#profileNav [role=tab]` in document order), while every other
    // consumer — `profileTabs.select(3)` and the two tests that parse the `wireTabs([…])` block — goes
    // through the dom.js ARRAY order. They agree today and nothing made them agree. Reorder the six
    // buttons in the markup without reordering dom.js and `select(3)` shows panelMcp while
    // `enterPrefsDetail(3)` titles the detail bar with a different section — a silent mismatch.
    [Fact]
    public async Task RailMarkupOrder_MatchesTheProfileTabsArrayOrder()
    {
        var index = await _client.GetStringAsync("/");
        var app = await _fixture.GetAppScriptAsync();

        var nav = index[index.IndexOf("id=\"profileNav\"", StringComparison.Ordinal)..];
        nav = nav[..nav.IndexOf("</nav>", StringComparison.Ordinal)];
        var markupOrder = System.Text.RegularExpressions.Regex.Matches(nav, "id=\"(tab[A-Za-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        var start = app.IndexOf("const profileTabs = wireTabs(", StringComparison.Ordinal);
        Assert.True(start >= 0, "profileTabs wireTabs call not found — renamed?");
        var wire = app[start..app.IndexOf("]);", start, StringComparison.Ordinal)];
        var arrayOrder = System.Text.RegularExpressions.Regex.Matches(wire, """getElementById\("(tab[A-Za-z]+)"\)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(7, markupOrder.Length);
        Assert.Equal(arrayOrder, markupOrder);
        Assert.Equal("tabMcp", markupOrder[3]); // and MCP is still the 4th rail item (ADR 0025/0064)
    }

    // AC6 — the prefs CSS is split across two places in styles.css, constrained from OPPOSITE ends:
    //
    //   · `.prefs` and `.agent-dialog` are both single classes, so specificity TIES and SOURCE ORDER
    //     decides. Authored above it, the dialog silently stays 520px and the whole redesign is inert on
    //     desktop — with every other test in this file still green.
    //   · the prefs media block must NOT be the first `@media (max-width: 760px)` in the file, because
    //     TopbarActionsAssetsTests takes exactly that one (a plain IndexOf over the stylesheet, comments
    //     included) and requires the `.topbar__brand {` base rule to appear before it.
    //
    // Hence: base rules after the dialog rules, media block at the end of the file.
    [Fact]
    public async Task PrefsCss_IsOrderedAgainstBothConstraints()
    {
        var styles = await _client.GetStringAsync("/styles.css");

        var dialogAt = styles.IndexOf(".agent-dialog {", StringComparison.Ordinal);
        var prefsAt = styles.IndexOf(".prefs {", StringComparison.Ordinal);
        Assert.True(dialogAt >= 0, ".agent-dialog rule is missing");
        Assert.True(
            prefsAt > dialogAt,
            ".prefs must be authored AFTER .agent-dialog — they tie on specificity, so source order decides");

        const string Media = "@media (max-width: 760px)";
        var firstMediaAt = styles.IndexOf(Media, StringComparison.Ordinal);
        var isDetailAt = styles.IndexOf(".prefs.is-detail", StringComparison.Ordinal);
        Assert.True(firstMediaAt >= 0, "no 760px media block at all");
        Assert.True(isDetailAt >= 0, "the prefs drill-down rules are missing");

        // The at-rule OPENING THE BLOCK THAT CONTAINS the prefs rules — the nearest one preceding them —
        // never `.prefs.is-detail`'s own index. Comparing the rules' index against firstMediaAt cannot
        // fail for the condition named above: were this block moved to the top of the file, firstMediaAt
        // would point at *this* block's at-rule and a rule inside it is still greater. Only the block's
        // own opener distinguishes "first" from "not first".
        var prefsMediaAt = styles.LastIndexOf(Media, isDetailAt, StringComparison.Ordinal);
        Assert.True(prefsMediaAt >= 0, ".prefs.is-detail must live inside a 760px media block");
        Assert.True(
            prefsMediaAt > firstMediaAt,
            "the prefs media block must not be the FIRST 760px block — TopbarActionsAssetsTests reads that one");

        // ...and the same property from the other side, so this test fails on its own rather than only
        // taking TopbarActionsAssetsTests down with it.
        var brandAt = styles.IndexOf(".topbar__brand {", StringComparison.Ordinal);
        Assert.True(brandAt >= 0 && brandAt < firstMediaAt, "the base .topbar__brand rule must precede the first 760px block");

        // The full-bleed rule needs BOTH declarations, and they must be in the MOBILE block: <dialog>
        // carries a UA default `max-height: calc(100% - 6px - 2em)` that would clip 100dvh into a centred
        // box with gaps top and bottom.
        //
        // Scoped to that block and with comments stripped, deliberately. Scanning from `.prefs {` onward
        // would pass on the BASE rule's own `max-height: none`, and `100dvh` also appears in the prose
        // explaining all this — so the loose version stayed green with either mobile declaration deleted,
        // which is the very trap the new gotcha records (IndexOf does not skip comments).
        var prefsMediaBlock = StripComments(styles[prefsMediaAt..]);
        Assert.Contains("height: 100dvh;", prefsMediaBlock);
        Assert.Contains("max-height: none;", prefsMediaBlock);
    }

    private static string StripComments(string css) =>
        System.Text.RegularExpressions.Regex.Replace(css, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);

    // AC7 — tool rows render as switches. The ONE `.tool-check input` rule (not a second rule beside the
    // old one) sets `appearance: none`, which is what makes the ::after knob possible at all.
    [Fact]
    public async Task ToolRows_RenderAsSwitches()
    {
        var styles = await _client.GetStringAsync("/styles.css");

        var ruleAt = styles.IndexOf(".tool-check input {", StringComparison.Ordinal);
        Assert.True(ruleAt >= 0, "the .tool-check input rule is missing — renamed?");
        var rule = styles[ruleAt..styles.IndexOf('}', ruleAt)];

        Assert.Contains("appearance: none", rule);
        Assert.Contains("-webkit-appearance: none", rule);
        // The old checkbox treatment is GONE, not merely overridden by a later rule.
        Assert.DoesNotContain("accent-color", rule);
        Assert.Contains(".tool-check input:checked::after", styles); // the knob
    }

    // AC11 — the status line is wired to the four Save-persisted fields ONLY.
    //
    // Preferences mixes two persistence models: language / theme / suggest / the tool checks are PUT by
    // Save, while the three model selects and every provider, MCP and skill control persist themselves the
    // moment they change. A form-level input/change listener would therefore announce "Unsaved changes" for
    // a default model that is already stored.
    [Fact]
    public async Task SavedStatus_TracksOnlyTheSavePersistedFields()
    {
        var app = await _fixture.GetAppScriptAsync();
        var wire = StaticAssetsServerFixture.Body(app, "export function wireProfile()");

        // The existing "submit" listener on the form is fine and must not trip this — only input/change.
        Assert.DoesNotContain("profileForm.addEventListener(\"input\"", app);
        Assert.DoesNotContain("profileForm.addEventListener(\"change\"", app);

        Assert.Contains("profileLanguage.addEventListener(\"change\", markPrefsDirty)", wire);
        Assert.Contains("profileSuggest.addEventListener(\"change\", markPrefsDirty)", wire);
        Assert.Contains("profileToolChecks.addEventListener(\"change\"", wire);
        // The theme handler specifically — it already existed (it live-previews the theme) and had to GAIN
        // the dirty mark. A bare Contains("markPrefsDirty()") over `wire` is satisfied by the tool-checks
        // handler a few lines above, so it would not pin this at all.
        Assert.Contains(
            "applyTheme(profileTheme.value);",
            StaticAssetsServerFixture.Body(app, "profileTheme.addEventListener(\"change\""));
        Assert.Contains(
            "markPrefsDirty();",
            StaticAssetsServerFixture.Body(app, "profileTheme.addEventListener(\"change\""));

        // Reset on open, and NO post-save write: saveProfile() closes the dialog, so "All changes saved"
        // is only ever observable as the opening state.
        Assert.Contains(
            "resetPrefsStatus()",
            StaticAssetsServerFixture.Body(app, "export async function openProfileDialog()"));
        Assert.DoesNotContain(
            "prefsStatus",
            StaticAssetsServerFixture.Body(app, "async function saveProfile()"));
    }

    // AC12 — the agent editor's tab strip is NOT restyled.
    //
    // #agentDialog shares .agent-form__tablist / .agent-form__tab, and nothing in tests/ asserts them, so an
    // unscoped rail rule would mangle the agent editor with the whole suite still green. This is the highest
    // risk of silent regression in the redesign, which is why it is asserted structurally: EVERY selector in
    // the stylesheet that mentions the nav family and sits after the `.prefs {` rule must be .prefs-scoped.
    [Fact]
    public async Task AgentDialogTabs_AreNotRestyled()
    {
        var styles = await _client.GetStringAsync("/styles.css");
        var prefsAt = styles.IndexOf(".prefs {", StringComparison.Ordinal);
        Assert.True(prefsAt >= 0, ".prefs rule is missing");

        // All FIVE members the prefs redesign restyles, not just the two on the rail: an unscoped
        // `.agent-form__title` or `.agent-form__actions` rule would reach #agentDialog exactly as an
        // unscoped `.agent-form__tab` one would. (`__tablist` is caught by the `__tab` substring; it is
        // listed anyway so the intent survives a rename.)
        string[] navFamily =
            ["agent-form__tablist", "agent-form__tab", "agent-form__panel", "agent-form__title", "agent-form__actions"];

        var offending = SelectorLists(styles[prefsAt..])
            .Where(s => !s.StartsWith('@'))                                    // at-rule preludes are not selectors
            .Where(s => navFamily.Any(n => s.Contains(n, StringComparison.Ordinal)))
            .Where(s => !s.Contains(".prefs", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "every nav rule below .prefs must be .prefs-scoped or it restyles #agentDialog too: "
                + string.Join(" | ", offending));

        // And the rail really is scoped, rather than there being no rail rules at all.
        Assert.Contains(".prefs .agent-form__tablist {", styles);
        Assert.Contains(".prefs .agent-form__tab {", styles);
    }

    /// <summary>
    /// Every selector list in <paramref name="css"/> — the text between one rule's <c>}</c> (or an at-rule's
    /// <c>{</c>) and the next <c>{</c>, comments stripped.
    /// <para>
    /// Brace-delimited rather than line-delimited on purpose. A "lines ending in <c>{</c>" filter misses a
    /// SINGLE-LINE rule, and single-line rules are a live idiom in this stylesheet and inside the prefs block
    /// itself (<c>.prefs .agent-form__tab { padding: 14px 12px; }</c>) — so that filter would wave through
    /// exactly the unscoped one-liner this guard exists to catch.
    /// </para>
    /// </summary>
    private static IEnumerable<string> SelectorLists(string css)
    {
        var stripped = StripComments(css);
        var start = 0;
        for (var i = 0; i < stripped.Length; i++)
        {
            if (stripped[i] == '{')
            {
                yield return stripped[start..i].Trim();
                start = i + 1;
            }
            else if (stripped[i] == '}')
            {
                start = i + 1;
            }
        }
    }
}
