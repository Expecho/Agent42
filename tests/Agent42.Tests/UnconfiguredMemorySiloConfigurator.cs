using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// A silo with long-term memory UNCONFIGURED — <c>HindsightIntegration(null)</c>, the state of any
/// deployment with no <c>Hindsight:ApiKey</c> (ADR 0056). Otherwise identical to
/// <see cref="TestSiloConfigurator"/>.
///
/// <para>It exists because the shared cluster deliberately has memory configured, and the property under
/// test is that the provider is never <em>attached</em>: no recall, no retain, no call of any kind. A
/// <see cref="RecordingHindsightClient"/> is still registered as its concrete type — unwrapped, so the
/// grain cannot reach it — precisely so a test can prove nothing arrived. That distinction is invisible in
/// <c>FakeChatClient.InstructionsEcho</c>, where "no provider attached" and "attached, recalled nothing"
/// both produce instructions with no <c>## Memories</c> block.</para>
/// </summary>
public sealed class UnconfiguredMemorySiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<FakeChatClient>();
            services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<FakeChatClient>());
            services.AddSingleton<IChatClientFactory>(sp =>
                new SingleChatClientFactory(sp.GetRequiredService<IChatClient>(), TestSiloConfigurator.DefaultModel));
            services.AddSingleton<IUserChatClientFactoryProvider>(sp =>
                new SingleUserChatClientFactoryProvider(sp.GetRequiredService<IChatClientFactory>()));
            services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider());
            services.AddSingleton<ISkillPackageStore, InMemorySkillPackageStore>();
            services.AddSingleton<IUserSkillProvider, UserSkillProvider>();
            services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
            // The recorder is registered so the test can read it, but it is NOT wrapped into the
            // HindsightIntegration — that null is the whole point of this silo.
            services.AddSingleton<RecordingHindsightClient>();
            services.AddSingleton(new HindsightIntegration(null));
            services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
            services.AddDocumentRagFakes();
            services.AddWebToolFakes();
        });
    }
}
