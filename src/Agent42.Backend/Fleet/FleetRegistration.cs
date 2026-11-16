using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent42.Backend;

/// <summary>
/// Registers the agent fleet (ADR 0097): its options, its store, its tool catalog and the task service, which is also
/// a hosted service so a shutdown persists running tasks as interrupted. Also the chat's door to it (ADR 0115): the
/// <see cref="IFleetLauncher"/> port the grain needs, and the notifier that sends a result back to the conversation.
/// Both hosts call this BEFORE <c>AddAgent42Silo()</c>, which fails startup on a missing port. Lifted out of
/// <c>Program.cs</c> the way <see cref="HindsightRegistration"/> was (ADR 0050, rule 9).
///
/// <para>The endpoints resolve <see cref="FleetTaskService"/> optionally (the <c>/api/speech</c> pattern), so a host
/// without this answers 503 on <c>/api/fleet</c> instead of failing.</para>
/// </summary>
public static class FleetRegistration
{
    /// <summary>Production: the store in the <c>agent-fleet</c> Blob container, and the Copilot runtime host. Call it
    /// after <c>AddCopilotAgents</c>, which registers the <see cref="CopilotRuntimeOptions"/> the host reuses.</summary>
    public static void AddAgentFleet(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAgentFleetCore(FleetOptions.From(configuration));
        services.TryAddSingleton<IFleetBlobs, AzureFleetBlobs>();
        services.AddSingleton<FleetCopilotHost>();
    }

    /// <summary>DevHost and tests: an in-memory store and no Copilot runtime, so Copilot agents are switched off.</summary>
    public static void AddAgentFleetInMemory(this IServiceCollection services, IConfiguration configuration)
    {
        var options = FleetOptions.From(configuration);
        options.CopilotAgentsEnabled = false;
        services.AddAgentFleetCore(options);
        services.AddSingleton<IFleetBlobs, InMemoryFleetBlobs>();
    }

    private static void AddAgentFleetCore(this IServiceCollection services, FleetOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IFleetStore, FleetStore>();
        services.AddSingleton<FleetToolCatalog>();
        services.AddSingleton<IFleetCompletionNotifier, FleetConversationNotifier>();
        // The grain's port (ADR 0115). A host without the fleet registers DisabledFleetLauncher instead.
        services.AddSingleton<IFleetLauncher, FleetLauncher>();
        services.AddSingleton<FleetSettingsService>();
        services.AddSingleton<FleetTaskService>();
        services.AddSingleton<FleetLessonService>();
        services.AddHostedService(sp => sp.GetRequiredService<FleetTaskService>());
    }
}
