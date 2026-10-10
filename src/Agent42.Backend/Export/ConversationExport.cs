using System.Text.Json;
using System.Text.Json.Serialization;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Everything the export reads for one conversation, gathered from the grains before a byte is written (ADR 0091).
/// <see cref="Turns"/> is <c>GetHistoryAsync</c>'s projection exactly as <c>/messages</c> returns it — never a second
/// projection. Reasoning is absent by construction: it is stripped before storage (ADR 0071).
/// </summary>
public sealed record ConversationExport(
    ConversationInfo Conversation,
    string Model,
    AgentBinding? Agent,
    string? SharedStateJson,
    IReadOnlyList<ChatTurn> Turns,
    IReadOnlyList<ArtifactRecord> Artifacts)
{
    /// <summary>
    /// A continuous agent chat (ADR 0016): its history is pruned to 24 h (ADR 0068), so the export is that window.
    /// The id prefix is the only server-side signal — <c>Continuous</c> is never snapshotted into the binding.
    /// </summary>
    public bool IsContinuous => Conversation.Id.StartsWith(RetentionLimits.ContinuousConversationIdPrefix, StringComparison.Ordinal);

    /// <summary>The name an assistant turn is headed with.</summary>
    public string AssistantName => Agent?.Name ?? Conversation.AgentName ?? "Agent 42";
}

/// <summary>
/// Where each file sits inside an archive, relative to its conversation's folder: attachment id → path and
/// <c>{artifactId}/v{n}</c> → path. A separate map rather than a field on <see cref="AttachmentRef"/> or
/// <see cref="ChatTurn"/>, so the turns stay exactly the <c>/messages</c> projection in every mode. A file whose blob
/// was missing is not in it.
/// </summary>
public sealed record ExportFiles(
    IReadOnlyDictionary<string, string> Attachments,
    IReadOnlyDictionary<string, string> Artifacts)
{
    public static string ArtifactKey(string artifactId, int version) => $"{artifactId}/v{version}";
}

/// <summary>The <c>conversation.json</c> document (schema <see cref="SchemaName"/>).</summary>
public sealed record ConversationExportDocument(
    string Schema,
    DateTimeOffset ExportedAt,
    ExportedConversation Conversation,
    string HistoryMode,
    string Model,
    ExportedAgent? Agent,
    JsonElement? SharedState,
    IReadOnlyList<ChatTurn> Turns,
    IReadOnlyList<ArtifactRecord> Artifacts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExportFiles? Files)
{
    public const string SchemaName = "agent42.conversation-export/1";

    // Camel case like every other API payload, indented because a person may open the file.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { WriteIndented = true };

    public static ConversationExportDocument From(ConversationExport export, DateTimeOffset exportedAt, ExportFiles? files) => new(
        SchemaName,
        exportedAt,
        new ExportedConversation(export.Conversation.Id, export.Conversation.Title, export.Conversation.UpdatedAt,
            export.Conversation.AgentId, export.Conversation.AgentName),
        export.IsContinuous ? "continuous" : "perChat",
        export.Model,
        export.Agent is { } agent ? new ExportedAgent(agent.Name, agent.Kind, agent.Model, agent.SystemPrompt) : null,
        ParseSharedState(export.SharedStateJson),
        export.Turns,
        export.Artifacts,
        files);

    // The shared state is a JSON object the grain stores as text (ADR 0089); it is embedded as JSON, not as a string.
    private static JsonElement? ParseSharedState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed record ExportedConversation(string Id, string Title, DateTimeOffset UpdatedAt, string? AgentId, string? AgentName);

public sealed record ExportedAgent(string Name, string? Kind, string Model, string SystemPrompt);
