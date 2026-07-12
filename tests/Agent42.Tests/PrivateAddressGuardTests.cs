using System.Net;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The SSRF control for <c>fetch_web_page</c>, whose URL is chosen by the model. The address class
/// that matters most is link-local: 169.254.169.254 is Azure IMDS, which hands out managed-identity
/// tokens to anything that can issue an HTTP request from inside the container.
/// </summary>
public sealed class PrivateAddressGuardTests
{
    // AC6 — Azure IMDS, the managed-identity token endpoint. The single most valuable SSRF target here.
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    public void Rejects_ImdsAddress(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC6 — loopback reaches the backend itself (and the SearXNG sidecar, which shares the namespace).
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.99.42.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    public void Rejects_Loopback(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC6 — RFC1918, at both edges of each range (the boundaries are where an off-by-one hides).
    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.254")]
    public void Rejects_PrivateRanges(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC6 — 100.64.0.0/10 carrier-grade NAT.
    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    public void Rejects_Cgnat(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC6 — fc00::/7, the IPv6 equivalent of RFC1918, plus v6 link-local.
    [Theory]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")]
    public void Rejects_UniqueLocalIpv6(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC6 — the bypass a naive guard misses: ::ffff:127.0.0.1 is an IPv6 address that reaches IPv4
    // loopback, and it passes every IPv6-only check. It must be judged as the v4 address it wraps.
    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    public void Rejects_Ipv4MappedLoopback(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // AC7 — the guard must not block the actual web; a fetch tool that refuses everything is useless.
    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]     // example.com
    [InlineData("172.32.0.1")]        // just outside 172.16/12
    [InlineData("100.128.0.1")]       // just outside 100.64/10
    [InlineData("2606:4700:4700::1111")]
    public void Allows_PublicAddress(string ip) =>
        Assert.True(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // The other two ways an IPv6 address can carry an IPv4 one past a v6-only check: the deprecated
    // IPv4-compatible form (::a.b.c.d) and NAT64 (64:ff9b::/96). Both can spell IMDS.
    [Theory]
    [InlineData("::169.254.169.254")]      // IPv4-compatible IMDS
    [InlineData("::127.0.0.1")]            // IPv4-compatible loopback
    [InlineData("64:ff9b::a9fe:a9fe")]     // NAT64 IMDS (169.254.169.254)
    [InlineData("64:ff9b::7f00:1")]        // NAT64 loopback
    public void Rejects_Ipv4CompatibleAndNat64(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // ...but the same forms wrapping a public address stay allowed, so the check isn't just "reject v6".
    [Theory]
    [InlineData("64:ff9b::808:808")]       // NAT64 8.8.8.8
    public void Allows_Nat64PublicAddress(string ip) =>
        Assert.True(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));

    // Multicast and the reserved 240/4 block (which contains the 255.255.255.255 broadcast) are not pages.
    [Theory]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("ff02::1")]
    public void Rejects_MulticastAndReserved(string ip) =>
        Assert.False(PrivateAddressGuard.IsPubliclyRoutable(IPAddress.Parse(ip)));
}
