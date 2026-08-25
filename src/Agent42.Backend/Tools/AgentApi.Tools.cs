
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
        // truth in ToolCatalog; two groups are appended to them, and BOTH are now per-user:
        //
        //   • skills (ADR 0048, extended by ADR 0049) — one `skill:{name}` descriptor per skill, labelled
        //     with the skill's frontmatter name verbatim. The shipped set (SkillCatalog, per-deployment,
        //     read from disk) OVERLAID with this user's UPLOADS, an upload of the same name winning. ADR
        //     0049 is what made this portion per-user; before it, skills shipped with the image and there
        //     was no per-user flag to honour.
        //   • per-user MCP servers (ADR 0026) — one `mcp:{id}` descriptor per MANAGEMENT-ENABLED server (a
        //     disabled server contributes no tools, so it gets no checkbox).
        //
        // Neither bare wildcard (`skills`, `mcp`) is a descriptor — they are selection sentinels only.
        // Capability gating still happens server-side in BuildAgent.
        //
        // The uploaded skills are read from the GRAIN, never from the package blob or the materialized
        // directory: the picker must list what a user has installed even on a container that has not
        // materialized anything yet, and a disk read here would make the answer depend on which replica
        // served it.
        app.MapGet("/api/tools", async (IGrainFactory grains, HttpContext http) =>
        {
            var userId = http.GetUserId();

            var uploaded = (await grains.GetGrain<IUserSkillsGrain>(userId).GetAsync()).Skills;
            // OrdinalIgnoreCase, matching AgentGrain.MergedSkills: the picker and the gate must agree about
            // which skills exist, or a key the user can tick would gate nothing.
            var shadowed = uploaded.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var skillDescriptors = uploaded
                .Select(s => s.Name)
                .Concat(SkillCatalog.Skills.Select(s => s.Name).Where(n => !shadowed.Contains(n)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new ToolDescriptor(ToolCatalog.SkillKey(n), n, RequiresHostedTools: false));

            var overlay = await grains.GetGrain<IUserMcpConfigGrain>(userId).GetAsync();
            var mcpDescriptors = overlay.Servers
                .Where(s => s.Enabled)
                .Select(s => new ToolDescriptor(ToolCatalog.McpServerKey(s.Id), s.Name, RequiresHostedTools: false));
            return Results.Json<IReadOnlyList<ToolDescriptor>>(
                [.. ToolCatalog.Descriptors, .. skillDescriptors, .. mcpDescriptors]);
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
