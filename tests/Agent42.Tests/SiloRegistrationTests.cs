using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The shared silo registration (ADR 0105): it supplies the host-invariant services and refuses to start a host that
/// left a grain dependency unregistered, naming every one.
/// </summary>
public sealed class SiloRegistrationTests
{
    // The ports a host supplies itself: every direct grain dependency except the two AddAgent42Silo owns
    // (IUserSkillProvider, IDocumentIngestor). Written out rather than read from RequiredGrainServices, so a new
    // grain dependency makes this list visibly incomplete instead of silently following along.
    private static readonly Type[] Ports =
    [
        typeof(IUserChatClientFactoryProvider), typeof(IUserMcpToolProvider), typeof(IAttachmentStore),
        typeof(IDocumentIndex), typeof(IChatSearchIndex), typeof(IWebSearch), typeof(IWebPageFetcher),
        typeof(IWebDocumentFetcher), typeof(IGeocoder), typeof(HindsightIntegration),
        typeof(IPushNotificationSender), typeof(IAgentWebhookSender),
    ];

    private static ServiceCollection WithPorts(params Type[] except)
    {
        var services = new ServiceCollection();
        foreach (var port in Ports.Except(except))
        {
            services.AddSingleton(port, _ => throw new NotSupportedException("never resolved"));
        }

        return services;
    }

    [Fact]
    public void AddAgent42Silo_MissingPorts_ThrowsListingAll()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMcpConnectionFactory>(_ => throw new NotSupportedException());

        var error = Assert.Throws<InvalidOperationException>(() => services.AddAgent42Silo());

        Assert.Contains("IAttachmentStore (needed by AgentGrain)", error.Message, StringComparison.Ordinal);
        Assert.Contains("IPushNotificationSender (needed by AgentGrain)", error.Message, StringComparison.Ordinal);
        Assert.Contains("IAgentWebhookSender (needed by AgentWebhookGrain)", error.Message, StringComparison.Ordinal);
        // Every missing one, not only the first.
        foreach (var port in Ports)
        {
            Assert.Contains(port.Name, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AddAgent42Silo_OneMissingPort_NamesOnlyThatOne()
    {
        var error = Assert.Throws<InvalidOperationException>(() => WithPorts(typeof(IGeocoder)).AddAgent42Silo());

        Assert.EndsWith("IGeocoder (needed by AgentGrain).", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredGrainServices_ReflectsCtors_ExcludesOptionalAndFramework()
    {
        var required = Agent42SiloRegistration.RequiredGrainServices().Select(pair => pair.Service).ToList();

        Assert.Contains(typeof(HindsightIntegration), required);
        Assert.Contains(typeof(IUserChatClientFactoryProvider), required);
        Assert.Contains(typeof(IAgentWebhookSender), required);
        Assert.DoesNotContain(typeof(TimeProvider), required);
        Assert.DoesNotContain(typeof(ClientToolTimeouts), required);
        Assert.DoesNotContain(required, type => type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(IPersistentState<>)
                || type.GetGenericTypeDefinition() == typeof(ILogger<>)));
        // The host's ports plus the two invariants the method supplies: nothing else, so the Ports list above
        // cannot fall behind a new grain dependency.
        Assert.Equal(
            Ports.Append(typeof(IUserSkillProvider)).Append(typeof(IDocumentIngestor)).OrderBy(t => t.Name),
            required.Distinct().OrderBy(t => t.Name));
    }

    [Fact]
    public void AddAgent42Silo_WithEveryPort_SuppliesTheInvariants()
    {
        var services = WithPorts().AddAgent42Silo();

        foreach (var invariant in new[]
        {
            typeof(IUserSkillProvider), typeof(IDocumentTextExtractor), typeof(DocumentChunker),
            typeof(DocumentIngestionService), typeof(IDocumentIngestor),
        })
        {
            Assert.Single(services, d => d.ServiceType == invariant);
        }
    }

    [Fact]
    public void AddAgent42Silo_KeepsPreRegisteredInvariant()
    {
        var services = WithPorts();
        Func<IServiceProvider, object> own = _ => throw new NotSupportedException();
        services.AddSingleton(typeof(IDocumentIngestor), own);

        services.AddAgent42Silo();

        Assert.Same(own, Assert.Single(services, d => d.ServiceType == typeof(IDocumentIngestor)).ImplementationFactory);
    }

    [Fact]
    public void AddAgent42Api_TryAddsRouteMatcherInvariants()
    {
        var ephemeral = new ProviderSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton<IProviderSecretProtector>(ephemeral);

        services.AddAgent42Api();

        Assert.Same(ephemeral, Assert.Single(services, d => d.ServiceType == typeof(IProviderSecretProtector)).ImplementationInstance);
        Assert.Single(services, d => d.ServiceType == typeof(IMcpSecretProtector));
        Assert.Single(services, d => d.ServiceType == typeof(McpOAuthClient));

        // Supplied when absent, and resolvable on a Data Protection ring.
        var fresh = new ServiceCollection().AddLogging();
        fresh.AddDataProtection();
        using var provider = fresh.AddAgent42Api().BuildServiceProvider();
        Assert.IsType<ProviderSecretProtector>(provider.GetRequiredService<IProviderSecretProtector>());
        Assert.IsType<McpSecretProtector>(provider.GetRequiredService<IMcpSecretProtector>());
        Assert.NotNull(provider.GetRequiredService<McpOAuthClient>());
    }

    [Fact]
    public void AddAgent42Silo_CalledTwice_RegistersEachInvariantOnce()
    {
        var services = WithPorts().AddAgent42Silo();
        var count = services.Count;

        services.AddAgent42Silo();

        Assert.Equal(count, services.Count);
    }
}
