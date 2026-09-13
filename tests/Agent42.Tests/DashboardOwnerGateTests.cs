using System.Net;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The owner gate on the Orleans dashboard (ADR 0062). Most of these exercise
/// <see cref="DashboardOwnerGate.IsOwner"/> <b>directly</b> rather than through a host: the rule is the
/// security surface, so it is asserted as a rule — the <c>SearxngProxyTests</c> shape. The host-level
/// cases are the ones that only exist end to end: that the middleware actually covers a sub-path and not
/// only the page, and that it denies with 404 rather than 403.
/// </summary>
public sealed class DashboardOwnerGateTests
{
    // A stand-in for a real Entra object id. Any GUID does — the rule never parses it.
    private const string OwnerOid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    // A second account aliased onto the owner (ADR 0074), and one that is aliased to nothing.
    private const string AliasedOid = "5f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string StrangerOid = "7f2504e0-4f89-11d3-9a0c-0305e82c3301";

    /// <summary>
    /// AC 4. <b>Fail closed.</b> No owner configured denies everyone, including a caller presenting a
    /// perfectly good principal id. This is what makes it safe to ship <c>Dashboard:OwnerOid</c> present
    /// but empty: the dashboard is inert until someone fills it in, rather than open to every
    /// Easy-Auth-allowlisted user.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Gate_WithNoOwnerConfigured_DeniesEveryone(string? configured)
    {
        Assert.False(DashboardOwnerGate.IsOwner(configured, OwnerOid));
        Assert.False(DashboardOwnerGate.IsOwner(configured, "any-other-user"));

        // The one that would be a silent disaster: an unset value must not make the UNAUTHENTICATED
        // fallback identity an owner by matching blank against blank.
        Assert.False(DashboardOwnerGate.IsOwner(configured, "local-dev"));
    }

    /// <summary>
    /// AC 5. <c>local-dev</c> is <c>CurrentUser.GetUserId</c>'s fallback for a request carrying <b>no</b>
    /// Easy Auth principal header, so it is the single catastrophic value for this key: configured in
    /// <c>src/Agent42.Backend/appsettings.json</c> it would hand the production dashboard to any
    /// unauthenticated request that reached the app. The rule itself cannot prevent that misconfiguration —
    /// this pins the half it can, that the offline identity is not silently an owner of a real OID.
    /// </summary>
    [Fact]
    public void Gate_DoesNotTreatTheOfflineFallbackUserAsOwner()
    {
        Assert.False(DashboardOwnerGate.IsOwner(OwnerOid, "local-dev"));

        // And the inverse, which is what DevHost and the AppHost rely on: with local-dev configured
        // deliberately, the offline identity IS the owner — one rule, no environment branch.
        Assert.True(DashboardOwnerGate.IsOwner("local-dev", "local-dev"));
        Assert.False(DashboardOwnerGate.IsOwner("local-dev", OwnerOid));
    }

    /// <summary>
    /// AC 6. A GUID pasted in different casing is the same principal. An ordinal compare would fail
    /// closed on a casing difference and 404 the dashboard for its own owner — indistinguishable from
    /// the feature being broken, which is the exact outcome the plan's prerequisite note warns about.
    /// </summary>
    [Fact]
    public void Gate_MatchesTheOwnerIdCaseInsensitively()
    {
        Assert.True(DashboardOwnerGate.IsOwner(OwnerOid, OwnerOid.ToUpperInvariant()));
        Assert.True(DashboardOwnerGate.IsOwner(OwnerOid.ToUpperInvariant(), OwnerOid));

        // Case-insensitive, not fuzzy: a different id is still a different id.
        Assert.False(DashboardOwnerGate.IsOwner(OwnerOid, OwnerOid + "x"));
        Assert.False(DashboardOwnerGate.IsOwner(OwnerOid, ""));
    }

    /// <summary>
    /// The shipped configuration itself, not the rule. <c>local-dev</c> is
    /// <c>CurrentUser.GetUserId</c>'s fallback for a request carrying <b>no</b> Easy Auth principal
    /// header, so writing it into the Backend's own <c>appsettings.json</c> would hand the production
    /// dashboard to any unauthenticated request that reached the app. It is the correct value only in
    /// DevHost and the AppHost, which set it in code and have no Easy Auth in front of them.
    ///
    /// <para>ADR 0062 calls this "the one catastrophic value for that key" and guards it with a
    /// <c>//DANGER</c> comment. A comment is not a gate — this is. The GUID check catches the other
    /// realistic mistake: a truncated or mistyped id, which fails closed and 404s the dashboard for
    /// everyone while looking exactly like a broken feature.</para>
    /// </summary>
    [Fact]
    public void ShippedConfiguration_NeverNamesTheOfflineFallbackUserAsOwner()
    {
        var configured = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(BackendDir(), "appsettings.json"))
            .Build()[DashboardOwnerGate.OwnerOidConfigKey];

        Assert.False(
            string.Equals(configured, "local-dev", StringComparison.OrdinalIgnoreCase),
            "appsettings.json sets Dashboard:OwnerOid to the UNAUTHENTICATED fallback identity — "
                + "that opens the production dashboard to any request with no Easy Auth principal header.");

        // Blank is a supported state (fail-closed, the dashboard is inert). A non-blank value that is
        // not a GUID is always a mistake: an Entra object id is one.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Assert.True(
                Guid.TryParse(configured, out _),
                $"Dashboard:OwnerOid is '{configured}', which is not a GUID — an Entra object id is one. "
                    + "A wrong value fails closed and 404s the dashboard for everyone.");
        }
    }

    private static string BackendDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return Path.Combine(dir!.FullName, "src", "Agent42.Backend");
    }

    // ————— Host level: the middleware, not the predicate —————

    /// <summary>
    /// AC 2. The configured owner reaches the dashboard — the page and everything under it.
    /// </summary>
    [Fact]
    public async Task Gate_AllowsTheConfiguredOwner()
    {
        using var host = await BuildGatedHostAsync(OwnerOid);
        using var client = host.GetTestClient();

        foreach (var path in GatedPaths)
        {
            var response = await client.SendAsync(Request(path, OwnerOid));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("stub", await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// AC 3. A non-owner is denied on a <b>sub-path</b> as well as on the page, and with <b>404</b> —
    /// a 403 would confirm the page exists.
    ///
    /// <para><b>What this does NOT prove, deliberately.</b> It does not establish the
    /// middleware-vs-endpoint-filter choice recorded in ADR 0062: this host maps its own stub
    /// endpoints, so a filter applied to our own builder would propagate to them by construction and
    /// this test would stay green either way. Falsifying that would need the real package's endpoints,
    /// and no test spins up a silo with it.
    /// <c>ProjectLayoutTests.Program_GatesTheOrleansDashboardBeforeServingIt</c> carries the weight
    /// instead — it is what stops a future edit mapping the dashboard without the gate at all.</para>
    /// </summary>
    [Fact]
    public async Task Gate_Blocks_A_NonOwner_OnSubPathsAsWellAsThePage()
    {
        using var host = await BuildGatedHostAsync(OwnerOid);
        using var client = host.GetTestClient();

        foreach (var path in GatedPaths)
        {
            var response = await client.SendAsync(Request(path, "11111111-2222-3333-4444-555555555555"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }

        // Nothing outside the prefix is affected — the gate is scoped, not a global deny.
        var ungated = await client.SendAsync(Request("/api/anything", "11111111-2222-3333-4444-555555555555"));
        Assert.Equal(HttpStatusCode.OK, ungated.StatusCode);
    }

    /// <summary>
    /// AC 4, at the host level. Blank config denies everyone, including a caller who presents the id
    /// that <em>would</em> be the owner if anything were configured. This is the state the feature
    /// ships in until an operator fills the key in.
    /// </summary>
    [Fact]
    public async Task Gate_WithNoOwnerConfigured_DeniesEveryoneThroughTheHost()
    {
        using var host = await BuildGatedHostAsync(configuredOwnerOid: "");
        using var client = host.GetTestClient();

        foreach (var path in GatedPaths)
        {
            var response = await client.SendAsync(Request(path, OwnerOid));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>
    /// The two halves must be wired together, and this pins that they <b>fail fast</b> when they are
    /// not. `MapOrleansDashboardUi()` without a matching `silo.AddDashboard()` is not a compile error
    /// and does not silently serve an empty page — it throws at startup with a message naming the
    /// missing call. Worth pinning because the two live in different parts of `Program.cs` (one inside
    /// the `UseOrleans` lambda, one beside `MapAgentApi`), so dropping one while keeping the other is
    /// an easy edit.
    ///
    /// <para>This is also the only test that <b>executes</b> <see cref="DashboardRegistration.MapOrleansDashboardUi"/>.
    /// Serving the dashboard needs a real silo, but reaching the package's own guard does not.</para>
    /// </summary>
    [Fact]
    public void MappingTheDashboard_WithoutTheSiloRegistration_FailsFast()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapOrleansDashboardUi());

        Assert.Contains("AddDashboard", ex.Message, StringComparison.Ordinal);
    }

    // The page, the trailing-slash form the package 301s to, and a subresource — the 3.5 MB asset route
    // is the one an endpoint-level control is most likely to miss.
    private static readonly string[] GatedPaths =
        ["/dashboard", "/dashboard/", "/dashboard/index.min.js"];

    /// <summary>
    /// AC18 (ADR 0074). The gate compares <c>context.GetUserId()</c>, which is where the identity alias
    /// is applied — so a second account aliased onto the configured owner reaches the dashboard. That is
    /// correct (it is the same person) but it is a real widening of ADR 0062's "it is ONE principal"
    /// property, so it is asserted deliberately rather than left to be discovered.
    ///
    /// <para>The second half is the one that must never regress: an alias grants exactly the principal
    /// named in it, and everyone else still gets 404.</para>
    /// </summary>
    [Fact]
    public async Task Gate_AllowsAnAliasedPrincipal_OfTheConfiguredOwner()
    {
        var aliases = UserIdAliases.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{UserIdAliases.SectionName}:{AliasedOid}"] = OwnerOid,
                    })
                .Build());

        using var host = await BuildGatedHostAsync(OwnerOid, aliases);
        using var client = host.GetTestClient();

        var aliased = await client.SendAsync(Request("/dashboard", AliasedOid));
        Assert.Equal(HttpStatusCode.OK, aliased.StatusCode);

        var stranger = await client.SendAsync(Request("/dashboard", StrangerOid));
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    private static HttpRequestMessage Request(string path, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        // What Easy Auth injects; CurrentUser.GetUserId reads exactly this header.
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL-ID", userId);
        return request;
    }

    /// <summary>
    /// A minimal host carrying the real gate installation and stub endpoints under the prefix.
    /// <b>No Orleans silo</b> — the gate is ASP.NET middleware and needs none, which is what makes the
    /// security-relevant half of <see cref="DashboardRegistration"/> executable by a test at all
    /// (<c>HindsightRegistrationTests</c> executes its registration for the same reason).
    /// </summary>
    private static async Task<WebApplication> BuildGatedHostAsync(
        string configuredOwnerOid, UserIdAliases? aliases = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [DashboardOwnerGate.OwnerOidConfigKey] = configuredOwnerOid });
        // Registered unconditionally (Empty when the caller passes none) so the host shape is identical
        // whether or not the test is about aliasing.
        builder.Services.AddSingleton(aliases ?? UserIdAliases.Empty);

        var app = builder.Build();
        app.UseOrleansDashboardOwnerGate();

        // A page route plus a catch-all, which is the shape the real package maps (an index and its
        // assets) — and mapping "/dashboard" and "/dashboard/" as two literals is an
        // AmbiguousMatchException, since ASP.NET treats them as the same template.
        app.MapGet("/dashboard", () => "stub");
        app.MapGet("/dashboard/{**rest}", () => "stub");

        app.MapGet("/api/anything", () => "stub");

        await app.StartAsync();
        return app;
    }
}
