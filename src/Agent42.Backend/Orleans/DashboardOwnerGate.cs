namespace Agent42.Backend;

/// <summary>
/// The authorization rule for the Orleans dashboard at <c>/dashboard</c> (ADR 0062), and the
/// middleware that enforces it.
///
/// <para>Easy Auth already gates the path — it is absent from <c>excludedPaths</c>, which is
/// default-deny (ADR 0002/0042), and <c>EasyAuthExclusionTests</c> pins that. This adds a second,
/// narrower rule on top: of the users Easy Auth lets in, exactly <b>one</b> reaches the dashboard.
/// The point is to stop it becoming a third cross-user read surface beside <c>/logs</c> (ADR 0054)
/// and <c>/usage</c> (ADR 0031).</para>
///
/// <para><b>One rule, no environment branching.</b> DevHost and the AppHost configure
/// <see cref="OwnerOidConfigKey"/> as <c>local-dev</c> — precisely what <c>CurrentUser.GetUserId</c>
/// falls back to when no Easy Auth principal header is present — so offline is open through the
/// <em>same</em> rule that locks production down. There is deliberately no <c>IsDevelopment()</c>
/// check anywhere: an environment branch is a second code path that only ever executes where you
/// cannot test it.</para>
///
/// <para>Deliberately free of DI and of <see cref="HttpContext"/> in <see cref="IsOwner"/>: the rule
/// is the security surface, so it is unit-testable directly rather than only through a host — the
/// <c>SearxngProxy</c> shape.</para>
/// </summary>
public static class DashboardOwnerGate
{
    /// <summary>The path the dashboard is served under. Also the prefix the gate covers.</summary>
    /// <remarks>
    /// The service worker bypasses this prefix (<c>sw.js</c>) for the same reason it bypasses
    /// <c>/searxng</c>: it is server-rendered, not part of the cached shell.
    /// </remarks>
    public const string RoutePrefix = "/dashboard";

    /// <summary>Configuration key holding the Entra object id of the single permitted user.</summary>
    public const string OwnerOidConfigKey = "Dashboard:OwnerOid";

    /// <summary>
    /// Whether <paramref name="requestUserId"/> is the configured dashboard owner.
    ///
    /// <para><b>Fails closed.</b> A blank, whitespace or absent
    /// <paramref name="configuredOwnerOid"/> denies <em>everyone</em>. A missing config value must
    /// never widen access, and this is the property that makes shipping the key empty safe.</para>
    ///
    /// <para><b>Compared <see cref="StringComparison.OrdinalIgnoreCase"/>, not Ordinal.</b> An Entra
    /// object id is a GUID, and a GUID pasted in different casing is the same principal. An ordinal
    /// compare would fail closed on a casing difference and 404 the dashboard for its own owner —
    /// indistinguishable from the feature being broken.</para>
    /// </summary>
    public static bool IsOwner(string? configuredOwnerOid, string requestUserId)
        => !string.IsNullOrWhiteSpace(configuredOwnerOid)
            && string.Equals(configuredOwnerOid, requestUserId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Denies a non-owner with <b>404, not 403</b> — a 403 confirms the page exists, and there is
    /// nothing to gain by telling an authenticated non-owner that a cluster dashboard is mounted here.
    ///
    /// <para>Kept as a named delegate (not an inline lambda) so a test can drive it with a real
    /// request through a host, the <see cref="UserTelemetryMiddleware"/> shape.</para>
    /// </summary>
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var configuredOwnerOid = context.RequestServices
            .GetRequiredService<IConfiguration>()[OwnerOidConfigKey];

        if (!IsOwner(configuredOwnerOid, context.GetUserId()))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}
