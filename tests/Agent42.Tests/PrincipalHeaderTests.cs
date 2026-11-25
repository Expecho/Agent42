using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using Agent42.Backend;
using Microsoft.AspNetCore.Http;

namespace Agent42.Tests;

public sealed class PrincipalHeaderTests
{
    private const string Oid = "11111111-2222-3333-4444-555555555555";

    private static ClaimsPrincipal Signed(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Cookies"));

    private static ClaimsPrincipal FullUser() => Signed(
        ("oid", Oid), ("sub", "pairwise-sub"), ("name", "Ada Lovelace"), ("preferred_username", "ada@example.com"),
        ("roles", "Admin"), ("roles", "Reader"));

    private static async Task<DefaultHttpContext> Run(ClaimsPrincipal? user, Action<IHeaderDictionary>? seed = null, string path = "/api/x")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (user is not null)
        {
            ctx.User = user;
        }

        seed?.Invoke(ctx.Request.Headers);
        await PrincipalHeaderMiddleware.InvokeAsync(ctx, _ => Task.CompletedTask);
        return ctx;
    }

    private static void Forge(IHeaderDictionary h)
    {
        h["x-ms-client-principal-id"] = "attacker";
        h["X-MS-CLIENT-PRINCIPAL-NAME"] = "attacker@evil";
        h["X-Ms-Client-Principal"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"claims\":[{\"typ\":\"roles\",\"val\":\"Admin\"}]}"));
        h["X-MS-CLIENT-FOO"] = new Microsoft.Extensions.Primitives.StringValues(["a", "b"]);
    }

    [Fact]
    public async Task Anonymous_ForgedPrincipalHeader_IsStripped()
    {
        var ctx = await Run(null, Forge);

        Assert.DoesNotContain(ctx.Request.Headers.Keys, k => k.StartsWith("X-MS-CLIENT-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("local-dev", ctx.GetUserId());
    }

    [Fact]
    public async Task ForgedHeader_OnPublicPath_NeverReachesGetUserId()
    {
        var ctx = await Run(null, Forge, "/api/agent-run");

        Assert.True(PublicPaths.IsPublic(ctx.Request.Path));
        Assert.Equal("local-dev", ctx.GetUserId());
    }

    [Fact]
    public async Task Authenticated_ClientHeader_IsReplaced()
    {
        var ctx = await Run(FullUser(), Forge);

        Assert.Equal(Oid, ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].ToString());
        Assert.Single(ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"]);
        Assert.Equal("ada@example.com", ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString());
        Assert.False(ctx.Request.Headers.ContainsKey("X-MS-CLIENT-FOO"));
    }

    [Fact]
    public async Task Principal_UsesOid_NotSub()
    {
        var ctx = await Run(FullUser());

        Assert.Equal(Oid, ctx.GetUserId());
        Assert.NotEqual("pairwise-sub", ctx.GetUserId());
    }

    [Fact]
    public async Task Principal_WithoutOid_StampsNothing()
    {
        var ctx = await Run(Signed(("sub", "s"), ("name", "n")), Forge);

        Assert.Equal("local-dev", ctx.GetUserId());
        Assert.False(ctx.Request.Headers.ContainsKey("X-MS-CLIENT-PRINCIPAL"));
    }

    [Fact]
    public async Task RoundTrip_ThroughCurrentUser()
    {
        var ctx = await Run(FullUser());

        var t = ctx.Request.Headers.GetUserTelemetryContext(null);
        Assert.Equal(Oid, t.Id);
        Assert.Equal("ada@example.com", t.Name);
        Assert.Equal("ada@example.com", t.Email);
        Assert.Equal("Ada Lovelace", t.FullName);
        Assert.Equal(["Admin", "Reader"], t.Roles);
        Assert.Equal("aad", t.Idp);
    }

    [Fact]
    public async Task ServerSpan_UserTags_ComeFromPrincipalNotForgedHeader()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == nameof(PrincipalHeaderTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(nameof(PrincipalHeaderTests));
        using var activity = source.StartActivity("server")!;

        await Run(FullUser(), Forge);

        Assert.Equal(Oid, activity.GetTagItem("user.id"));
        Assert.Equal("ada@example.com", activity.GetTagItem("user.email"));
        Assert.DoesNotContain(activity.TagObjects, t => Equals(t.Value, "attacker") || Equals(t.Value, "attacker@evil"));
    }
}
