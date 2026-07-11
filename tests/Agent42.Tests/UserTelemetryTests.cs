using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agent42.Backend;
using Microsoft.AspNetCore.Http;

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

        var tags = headers.GetUserTelemetryTags().ToDictionary(t => t.Key, t => t.Value);

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

        var tags = headers.GetUserTelemetryTags().ToDictionary(t => t.Key, t => t.Value);

        var roles = Assert.IsType<string[]>(tags["user.roles"]);
        Assert.Equal(new[] { "admin", "editor", "viewer" }, roles);
    }

    // AC3: id-only header (no blob, no name/idp) → only user.id, no throw.
    [Fact]
    public void GetUserTelemetryTags_IdOnly_EmitsIdOnly()
    {
        var headers = new HeaderDictionary { [IdHeader] = "just-an-id" };

        var tags = headers.GetUserTelemetryTags().ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("just-an-id", tags["user.id"]);
        Assert.Single(tags);
    }

    // AC4: no Easy Auth headers → user.id == "local-dev", no PII, no throw.
    [Fact]
    public void GetUserTelemetryTags_NoHeaders_FallsBackToLocalDev()
    {
        var headers = new HeaderDictionary();

        var tags = headers.GetUserTelemetryTags().ToDictionary(t => t.Key, t => t.Value);

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

        var tags = headers.GetUserTelemetryTags().ToDictionary(t => t.Key, t => t.Value);

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

        var tags = headers.GetUserTelemetryTags().ToDictionary(t => t.Key, t => t.Value);

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
        activity.ApplyUserTelemetryTags(headers);

        Assert.Equal("oid-123", activity.GetTagItem("user.id"));
        Assert.Equal("carol@contoso.com", activity.GetTagItem("user.name"));
        Assert.Equal("aad", activity.GetTagItem("user.idp"));
        Assert.Equal("carol@contoso.com", activity.GetTagItem("user.email"));
        Assert.Equal("Carol Example", activity.GetTagItem("user.full_name"));
        var roles = Assert.IsType<string[]>(activity.GetTagItem("user.roles"));
        Assert.Equal(new[] { "admin", "user" }, roles);
    }

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
