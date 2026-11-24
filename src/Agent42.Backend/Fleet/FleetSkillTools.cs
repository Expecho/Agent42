using System.ComponentModel;
using System.Text;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The user's skills for one fleet task's regular agents (ADR 0144): <c>load_skill</c>, which returns a selected skill's
/// <c>SKILL.md</c> and a listing of its files, and the chat's <see cref="ReadSkillResourceTool"/>. A fleet agent is a
/// declarative YAML agent with no context-provider slot, and <c>TruncatingSkillsProvider</c> can't be invoked without an
/// <c>AIAgent</c>, so this is the fleet's own <c>load_skill</c> over the chat's caps (<see cref="SkillToolLimits"/>).
/// There is no <c>run_skill_script</c>: nothing here executes a skill's file. Copilot agents load skills natively and get
/// only <see cref="ReadSkillResourceTool"/> (<see cref="FleetCopilotAgentFactory"/>).
/// </summary>
public sealed class FleetSkillTools
{
    public const string LoadName = "load_skill";

    /// <summary>The longest skill description listed in <c>load_skill</c>'s description.</summary>
    public const int MaxListedDescriptionLength = 200;

    /// <summary>Most files listed under a loaded skill.</summary>
    public const int MaxListedFiles = 50;

    private static readonly EnumerationOptions FilesOnly = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    public FleetSkillTools(IReadOnlyList<SkillInfo> skills)
    {
        Skills = skills;
        Tools = skills.Count == 0
            ? []
            :
            [
                AIFunctionFactory.Create(
                    ([Description("The skill's name, as listed in this tool's description.")] string name) => Load(skills, name),
                    LoadName,
                    Describe(skills)),
                ReadSkillResourceTool.Create(skills),
            ];
    }

    /// <summary>The selected skills.</summary>
    public IReadOnlyList<SkillInfo> Skills { get; }

    /// <summary><c>load_skill</c> and <c>read_skill_resource</c>, or none when no skill is selected.</summary>
    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>
    /// The skills a fleet task gets: the user's uploads over the shipped catalog (<see cref="ToolSelection.Merge"/>), those the
    /// selection enables, and a companion skill (ADR 0126) only when its tool is one of <paramref name="fleetToolKeys"/>: the
    /// fleet has no <c>schedule_prompt</c>, so <c>scheduledprompt</c> would describe a tool no agent has.
    /// </summary>
    public static IReadOnlyList<SkillInfo> Select(IReadOnlyList<SkillInfo> userSkills, ISet<string> enabled, ISet<string> fleetToolKeys)
        =>
        [
            .. ToolSelection.Merge(userSkills).Where(s => ToolCatalog.IsSkillEnabled(s.Name, enabled)
                && (!ToolCatalog.CompanionSkills.TryGetValue(s.Name, out var toolKey) || fleetToolKeys.Contains(toolKey))),
        ];

    /// <summary>The named skill's <c>SKILL.md</c> and its files, cut at <see cref="SkillToolLimits.MaxSkillBodyLength"/>.</summary>
    public static string Load(IReadOnlyList<SkillInfo> skills, string name)
    {
        var skill = skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            return $"No selected skill is named '{name}'. Use one of: {string.Join(", ", skills.Select(s => s.Name))}.";
        }

        var file = new FileInfo(Path.Combine(skill.Path, "SKILL.md"));
        if (!file.Exists || file.LinkTarget is not null)
        {
            return $"The skill '{skill.Name}' has no SKILL.md.";
        }

        var text = new StringBuilder(ReadAtMost(file, SkillToolLimits.MaxSkillBodyLength + 1));
        var files = Files(skill.Path);
        if (files.Count > 0)
        {
            text.Append("\n\nFiles of this skill, readable with ").Append(ReadSkillResourceTool.Name).Append(":\n")
                .AppendJoin('\n', files.Select(f => "- " + f));
        }

        var result = text.ToString();
        return result.Length > SkillToolLimits.MaxSkillBodyLength
            ? result[..SkillToolLimits.MaxSkillBodyLength] + SkillToolLimits.TruncationMarker
            : result;
    }

    private static string Describe(IReadOnlyList<SkillInfo> skills)
        => "Load the instructions of one of your skills before you do work it covers, then follow them. Skills:\n"
            + string.Join('\n', skills.Select(s => $"- {s.Name}: {FleetTeamRunner.Cut(s.Description, MaxListedDescriptionLength)}"));

    private static List<string> Files(string root)
        => Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*", FilesOnly)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(f => !f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Take(MaxListedFiles)]
            : [];

    // At most max characters are read, so a huge SKILL.md costs no more than a small one.
    private static string ReadAtMost(FileInfo file, int max)
    {
        using var reader = new StreamReader(file.FullName);
        var buffer = new char[max];
        var read = reader.ReadBlock(buffer, 0, max);
        return new string(buffer, 0, read);
    }
}
