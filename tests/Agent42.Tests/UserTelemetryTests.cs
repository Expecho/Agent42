using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Agent42.Tests;

/// <summary>
/// Covers the user.* telemetry enrichment (ADR 0009). All deterministic and host-free: the repo
/// has no WebApplicationFactory/Mvc.Testing and OTel's AspNetCore instrumentation (the only thing
/// that invokes EnrichWithHttpRequest) is prod-only, so wiring is proven at the helper level —
/// GetUserTelemetryTags on hand-built headers, and ApplyUserTelemetryTags stamping a real Activity.
/// The claim-typ spellings in the synthetic blob mirror the extractor's candidate lists; the real
/// prod mapping is an AC9 prod-smoke check, not offline-verifiable (Easy Auth is absent locally).
/// </summary>
public sealed class UserTelemetryTests
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string NameHeader = "X-MS-CLIENT-PRINCIPAL-NAME";
    private const string IdpHeader = "X-MS-CLIENT-PRINCIPAL-IDP";
    private const string BlobHeader = "X-MS-CLIENT-PRINCIPAL";

    // AC1: full Easy Auth identity → all six user.* attributes present on the span.
    [Fact]
    public void GetUserTelemetryTags_FullPrincipal_EmitsAllAttributes()
    {
        var headers = new HeaderDictionary
        {
            [IdHeader] = "00000000-1111-2222-3333-444444444444",
            [NameHeader] = "alice@contoso.com",
            [IdpHeader] = "aad",
            [BlobHeader] = BuildPrincipalBlob(
                ("preferred_username", "alice@contoso.com"),
                ("name", "Alice Example"),
                ("roles", "admin"),
                ("roles", "user")),
        };

        var tags = headers.GetUserTelemetryTags(null).ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("00000000-1111-2222-3333-444444444444", tags["user.id"]);
        Assert.Equal("alice@contoso.com", tags["user.name"]);
        Assert.Equal("aad", tags["user.idp"]);
        Assert.Equal("alice@contoso.com", tags["user.email"]);
        Assert.Equal("Alice Example", tags["user.full_name"]);
        Assert.Equal(new[] { "admin", "user" }, Assert.IsType<string[]>(tags["user.roles"]));
    }

    // AC2: user.roles is a multi-value (array) attribute, not a joined string.
    [Fact]
    public void GetUserTelemetryTags_MultipleRoleClaims_EmitsArray()
    {
        var headers = new HeaderDictionary
        {
            [IdHeader] = "user-with-roles",
            [BlobHeader] = BuildPrincipalBlob(
                ("roles", "admin"),
                ("roles", "editor"),
                ("roles", "viewer")),
        };

        var tags = headers.GetUserTelemetryTags(null).ToDictionary(t => t.Key, t => t.Value);

        var roles = Assert.IsType<string[]>(tags["user.roles"]);
        Assert.Equal(new[] { "admin", "editor", "viewer" }, roles);
    }

    // AC3: id-only header (no blob, no name/idp) → only user.id, no throw.
    [Fact]
    public void GetUserTelemetryTags_IdOnly_EmitsIdOnly()
    {
        var headers = new HeaderDictionary { [IdHeader] = "just-an-id" };

        var tags = headers.GetUserTelemetryTags(null).ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("just-an-id", tags["user.id"]);
        Assert.Single(tags);
    }

    // AC4: no Easy Auth headers → user.id == "local-dev", no PII, no throw.
    [Fact]
    public void GetUserTelemetryTags_NoHeaders_FallsBackToLocalDev()
    {
        var headers = new HeaderDictionary();

        var tags = headers.GetUserTelemetryTags(null).ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("local-dev", tags["user.id"]);
        Assert.Single(tags);
    }

    // AC5: a malformed / non-base64 principal blob must not throw; header attrs still emit.
    [Fact]
    public void GetUserTelemetryTags_MalformedPrincipal_DoesNotThrow()
    {
        var headers = new HeaderDictionary
        {
            [IdHeader] = "resilient-id",
            [NameHeader] = "bob@contoso.com",
            [IdpHeader] = "aad",
            [BlobHeader] = "!!!not-base64-at-all!!!",
        };

        var tags = headers.GetUserTelemetryTags(null).ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("resilient-id", tags["user.id"]);
        Assert.Equal("bob@contoso.com", tags["user.name"]);
        Assert.Equal("aad", tags["user.idp"]);
        // The blob-derived attributes are simply absent — no exception, no email/full_name/roles.
        Assert.False(tags.ContainsKey("user.email"));
        Assert.False(tags.ContainsKey("user.full_name"));
        Assert.False(tags.ContainsKey("user.roles"));
    }

    // AC5 (robustness): a well-formed base64+JSON blob that simply has no "claims" array must not
    // throw; the header attributes still emit and no blob-derived attributes appear.
    [Fact]
    public void GetUserTelemetryTags_PrincipalWithoutClaimsArray_DoesNotThrow()
    {
        var blobWithoutClaims = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("{\"auth_typ\":\"aad\"}"));
        var headers = new HeaderDictionary
        {
            [IdHeader] = "no-claims-id",
            [NameHeader] = "dave@contoso.com",
            [BlobHeader] = blobWithoutClaims,
        };

        var tags = headers.GetUserTelemetryTags(null).ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("no-claims-id", tags["user.id"]);
        Assert.Equal("dave@contoso.com", tags["user.name"]);
        Assert.False(tags.ContainsKey("user.email"));
        Assert.False(tags.ContainsKey("user.roles"));
    }

    // AC6 (the wiring proof): ApplyUserTelemetryTags stamps user.* onto a live Activity via SetTag —
    // the exact code the production EnrichWithHttpRequest callback runs. A directly-constructed
    // Activity records tags with no ActivityListener needed.
    [Fact]
    public void ApplyUserTelemetryTags_FullPrincipal_StampsActivity()
    {
        var headers = new HeaderDictionary
        {
            [IdHeader] = "oid-123",
            [NameHeader] = "carol@contoso.com",
            [IdpHeader] = "aad",
            [BlobHeader] = BuildPrincipalBlob(
                ("preferred_username", "carol@contoso.com"),
                ("name", "Carol Example"),
                ("roles", "admin"),
                ("roles", "user")),
        };

        using var activity = new Activity("test").Start();
        activity.ApplyUserTelemetryTags(headers, null);

        Assert.Equal("oid-123", activity.GetTagItem("user.id"));
        Assert.Equal("carol@contoso.com", activity.GetTagItem("user.name"));
        Assert.Equal("aad", activity.GetTagItem("user.idp"));
        Assert.Equal("carol@contoso.com", activity.GetTagItem("user.email"));
        Assert.Equal("Carol Example", activity.GetTagItem("user.full_name"));
        var roles = Assert.IsType<string[]>(activity.GetTagItem("user.roles"));
        Assert.Equal(new[] { "admin", "user" }, roles);
    }

    // ————— Identity aliases (ADR 0074) —————

    private const string AliasOid = "11111111-1111-1111-1111-111111111111";
    private const string CanonicalOid = "22222222-2222-2222-2222-222222222222";

    /// <summary>
    /// AC11a. The alias rewrites <c>user.id</c> and <b>nothing else</b>. <c>user.name</c> comes from the
    /// principal-name header and <c>user.email</c>/<c>user.full_name</c> from the claims blob, none of
    /// which passes through the alias — so every span still names the account that actually signed in.
    /// That is the property attribution on <c>/logs</c> (ADR 0054) rests on; if this ever fails, an
    /// aliased sign-in becomes indistinguishable from the canonical user's own.
    /// </summary>
    [Fact]
    public void GetUserTelemetryTags_WithAnAlias_RewritesOnlyTheId()
    {
        var headers = new HeaderDictionary
        {
            [IdHeader] = AliasOid,
            [NameHeader] = "msn@2000miles.nl",
            [IdpHeader] = "aad",
            [BlobHeader] = BuildPrincipalBlob(
                ("preferred_username", "msn@2000miles.nl"), ("name", "Second Account")),
        };

        var tags = headers.GetUserTelemetryTags(Aliases()).ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal(CanonicalOid, tags["user.id"]);
        Assert.Equal("msn@2000miles.nl", tags["user.name"]);
        Assert.Equal("msn@2000miles.nl", tags["user.email"]);
        Assert.Equal("Second Account", tags["user.full_name"]);
    }

    /// <summary>
    /// AC12. The alias must reach the Orleans <c>RequestContext</c> carrier too (ADR 0010), not only the
    /// ASP.NET server span: the carrier is what <c>UserTelemetrySpanProcessor</c> stamps onto the
    /// <b>in-grain</b> spans. Aliasing one and not the other would split <c>user.id</c> across a single
    /// trace — and offline that is invisible, since the real OTel stamp is a prod-smoke check.
    /// </summary>
    [Fact]
    public async Task UserTelemetryMiddleware_WithAnAlias_PublishesTheCanonicalId()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[IdHeader] = AliasOid;
        context.Request.Headers[NameHeader] = "msn@2000miles.nl";
        context.RequestServices = new ServiceCollection()
            .AddSingleton(Aliases())
            .BuildServiceProvider();

        UserTelemetryContext? published = null;
        await UserTelemetryMiddleware.InvokeAsync(context, _ =>
        {
            published = (UserTelemetryContext?)RequestContext.Get(UserTelemetryContext.RequestContextKey);
            return Task.CompletedTask;
        });

        Assert.NotNull(published);
        Assert.Equal(CanonicalOid, published!.Id);
        // Same rule as AC11a: only the id is aliased, so the carrier still names the real account.
        Assert.Equal("msn@2000miles.nl", published.Name);
    }

    /// <summary>
    /// AC13 (security set). The <c>local-dev</c> fallback is returned <b>before</b> the map is consulted,
    /// so an alias can neither rewrite nor be keyed on the unauthenticated identity. This is what keeps
    /// the excludedPaths rule intact — an endpoint reached with no principal header must still see
    /// exactly <c>local-dev</c>, aliases configured or not.
    /// </summary>
    [Fact]
    public void ResolveUserId_WithNoPrincipalHeader_IgnoresTheAliasMap()
    {
        var withAliases = new HeaderDictionary();

        var tags = withAliases.GetUserTelemetryTags(Aliases()).ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("local-dev", tags["user.id"]);
        Assert.Single(tags);

        // The grain-keying path (GetUserId), not just the telemetry one.
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddSingleton(Aliases()).BuildServiceProvider();
        Assert.Equal("local-dev", context.GetUserId());
    }

    /// <summary>
    /// AC17 (security set, the D5 failure direction). With no map registered at all, resolution is
    /// exactly the pre-ADR-0074 behaviour: the raw principal id, never a rewrite.
    /// </summary>
    [Fact]
    public void GetUserId_WithNoAliasServiceRegistered_IsAPassthrough()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[IdHeader] = AliasOid;
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(AliasOid, context.GetUserId());
    }

    /// <summary>
    /// Regression: <c>HttpContext.RequestServices</c> is <b>null</b> on a bare <c>DefaultHttpContext</c>,
    /// and <c>GetService&lt;T&gt;()</c> throws <see cref="ArgumentNullException"/> on a null provider
    /// rather than returning null. Identity resolution runs on every request — and the telemetry
    /// middleware is registered FIRST, before request services are guaranteed — so it must degrade to
    /// "no alias" instead of throwing. Caught by the existing middleware tests, pinned here for
    /// <c>GetUserId</c> too.
    /// </summary>
    [Fact]
    public void GetUserId_WithNoRequestServices_DoesNotThrow()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[IdHeader] = AliasOid;

        Assert.Null(context.RequestServices);
        Assert.Equal(AliasOid, context.GetUserId());
    }

    private static UserIdAliases Aliases()
        => UserIdAliases.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection([
                    new KeyValuePair<string, string?>(
                        $"{UserIdAliases.SectionName}:{AliasOid}", CanonicalOid),
                ])
                .Build());

    // Builds a base64(JSON) X-MS-CLIENT-PRINCIPAL blob in Easy Auth's shape from (typ, val) claims.
    private static string BuildPrincipalBlob(params (string Typ, string Val)[] claims)
    {
        var payload = new
        {
            auth_typ = "aad",
            name_typ = "name",
            role_typ = "roles",
            claims = claims.Select(c => new { typ = c.Typ, val = c.Val }).ToArray(),
        };
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
