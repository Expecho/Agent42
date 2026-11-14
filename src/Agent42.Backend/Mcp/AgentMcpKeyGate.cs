using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The credential rule for the server-side MCP surface at <c>/api/agent-mcp</c> (ADR 0070), and the
/// middleware that enforces it.
///
/// <para><b>Middleware over the path prefix, not a check inside the tool.</b> <c>MapMcp</c> serves the
/// whole JSON-RPC surface from one endpoint — <c>initialize</c>, <c>tools/list</c> and <c>tools/call</c>
/// all arrive as POSTs to the same route — and the SDK enforces nothing of its own. A check living only
/// inside the tool method would leave <c>initialize</c> and <c>tools/list</c> anonymously reachable the
/// moment the <c>excludedPaths</c> entries land, which is exactly what the "an Easy-Auth
/// <c>excludedPaths</c> endpoint is publicly reachable" gotcha forbids. This is the same problem ADR 0062
/// solved for the Orleans dashboard's package-owned routes, and <see cref="OwnerGate"/> is the
/// shape copied here.</para>
///
/// <para><b>Failures are uniform.</b> Missing key, unknown key, deleted agent and flag-off all answer
/// with one identical 401 body. A caller holding no valid key must not be able to tell whether an agent
/// exists, nor whether an agent they once held a key for has merely been un-exposed — the same rule
/// <c>ResolveAgentRunCallerAsync</c> follows, and for the same reason: on an excluded path there is no
/// Easy Auth principal to fall back on.</para>
///
/// <para><b>The gate is where the owner is resolved, and it publishes the result.</b> The tool method
/// reads it back off <see cref="HttpContext.Items"/> rather than resolving the key a second time, so
/// there is exactly one place the credential is turned into an identity. Measured on
/// <c>ModelContextProtocol.AspNetCore</c> 2.2.0: in the default stateless mode a tool method reaches the
/// live <see cref="HttpContext"/> through <c>IHttpContextAccessor</c>, which is what makes that handoff
/// possible at all.</para>
/// </summary>
public static class AgentMcpKeyGate
{
    /// <summary>
    /// The key under which the gate publishes the resolved caller for the tool method to read.
    /// </summary>
    public const string CallerItemKey = "agent42.mcp.caller";

    /// <summary>
    /// The single body every rejection returns. Deliberately says nothing about which check failed.
    /// </summary>
    public const string RejectionBody = "Missing or invalid X-Api-Key.";

    /// <summary>
    /// Resolves the request's <c>X-Api-Key</c> to an exposed agent, or <c>null</c> when it does not
    /// authenticate one.
    ///
    /// <para><b>Fails closed at every step</b>: a blank header, a hash with no directory entry, a
    /// directory entry whose agent has since been deleted, and an agent whose
    /// <see cref="AgentDefinition.McpExposed"/> is false all return <c>null</c>. The last of those is the
    /// opt-in itself — without it, every agent that has ever been given an ADR 0022 key would be callable
    /// here, and that key was minted under a write-only contract.</para>
    /// </summary>
    public static async Task<AgentMcpCaller?> ResolveAsync(string? apiKey, IGrainFactory grains)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var owner = await grains.GetGrain<IAgentKeyDirectoryGrain>(ApiKey.Hash(apiKey)).ResolveAsync();
        if (owner is null)
        {
            return null;
        }

        var definition = await grains.GetGrain<IAgentRegistryGrain>(owner.UserId).GetAsync(owner.AgentId);
        return definition is { McpExposed: true } ? new AgentMcpCaller(owner.UserId, definition) : null;
    }

    /// <summary>
    /// Rejects an unauthenticated request with 401 before it reaches the protocol surface, and stashes
    /// the resolved caller on <see cref="HttpContext.Items"/> for the tool method otherwise.
    /// </summary>
    /// <remarks>
    /// A named delegate rather than an inline lambda, so a test can drive it through a real host — the
    /// <see cref="OwnerGate.InvokeAsync"/> shape.
    /// </remarks>
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var grains = context.RequestServices.GetRequiredService<IGrainFactory>();
        var caller = await ResolveAsync(context.Request.Headers["X-Api-Key"].ToString(), grains);

        if (caller is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(RejectionBody);
            return;
        }

        context.Items[CallerItemKey] = caller;
        await next(context);
    }
}

/// <summary>
/// An authenticated MCP caller: the owning user and the exposed agent their key authenticates.
/// Never crosses a grain boundary — it is resolved in the gate and read in the tool method within one
/// request — so it is a plain record with no Orleans attributes.
/// </summary>
public sealed record AgentMcpCaller(string UserId, AgentDefinition Agent);
