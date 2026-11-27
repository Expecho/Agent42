using System.Runtime.CompilerServices;
using Agent42.Backend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The full <c>MapAgentApi</c> route table (verb, pattern, endpoint metadata types) against a checked-in golden file.
/// <see cref="AgentApiReachabilityTests"/> only proves each <c>Map*Endpoints</c> part is called; this proves no route,
/// verb, auth requirement or filter was lost or added INSIDE a part, so a long map can be split without a behaviour
/// change. A deliberate route change regenerates the file: run with <c>AGENT42_UPDATE_ROUTE_GOLDEN=1</c>.
/// </summary>
public sealed class AgentApiRouteTableTests
{
    private static string GoldenPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "AgentApiRouteTable.golden.txt");

    [Fact]
    public async Task RouteTable_MatchesSnapshot()
    {
        var actual = await BuildTableAsync();

        if (Environment.GetEnvironmentVariable("AGENT42_UPDATE_ROUTE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(GoldenPath(), actual);
        }

        var expected = (await File.ReadAllTextAsync(GoldenPath())).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual.ReplaceLineEndings("\n"));
    }

    private static async Task<string> BuildTableAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(FakeProfileGrainFactory.Create(null).Factory);
        SharedApiFixture.AddRouteMatcherServices(builder.Services);
        await using var app = builder.Build();
        app.MapAgentApi();

        var lines = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var verbs = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];
                var metadata = string.Join(",", endpoint.Metadata
                    .Where(m => m is not System.Reflection.MethodInfo and not System.Runtime.CompilerServices.AsyncStateMachineAttribute and not System.Diagnostics.DebuggerStepThroughAttribute)
                    .Select(DescribeMetadata)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal));
                return verbs.Select(verb => $"{verb} {endpoint.RoutePattern.RawText} [{metadata}]");
            })
            .Order(StringComparer.Ordinal);
        return string.Join('\n', lines) + "\n";
    }

    private static string DescribeMetadata(object metadata) => metadata switch
    {
        IAuthorizeData auth => $"Authorize({auth.Policy};{auth.Roles})",
        _ => metadata.GetType().Name,
    };
}
