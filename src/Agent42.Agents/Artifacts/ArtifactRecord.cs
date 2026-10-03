using System.Text.Json.Serialization;

namespace Agent42.Agents;

/// <summary>
/// One model-authored artifact (an HTML page, an SVG, a markdown document or a code file) and every version
/// of it, kept in <see cref="AgentGrainState.Artifacts"/> keyed by <see cref="Id"/>. Only metadata lives
/// here: each version's source sits in blob storage (ADR 0004), stored as <c>application/octet-stream</c>
/// whatever the kind, because the attachment proxies serve any id under the owner key with its stored media
/// type and an <c>text/html</c> blob would render on the app origin. The kind lives here instead.
///
/// <para>A class, not a record, on purpose: the grain appends to <see cref="Versions"/> and renames
/// <see cref="Title"/> in place. A Cosmos document written before a property existed reads it back through
/// the property initializer, so the record-parameter null trap does not apply (gotchas.md).</para>
/// </summary>
// ADR 0039: flat namespace, frozen. Cosmos writes this type's full CLR name into the stored state.
[GenerateSerializer]
[Alias("Agent42.Agents.ArtifactRecord")]
public sealed class ArtifactRecord
{
    [Id(0)]
    public string Id { get; set; } = "";

    /// <summary>One of <see cref="ArtifactKind"/>'s constants.</summary>
    [Id(1)]
    public string Kind { get; set; } = "";

    [Id(2)]
    public string Title { get; set; } = "";

    /// <summary>The highlight.js language of a <see cref="ArtifactKind.Code"/> artifact; null for the others.</summary>
    [Id(3)]
    public string? Language { get; set; }

    [Id(4)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Oldest first; a version's number is its 1-based position.</summary>
    [Id(5)]
    public List<ArtifactVersionRef> Versions { get; set; } = [];
}

/// <summary>
/// One stored version of an <see cref="ArtifactRecord"/>: where its bytes are and how big they are.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ArtifactVersionRef")]
public sealed record ArtifactVersionRef(
    [property: Id(0)] int Version,
    // Kept out of the HTTP JSON like AttachmentRef.BlobPath: the browser reads a version through the
    // artifact routes, which send the sandbox headers, never through the raw attachment proxy.
    [property: Id(1)][property: JsonIgnore] string AttachmentId,
    [property: Id(2)] int SizeBytes,
    [property: Id(3)] DateTimeOffset CreatedAt);

/// <summary>
/// The artifact kinds. String constants rather than an enum, so the tool schema, the stored state and the
/// wire format all read as the same plain words.
/// </summary>
public static class ArtifactKind
{
    public const string Html = "html";
    public const string Svg = "svg";
    public const string Markdown = "markdown";
    public const string Code = "code";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Html, Svg, Markdown, Code,
    };

    /// <summary>The kinds the browser renders in a sandboxed iframe through the <c>/frame</c> route.</summary>
    public static bool IsFramed(string kind) => kind is Html or Svg;
}

/// <summary>
/// The card a turn shows for an artifact it created or updated: projected into <see cref="ChatTurn.Artifacts"/>
/// from the turn's artifact tool calls, never persisted. The public share response keeps it while it strips
/// the tool calls, which is why it is its own field rather than something the viewer parses out of
/// <see cref="ChatTurn.ToolCalls"/>.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ArtifactInfo")]
public sealed record ArtifactInfo(
    [property: Id(0)] string ArtifactId,
    [property: Id(1)] int Version,
    [property: Id(2)] string Kind,
    [property: Id(3)] string Title);
