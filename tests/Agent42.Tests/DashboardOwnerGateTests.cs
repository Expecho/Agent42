using System.Net;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The owner gate (ADR 0062, generalised by ADR 0121) through a real host: that the middleware covers
/// every owner-only prefix and its sub-paths, and that it denies with 404 rather than 403. The rule itself
/// is asserted directly in <see cref="OwnerPolicyTests"/>.
/// </summary>
public sealed class DashboardOwnerGateTests
{
    // A stand-in for a real Entra object id. Any GUID does — the rule never parses it.
    private const string OwnerOid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    // A second account aliased onto the owner (ADR 0074), and one that is aliased to nothing.
    private const string AliasedOid = "5f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string StrangerOid = "7f2504e0-4f89-11d3-9a0c-0305e82c3301";

    // Each owner-only prefix: the page, the trailing-slash form the dashboard package 301s to, and a
    // subresource. The 3.5 MB asset route is the one an endpoint-level control is most likely to miss.
    private static readonly string[] GatedPaths =
    [
        "/dashboard", "/dashboard/", "/dashboard/index.min.js",
        "/logs", "/logs/", "/logs/logs.js",
        "/api/diagnostics/traces", "/api/diagnostics/logs", "/api/diagnostics/metrics",
        "/api/config-health",
    ];

    /// <summary>The owner reaches every gated surface — the page and everything under it.</summary>
    [Fact]
    public async Task Gate_AllowsAnOwner_OnEveryOwnerOnlyPath()
    {
        using var host = await BuildGatedHostAsync(new OwnerPolicy([OwnerOid]));
        using var client = host.GetTestClient();

        foreach (var path in GatedPaths)
        {
            var response = await client.SendAsync(Request(path, OwnerOid));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("stub", await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// A non-owner is denied on every prefix and sub-path, with <b>404</b> (a 403 would confirm the surface
    /// exists), including <c>?probe=true</c> on config health, whose probes are billable.
    ///
    /// <para><b>What this does NOT prove:</b> the middleware-vs-endpoint-filter choice (ADR 0062); this host
    /// maps its own stub endpoints. <c>ProjectLayoutTests</c> carries the ordering and presence guard.</para>
    /// </summary>
    [Fact]
    public async Task Gate_Blocks_A_NonOwner_OnEveryOwnerOnlyPath()
    {
        using var host = await BuildGatedHostAsync(new OwnerPolicy([OwnerOid]));
        using var client = host.GetTestClient();

        foreach (var path in GatedPaths.Append("/api/config-health?probe=true"))
        {
            var response = await client.SendAsync(Request(path, StrangerOid));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }

        // Nothing outside the prefixes is affected, including look-alikes: the gate is scoped, not a global
        // deny, and matches whole segments.
        foreach (var path in new[] { "/api/anything", "/logsfoo", "/api/config-healthy", "/dashboardish" })
        {
            var ungated = await client.SendAsync(Request(path, StrangerOid));
            Assert.Equal(HttpStatusCode.OK, ungated.StatusCode);
        }
    }

    /// <summary>Case does not matter: <c>/Logs</c> is the same path to the static-file middleware.</summary>
    [Fact]
    public async Task Gate_Blocks_A_NonOwner_RegardlessOfPathCase()
    {
        using var host = await BuildGatedHostAsync(new OwnerPolicy([OwnerOid]));
        using var client = host.GetTestClient();

        var response = await client.SendAsync(Request("/LOGS", StrangerOid));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Blank owners deny everyone, even a caller presenting the id that would be the owner.</summary>
    [Fact]
    public async Task Gate_WithNoOwnerConfigured_DeniesEveryone()
    {
        using var host = await BuildGatedHostAsync(OwnerPolicy.None);
        using var client = host.GetTestClient();

        foreach (var path in GatedPaths)
        {
            var response = await client.SendAsync(Request(path, OwnerOid));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>
    /// Fail closed on a missing registration: a host that forgot to register <see cref="IOwnerPolicy"/>
    /// denies everyone rather than waving them through. The policy is resolved optionally so no host gains
    /// a mandatory registration (CLAUDE.md rule 7), and the cost of that is exactly this default.
    /// </summary>
    [Fact]
    public async Task Gate_WithNoPolicyRegistered_DeniesEveryone()
    {
        using var host = await BuildGatedHostAsync(policy: null);
        using var client = host.GetTestClient();

        var response = await client.SendAsync(Request("/dashboard", OwnerOid));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The gate compares <c>context.GetUserId()</c>, where the alias is applied (ADR 0074), so a second
    /// account aliased onto an owner reaches the surfaces. Correct (same person), and asserted deliberately.
    /// An alias grants exactly the principal named in it; everyone else still gets 404.
    /// </summary>
    [Fact]
    public async Task Gate_AllowsAnAliasedPrincipal_OfAnOwner()
    {
        var aliases = UserIdAliases.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{UserIdAliases.SectionName}:{AliasedOid}"] = OwnerOid,
                    })
                .Build());

        using var host = await BuildGatedHostAsync(new OwnerPolicy([OwnerOid]), aliases);
        using var client = host.GetTestClient();

        var aliased = await client.SendAsync(Request("/dashboard", AliasedOid));
        Assert.Equal(HttpStatusCode.OK, aliased.StatusCode);

        var stranger = await client.SendAsync(Request("/dashboard", StrangerOid));
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    /// <summary>
    /// <c>MapOrleansDashboardUi()</c> without a matching <c>silo.AddDashboard()</c> throws at startup with
    /// a message naming the missing call. The two live in different parts of <c>Program.cs</c>, so dropping
    /// one is an easy edit. This is also the only test that executes <c>MapOrleansDashboardUi</c>.
    /// </summary>
    [Fact]
    public void MappingTheDashboard_WithoutTheSiloRegistration_FailsFast()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapOrleansDashboardUi());

        Assert.Contains("AddDashboard", ex.Message, StringComparison.Ordinal);
    }

    private static HttpRequestMessage Request(string path, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        // What Easy Auth injects; CurrentUser.GetUserId reads exactly this header.
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", userId);
        return request;
    }

    /// <summary>
    /// A minimal host carrying the real gate installation and stub endpoints under every prefix. <b>No
    /// Orleans silo</b>: the gate is ASP.NET middleware and needs none.
    /// </summary>
    private static async Task<WebApplication> BuildGatedHostAsync(
        IOwnerPolicy? policy, UserIdAliases? aliases = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        if (policy is not null)
        {
            builder.Services.AddSingleton(policy);
        }

        // Registered unconditionally (Empty when the caller passes none) so the host shape is identical
        // whether or not the test is about aliasing.
        builder.Services.AddSingleton(aliases ?? UserIdAliases.Empty);

        var app = builder.Build();
        app.UseOwnerGate();

        // Catch-all stubs: the real package maps an index and its assets, and mapping "/dashboard" and
        // "/dashboard/" as two literals is an AmbiguousMatchException (same template to ASP.NET).
        app.MapGet("/dashboard/{**rest}", () => "stub");
        app.MapGet("/dashboard", () => "stub");
        app.MapGet("/logs/{**rest}", () => "stub");
        app.MapGet("/logs", () => "stub");
        app.MapGet("/api/diagnostics/{**rest}", () => "stub");
        app.MapGet("/api/config-health", () => "stub");
        app.MapGet("/api/config-healthy", () => "stub");
        app.MapGet("/logsfoo", () => "stub");
        app.MapGet("/dashboardish", () => "stub");
        app.MapGet("/api/anything", () => "stub");

        await app.StartAsync();
        return app;
    }
}
