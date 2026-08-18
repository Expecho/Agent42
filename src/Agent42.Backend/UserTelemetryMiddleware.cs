using Orleans.Runtime;
using Agent42.Agents;

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
        RequestContext.Set(
            UserTelemetryContext.RequestContextKey, context.Request.Headers.GetUserTelemetryContext());
        await next(context);
    }
}
