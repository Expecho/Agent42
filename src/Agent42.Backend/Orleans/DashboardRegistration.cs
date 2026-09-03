using Orleans.Dashboard;

namespace Agent42.Backend;

/// <summary>
/// Mounts the official Orleans dashboard at <c>/dashboard</c> (ADR 0062) — the one startup decision
/// the Orleans feature owns, lifted out of <c>Program.cs</c> in the
/// <see cref="HindsightRegistration"/> / <c>DocumentIndexRegistration</c> shape (ADR 0040: a feature's
/// registration belongs in that feature's folder).
///
/// <para><b>Split deliberately into two members.</b> The gate half is installable on a plain
/// <see cref="IApplicationBuilder"/> and is therefore executable by a test with no silo running, which
/// is what lets the security-relevant wiring be covered rather than merely asserted as source text.
/// The map half cannot run without a silo and is kept to a single line for exactly that reason.</para>
///
/// <para><b>The two are only ever called together.</b> Mapping the dashboard without installing the
/// gate compiles cleanly and ships a cluster console to every Easy-Auth-allowlisted user;
/// <c>ProjectLayoutTests.Program_GatesTheOrleansDashboardBeforeServingIt</c> is what stops that, since
/// no test executes <c>Program.cs</c>.</para>
/// </summary>
public static class DashboardRegistration
{
    /// <summary>
    /// Installs the owner gate over everything under <see cref="DashboardOwnerGate.RoutePrefix"/>.
    ///
    /// <para><b>Middleware over the path prefix, not an endpoint filter.</b> The dashboard's routes —
    /// including its 3.5 MB <c>index.min.js</c> — are mapped by the package, so a convention applied to
    /// the builder <see cref="MapOrleansDashboardUi"/> returns would depend on the package propagating
    /// it to every one of them. A security control must not rest on a third party's route-building
    /// details. <c>UseWhen</c> + <c>StartsWithSegments</c> covers the prefix whatever the package maps
    /// under it, is segment-safe (so <c>/dashboardish</c> is not caught), and is
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> by default (so <c>/Dashboard</c> is).</para>
    ///
    /// <para><b>Call this BEFORE <c>UseDefaultFiles</c>/<c>UseStaticFiles</c>.</b> Installed after them
    /// it still works today only because no <c>wwwroot/dashboard/</c> exists — anyone later dropping a
    /// file there would have it served ungated while the gate still looked installed. The ordering is
    /// asserted, not merely documented.</para>
    /// </summary>
    public static void UseOrleansDashboardOwnerGate(this IApplicationBuilder app)
        => app.UseWhen(
            http => http.Request.Path.StartsWithSegments(DashboardOwnerGate.RoutePrefix),
            gated => gated.Use(DashboardOwnerGate.InvokeAsync));

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
        => endpoints.MapOrleansDashboard(routePrefix: DashboardOwnerGate.RoutePrefix);
}
