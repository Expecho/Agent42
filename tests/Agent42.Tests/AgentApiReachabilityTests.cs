using System.Reflection;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// Every <c>AgentApi.Map*Endpoints</c> part is reachable from <c>MapAgentApi</c>, checked at RUNTIME (ADR 0040).
///
/// <para>A part that exists but is never called compiles cleanly and silently deletes every endpoint in it. The
/// guard this replaces was a hand-kept list plus a source-text <c>"{method}(app);"</c> match: it had fallen four
/// parts behind, and a commented-out call would have passed it. Here the parts are found by reflection, each is
/// mapped ALONE into its own host, and its routes must all appear in a host that maps <c>MapAgentApi</c>. No part
/// uses <c>MapGroup</c>, so mapping one alone yields exactly its routes.</para>
/// </summary>
public sealed class AgentApiReachabilityTests
{
    // 24 at the time this test was written. A floor, not an equality: a new part raises the count on its own, and a
    // rename that defeated the reflection (so it found nothing) must not pass vacuously.
    private const int MinimumParts = 24;

    internal static IReadOnlyList<MethodInfo> MapMethods() =>
    [
        .. typeof(AgentApi).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Map", StringComparison.Ordinal)
                && m.Name.EndsWith("Endpoints", StringComparison.Ordinal)
                && m.ReturnType == typeof(void)
                && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(IEndpointRouteBuilder))
            .OrderBy(m => m.Name, StringComparer.Ordinal),
    ];

    [Fact]
    public void ReflectsAllEndpointParts()
    {
        Assert.True(MapMethods().Count >= MinimumParts,
            $"Found {MapMethods().Count} AgentApi.Map*Endpoints parts by reflection; expected at least {MinimumParts}.");
    }

    [Fact]
    public async Task EveryEndpointPart_IsReachableFromMapAgentApi()
    {
        var all = await RoutesAsync("MapAgentApi", app => app.MapAgentApi());

        var failures = new List<string>();
        foreach (var method in MapMethods())
        {
            var part = await RoutesAsync(method.Name, app => method.Invoke(null, [app]));
            if (part.Count == 0)
            {
                failures.Add($"{method.Name} maps no routes.");
                continue;
            }

            var missing = part.Except(all).ToList();
            if (missing.Count > 0)
            {
                failures.Add($"{method.Name} is not reachable from MapAgentApi; missing: {string.Join(", ", missing)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // One slim host per mapping, with the services every MapAgentApi host needs. Enumerating the data source forces
    // the endpoints to BUILD, which is where parameter inference throws; the failure names the part.
    private static async Task<IReadOnlySet<string>> RoutesAsync(string part, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(FakeProfileGrainFactory.Create(null).Factory);
        SharedApiFixture.AddRouteMatcherServices(builder.Services);
        await using var app = builder.Build();

        try
        {
            map(app);
            return ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                    .Select(verb => $"{verb} {endpoint.RoutePattern.RawText}"))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Mapping {part} failed: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}", ex);
        }
    }
}
