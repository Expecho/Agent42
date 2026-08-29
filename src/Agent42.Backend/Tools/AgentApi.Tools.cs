
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
        //     disabled server contributes no tools, so it gets no checkbox), plus one nested
        //     `mcp:{id}:{toolName}` descriptor per entry in that server's KnownToolNames, carrying ParentKey
        //     = `mcp:{id}` so the picker can nest without splitting key strings.
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

            // Tool names come from the PERSISTED KnownToolNames, never from a live connection — and that is a
            // deliberate constraint, not a shortcut. Calling IUserMcpToolProvider here would (1) make a GET
            // take up to 10 s per dead server, sequentially, before the picker is usable, and (2) turn a page
            // load into a WRITE, because resolving an InteractiveOAuth token refreshes and saves it, bumping
            // the overlay version and invalidating the live-socket tool cache for every warm grain of the user.
            //
            // The same list reaches the grain on McpServerTools.KnownToolNames, so the checkboxes rendered here
            // and the keys AgentGrain.ExpandMcpWildcard projects are one source. They must not drift: a key the
            // projection mints without a checkbox here is dropped by the very next Save.
            //
            // Consequence worth knowing (the cold-start window): a newly added server has no names until the
            // user's next chat activation connects to it, so it shows a server checkbox and no tool rows until
            // then. It is fully usable meanwhile — mcp:{id} gates exactly as it always did; only per-tool
            // granularity waits.
            var overlay = await grains.GetGrain<IUserMcpConfigGrain>(userId).GetAsync();
            var mcpDescriptors = overlay.Servers
                .Where(s => s.Enabled)
                .SelectMany(s => new[] { new ToolDescriptor(ToolCatalog.McpServerKey(s.Id), s.Name, RequiresHostedTools: false) }
                    .Concat((s.KnownToolNames ?? []).Select(name => new ToolDescriptor(
                        ToolCatalog.McpToolKey(s.Id, name),
                        // The tool name VERBATIM, the ADR 0048 per-skill precedent. addCheck writes it with
                        // textContent, so there is no injection concern in rendering a server-supplied string.
                        name,
                        RequiresHostedTools: false,
                        ParentKey: ToolCatalog.McpServerKey(s.Id)))));
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
