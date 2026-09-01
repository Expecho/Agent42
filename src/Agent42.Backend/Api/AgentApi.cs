
namespace Agent42.Backend;

/// <summary>
/// The Agent42 HTTP surface. This part is the composition root: it registers each feature's endpoint
/// group and holds only what is genuinely shared between them. The endpoints themselves live beside
/// their feature (<c>Mcp/AgentApi.Mcp.cs</c>, <c>Sharing/AgentApi.Sharing.cs</c>, and so on), all
/// completing this one class — like the <c>AgentGrain</c> split of ADR 0039.
///
/// <para>Registration order below is the order the endpoints were declared in before the split, but
/// nothing depends on it: ASP.NET Core minimal-API routing resolves by template precedence, not by
/// registration order, and all 58 route registrations are distinct in method + path. The one
/// precedence subtlety in this API is handled by naming, not ordering — <c>PUT /api/default-model</c> is a distinct path rather than
/// <c>/api/providers/default</c>, because a user-chosen provider key would out-rank the literal
/// segment in <c>/api/providers/{key}</c>.</para>
/// </summary>
public static partial class AgentApi
{
    // Uploads are capped so a single request can't be abused. Images become chat attachments
    // whose bytes live in blob storage, never in the serialized session (ADR 0004); documents are
    // ingested for retrieval and never stored as attachments (ADR 0007).
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        MapConversationEndpoints(app);
        MapAttachmentEndpoints(app);
        MapSharingEndpoints(app);
        MapModelEndpoints(app);
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
        MapCalendarEndpoints(app);
        MapWebEndpoints(app);
        MapSearchEndpoints(app);

        return app;
    }

    // Agent grains are keyed per user, so a conversation id sent by one user can
    // never resolve to another user's chat.
    private static string AgentKey(string userId, string conversationId)
        => $"{userId}/{conversationId}";
}
