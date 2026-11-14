using System.Net;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The SSRF control for <c>read_web_document</c> (ADR 0066), whose URL is chosen by the model. The
/// address class that matters most is link-local: 169.254.169.254 is Azure IMDS, which hands out
/// managed-identity tokens to anything that can issue an HTTP request from inside the container.
///
/// <para>Restored from the commit that deleted it with ADR 0027. It guards the DOCUMENT path, which since
/// ADR 0125 is the only model-invoked web fetch.</para>
/// </summary>
public sealed class PrivateAddressGuardTests
{
    // AC1 — Azure IMDS, the managed-identity token endpoint. The single most valuable SSRF target here.
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    public void Guard_BlocksImdsAddress(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC1 — loopback reaches the backend itself (and the SearXNG sidecar, which shares the namespace).
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.99.42.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    public void Guard_BlocksLoopback(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC1 — RFC1918, at both edges of each range (the boundaries are where an off-by-one hides), plus
    // CGNAT (100.64/10) and the v6 equivalents of RFC1918 and link-local.
    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    public void Guard_BlocksPrivateRanges(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC3 — the three ways an IPv6 address can carry an IPv4 one past a v6-only check: the mapped form
    // (::ffff:a.b.c.d), the deprecated IPv4-compatible form (::a.b.c.d), and NAT64 (64:ff9b::/96). All
    // three can spell IMDS, and all three pass every IsIPv6* predicate.
    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::169.254.169.254")]      // IPv4-compatible IMDS
    [InlineData("::127.0.0.1")]            // IPv4-compatible loopback
    [InlineData("64:ff9b::a9fe:a9fe")]     // NAT64 IMDS (169.254.169.254)
    [InlineData("64:ff9b::7f00:1")]        // NAT64 loopback
    public void Guard_BlocksMappedAndNat64Forms(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // Multicast and the reserved 240/4 block (which contains the 255.255.255.255 broadcast) are not
    // documents.
    [Theory]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("ff02::1")]
    public void Guard_BlocksMulticastAndReserved(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // The guard must not block the actual web; a fetch tool that refuses everything is useless. The
    // NAT64 row is what keeps Guard_BlocksMappedAndNat64Forms from being satisfiable by "reject v6".
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]     // example.com
    [InlineData("172.32.0.1")]        // just outside 172.16/12
    [InlineData("100.128.0.1")]       // just outside 100.64/10
    [InlineData("2606:4700:4700::1111")]
    [InlineData("64:ff9b::808:808")]  // NAT64 8.8.8.8
    public void Guard_AllowsPublicAddress(string ip) =>
        Assert.True(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));
}
