using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The artifact tools: <c>create_artifact</c>, <c>update_artifact</c> and <c>read_artifact</c>. The model writes a
/// standalone HTML page, an SVG, a markdown document or a code file, and every update is a new version. The
/// bytes go to blob storage as <c>application/octet-stream</c> and the metadata to
/// <see cref="AgentGrainState.Artifacts"/>, through the <see cref="ArtifactList"/> port.
///
/// <para><b>The tools never persist state.</b> The end-of-turn write persists the new record, the
/// push tool's precedent. The blob is written first, so a crash in between orphans a blob (harmless) and never
/// leaves a ref to nothing.</para>
///
/// <para><b>The tool-call ARGUMENTS are as big as the artifact</b>, and the session keeps them (ADR 0004's 2 MB
/// item). <c>AgentGrain.CompactToolPayloads</c> stubs them after the turn; see the artifact entry in gotchas.md. The
/// compaction half stays in the grain (ADR 0150) and parses the exact JSON shape <c>ArtifactResult</c> writes.</para>
///
/// <para>Built per grain activation with <c>new</c> (ADR 0147, amended by plan cc-12 / ADR 0150); public only because
/// this repo has no <c>InternalsVisibleTo</c>. Holds no grain state.</para>
/// </summary>
public sealed class ArtifactToolSet(
    ArtifactList artifacts, IAttachmentStore attachments, TimeProvider timeProvider, ILogger logger, string grainKey)
{
    private const string ArtifactMediaType = "application/octet-stream";

    // The rules the model obeys are these descriptions (ADR 0067's lesson), so the policy is all here: when an
    // artifact is worth it, prefer update, the sandbox, and read before editing an older one. No promise of a
    // pane: a stored agent run through /api/agent-run has the tools too, and nobody is looking at a UI.
    public IEnumerable<AITool> Tools() =>
    [
        AIFunctionFactory.Create(
            CreateArtifactAsync,
            name: AgentGrain.CreateArtifactTool,
            description: "Create an artifact: a standalone piece of output the user may open, copy and download "
                + "separately from the chat (in the Agent42 UI it opens in a side pane). Use it for SUBSTANTIAL, "
                + "self-contained output: an interactive HTML page or small app, a diagram or illustration as SVG, "
                + "a document longer than about 40 lines, or a complete code file. Answer inline instead for short "
                + "snippets, explanations and anything conversational. To change an existing artifact call "
                + "update_artifact; do not create a new one. HTML runs in a sandbox with NO network access (fetch, "
                + "XHR and WebSockets fail) and no storage, so put all data, CSS and script inline; external "
                + "libraries may be loaded only from https://cdnjs.cloudflare.com. An <iframe> of an https page (a "
                + "video or map embed) is allowed, but it inherits the sandbox, so an embed that needs cookies may "
                + "not work; external images are blocked. HTML and SVG are shown in the app's theme: the page gets "
                + "its fonts and these CSS variables, light or dark to match the app: --bg, --bg-raised, --bg-inset "
                + "(surfaces), --ink, --ink-dim, --ink-faint (text), --line (borders), --amber (accent), --green, "
                + "--red, --mono, --serif. Style with these variables and currentColor, not fixed colors, and do not "
                + "set your own page background or font unless the user asks for a different look. Returns the artifact id and "
                + "version; do not repeat the content in your reply."),
        AIFunctionFactory.Create(
            UpdateArtifactAsync,
            name: AgentGrain.UpdateArtifactTool,
            description: "Replace the content of an existing artifact with a new FULL version (not a diff); the "
                + "previous versions are kept. Use this whenever the user asks to change an artifact. If the "
                + "artifact was created or last changed in an EARLIER turn, call read_artifact first, because its "
                + "content is no longer in your context. Returns the new version number."),
        AIFunctionFactory.Create(
            ReadArtifactAsync,
            name: AgentGrain.ReadArtifactTool,
            description: "Read the content of one version of an artifact in this conversation (the latest when no "
                + "version is given). Call it before editing an artifact from an earlier turn."),
    ];

    private async Task<string> CreateArtifactAsync(
        [Description("One of: html (a full page, may include inline script), svg, markdown, code.")] string kind,
        [Description("A short human title, e.g. 'Mortgage calculator'. At most 100 characters.")] string title,
        [Description("The complete content. At most 64 KB.")] string content,
        [Description("For kind=code only: the language, e.g. python, typescript, csharp, bash.")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        // Incognito (ADR 0034), first: an artifact is model-authored conversation content written to blob with
        // no purge path, which breaks "leaves no trace". The image precedent does not transfer — an image is
        // user-supplied.
        if (TurnContext.CurrentOrClosed.Ephemeral)
        {
            return "I cannot create artifacts in an incognito chat, because an artifact is stored outside the "
                + "conversation and would outlive it. Put the content in your reply instead.";
        }

        if (ValidateNewArtifact(kind, language) is { } kindProblem)
        {
            return kindProblem;
        }

        if (ValidateContent(content) is { } contentProblem)
        {
            return contentProblem;
        }

        if (artifacts.Count >= ArtifactLimits.MaxArtifactsPerConversation)
        {
            return $"This conversation already has {ArtifactLimits.MaxArtifactsPerConversation} artifacts, the "
                + "limit. Update an existing one instead, or suggest the user continue in a new chat.";
        }

        var now = timeProvider.GetUtcNow();
        var record = new ArtifactRecord
        {
            Id = "art_" + Guid.NewGuid().ToString("N")[..12],
            Kind = kind,
            Title = CleanArtifactTitle(title),
            Language = kind == ArtifactKind.Code ? language : null,
            CreatedAt = now,
        };

        await AppendArtifactVersionAsync(record, content, cancellationToken);
        artifacts.Add(record);
        return ArtifactResult(record);
    }

    private async Task<string> UpdateArtifactAsync(
        [Description("The artifactId create_artifact returned.")] string artifactId,
        [Description("The complete new content (not a diff). At most 64 KB.")] string content,
        [Description("A new title, only if it should change.")] string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (TurnContext.CurrentOrClosed.Ephemeral)
        {
            return "I cannot change artifacts in an incognito chat. Put the content in your reply instead.";
        }

        if (!artifacts.TryGet(artifactId, out var record))
        {
            return UnknownArtifact(artifactId);
        }

        if (ValidateContent(content) is { } contentProblem)
        {
            return contentProblem;
        }

        if (record.Versions.Count >= ArtifactLimits.MaxVersionsPerArtifact)
        {
            return $"Artifact {record.Id} already has {ArtifactLimits.MaxVersionsPerArtifact} versions, the limit. "
                + "Create a new artifact for further changes.";
        }

        await AppendArtifactVersionAsync(record, content, cancellationToken);
        if (!string.IsNullOrWhiteSpace(title))
        {
            record.Title = CleanArtifactTitle(title);
        }

        return ArtifactResult(record);
    }

    private async Task<string> ReadArtifactAsync(
        [Description("The artifactId.")] string artifactId,
        [Description("The version number; omit for the latest.")] int? version = null,
        CancellationToken cancellationToken = default)
    {
        if (!artifacts.TryGet(artifactId, out var record))
        {
            return UnknownArtifact(artifactId);
        }

        var entry = version is { } wanted ? record.Versions.Find(v => v.Version == wanted) : record.Versions[^1];
        if (entry is null)
        {
            return $"Artifact {record.Id} has versions {VersionList(record)}; there is no version {version}.";
        }

        var stored = await attachments.OpenReadAsync(grainKey, entry.AttachmentId, cancellationToken);
        if (stored is null)
        {
            return $"The content of artifact {record.Id} version {entry.Version} is missing from storage.";
        }

        // One version at most, and capped at write time, so this result is bounded by MaxContentBytes. It is
        // stubbed by compaction after the turn like the call arguments are.
        return JsonSerializer.Serialize(
            new ArtifactReadResult(record.Id, entry.Version, record.Kind, record.Title, record.Language,
                Encoding.UTF8.GetString(stored.Bytes)),
            JsonSerializerOptions.Web);
    }

    // The blob is saved before the version is appended, so a failed upload leaves the record untouched. The number
    // follows the LAST version, not the count: a fork drops a version whose blob is missing (ADR 0092), and a count
    // would then hand out a number that is already taken.
    private async Task AppendArtifactVersionAsync(ArtifactRecord record, string content, CancellationToken cancellationToken)
    {
        var version = (record.Versions.Count > 0 ? record.Versions[^1].Version : 0) + 1;
        var bytes = Encoding.UTF8.GetBytes(content);
        var saved = await attachments.SaveAsync(
            grainKey, $"{record.Id}-v{version}.bin", ArtifactMediaType, bytes, cancellationToken);
        record.Versions.Add(new ArtifactVersionRef(version, saved.Id, bytes.Length, timeProvider.GetUtcNow()));

        // Never log the title or the content: model-authored text about whatever the user is doing (ADR 0010).
        logger.LogInformation(
            "Agent grain {GrainKey} stored artifact {ArtifactId} v{Version} ({Kind}, {Bytes} bytes)",
            grainKey, record.Id, version, record.Kind, bytes.Length);
    }

    private static string? ValidateNewArtifact(string? kind, string? language)
    {
        if (kind is null || !ArtifactKind.All.Contains(kind))
        {
            return "kind must be one of: html, svg, markdown, code.";
        }

        return kind == ArtifactKind.Code && !ArtifactLimits.IsValidLanguage(language)
            ? "A code artifact needs a language: a lowercase name such as python, typescript, csharp or bash."
            : null;
    }

    private static string? ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "The content is empty. Pass the complete artifact content.";
        }

        // A stub from compaction copied back as content would store the stub as the artifact.
        if (content.StartsWith(ArtifactLimits.StubPrefix, StringComparison.Ordinal))
        {
            return "That content is a placeholder, not the artifact. Call read_artifact to get the real content, "
                + "then pass the complete new version.";
        }

        // Refused, never truncated: a truncated app is a broken app.
        var size = Encoding.UTF8.GetByteCount(content);
        return size > ArtifactLimits.MaxContentBytes
            ? $"The content is {size / 1024} KB; an artifact version may be at most {ArtifactLimits.MaxContentBytes / 1024} KB. "
                + "Nothing was stored. Make it smaller, or split it into several artifacts."
            : null;
    }

    // The version numbers an artifact holds. Not "1 to N": a truncation or a fork can leave a gap (ADR 0092).
    internal static string VersionList(ArtifactRecord record)
        => string.Join(", ", record.Versions.Select(v => v.Version));

    internal static string UnknownArtifact(string? artifactId)
        => $"There is no artifact '{artifactId}' in this conversation. Use an artifactId that create_artifact returned.";

    private static string CleanArtifactTitle(string? title)
    {
        var clean = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
        {
            return "Untitled";
        }

        return clean.Length <= ArtifactLimits.MaxTitleLength ? clean : clean[..ArtifactLimits.MaxTitleLength];
    }

    // The compact result: the history projection parses exactly this shape back into an ArtifactInfo, so a
    // refusal (plain text) projects no card.
    private static string ArtifactResult(ArtifactRecord record)
        => JsonSerializer.Serialize(
            new ArtifactInfo(record.Id, record.Versions[^1].Version, record.Kind, record.Title), JsonSerializerOptions.Web);

    private sealed record ArtifactReadResult(
        string ArtifactId, int Version, string Kind, string Title, string? Language, string Content);
}
