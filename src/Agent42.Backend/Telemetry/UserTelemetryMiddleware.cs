using Agent42.Agents;
using Orleans.Runtime;

namespace Agent42.Backend;

/// <summary>
/// Request-scoped middleware that extracts the caller's identity from the Easy Auth headers once and
/// publishes it into Orleans <see cref="RequestContext"/>, from where the
/// <see cref="UserTelemetrySpanProcessor"/> stamps it onto downstream and in-grain spans (ADR 0010).
/// Registered first in the pipeline. Kept as a named delegate (not an inline lambda) so a test can
/// invoke it with a <c>DefaultHttpContext</c> and assert the carrier lands in <c>RequestContext</c>.
/// </summary>
public static class UserTelemetryMiddleware
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // The ADR 0074 alias must reach this carrier too, not only the server span: the carrier is what
        // stamps the IN-GRAIN spans, so aliasing one and not the other would split user.id across a
        // single trace. Resolved from request services here (unlike the Program.cs enrichment callback,
        // which runs before the pipeline) because this middleware has a real HttpContext — but
        // NULL-CONDITIONALLY: RequestServices is null on a bare DefaultHttpContext, where GetService
        // THROWS rather than returning null. A real request always has it (the framework's own
        // startup filter guarantees that), so this is for the unit-test path — but telemetry
        // enrichment is the last thing that should be able to fail a request.
        RequestContext.Set(
            UserTelemetryContext.RequestContextKey,
            context.Request.Headers.GetUserTelemetryContext(
                context.RequestServices?.GetService<UserIdAliases>()));
        await next(context);
    }
}
