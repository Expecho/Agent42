using System.Diagnostics;
using Agent42.Backend;
using Microsoft.AspNetCore.Http;
using Orleans.Runtime;
using Agent42.Agents.Telemetry;

namespace Agent42.Tests;

/// <summary>
/// Covers downstream/in-grain user.* attribution (ADR 0010) offline, by composition (a real OTel host
/// and Easy Auth are prod-only, so the end-to-end stamp is AC8 prod-smoke): the processor stamps every
/// span from the identity in RequestContext (AC1/AC2/AC4), the middleware writes that identity from the
/// Easy Auth headers (AC5), and the carrier projects its fields to the tag shape. Orleans propagates
/// RequestContext into the streaming grain path in RequestContextPropagationTests (AC3) — together these
/// prove every link of the chain without booting OTel.
/// </summary>
public sealed class UserTelemetryProcessorTests
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string NameHeader = "X-MS-CLIENT-PRINCIPAL-NAME";
    private const string IdpHeader = "X-MS-CLIENT-PRINCIPAL-IDP";

    // AC1: a span started while a full identity is in RequestContext gets every user.* attribute.
    [Fact]
    public void Processor_WithIdentityInRequestContext_StampsAllTags()
    {
        RequestContext.Set(
            UserTelemetryContext.RequestContextKey,
            new UserTelemetryContext(
                "oid-1", "alice@contoso.com", "alice@contoso.com", "Alice Example", ["admin", "user"], "aad"));
        try
        {
            using var activity = StampStartedActivity();

            Assert.Equal("oid-1", activity.GetTagItem("user.id"));
            Assert.Equal("alice@contoso.com", activity.GetTagItem("user.name"));
            Assert.Equal("aad", activity.GetTagItem("user.idp"));
            Assert.Equal("alice@contoso.com", activity.GetTagItem("user.email"));
            Assert.Equal("Alice Example", activity.GetTagItem("user.full_name"));
            var roles = Assert.IsType<string[]>(activity.GetTagItem("user.roles"));
            Assert.Equal(new[] { "admin", "user" }, roles);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    // AC2: user.roles lands on a downstream span as a string[] (multi-value), not a joined string.
    [Fact]
    public void Processor_StampsRolesAsArray()
    {
        RequestContext.Set(
            UserTelemetryContext.RequestContextKey,
            new UserTelemetryContext("oid-2", null, null, null, ["admin", "editor", "viewer"], null));
        try
        {
            using var activity = StampStartedActivity();

            var roles = Assert.IsType<string[]>(activity.GetTagItem("user.roles"));
            Assert.Equal(new[] { "admin", "editor", "viewer" }, roles);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    // AC4: with nothing in RequestContext (background/reminder/startup spans), the processor is a
    // no-op — it never invents attribution.
    [Fact]
    public void Processor_NoRequestContext_StampsNothing()
    {
        RequestContext.Clear();

        using var activity = StampStartedActivity();

        Assert.Null(activity.GetTagItem("user.id"));
        Assert.Empty(activity.TagObjects);
    }

    // AC5: the real middleware writes the header-derived identity into RequestContext, visible to the
    // rest of the pipeline (where downstream spans start).
    [Fact]
    public async Task Middleware_WithEasyAuthHeaders_SetsRequestContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[IdHeader] = "oid-mw";
        context.Request.Headers[NameHeader] = "bob@contoso.com";
        context.Request.Headers[IdpHeader] = "aad";

        UserTelemetryContext? captured = null;
        await UserTelemetryMiddleware.InvokeAsync(
            context,
            _ =>
            {
                // Read RequestContext exactly where a downstream span would, INSIDE the pipeline.
                captured = RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext;
                return Task.CompletedTask;
            });

        Assert.NotNull(captured);
        Assert.Equal("oid-mw", captured!.Id);
        Assert.Equal("bob@contoso.com", captured.Name);
        Assert.Equal("aad", captured.Idp);
        RequestContext.Clear();
    }

    // No headers → the middleware still publishes a carrier, carrying the local-dev fallback id (the
    // same fallback the server span uses), so downstream spans stay attributable offline.
    [Fact]
    public async Task Middleware_NoHeaders_SetsLocalDevIdentity()
    {
        var context = new DefaultHttpContext();

        UserTelemetryContext? captured = null;
        await UserTelemetryMiddleware.InvokeAsync(
            context,
            _ =>
            {
                captured = RequestContext.Get(UserTelemetryContext.RequestContextKey) as UserTelemetryContext;
                return Task.CompletedTask;
            });

        Assert.NotNull(captured);
        Assert.Equal("local-dev", captured!.Id);
        Assert.Null(captured.Name);
        RequestContext.Clear();
    }

    // Carrier: every present field projects to its user.* tag; id is always emitted.
    [Fact]
    public void ToTags_FullIdentity_EmitsEveryAttribute()
    {
        var tags = new UserTelemetryContext(
                "oid-3", "carol@contoso.com", "carol@contoso.com", "Carol Example", ["admin"], "aad")
            .ToTags()
            .ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("oid-3", tags["user.id"]);
        Assert.Equal("carol@contoso.com", tags["user.name"]);
        Assert.Equal("aad", tags["user.idp"]);
        Assert.Equal("carol@contoso.com", tags["user.email"]);
        Assert.Equal("Carol Example", tags["user.full_name"]);
        Assert.Equal(new[] { "admin" }, Assert.IsType<string[]>(tags["user.roles"]));
    }

    // Carrier: absent (null/empty) fields are omitted; only user.id survives an id-only identity.
    [Fact]
    public void ToTags_IdOnly_EmitsIdOnly()
    {
        var tags = new UserTelemetryContext("just-an-id", null, null, null, null, null)
            .ToTags()
            .ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("just-an-id", tags["user.id"]);
        Assert.Single(tags);
    }

    // Carrier: an empty roles array is not emitted (guards the { Length: > 0 } branch).
    [Fact]
    public void ToTags_EmptyRoles_OmitsRolesTag()
    {
        var tags = new UserTelemetryContext("id-4", null, null, null, [], null)
            .ToTags()
            .ToDictionary(t => t.Key, t => t.Value);

        Assert.False(tags.ContainsKey("user.roles"));
        Assert.Single(tags);
    }

    // AC9: the identity must never ride an outbound `baggage` header — the load-bearing reason the
    // design uses Orleans RequestContext, not Activity.Baggage (which the default propagator would
    // inject into HttpClient requests to Azure OpenAI). Guard the *write* APIs that would cause that
    // injection (SetBaggage/AddBaggage); reading `Activity.Baggage` or naming it in a comment is fine.
    // A source-text guard (there is no OTel host offline to observe the header) — proves the invariant
    // holds in the code, not at runtime.
    [Theory]
    [InlineData("src", "Agent42.Backend")]
    [InlineData("src", "Agent42.Agents")]
    public void NoBaggageWriteApi_IsUsedInTelemetryPath(string top, string project)
    {
        var projectDir = Path.Combine(RepoRoot(), top, project);
        var offenders = Directory
            .EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            // Skip build output — obj/bin hold generated sources (GlobalUsings, Orleans codegen) that
            // aren't the hand-written telemetry path and could carry the substring incidentally.
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file) is var text
                && (text.Contains("SetBaggage") || text.Contains("AddBaggage")))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Baggage write API used (would leak user PII to an outbound `baggage` header — ADR 0010): "
                + string.Join(", ", offenders));
    }

    // Walk up from the test assembly to the repo root (the directory holding Agent42.slnx) — robust
    // across Debug/Release/CI, mirroring StaticAssetsServerFixture.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    // Starts a real Activity and runs the processor's OnStart against it — a directly-constructed
    // Activity records tags with no ActivityListener needed (same shape as UserTelemetryTests).
    private static Activity StampStartedActivity()
    {
        var activity = new Activity("downstream").Start();
        new UserTelemetrySpanProcessor().OnStart(activity);
        return activity;
    }
}
