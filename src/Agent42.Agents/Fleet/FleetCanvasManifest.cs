using System.Text.RegularExpressions;

namespace Agent42.Agents;

/// <summary>
/// The index of a fleet workspace's canvas (ADR 0102): its pages and the versions each one still keeps. Our own Blob
/// JSON, written by <see cref="IFleetStore.PutCanvasManifestAsync"/> after each committed version, never grain state.
/// A missing manifest is an empty canvas.
/// </summary>
public sealed record FleetCanvasManifest
{
    public IReadOnlyList<FleetCanvasPage> Pages { get; init; } = [];
}

/// <summary>
/// One canvas page: a name (the blob and URL segment), a kind from <see cref="ArtifactKind"/>, a title, an optional
/// code language, and the version numbers still stored, oldest first. Versions only grow, so a pruned number is never
/// reused.
/// </summary>
public sealed partial record FleetCanvasPage
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Language { get; init; }
    public IReadOnlyList<int> Versions { get; init; } = [];

    public int LatestVersion => Versions.Count == 0 ? 0 : Versions[^1];

    /// <summary>True when <paramref name="name"/> is safe as one blob segment and one URL segment.</summary>
    public static bool IsValidName(string? name) => name is not null && NameRule().IsMatch(name);

    // \z, not $: $ also matches before a trailing newline, so "report\n" would pass as a segment.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRule();
}
