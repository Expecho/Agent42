using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent42.Backend;

/// <summary>
/// Registers the chat gateway (ADR 0111): the feature's own startup wiring, kept out of <c>Program.cs</c> for the
/// S3776 reason <see cref="HindsightRegistration"/> records. Called by Backend and DevHost (both pinned by
/// <c>ProjectLayoutTests.Hosts_RegisterChatGateway</c>, since nothing in the suite runs <c>Program.cs</c>).
/// </summary>
public static class ChatGatewayRegistration
{
    /// <summary>
    /// The supervisor (a hosted service), the turn pipeline, the streamer, the gateway-owned dedupe and session
    /// registry, the credential protector, and every platform adapter this build ships.
    ///
    /// <para>The lease is <c>TryAdd</c>ed as the Blob lease: a host that registers its own first (DevHost's no-op)
    /// keeps it. Everything here is a singleton, and the gateway holds its sessions in process, which is why CLAUDE.md
    /// rule 12's single replica is load-bearing for it.</para>
    /// </summary>
    public static IServiceCollection AddChatGateway(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IChatGatewaySecretProtector, ChatGatewaySecretProtector>();
        services.TryAddSingleton<IChatGatewayLease, BlobChatGatewayLease>();
        services.AddSingleton<InboundDeduper>();
        services.AddSingleton<ChatSessionRegistry>();
        services.AddSingleton(sp => new ChatReplyStreamer(
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ChatReplyStreamer>>()));
        services.AddSingleton<ChatTurnPipeline>();
        services.AddSingleton<IChatInboundSink>(sp => sp.GetRequiredService<ChatTurnPipeline>());
        services.AddSingleton(sp => new ChatGatewayService(
            sp.GetRequiredService<IGrainFactory>(),
            sp.GetServices<IChatPlatformAdapter>(),
            sp.GetRequiredService<IChatGatewaySecretProtector>(),
            sp.GetRequiredService<IChatInboundSink>(),
            sp.GetRequiredService<IChatGatewayLease>(),
            sp.GetRequiredService<ChatSessionRegistry>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ChatGatewayService>>()));
        services.AddSingleton<IChatGatewayControl>(sp => sp.GetRequiredService<ChatGatewayService>());
        services.AddHostedService(sp => sp.GetRequiredService<ChatGatewayService>());
        services.AddTelegramChat();
        return services;
    }

    /// <summary>Telegram (phase 1a): the hand-rolled Bot API on its two token-safe named clients, and the adapter.</summary>
    public static IServiceCollection AddTelegramChat(this IServiceCollection services)
    {
        services.TryAddSingleton(TelegramOptions.Default);
        TelegramBotApi.AddHttpClients(services);
        services.AddSingleton<TelegramBotApi>();
        services.AddSingleton<IChatPlatformAdapter, TelegramAdapter>();
        return services;
    }
}
