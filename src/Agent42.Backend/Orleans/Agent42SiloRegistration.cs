using System.Reflection;
using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent42.Backend;

/// <summary>
/// The grain-side registration every silo host shares: Backend, DevHost and every test silo (ADR 0105).
///
/// <para>The hosts share service TYPES, not implementations. Backend registers a Blob attachment store,
/// DevHost an in-memory one, and a test silo a recording fake it reads back. So this method cannot choose the
/// implementations that vary. It owns the few that are identical everywhere (registered with <c>TryAdd</c>, so a
/// host's own earlier registration wins), and it <b>verifies</b> the rest: every direct constructor dependency of
/// every grain must already be registered, or startup throws naming each missing type and the grain that needs
/// it. Before this, a host that missed one booted fine and failed at the grain's first activation.</para>
///
/// <para>The check is shallow on purpose: it reads the grain constructors' direct parameters, not what those
/// services need in turn. A transitive dependency (<c>UserSkillProvider</c>→<c>ISkillPackageStore</c>) still fails
/// at first resolve.</para>
/// </summary>
public static class Agent42SiloRegistration
{
    private static readonly Lazy<IReadOnlyList<(Type Service, Type Grain)>> Required = new(ReflectRequiredGrainServices);

    /// <summary>
    /// Registers the host-invariant grain-side services, then validates that the host registered every grain
    /// dependency (its "ports"). Call it AFTER the ports. Idempotent, so a layered test configurator that wraps a
    /// leaf one is safe.
    /// </summary>
    public static IServiceCollection AddAgent42Silo(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(Agent42SiloMarker)))
        {
            return services;
        }

        services.AddSingleton<Agent42SiloMarker>();

        // Uploaded agent skills (ADR 0049): IUserSkillProvider is an AgentGrain constructor dependency, so it is
        // mandatory. Its ISkillPackageStore is a port: Blob in production, in memory elsewhere.
        services.TryAddSingleton<IUserSkillProvider, UserSkillProvider>();

        // Document RAG (ADR 0007/0018): the extractor, chunker and ingestion service are Azure-free and identical in
        // every host. There is no operator embedding generator; ingestion resolves the user's from their chat-client
        // factory. The index, the chat search index and the generator are ports.
        services.TryAddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
        services.TryAddSingleton<DocumentChunker>();
        services.TryAddSingleton<DocumentIngestionService>();

        // read_web_document (ADR 0066): the ingestion port the tool reaches the pipeline above through. Only the
        // FETCHER varies per host, because only it opens a socket.
        services.TryAddSingleton<IDocumentIngestor, WebDocumentIngestor>();

        ThrowIfPortsMissing(services);
        return services;
    }

    /// <summary>
    /// Every service a grain's constructor requires: each non-optional parameter of each concrete <see cref="Grain"/>
    /// in the Agents assembly, minus what Orleans and logging supply (<see cref="IPersistentState{TState}"/>,
    /// <see cref="ILogger{TCategoryName}"/>, <see cref="ILoggerFactory"/>).
    ///
    /// <para>Public only because this repo has no <c>InternalsVisibleTo</c>; the tests read it.</para>
    /// </summary>
    public static IReadOnlyList<(Type Service, Type Grain)> RequiredGrainServices() => Required.Value;

    private static IReadOnlyList<(Type Service, Type Grain)> ReflectRequiredGrainServices() =>
    [
        .. typeof(AgentGrain).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(Grain)))
            .SelectMany(grain => grain.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(ctor => ctor.GetParameters())
                .Where(parameter => !parameter.HasDefaultValue && !IsFrameworkSupplied(parameter.ParameterType))
                .Select(parameter => (parameter.ParameterType, grain)))
            .Distinct()
            .OrderBy(pair => pair.ParameterType.Name, StringComparer.Ordinal),
    ];

    private static bool IsFrameworkSupplied(Type type) =>
        type == typeof(ILoggerFactory)
        || (type.IsGenericType && type.GetGenericTypeDefinition() is var open
            && (open == typeof(IPersistentState<>) || open == typeof(ILogger<>)));

    private static void ThrowIfPortsMissing(IServiceCollection services)
    {
        var registered = services.Select(d => d.ServiceType).ToHashSet();
        var missing = RequiredGrainServices()
            .Where(pair => !registered.Contains(pair.Service))
            .Select(pair => $"{pair.Service.Name} (needed by {pair.Grain.Name})")
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "This silo host does not register every grain dependency. Register each of these before calling "
                + $"{nameof(AddAgent42Silo)}(): {string.Join(", ", missing)}.");
        }
    }

    // Present once AddAgent42Silo has run, so a second call is a no-op.
    private sealed class Agent42SiloMarker;
}
