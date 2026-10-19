using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// AC10 (ADR 0106): the tool-selection resolution of ADR 0021/0026/0048/0049 without a silo. The grain-level
/// <c>NoToolModel_*</c>, <c>McpTools_*</c>, <c>UserSkills_*</c>, <c>BindAgent_*</c>, <c>LegacyState_*</c> and
/// <c>GetEnabledTools_*</c> cases in <see cref="AgentGrainTests"/> still pin it end to end.
/// </summary>
public sealed class ToolSelectionTests
{
    private static readonly ModelCapabilities Tools = new(SupportsTools: true, SupportsVision: false);
    private static readonly ModelCapabilities NoTools = new(SupportsTools: false, SupportsVision: false);

    private readonly ToolSelection _selection = new();

    private static ToolSelectionContext Context(
        IReadOnlyList<string>? conversation = null,
        IReadOnlyList<string>? userDefault = null,
        IReadOnlyList<McpServerTools>? servers = null,
        IReadOnlyList<SkillInfo>? skills = null)
        => new(conversation, userDefault, servers ?? [], skills ?? []);

    private static McpServerTools Server(string id, IReadOnlyList<string>? known, params string[] live)
        => new(id, [.. live.Select(name => AIFunctionFactory.Create(() => "ok", name))], known);

    private static HashSet<string> Set(IEnumerable<string> keys) => keys.ToHashSet(StringComparer.Ordinal);

    // null == all: with no override and no default, every selectable key, wildcards included.
    [Fact]
    public void NullEverywhere_MeansAll()
        => Assert.Equal(ToolCatalog.AllSelectableToolKeys, _selection.EffectiveEnabledTools(Context()));

    [Fact]
    public void OverrideBeatsDefault_AndDefaultBeatsAll()
    {
        Assert.Equal([ToolCatalog.WebSearch], _selection.EffectiveEnabledTools(
            Context(conversation: [ToolCatalog.WebSearch], userDefault: [ToolCatalog.Memory])));
        Assert.Equal([ToolCatalog.Memory], _selection.EffectiveEnabledTools(Context(userDefault: [ToolCatalog.Memory])));
    }

    // The `mcp` wildcard expands two levels: to each server, then to each known tool of that server.
    [Fact]
    public void McpWildcard_ExpandsToEveryServersKnownTools()
    {
        var expanded = _selection.ExpandedEnabledTools(Context(
            conversation: [ToolCatalog.Mcp],
            servers: [Server("cal", ["list", "add"]), Server("docs", ["read"])]));

        Assert.Equal(
            [ToolCatalog.McpToolKey("cal", "list"), ToolCatalog.McpToolKey("cal", "add"), ToolCatalog.McpToolKey("docs", "read")],
            expanded);
    }

    // A server whose tool list is unknown keeps its bare key: zero children would make both master states wrong.
    [Fact]
    public void UnknownToolList_KeepsTheServerKeyIntact()
    {
        var expanded = _selection.ExpandedEnabledTools(Context(
            conversation: [ToolCatalog.McpServerKey("cal")], servers: [Server("cal", known: null)]));

        Assert.Equal([ToolCatalog.McpServerKey("cal")], expanded);
    }

    [Fact]
    public void SkillsWildcard_ExpandsToTheMergedCatalog_UploadsIncluded()
    {
        var upload = new SkillInfo("zz-my-upload", "Mine.", "/uploads/zz-my-upload");

        var expanded = _selection.ExpandedEnabledTools(Context(conversation: [ToolCatalog.Skills], skills: [upload]));

        Assert.DoesNotContain(ToolCatalog.Skills, expanded);
        Assert.Contains(ToolCatalog.SkillKey(upload.Name), expanded);
        Assert.Equal(SkillCatalog.Skills.Count + 1, expanded.Count);
    }

    [Fact]
    public void AnUploadWinsOnANameClash_CaseInsensitively()
    {
        Assert.NotEmpty(SkillCatalog.Skills);
        var shipped = SkillCatalog.Skills[0];
        var upload = new SkillInfo(shipped.Name.ToUpperInvariant(), "Mine.", "/uploads/mine");

        var merged = _selection.MergedSkills(Context(skills: [upload]));

        Assert.Equal(SkillCatalog.Skills.Count, merged.Count);
        Assert.Contains(upload, merged);
        Assert.DoesNotContain(shipped, merged);
    }

    // The capability gate: a no-tools model gets no skills and no MCP tools, whatever is selected. (Memory is not a
    // tool and is gated in the grain by selection alone, ADR 0057.)
    [Fact]
    public void NoToolsModel_SelectsNoSkillsAndNoMcpTools()
    {
        var context = Context(servers: [Server("cal", ["list"], "list")]);
        var all = Set(_selection.EffectiveEnabledTools(context));

        Assert.Empty(_selection.SelectedSkills(context, NoTools, all));
        Assert.Empty(_selection.SelectedMcpTools(context, NoTools, all));
        Assert.Contains(ToolCatalog.Memory, all);
    }

    // Per-tool MCP selection, and the tools of each server sorted by name (ADR 0104's stable prefix).
    [Fact]
    public void SelectedMcpTools_FollowsTheSelection_SortedPerServer()
    {
        var context = Context(servers: [Server("cal", ["list", "add", "delete"], "list", "delete", "add")]);
        var enabled = Set([ToolCatalog.McpToolKey("cal", "list"), ToolCatalog.McpToolKey("cal", "add")]);

        var selected = _selection.SelectedMcpTools(context, Tools, enabled).Select(s => s.Tool.Name);

        Assert.Equal(["add", "list"], selected);
    }

    // Rule 11: a list someone saved before a key existed does not contain it, so that tool lands OFF; a tool with no
    // catalog key (get_date) is always kept.
    [Fact]
    public void ASavedList_LeavesANewKeyOff()
    {
        var saved = Set(_selection.EffectiveEnabledTools(Context(conversation: [ToolCatalog.WebSearch])));

        Assert.True(_selection.IsSelected("web_search", saved));
        Assert.False(_selection.IsSelected("send_push_notification", saved));
        Assert.True(_selection.IsSelected(ToolCatalog.AlwaysOnDateTool, saved));
    }

    [Fact]
    public void SelectedSkills_KeepsOnlyTheSelectedOnes()
    {
        var upload = new SkillInfo("zz-picked", "Mine.", "/uploads/zz-picked");
        var context = Context(skills: [upload]);

        var selected = _selection.SelectedSkills(context, Tools, Set([ToolCatalog.SkillKey(upload.Name)]));

        Assert.Equal([upload], selected);
    }
}
