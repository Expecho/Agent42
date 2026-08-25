namespace Agent42.Tests;

/// <summary>
/// Guards the uploaded-skills UI wiring (ADR 0049). Serves the real Backend <c>wwwroot</c> through the
/// shared <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same
/// source-text-heuristic shape as <see cref="ConfigHealthAssetsTests"/> (the repo has no JS runner by
/// design, so these prove the code <i>says</i> the right thing, not that a browser <i>does</i> it).
/// </summary>
[Collection("StaticAssets")]
public sealed class UserSkillPickerAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public UserSkillPickerAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // AC22: the Skills tab, its panel, and the three controls the module reaches for exist in the markup.
    [Fact]
    public async Task SkillsTab_IsPresentInMarkup()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"tabSkills\"", index);
        Assert.Contains("id=\"panelSkills\"", index);
        Assert.Contains("id=\"skillList\"", index);
        Assert.Contains("id=\"addSkillBtn\"", index);
        Assert.Contains("id=\"skillFile\"", index);
        // Only the two formats the normalizer accepts; anything else is a 400 the user could have avoided.
        Assert.Contains("accept=\".md,.zip\"", index);
    }

    // AC22: the tab is APPENDED — after Configuration, which is itself after MCP. The MCP-OAuth return
    // deep-links to profileTabs.select(3), so MCP must stay index 3 and Configuration 4; a Skills tab
    // inserted anywhere earlier would silently break that return.
    [Fact]
    public async Task SkillsTab_IsAppendedAfterConfiguration_LeavingMcpAtIndexThree()
    {
        var app = await _fixture.GetAppScriptAsync();

        var start = app.IndexOf("const profileTabs = wireTabs(", StringComparison.Ordinal);
        Assert.True(start >= 0, "profileTabs wireTabs call not found — renamed?");
        var end = app.IndexOf("]);", start, StringComparison.Ordinal);
        Assert.True(end > start, "profileTabs array not terminated");
        var wire = app[start..end];

        var mcpAt = wire.IndexOf("tabMcp", StringComparison.Ordinal);
        var configAt = wire.IndexOf("tabConfig", StringComparison.Ordinal);
        var skillsAt = wire.IndexOf("tabSkills", StringComparison.Ordinal);

        Assert.True(mcpAt >= 0, "tabMcp missing from profileTabs");
        Assert.True(configAt > mcpAt, "tabConfig must stay after tabMcp");
        Assert.True(skillsAt > configAt, "tabSkills must be appended AFTER tabConfig so MCP stays index 3");

        Assert.Contains("profileTabs.select(3)", app);
    }

    // AC22: the module is reachable from main.js (the fixture walks the import graph over HTTP, so a
    // module no one imports is invisible to every source-text guard) and its CRUD calls are wired.
    [Fact]
    public async Task Skills_UploadListAndDeleteAreWired()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("\"/api/skills\"", app);
        Assert.Contains("`/api/skills/${encodeURIComponent(skill.id)}`", app);
        Assert.Contains("method: \"POST\", body", app);
        // Opening Preferences must refresh the list, exactly as it does for providers and MCP servers.
        Assert.Contains("loadSkills();", app);
    }

    // The picker's key set changes when a skill is added or removed, so the catalog must be re-fetched —
    // otherwise the new skill:{name} checkbox only appears after a full page reload, and a deleted skill's
    // stale checkbox stays tickable. Same reason ADR 0026's MCP CRUD calls it.
    [Fact]
    public async Task Skills_RefreshTheToolCatalogAfterUploadAndDelete()
    {
        var app = await _fixture.GetAppScriptAsync();
        var module = await _client.GetStringAsync("/js/skills.js");

        Assert.Contains("refreshToolCatalog", app);
        // Twice in skills.js specifically: once after upload, once after delete. One call would leave the
        // other path stale, and the whole-bundle assertion above cannot tell the difference.
        Assert.Equal(2, CountOccurrences(module, "refreshToolCatalog()"));
    }

    // The upload response's skipped/warning report is surfaced, not swallowed. "Silently drop the bad zip
    // entries and keep the rest" is only an acceptable behaviour BECAUSE the drop is reported — otherwise a
    // user whose scripts/ folder vanished has no way to learn that it was deliberate.
    [Fact]
    public async Task Skills_SurfaceSkippedEntriesAndShadowingWarnings()
    {
        var module = await _client.GetStringAsync("/js/skills.js");

        Assert.Contains("data.skipped", module);
        Assert.Contains("data.warnings", module);
    }

    // The file input is cleared BEFORE the upload is awaited. Without it, re-picking the same file fires
    // no `change` event, so a failed upload cannot be retried without choosing a different file first.
    [Fact]
    public async Task Skills_ResetTheFileInputSoTheSameFileCanBeRetried()
    {
        var module = await _client.GetStringAsync("/js/skills.js");

        var reset = module.IndexOf("skillFileInput.value = \"\";", StringComparison.Ordinal);
        var upload = module.IndexOf("if (file) uploadSkill(file);", StringComparison.Ordinal);

        Assert.True(reset >= 0, "the file input is never reset");
        Assert.True(upload > reset, "the input must be reset BEFORE the upload is started");
    }

    // The status line doubles as the error surface, so the modifier it toggles must actually be styled —
    // a class with no rule leaves a failed upload reading as a faint informational note.
    [Fact]
    public async Task Skills_ErrorStatusHasAStyle()
    {
        var module = await _client.GetStringAsync("/js/skills.js");
        var styles = await _client.GetStringAsync("/styles.css");

        Assert.Contains("agent-form__note--error", module);
        Assert.Contains(".agent-form__note--error", styles);
    }

    // The panel note has to carry the two things a user cannot infer: the ADR 0049 activation staleness
    // (a warm chat keeps its old skills), and that uploaded instruction text is executed by the model.
    [Fact]
    public async Task SkillsPanel_WarnsAboutStalenessAndTrust()
    {
        var index = await _client.GetStringAsync("/");
        var panel = Panel(index, "panelSkills");

        Assert.Contains("next new chat", panel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only skills you trust", panel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaces it", panel, StringComparison.OrdinalIgnoreCase);
    }

    private static string Panel(string index, string id)
    {
        var start = index.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"panel {id} not found");
        // Terminate on the panel's closing </section>, not the first </div> — the panel opens with a
        // nested .providers__head div, so a </div> scan stops before the note this asserts on.
        var end = index.IndexOf("</section>", start, StringComparison.Ordinal);
        return index[start..(end < 0 ? index.Length : end)];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
