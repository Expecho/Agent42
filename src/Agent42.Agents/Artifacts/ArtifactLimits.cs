using System.Text.RegularExpressions;

namespace Agent42.Agents;

/// <summary>
/// The caps the artifact tools run inside, the same discipline <see cref="PushToolLimits"/> and
/// <see cref="WebToolLimits"/> carry for their own tools.
///
/// <para><b>Over-cap content is REFUSED, never truncated.</b> A truncated HTML app is a broken app, and a
/// web tool's "shortened" return has no equivalent here. The per-version cap also bounds what one turn can
/// put into the session before compaction stubs it (ADR 0004's 2 MB item): within the turn the model saw the
/// full source, so the tool-call argument is as big as the content it carried.</para>
/// </summary>
public static partial class ArtifactLimits
{
    /// <summary>Maximum size of one version's content, in UTF-8 bytes.</summary>
    public const int MaxContentBytes = 64 * 1024;

    /// <summary>Maximum number of versions one artifact may have. Each update is a new version.</summary>
    public const int MaxVersionsPerArtifact = 20;

    /// <summary>Maximum number of artifacts one conversation may hold.</summary>
    public const int MaxArtifactsPerConversation = 20;

    /// <summary>Maximum title length, in characters. A longer title is shortened, not refused.</summary>
    public const int MaxTitleLength = 100;

    /// <summary>
    /// The prefix of the stub that compaction writes over an artifact tool call's <c>content</c> argument
    /// and a <c>read_artifact</c> result once the turn is over. A model that later sees the stub in its
    /// history might copy it back as new content, which would store the stub as the artifact; the tools
    /// refuse content that starts with it.
    /// </summary>
    public const string StubPrefix = "[stored as artifact ";

    /// <summary>The stub compaction writes for one version of one artifact.</summary>
    public static string Stub(string artifactId, int version)
        => $"{StubPrefix}{artifactId} v{version}; call read_artifact to see it]";

    /// <summary>True for a highlight.js-style language name: lowercase letters, digits, <c>+ # -</c>.</summary>
    public static bool IsValidLanguage(string? language)
        => language is not null && LanguagePattern().IsMatch(language);

    [GeneratedRegex("^[a-z0-9+#-]{1,20}$")]
    private static partial Regex LanguagePattern();
}
