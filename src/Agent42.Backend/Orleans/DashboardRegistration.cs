using Orleans.Dashboard;

namespace Agent42.Backend;

/// <summary>
/// Mounts the official Orleans dashboard at <c>/dashboard</c> (ADR 0062) — the one startup decision
/// the Orleans feature owns, lifted out of <c>Program.cs</c> in the
/// <see cref="HindsightRegistration"/> / <c>DocumentIndexRegistration</c> shape (ADR 0040: a feature's
/// registration belongs in that feature's folder).
///
/// <para>The map half cannot run without a silo and is kept to a single line for exactly that reason.</para>
///
/// <para><b>The gate is installed separately, by <c>app.UseOwnerGate()</c> (ADR 0121).</b> Mapping the
/// dashboard without it compiles cleanly and ships a cluster console to every signed-in user;
/// <c>ProjectLayoutTests.Program_GatesTheOrleansDashboardBeforeServingIt</c> is what stops that, since
/// no test executes <c>Program.cs</c>.</para>
/// </summary>
public static class DashboardRegistration
{
    /// <summary>The path the dashboard is served under. The owner gate (<see cref="OwnerGate"/>) covers it.</summary>
    /// <remarks>
    /// The service worker bypasses this prefix (<c>sw.js</c>) for the same reason it bypasses
    /// <c>/searxng</c>: it is server-rendered, not part of the cached shell.
    /// </remarks>
    public const string RoutePrefix = "/dashboard";

    /// <summary>
    /// Maps the dashboard's own endpoints. Needs a silo (the package reads cluster state), so this line
    /// is the one part of the feature no test executes — kept to a single call for that reason.
    /// </summary>
    /// <remarks>
    /// The package emits markup that references its assets <em>relatively</em> (<c>./index.min.js</c>),
    /// so <c>GET /dashboard</c> 301s to <c>/dashboard/</c> before they resolve. The package emits that
    /// redirect itself; no redirect handling is ours to write. Measured: nothing is served at the root —
    /// <c>/index.min.js</c> is a 404.
    /// </remarks>
    public static void MapOrleansDashboardUi(this IEndpointRouteBuilder endpoints)
        => endpoints.MapOrleansDashboard(routePrefix: RoutePrefix);
}
