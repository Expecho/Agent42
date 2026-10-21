
namespace Agent42.Backend;

/// <summary>
/// The Agent42 HTTP surface. This part is the composition root: it registers each feature's endpoint
/// group and holds only what is genuinely shared between them. The endpoints themselves live beside
/// their feature (<c>Mcp/AgentApi.Mcp.cs</c>, <c>Sharing/AgentApi.Sharing.cs</c>, and so on), all
/// completing this one class — like the <c>AgentGrain</c> split of ADR 0039.
///
/// <para>Registration order below is the order the endpoints were declared in before the split, but
/// nothing depends on it: ASP.NET Core minimal-API routing resolves by template precedence, not by
/// registration order, and all 82 route registrations are distinct in method + path. That figure
/// counts <c>app.Map{Get,Post,Put,Delete,Methods}(</c> calls across the <c>AgentApi.*.cs</c> parts
/// only — it deliberately excludes ADR 0070's <c>MapMcp</c>, which is not an <c>AgentApi</c> part.
/// (It read 58 before ADR 0073, 64 before ADR 0084, which added six, 71 before ADR 0089, which added
/// four, 75 before ADR 0091, which added two, 77 before ADR 0092, which added one, and 78 before ADR 0094, which added four; the first two were off by one. The rule is stated
/// here now so the next reader can reproduce it.) The one
/// precedence subtlety in this API is handled by naming, not ordering — <c>PUT /api/default-model</c> is a distinct path rather than
/// <c>/api/providers/default</c>, because a user-chosen provider key would out-rank the literal
/// segment in <c>/api/providers/{key}</c>.</para>
/// </summary>
public static partial class AgentApi
{
    // Uploads are capped so a single request can't be abused. Images become chat attachments
    // whose bytes live in blob storage, never in the serialized session (ADR 0004); documents are
    // ingested for retrieval and never stored as attachments (ADR 0007).
    internal const long MaxAttachmentBytes = 10 * 1024 * 1024;

    // A copilot agent's chat has no document retrieval (the copilot composition has no context providers), so both
    // document paths (the browser upload and /api/agent-run) refuse a document with this, before ingesting it.
    private const string CopilotDocumentsRefused = "A copilot agent's chat doesn't support documents.";

    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        MapConversationEndpoints(app);
        MapChatGroupEndpoints(app);
        MapAttachmentEndpoints(app);
        MapArtifactEndpoints(app);
        MapSharingEndpoints(app);
        MapModelEndpoints(app);
        MapGitHubCopilotEndpoints(app);
        MapToolEndpoints(app);
        MapStoredAgentEndpoints(app);
        MapUserEndpoints(app);
        MapSpeechEndpoints(app);
        MapPushEndpoints(app);
        MapConfigurationEndpoints(app);
        MapTelemetryEndpoints(app);
        MapDiagnosticsEndpoints(app);
        MapMcpEndpoints(app);
        MapUserSkillEndpoints(app);
        MapWebEndpoints(app);
        MapSearchEndpoints(app);
        MapClientToolEndpoints(app);
        MapSharedStateEndpoints(app);
        MapExportEndpoints(app);
        MapWebhookEndpoints(app);
        MapEmailEndpoints(app);
        MapFleetEndpoints(app);
        MapChatGatewayEndpoints(app);

        return app;
    }

    // Agent grains are keyed per user, so a conversation id sent by one user can
    // never resolve to another user's chat.
    private static string AgentKey(string userId, string conversationId)
        => $"{userId}/{conversationId}";
}
