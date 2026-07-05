using System.Reflection;

namespace Agent42.Agents;

/// <summary>
/// Loads the agent's "skills": markdown documents embedded under <c>Skills/</c> that
/// teach the agent how and when to use its tools. Every skill is appended to the
/// agent's base instructions when the grain activates, so adding a capability is as
/// simple as dropping a new <c>Skills/*.md</c> file into this project.
/// </summary>
internal static class SkillLibrary
{
    private const string SkillMarker = ".Skills.";

    /// <summary>
    /// Returns every embedded skill document, concatenated in a stable order, or an
    /// empty string when no skills are present.
    /// </summary>
    public static string LoadAll()
    {
        var assembly = typeof(SkillLibrary).Assembly;

        var skills = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(SkillMarker, StringComparison.Ordinal)
                && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => ReadResource(assembly, name))
            .Where(content => !string.IsNullOrWhiteSpace(content));

        return string.Join("\n\n", skills);
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
