using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>The fleet's skill tools (ADR 0144): selection, the upload overlay, companions, the caps, and no script tool.</summary>
public sealed class FleetSkillToolsTests : IDisposable
{
    private static readonly ScriptedFleetInteraction Interaction = new();
    private readonly TempSkills _skills = new();

    public void Dispose() => _skills.Dispose();

    private static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.Ordinal);

    private static async Task<string> InvokeAsync(IReadOnlyList<AIFunction> tools, string name, AIFunctionArguments arguments)
        => (await tools.Single(t => t.Name == name).InvokeAsync(arguments))!.ToString()!;

    private static FleetToolCatalog Catalog(IUserSkillProvider skills)
        => new(FakeUserGrainFactory.Create().Factory, NullLogger<FleetToolCatalog>.Instance, new InMemoryWebSearch(), skills: skills);

    [Fact]
    public async Task Skills_Selected_Exposed()
    {
        var recipe = _skills.Add("recipes", "Cook it slowly.");

        var set = await Catalog(new FakeUserSkillProvider(recipe)).BuildAsync(
            new FleetToolContext("alice", "t1", "", [ToolCatalog.SkillKey("recipes")], Interaction), default);

        Assert.Equal(["recipes"], set.Skills.Select(s => s.Name));
        Assert.Equal([FleetSkillTools.LoadName, ReadSkillResourceTool.Name], set.Tools.Select(t => t.Name));
        Assert.Contains("- recipes: The recipes skill.", set.Tools[0].Description);
        Assert.StartsWith("Cook it slowly.", await InvokeAsync(set.Tools, FleetSkillTools.LoadName, new() { ["name"] = "recipes" }));
    }

    [Fact]
    public async Task Skills_Deselected_Absent()
    {
        var recipe = _skills.Add("recipes", "Cook it slowly.");

        var set = await Catalog(new FakeUserSkillProvider(recipe)).BuildAsync(
            new FleetToolContext("alice", "t1", "", [ToolCatalog.Memory], Interaction), default);

        Assert.Empty(set.Skills);
        Assert.DoesNotContain(set.Tools, t => t.Name is FleetSkillTools.LoadName or ReadSkillResourceTool.Name);
    }

    /// <summary>An upload named like the shipped <c>websearch</c> companion replaces it (ADR 0049), under the same switch.</summary>
    [Fact]
    public void Skills_UploadShadowsShipped()
    {
        var upload = _skills.Add("websearch", "My own search guidance.");

        var selected = FleetSkillTools.Select([upload], Keys(ToolCatalog.WebSearch), Keys(ToolCatalog.WebSearch));

        Assert.Equal(upload.Path, Assert.Single(selected, s => s.Name == "websearch").Path);
    }

    /// <summary>The fleet has no schedule_prompt, so <c>scheduledprompt</c> never comes along, even with scheduling selected.</summary>
    [Fact]
    public void Skills_CompanionWithoutTool_Excluded()
    {
        var all = FleetSkillTools.Select([], Keys(ToolCatalog.Scheduling, ToolCatalog.WebSearch, ToolCatalog.Skills), Keys(ToolCatalog.WebSearch));
        var noSearch = FleetSkillTools.Select([], Keys(ToolCatalog.WebSearch, ToolCatalog.Skills), Keys());

        Assert.DoesNotContain(all, s => s.Name == "scheduledprompt");
        Assert.Contains(all, s => s.Name == "websearch");
        Assert.DoesNotContain(noSearch, s => s.Name == "websearch");
    }

    /// <summary>No run_skill_script, in any selection, and an MCP tool can't take that name either.</summary>
    [Fact]
    public async Task Skills_NoRunScript()
    {
        var recipe = _skills.Add("recipes", "Run scripts/x.sh.", ("scripts/x.sh", "rm -rf /"));
        var mcp = new McpServerTools("s1", [AIFunctionFactory.Create(() => "ran", "run_skill_script")]);

        var set = await Catalog(new FakeUserSkillProvider(recipe)).BuildAsync(new FleetToolContext("alice", "t1", "", null, Interaction), default);
        var withMcp = Catalog(new FakeUserSkillProvider(recipe)).Build(null, "", [mcp], Interaction, personalTools: set.Tools);

        Assert.DoesNotContain(set.Tools, t => t.Name == "run_skill_script");
        Assert.DoesNotContain(withMcp, t => t.Name == "run_skill_script");
    }

    [Fact]
    public void LoadSkill_LongBody_Cut()
    {
        var big = _skills.Add("big", new string('x', SkillToolLimits.MaxSkillBodyLength * 3));

        var text = FleetSkillTools.Load([big], "big");

        Assert.Equal(SkillToolLimits.MaxSkillBodyLength + SkillToolLimits.TruncationMarker.Length, text.Length);
        Assert.EndsWith(SkillToolLimits.TruncationMarker, text);
    }

    [Fact]
    public async Task LoadSkill_ListsFiles_AndResourceReadStaysInside()
    {
        var guide = _skills.Add("guide", "Read references/a.md.", ("references/a.md", "fact: 42"));
        var tools = new FleetSkillTools([guide]).Tools;

        var loaded = FleetSkillTools.Load([guide], "guide");
        var resource = await InvokeAsync(tools, ReadSkillResourceTool.Name, new() { ["skill"] = "guide", ["path"] = "references/a.md" });
        var escape = await InvokeAsync(tools, ReadSkillResourceTool.Name, new() { ["skill"] = "guide", ["path"] = "../../x" });

        Assert.Contains("- references/a.md", loaded);
        Assert.Equal("fact: 42", resource);
        Assert.Contains("inside the skill's own folder", escape);
        Assert.StartsWith("No selected skill", FleetSkillTools.Load([guide], "other"));
    }
}
