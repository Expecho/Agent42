using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The MCP App frame policy (ADR 0133), asserted as EXACT strings, the way <c>ArtifactsApiTests</c> pins
/// <c>AgentApi.ArtifactCsp</c>: a view's declared domains are untrusted third-party input written into a response
/// header, so the literal policy and every refusal rule are pinned here.
/// </summary>
public sealed class McpAppCspTests
{
    private static readonly string[] Own = ["chat.agent42.nl"];

    // AC5: a view that declares nothing is served the narrowest policy: no network, no embeds, no forms, no 'self'.
    [Fact]
    public void NoDeclaredDomains_IsTheNarrowestPolicy()
    {
        const string Expected =
            "sandbox allow-scripts; default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; "
            + "img-src data: blob:; font-src data:; media-src data: blob:; connect-src 'none'; frame-src 'none'; "
            + "base-uri 'none'; object-src 'none'; form-action 'none'; frame-ancestors 'self'";

        Assert.Equal(Expected, McpAppCsp.Default);
        Assert.Equal(Expected, McpAppCsp.Build(null, Own));
        Assert.Equal(Expected, McpAppCsp.Build("", Own));
        Assert.Equal(Expected, McpAppCsp.Build("{not json", Own));
        Assert.Equal(Expected, McpAppCsp.Build("""{"csp":{}}""", Own));
        Assert.Equal(Expected, McpAppCsp.Build("""{"csp":{"connectDomains":"https://a.example.com"}}""", Own));
    }

    // AC5: declared origins land in exactly the directives their role maps to, in canonical lowercase form.
    [Fact]
    public void DeclaredDomains_LandInTheirOwnDirectives()
    {
        const string Meta = """
            {"csp":{
              "connectDomains":["https://API.example.com","wss://ws.example.com:8443"],
              "resourceDomains":["https://cdn.example.com","https://*.static.example.com"],
              "frameDomains":["https://embed.example.com"],
              "baseUriDomains":["https://base.example.com"]}}
            """;
        const string Resources = " https://cdn.example.com https://*.static.example.com";

        var csp = McpAppCsp.Build(Meta, Own);

        Assert.Equal(
            "sandbox allow-scripts; default-src 'none'; "
            + $"script-src 'unsafe-inline'{Resources}; style-src 'unsafe-inline'{Resources}; "
            + $"img-src data: blob:{Resources}; font-src data:{Resources}; media-src data: blob:{Resources}; "
            + "connect-src https://api.example.com wss://ws.example.com:8443; frame-src https://embed.example.com; "
            + "base-uri https://base.example.com; object-src 'none'; form-action 'none'; frame-ancestors 'self'",
            csp);
    }

    // AC5: whatever a server declares, these never appear.
    [Fact]
    public void ThePolicy_NeverContainsSelfOrSameOriginOrAWildcardSource()
    {
        var csp = McpAppCsp.Build("""{"csp":{"connectDomains":["https://*","https://*.com","'self'","*"]}}""", Own);

        Assert.DoesNotContain("'self' ", csp.Replace("frame-ancestors 'self'", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("allow-same-origin", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("*", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", csp, StringComparison.Ordinal);
    }

    // AC5: an entry that could break out of its directive, or that names somewhere a view must not reach, is dropped.
    [Theory]
    [InlineData("http://a.example.com")]
    [InlineData("ws://a.example.com")]
    [InlineData("data:text/html,x")]
    [InlineData("blob:https://a.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("a.example.com")]
    [InlineData("//a.example.com")]
    [InlineData("https://a.example.com; script-src *")]
    [InlineData("https://a.example.com'")]
    [InlineData("https://a.example.com\"")]
    [InlineData("https://a.example.com,https://b.example.com")]
    [InlineData("https://a.example.com ")]
    [InlineData(" https://a.example.com")]
    [InlineData("https://a.example.com\n")]
    [InlineData("https://a.example.com\r\nX-Evil: 1")]
    [InlineData("https://a .example.com")]
    [InlineData("https://a.example.com/path")]
    [InlineData("https://a.example.com?x=1")]
    [InlineData("https://user@a.example.com")]
    [InlineData("https://a.example.com:99999")]
    [InlineData("https://a.example.com:0")]
    [InlineData("https://*")]
    [InlineData("https://*.com")]
    [InlineData("https://*.*.example.com")]
    [InlineData("https://*example.com")]
    [InlineData("https://com")]
    [InlineData("https://localhost")]
    [InlineData("https://foo.localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://127.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://172.16.0.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://8.8.8.8")]
    [InlineData("https://0x7f.1")]
    [InlineData("https://2130706433")]
    [InlineData("https://printer.local")]
    [InlineData("https://db.internal")]
    [InlineData("https://nas.lan")]
    [InlineData("https://chat.agent42.nl")]
    [InlineData("https://CHAT.AGENT42.NL")]
    [InlineData("https://chat.agent42.nl:8443")]
    [InlineData("https://*.agent42.nl")]
    [InlineData("wss://chat.agent42.nl")]
    [InlineData("")]
    public void TryNormalize_DropsWhatMustNotReachTheHeader(string entry)
    {
        Assert.Null(McpAppCsp.TryNormalize(entry, Own));
    }

    [Fact]
    public void TryNormalize_DropsAnOverlongEntry()
    {
        Assert.Null(McpAppCsp.TryNormalize("https://" + new string('a', 300) + ".example.com", Own));
    }

    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com")]
    [InlineData("HTTPS://API.Example.COM", "https://api.example.com")]
    [InlineData("https://api.example.com:8443", "https://api.example.com:8443")]
    [InlineData("wss://ws.example.com", "wss://ws.example.com")]
    [InlineData("https://*.example.com", "https://*.example.com")]
    [InlineData("https://a-b.example.co.uk", "https://a-b.example.co.uk")]
    [InlineData("https://xn--bcher-kva.example", "https://xn--bcher-kva.example")]
    [InlineData("https://agent42.nl", "https://agent42.nl")]
    public void TryNormalize_KeepsAValidOrigin_InCanonicalForm(string entry, string expected)
    {
        Assert.Equal(expected, McpAppCsp.TryNormalize(entry, Own));
    }

    // A wildcard whose parent covers this app is refused, but one that merely shares a suffix string is not.
    [Fact]
    public void AWildcard_IsRefusedOnlyWhenItCoversOurHost()
    {
        Assert.Null(McpAppCsp.TryNormalize("https://*.agent42.nl", ["chat.agent42.nl"]));
        Assert.Null(McpAppCsp.TryNormalize("https://*.chat.agent42.nl", ["chat.agent42.nl"]));
        Assert.Equal("https://*.gent42.nl", McpAppCsp.TryNormalize("https://*.gent42.nl", ["chat.agent42.nl"]));
        Assert.Equal("https://*.example.com", McpAppCsp.TryNormalize("https://*.example.com", ["chat.agent42.nl"]));
    }

    // Every host the request was addressed to counts, so a forwarded host cannot be named either.
    [Fact]
    public void EveryOwnHost_IsRefused()
    {
        Assert.Null(McpAppCsp.TryNormalize("https://front.example.org", ["chat.agent42.nl", "front.example.org"]));
        Assert.Null(McpAppCsp.TryNormalize("https://chat.agent42.nl", ["front.example.org", "chat.agent42.nl"]));
    }

    [Fact]
    public void AList_IsCapped_AndDeduplicated()
    {
        var many = string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"https://h{i}.example.com\""));
        var csp = McpAppCsp.Build($$$"""{"csp":{"connectDomains":[{{{many}}},"https://h0.example.com"]}}""", Own);

        var connect = csp.Split("; ").Single(d => d.StartsWith("connect-src ", StringComparison.Ordinal)).Split(' ').Skip(1).ToList();
        Assert.Equal(McpAppCsp.MaxEntries, connect.Count);
        Assert.Equal(connect.Count, connect.Distinct().Count());
    }

    // One bad entry costs only itself: the valid ones around it stay.
    [Fact]
    public void AnInvalidEntry_DoesNotTakeItsNeighboursWithIt()
    {
        var csp = McpAppCsp.Build(
            """{"csp":{"connectDomains":["https://a.example.com","https://evil.example.com; script-src *","https://b.example.com"]}}""",
            Own);

        Assert.Contains("connect-src https://a.example.com https://b.example.com;", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", csp, StringComparison.Ordinal);
    }
}
