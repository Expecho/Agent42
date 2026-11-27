using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The compaction half of the artifact feature, which stays in the grain (ADR 0147/0150): it reads the live session
/// and is used by the persist tail, fork, image turn and retention. The tools themselves are
/// <see cref="ArtifactToolSet"/>.
/// </summary>
public sealed partial class AgentGrain
{
    // The three tool names, shared with the tool set, compaction and the history projection.
    internal const string CreateArtifactTool = "create_artifact";
    internal const string UpdateArtifactTool = "update_artifact";
    internal const string ReadArtifactTool = "read_artifact";

    public Task<IReadOnlyList<ArtifactRecord>> GetArtifactsAsync()
        => Task.FromResult<IReadOnlyList<ArtifactRecord>>(
            _state.State.Artifacts.Values.OrderBy(a => a.CreatedAt).ToList());

    /// <summary>
    /// Stubs bulky tool payloads out of the session before it is serialized. Two families: the artifact source —
    /// every <c>create_artifact</c> / <c>update_artifact</c> call's <c>content</c> argument, and every
    /// <c>read_artifact</c> result (ADR 0084) — and every successful <c>read_document</c> part (ADR 0098). Within the
    /// turn the model saw the full text; on a later turn it calls the read tool again. Without this, ten
    /// revisions of a 60 KB app leave 600 KB of dead text in the 2 MB session item (ADR 0004), re-sent every turn.
    ///
    /// <para>Runs at the five post-turn persist sites, beside <c>ApplyRetention</c>, and mutates the
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

        if (call.Name is PastChatLimits.SearchToolName or PastChatLimits.ReadToolName)
        {
            CompactPastChatResult(call, result);
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
    internal static ArtifactInfo? ParseArtifactResult(object? result)
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
}
