namespace Agent42.Agents;

/// <summary>
/// The colour palettes a user can pick (a second axis beside the system/light/dark mode, ADR 0129). The id
/// list is the contract: <c>themes.css</c> carries one dark and one light token set per id, and
/// <c>ArtifactTheme</c> mirrors them for framed artifacts. Kept here so the profile grain (clamp) and the
/// backend (frame tokens) read the same list.
/// </summary>
public static class ThemePalettes
{
    public const string Default = "amber";

    public static readonly IReadOnlyList<string> Ids =
        ["amber", "ocean", "indigo", "violet", "rose", "crimson", "coral", "forest", "teal", "graphite"];

    /// <summary>Normalizes a client value to a known palette id; null/blank/unknown becomes <see cref="Default"/>.</summary>
    public static string Normalize(string? id)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        return Ids.Contains(key) ? key : Default;
    }
}
