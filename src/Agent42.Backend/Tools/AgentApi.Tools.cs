
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// The selectable-tool catalogue and per-conversation overrides (ADR 0021/0026).
/// </summary>
public static partial class AgentApi
{
    private static void MapToolEndpoints(IEndpointRouteBuilder app)
    {
        // The selectable-tools catalog (ADR 0021): the curated keys the UI renders as checkbox groups in
        // Preferences, the agent editor and the composer popover. The static keys are the single source of
        // truth in ToolCatalog; per-user MCP servers (ADR 0026) are appended as one `mcp:{id}` descriptor per
        // MANAGEMENT-ENABLED server (a disabled server contributes no tools, so it gets no checkbox). Capability
        // gating still happens server-side in BuildAgent.
        app.MapGet("/api/tools", async (IGrainFactory grains, HttpContext http) =>
        {
            var overlay = await grains.GetGrain<IUserMcpConfigGrain>(http.GetUserId()).GetAsync();
            var mcpDescriptors = overlay.Servers
                .Where(s => s.Enabled)
                .Select(s => new ToolDescriptor(ToolCatalog.McpServerKey(s.Id), s.Name, RequiresHostedTools: false));
            return Results.Json<IReadOnlyList<ToolDescriptor>>([.. ToolCatalog.Descriptors, .. mcpDescriptors]);
        });

        // The EFFECTIVE tools for one conversation (override ?? user-default ?? all), so the composer popover
        // can restore its checkboxes when the conversation is reopened.
        app.MapGet("/api/conversations/{conversationId}/tools", async (string conversationId, IGrainFactory grains, HttpContext http) =>
            Results.Json(await grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).GetEnabledToolsAsync()));

        // Sets the per-conversation tool override (ADR 0021) and returns the resulting effective set. The
        // grain sanitizes unknown keys; the UI represents "all" as the full explicit list (never null), so a
        // PUT is always an explicit selection. Scoped to non-agent chats in the UI — an agent chat's tools
        // come from the bind snapshot, which would clobber a pre-first-turn override.
        app.MapPut("/api/conversations/{conversationId}/tools", async (string conversationId, ToolSelectionRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var agent = grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId));
            await agent.SetEnabledToolsAsync(request.Tools);
            return Results.Json(await agent.GetEnabledToolsAsync());
        });

        // ————— stored agents (per-user personas) —————
    }
}
