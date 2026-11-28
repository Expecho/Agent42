using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent42.Backend;

/// <summary>
/// Registers the resumable chat turn (ADR 0153): its options, the stores, the replica identity and the retention sweeper.
/// The blob port is registered per host (ADR 0105): production takes the Azure adapter with <c>TryAdd</c>, so the file
/// adapter that <c>AddFileStores</c> registered first (self-host) wins, and DevHost takes the in-memory twin. Lifted out of
/// <c>Program.cs</c> the way <see cref="FleetRegistration"/> was (ADR 0050, rule 9).
/// </summary>
public static class ChatTurnRegistration
{
    /// <summary>Production and self-host: the durable store in the <c>chat-turns</c> Blob container, unless a host registered its own port first.</summary>
    public static void AddChatTurns(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddChatTurnsCore(configuration);
        services.TryAddSingleton<IChatTurnBlobs, AzureChatTurnBlobs>();
    }

    /// <summary>DevHost and tests: the in-memory store, so a reattach works offline and is gone on restart.</summary>
    public static void AddChatTurnsInMemory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddChatTurnsCore(configuration);
        services.TryAddSingleton<IChatTurnBlobs>(sp => new InMemoryChatTurnBlobs(sp.GetRequiredService<TimeProvider>()));
    }

    private static void AddChatTurnsCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(configuration.GetSection(ChatTurnOptions.SectionName).Get<ChatTurnOptions>() ?? new ChatTurnOptions());
        services.AddSingleton<IReplicaIdentity, EnvironmentReplicaIdentity>();
        services.AddSingleton<ChatTurnStores>();
        services.AddSingleton<ChatTurnRegistry>();
        services.AddSingleton<ChatTurnTail>();
        services.AddSingleton<ChatTurnRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<ChatTurnRunner>());
        services.AddHostedService<ChatTurnSweeper>();
    }
}
