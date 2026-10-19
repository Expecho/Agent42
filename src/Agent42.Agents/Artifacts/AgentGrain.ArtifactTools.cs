using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The artifact tools: <c>create_artifact</c>, <c>update_artifact</c> and <c>read_artifact</c>. The model writes a
/// standalone HTML page, an SVG, a markdown document or a code file, and every update is a new version. The
/// bytes go to blob storage as <c>application/octet-stream</c> and the metadata to
/// <see cref="AgentGrainState.Artifacts"/>.
///
/// <para><b>The tools never call <c>WriteStateAsync</c>.</b> The end-of-turn write persists the new record, the
/// push tool's precedent. The blob is written first, so a crash in between orphans a blob (harmless) and never
/// leaves a ref to nothing.</para>
///
/// <para><b>The tool-call ARGUMENTS are as big as the artifact</b>, and the session keeps them (ADR 0004's 2 MB
/// item). <see cref="CompactToolPayloads"/> stubs them after the turn; see the artifact entry in gotchas.md.</para>
/// </summary>
public sealed partial class AgentGrain
{
    private const string ArtifactMediaType = "application/octet-stream";

    // The three tool names, shared with compaction and the history projection.
    private const string CreateArtifactTool = "create_artifact";
    private const string UpdateArtifactTool = "update_artifact";
    private const string ReadArtifactTool = "read_artifact";

    // The rules the model obeys are these descriptions (ADR 0067's lesson), so the policy is all here: when an
    // artifact is worth it, prefer update, the sandbox, and read before editing an older one. No promise of a
    // pane: a stored agent run through /api/agent-run has the tools too, and nobody is looking at a UI.
    private IEnumerable<AITool> ArtifactTools() =>
    [
        AIFunctionFactory.Create(
            CreateArtifactAsync,
            name: CreateArtifactTool,
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
            name: UpdateArtifactTool,
            description: "Replace the content of an existing artifact with a new FULL version (not a diff); the "
                + "previous versions are kept. Use this whenever the user asks to change an artifact. If the "
                + "artifact was created or last changed in an EARLIER turn, call read_artifact first, because its "
                + "content is no longer in your context. Returns the new version number."),
        AIFunctionFactory.Create(
            ReadArtifactAsync,
            name: ReadArtifactTool,
            description: "Read the content of one version of an artifact in this conversation (the latest when no "
                + "version is given). Call it before editing an artifact from an earlier turn."),
    ];

    public Task<IReadOnlyList<ArtifactRecord>> GetArtifactsAsync()
        => Task.FromResult<IReadOnlyList<ArtifactRecord>>(
            _state.State.Artifacts.Values.OrderBy(a => a.CreatedAt).ToList());

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

        if (_state.State.Artifacts.Count >= ArtifactLimits.MaxArtifactsPerConversation)
        {
            return $"This conversation already has {ArtifactLimits.MaxArtifactsPerConversation} artifacts, the "
                + "limit. Update an existing one instead, or suggest the user continue in a new chat.";
        }

        var now = _timeProvider.GetUtcNow();
        var record = new ArtifactRecord
        {
            Id = "art_" + Guid.NewGuid().ToString("N")[..12],
            Kind = kind,
            Title = CleanArtifactTitle(title),
            Language = kind == ArtifactKind.Code ? language : null,
            CreatedAt = now,
        };

        await AppendArtifactVersionAsync(record, content, cancellationToken);
        _state.State.Artifacts[record.Id] = record;
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

        if (!_state.State.Artifacts.TryGetValue(artifactId ?? "", out var record))
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
        if (!_state.State.Artifacts.TryGetValue(artifactId ?? "", out var record))
        {
            return UnknownArtifact(artifactId);
        }

        var entry = version is { } wanted ? record.Versions.Find(v => v.Version == wanted) : record.Versions[^1];
        if (entry is null)
        {
            return $"Artifact {record.Id} has versions {VersionList(record)}; there is no version {version}.";
        }

        var stored = await _attachments.OpenReadAsync(this.GetPrimaryKeyString(), entry.AttachmentId, cancellationToken);
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
        var saved = await _attachments.SaveAsync(
            this.GetPrimaryKeyString(), $"{record.Id}-v{version}.bin", ArtifactMediaType, bytes, cancellationToken);
        record.Versions.Add(new ArtifactVersionRef(version, saved.Id, bytes.Length, _timeProvider.GetUtcNow()));

        // Never log the title or the content: model-authored text about whatever the user is doing (ADR 0010).
        _logger.LogInformation(
            "Agent grain {GrainKey} stored artifact {ArtifactId} v{Version} ({Kind}, {Bytes} bytes)",
            this.GetPrimaryKeyString(), record.Id, version, record.Kind, bytes.Length);
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
    private static string VersionList(ArtifactRecord record)
        => string.Join(", ", record.Versions.Select(v => v.Version));

    private static string UnknownArtifact(string? artifactId)
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

    /// <summary>
    /// Stubs bulky tool payloads out of the session before it is serialized. Two families: the artifact source —
    /// every <c>create_artifact</c> / <c>update_artifact</c> call's <c>content</c> argument, and every
    /// <c>read_artifact</c> result (ADR 0084) — and every successful <c>read_document</c> part (ADR 0098). Within the
    /// turn the model saw the full text; on a later turn it calls the read tool again. Without this, ten
    /// revisions of a 60 KB app leave 600 KB of dead text in the 2 MB session item (ADR 0004), re-sent every turn.
    ///
    /// <para>Runs at the five post-turn persist sites, beside <c>ApplyRetentionAsync</c>, and mutates the
    /// <c>TryGetInMemoryChatHistory</c> list in place the way retention does. Measured on Agent Framework 1.22
    /// (spike in the plan): a replaced <see cref="FunctionCallContent"/> survives serialize → JSON → deserialize,
    /// and the next turn still sends the call paired with its result. A call is REPLACED with a copy rather than
    /// having its argument dictionary edited, because the framework may share that dictionary with the live
    /// stream. Idempotent: an already-stubbed call or result is left alone.</para>
    /// </summary>
    private void CompactToolPayloads()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        var results = new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
        foreach (var result in messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
        {
            results[result.CallId] = result;
        }

        foreach (var message in messages)
        {
            for (var i = 0; i < message.Contents.Count; i++)
            {
                if (message.Contents[i] is FunctionCallContent call)
                {
                    message.Contents[i] = CompactToolCall(call, results.GetValueOrDefault(call.CallId));
                }
            }
        }
    }

    // One call, compacted, or the same instance when there is nothing to compact. The result is joined by CallId,
    // never by position: a result arrives on a later message than its call (gotchas.md).
    private static AIContent CompactToolCall(FunctionCallContent call, FunctionResultContent? result)
    {
        if (call.Name == ToolCatalog.ReadDocument)
        {
            CompactReadDocumentResult(call, result);
            return call;
        }

        if (call.Name == ReadArtifactTool)
        {
            if (result is not null && ParseArtifactResult(result.Result) is { } read)
            {
                result.Result = ArtifactLimits.Stub(read.ArtifactId, read.Version);
            }

            return call;
        }

        if (call.Name is not (CreateArtifactTool or UpdateArtifactTool)
            || call.Arguments?.TryGetValue("content", out var content) != true
            || ToolText(content) is not { } text
            || IsCompacted(text))
        {
            return call;
        }

        // A refused call stored nothing, so there is no artifact to point at. Its content is dropped all the same.
        var stub = ParseArtifactResult(result?.Result) is { } stored
            ? ArtifactLimits.Stub(stored.ArtifactId, stored.Version)
            : RefusedContentStub;
        var arguments = new Dictionary<string, object?>(call.Arguments, StringComparer.Ordinal) { ["content"] = stub };
        return new FunctionCallContent(call.CallId, call.Name, arguments)
        {
            AdditionalProperties = call.AdditionalProperties,
            InformationalOnly = call.InformationalOnly,
        };
    }

    private const string RefusedContentStub = "[content omitted: this call was refused and nothing was stored]";

    // Exactly the two stubs compaction writes, never "starts with [": a JSON-array code artifact or a markdown
    // document opening with [TOC] is real content and must be compacted like any other.
    private static bool IsCompacted(string text)
        => text.StartsWith(ArtifactLimits.StubPrefix, StringComparison.Ordinal)
            || text.Equals(RefusedContentStub, StringComparison.Ordinal);

    /// <summary>
    /// The id and version in an artifact tool's JSON result, or null for a refusal (plain text) or a stub. Shared by
    /// compaction and the history projection, which therefore agree on what counts as a stored artifact.
    /// </summary>
    private static ArtifactInfo? ParseArtifactResult(object? result)
    {
        var text = ToolText(result);
        if (text is null || !text.StartsWith('{'))
        {
            return null;
        }

        try
        {
            var info = JsonSerializer.Deserialize<ArtifactInfo>(text, JsonSerializerOptions.Web);
            return info is { ArtifactId.Length: > 0, Version: > 0 } ? info : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The artifact cards a turn shows, projected from its harvested tool calls (and so joined to results by
    /// <c>CallId</c>), or null when it created or updated none. A refused call carries a plain-text result and
    /// projects nothing. Projected, never stored: cards can still vanish with their tool messages (a chain open at
    /// a user turn, <c>StripToolHistory</c>, retention), which is why the pane also lists the conversation's
    /// artifacts from state.
    /// </summary>
    private static List<ArtifactInfo>? ArtifactsFromToolCalls(IReadOnlyList<ChatToolCall> calls)
    {
        List<ArtifactInfo>? cards = null;
        foreach (var call in calls)
        {
            if (call.ToolName is CreateArtifactTool or UpdateArtifactTool
                && call.ToolResult is { } serialized
                && ParseArtifactResult(JsonSerializer.Deserialize<JsonElement>(serialized)) is { } info)
            {
                (cards ??= []).Add(info);
            }
        }

        return cards;
    }

    // A tool value as text: the string a tool returned in memory, or the JSON string element it becomes once the
    // session has been through a serialize round-trip.
    private static string? ToolText(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement e => e.GetRawText(),
        _ => null,
    };

    private sealed record ArtifactReadResult(
        string ArtifactId, int Version, string Kind, string Title, string? Language, string Content);
}
