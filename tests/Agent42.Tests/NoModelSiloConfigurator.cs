using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// A per-user factory provider whose returned factory flips between "no chat provider configured" (an
/// empty <see cref="MultiProviderChatClientFactory"/> whose <c>DefaultModelId</c> is <c>""</c> and whose
/// <c>GetClient</c> hands out the no-op <c>NoModelChatClient</c>) and a working single fake factory
/// (ADR 0019). This is the factory-provider double AC5/6/7 need: the grain resolves its factory ONCE on
/// activation, so modelling "a user configured their first provider on an already-activated grain" needs a
/// provider whose answer can change between an activation and the next grain call — a fixed
/// <see cref="IChatClientFactory"/> cannot express it.
///
/// <para><see cref="HasModel"/> is flipped by the test between grain calls; the grain re-resolves
/// <c>GetForUserAsync</c> on its no-model path and picks up the change.</para>
/// </summary>
public sealed class SwitchableChatClientFactoryProvider : IUserChatClientFactoryProvider
{
    private readonly IChatClientFactory _empty = new MultiProviderChatClientFactory(
        new Dictionary<string, MultiProviderChatClientFactory.Provider>(),
        "", "test", NullLogger<MultiProviderChatClientFactory>.Instance);

    private readonly IChatClientFactory _withModel;

    public SwitchableChatClientFactoryProvider(IChatClient client) =>
        _withModel = new SingleChatClientFactory(client, TestSiloConfigurator.DefaultModel);

    /// <summary>When true, the user has a configured chat provider; when false, none (empty picker).</summary>
    public bool HasModel { get; set; }

    public Task<IChatClientFactory> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(HasModel ? _withModel : _empty);
}

/// <summary>
/// A silo whose <see cref="IUserChatClientFactoryProvider"/> is the switchable double above — starts with
/// NO chat provider configured (ADR 0019). Otherwise identical to <see cref="TestSiloConfigurator"/>. The
/// double is registered as its concrete type too, so a test can fetch it from the silo's container and flip
/// <see cref="SwitchableChatClientFactoryProvider.HasModel"/> between grain calls.
/// </summary>
public sealed class NoModelSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient, FakeChatClient>();
            services.AddSingleton<SwitchableChatClientFactoryProvider>(sp =>
                new SwitchableChatClientFactoryProvider(sp.GetRequiredService<IChatClient>()));
            services.AddSingleton<IUserChatClientFactoryProvider>(sp =>
                sp.GetRequiredService<SwitchableChatClientFactoryProvider>());
            services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider());
            // Recording sender (not the Null of the other three degraded silos): AC9 asserts a no-model
            // agent-run sends ZERO pushes, which is only observable with a recorder — and this is the one
            // silo whose provider can be flipped no-model → model to prove the gate suppressed the send.
            services.AddSingleton<RecordingPushNotificationSender>();
            services.AddSingleton<IPushNotificationSender>(sp => sp.GetRequiredService<RecordingPushNotificationSender>());
            services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
            services.AddDocumentRagFakes();
            services.AddWebToolFakes();
        });
    }
}
