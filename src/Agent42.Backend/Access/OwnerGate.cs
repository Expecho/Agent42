namespace Agent42.Backend;

/// <summary>
/// The middleware that keeps owner-only surfaces owner-only (ADR 0062, generalised by ADR 0121): the Orleans
/// dashboard, the <c>/logs</c> console and its <c>/api/diagnostics/*</c> feed, and <c>/api/config-health</c>.
///
/// <para>Easy Auth already gates each path (none is in <c>excludedPaths</c>, which is default-deny, ADR
/// 0002/0042). This is the second, narrower rule on top: of the users Easy Auth lets in, only owners get
/// through. Advisory, not a boundary; see <see cref="IOwnerPolicy"/>.</para>
///
/// <para>Denies with <b>404, not 403</b>: a 403 confirms the surface exists. <b>Fails closed</b>: no
/// <see cref="IOwnerPolicy"/> registered denies everyone.</para>
/// </summary>
public static class OwnerGate
{
    /// <summary>The path prefixes only owners reach. Matched segment-wise and case-insensitively.</summary>
    public static readonly string[] OwnerOnlyPrefixes =
        [DashboardRegistration.RoutePrefix, "/logs", "/api/diagnostics", "/api/config-health"];

    /// <summary>
    /// Installs the gate over every <see cref="OwnerOnlyPrefixes"/> entry.
    ///
    /// <para><b>Call this BEFORE <c>UseDefaultFiles</c>/<c>UseStaticFiles</c></b> (the <c>/logs</c> page is a
    /// static file): installed after them, a gated path that is a file would be served ungated while the gate
    /// still looked installed. <c>ProjectLayoutTests</c> asserts the ordering.</para>
    /// </summary>
    public static void UseOwnerGate(this IApplicationBuilder app)
        => app.UseWhen(
            http => IsOwnerOnly(http.Request.Path),
            gated => gated.Use(InvokeAsync));

    /// <summary>Whether <paramref name="path"/> is under an owner-only prefix (segment-safe: <c>/logsfoo</c> is not).</summary>
    public static bool IsOwnerOnly(PathString path)
        => OwnerOnlyPrefixes.Any(prefix => path.StartsWithSegments(prefix));

    /// <summary>Named (not an inline lambda) so a test can drive it with a real request through a host.</summary>
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var policy = context.RequestServices.GetService<IOwnerPolicy>();

        if (policy is null || !policy.IsOwner(context.GetUserId()))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}
