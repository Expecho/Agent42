using System.Diagnostics;
using Agent42.Agents;
using OpenTelemetry;
using Orleans.Runtime;
using Agent42.Agents.Telemetry;

namespace Agent42.Backend;

/// <summary>
/// Stamps the <c>user.*</c> identity attributes onto every span started while a request identity is
/// present in Orleans <see cref="RequestContext"/> (ADR 0010). OTel attributes are per-span and are
/// not inherited by child spans, so the server-span enrichment (ADR 0009) does not reach downstream
/// spans — most importantly the Azure OpenAI chat-completion span created <b>inside</b> the Orleans
/// grain. A request-scoped middleware writes the identity into <c>RequestContext</c>; because backend
/// and silo are co-hosted on one <c>TracerProvider</c> and <c>RequestContext</c> propagates across the
/// grain call, this single processor observes both in-request and in-grain spans and stamps them from
/// the same ambient identity.
///
/// No-op when no identity is set (startup/background/reminder spans), so it never invents attribution.
/// It does not stamp the ASP.NET Core server span: that Activity starts before user middleware runs,
/// so its <see cref="OnStart"/> fires before <c>RequestContext</c> is set — the server span stays owned
/// by the PR #24 <c>EnrichWithHttpRequest</c> callback (no double-stamping; different spans).
/// </summary>
public sealed class UserTelemetrySpanProcessor : BaseProcessor<Activity>
{
    public override void OnStart(Activity activity)
    {
        // Usage-dashboard "application" dimension (ADR 0031): the grain publishes the agent name into
        // RequestContext for the turn; stamp it onto the in-grain chat/tool spans so the usage projector
        // can read it. Same ambient-RequestContext-at-OnStart mechanism as the user.* identity below —
        // a regular (non-agent) chat leaves it unset, and the projector falls back to the service constant.
        if (RequestContext.Get(UsageSpanProjector.ApplicationContextKey) is string application)
        {
            activity.SetTag(UsageSpanProjector.ApplicationTag, application);
        }

        if (RequestContext.Get(UserTelemetryContext.RequestContextKey) is not UserTelemetryContext identity)
        {
            return;
        }

        foreach (var tag in identity.ToTags())
        {
            activity.SetTag(tag.Key, tag.Value);
        }
    }
}
