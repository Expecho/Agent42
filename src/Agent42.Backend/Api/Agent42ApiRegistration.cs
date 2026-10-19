using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent42.Backend;

/// <summary>
/// The endpoint-side registration every <c>MapAgentApi</c> host shares (ADR 0105): the services whose implementation
/// is identical in Backend and DevHost, and which a handler takes as a PARAMETER, so a host missing one breaks route
/// matching for every endpoint it maps (the route-matcher entry in <c>gotchas.md</c>).
///
/// <para>Separate from <see cref="Agent42SiloRegistration.AddAgent42Silo"/> because a test's API host and its silo
/// are separate containers; production and DevHost run both in one and call both. Everything here is <c>TryAdd</c>,
/// so a test host that registered its own (an ephemeral key ring) keeps it.</para>
/// </summary>
public static class Agent42ApiRegistration
{
    /// <summary>Registers the host-invariant endpoint dependencies. Needs <c>AddDataProtection()</c> in the host.</summary>
    public static IServiceCollection AddAgent42Api(this IServiceCollection services)
    {
        // Provider secret encryption (ADR 0018): /api/providers encrypts keys before the grain, on the host's Data
        // Protection key ring (persisted to Blob in production, ephemeral elsewhere).
        services.TryAddSingleton<IProviderSecretProtector, ProviderSecretProtector>();

        // MCP secret encryption (config-driven remote MCP servers): the same key ring under a distinct protection
        // scope (IMcpSecretProtector's own purpose), so MCP-secret rotation is independent of model-provider keys.
        services.TryAddSingleton<IMcpSecretProtector, McpSecretProtector>();

        // Drives the interactive-OAuth HTTP exchanges (PRM discovery, code→token, refresh — Phase 3) over a plain
        // HttpClient (not DI-resolved, so no AddServiceDefaults resilience inheritance). /api/mcp/oauth/start takes it
        // as a handler parameter, and McpOAuthTokenResolver refreshes per request through it since ADR 0081.
        services.TryAddSingleton(_ => new McpOAuthClient(new HttpClient()));

        return services;
    }
}
