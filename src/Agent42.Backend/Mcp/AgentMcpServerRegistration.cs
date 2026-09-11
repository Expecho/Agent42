using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Wires the server-side MCP surface at <c>/api/agent-mcp</c> (ADR 0070) — the one startup decision this
/// feature owns, lifted out of <c>Program.cs</c> in the <see cref="DashboardRegistration"/> /
/// <c>DocumentIndexRegistration</c> shape (ADR 0040: a feature's registration belongs in its folder).
///
/// <para><b>Deliberately NOT an <c>AgentApi.*</c> partial.</b> Ten call sites invoke
/// <c>MapAgentApi</c> — two hosts and eight test fixtures — and minimal API infers every mapped
/// endpoint's parameters up front, so a dependency one host has not registered breaks routing for
/// <i>every</i> endpoint in that host. This repo has been bitten by that twice (the
/// <c>IMcpSecretProtector</c> and <c>PushConfig</c> entries in <c>gotchas.md</c>), and this surface would
/// be a third: <c>MapMcp</c> throws outright without <see cref="AddAgentMcpServer"/> in DI. ADR 0062 set
/// the precedent — the Orleans dashboard maps its own routes from the composition root — and a host that
/// does not want the surface simply does not call these. Consequently
/// <c>ProjectLayoutTests.AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot</c> does not apply here
/// and must not be extended to cover it.</para>
///
/// <para><b>Split into three members</b> for the reason <see cref="DashboardRegistration"/> gives: the
/// gate half must be installable and executable by a test, because it is the security surface.</para>
/// </summary>
public static class AgentMcpServerRegistration
{
    /// <summary>
    /// Registers the MCP server, its HTTP transport and the single tool type.
    ///
    /// <para><c>WithHttpTransport()</c> is <b>mandatory</b>, not decoration: measured on
    /// <c>ModelContextProtocol.AspNetCore</c> 2.2.0, <c>AddMcpServer()</c> alone makes <c>MapMcp</c> throw
    /// <c>InvalidOperationException</c> at startup. It leaves the transport in its default
    /// <c>SessionMode.Stateless</c>, which is what this surface wants — a caller's whole interaction is
    /// one <c>ask_agent</c> call, all durable state lives in the grain, and stateless avoids a
    /// server-side session table with a two-hour idle timeout.</para>
    ///
    /// <para><c>AddHttpContextAccessor()</c> is how the tool method reaches the caller the gate resolved.
    /// It works because stateless mode leaves <c>PerSessionExecutionContext</c> off, so the tool runs on
    /// the request's own execution context.</para>
    /// </summary>
    public static void AddAgentMcpServer(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer().WithHttpTransport().WithTools<AgentMcpTools>();
    }

    /// <summary>
    /// Installs the API-key gate over everything under <see cref="AgentMcpLimits.RoutePrefix"/>.
    ///
    /// <para><b>Middleware over the path prefix, not a check inside the tool</b> — see
    /// <see cref="AgentMcpKeyGate"/> for why. <c>UseWhen</c> + <c>StartsWithSegments</c> covers the prefix
    /// whatever the SDK maps under it, is segment-safe (so <c>/api/agent-mcpzzz</c> is not caught), and is
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> by default.</para>
    ///
    /// <para><b>Call this BEFORE <c>UseDefaultFiles</c>/<c>UseStaticFiles</c></b>, for the reason ADR 0062
    /// gives: installed below them it works only while no <c>wwwroot/api/agent-mcp/</c> exists, and anyone
    /// later adding one would have it served ungated while the gate still looked installed.</para>
    /// </summary>
    public static void UseAgentMcpKeyGate(this IApplicationBuilder app)
        => app.UseWhen(
            http => http.Request.Path.StartsWithSegments(AgentMcpLimits.RoutePrefix),
            gated => gated.Use(AgentMcpKeyGate.InvokeAsync));

    /// <summary>
    /// Maps the JSON-RPC surface.
    /// </summary>
    /// <remarks>
    /// Measured on 2.2.0: this registers exactly <b>one</b> endpoint, <c>POST /api/agent-mcp/</c>, which
    /// serves <c>initialize</c>, <c>tools/list</c> and <c>tools/call</c> alike and matches both the bare
    /// prefix and its trailing-slash form. Both of those forms therefore need their own
    /// <c>excludedPaths</c> entry in <c>container-app.bicep</c>, because ADR 0042 measured a bare entry as
    /// matching <i>exactly</i>.
    /// </remarks>
    public static void MapAgentMcpServer(this IEndpointRouteBuilder endpoints)
        => endpoints.MapMcp(AgentMcpLimits.RoutePrefix);
}
