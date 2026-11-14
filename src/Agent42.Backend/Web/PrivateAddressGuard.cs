using System.Net;
using System.Net.Sockets;

namespace Agent42.Backend;

/// <summary>
/// Decides whether an IP address is a legitimate target for <c>read_web_document</c> — i.e. a public
/// internet address and not something inside our own network boundary.
///
/// This is the SSRF control for a tool whose URL is chosen by the <b>model</b>, and it is not a
/// formality. The container app runs with a system-assigned managed identity holding Cosmos, Storage
/// and Azure OpenAI rights, and Azure's IMDS endpoint (<c>169.254.169.254</c>) will mint tokens for
/// that identity to anything able to make an HTTP request from inside the container. A document the
/// model was told to read is exactly such a request — and since <c>/api/agent-run</c> is on
/// <c>excludedPaths</c> (ADR 0022), a URL in its prompt is whitelisted for fetching by an API key
/// holder with no browser session at all.
///
/// <b>Where this runs matters as much as what it checks.</b> It must be applied to the address
/// actually being dialled — <c>SocketsHttpHandler.ConnectCallback</c>, see
/// <see cref="GuardedDocumentFetcher.CreateHandler"/> — never to a hostname's DNS answer before handing
/// the hostname to <see cref="HttpClient"/>. That pattern is TOCTOU-broken: the socket resolves a second
/// time and the attacker's second answer is the address you just rejected. Validating at connect time
/// also covers every redirect hop for free.
///
/// <b>This guard belongs to the document path (ADR 0066), the only model-invoked web fetch.</b>
/// </summary>
public static class PrivateAddressGuard
{
    /// <summary>
    /// True when <paramref name="address"/> is a public, routable internet address the fetcher may
    /// connect to. Everything else — loopback, link-local (incl. IMDS), private, CGNAT, unspecified,
    /// multicast — is refused.
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // An IPv4-mapped IPv6 address (::ffff:127.0.0.1) reaches the same host as the IPv4 address it
        // wraps, so it must be judged as that address — not as an opaque v6 address that passes the
        // v6 checks below.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIpv4(address),
            AddressFamily.InterNetworkV6 => IsPublicIpv6(address),
            // Anything that isn't IP (Unix sockets, IPX, …) is not a document.
            _ => false,
        };
    }

    private static bool IsPublicIpv4(IPAddress address)
    {
        Span<byte> b = stackalloc byte[4];
        if (!address.TryWriteBytes(b, out _))
        {
            return false;
        }

        return b[0] switch
        {
            // 0.0.0.0/8 — "this network"; 0.0.0.0 also means "any local address" to the stack.
            0 => false,
            // 10.0.0.0/8 — private.
            10 => false,
            // 127.0.0.0/8 — loopback.
            127 => false,
            // 169.254.0.0/16 — link-local. This is the one that matters most: it contains Azure's
            // IMDS endpoint 169.254.169.254, the managed-identity token source.
            169 when b[1] == 254 => false,
            // 172.16.0.0/12 — private.
            172 when b[1] >= 16 && b[1] <= 31 => false,
            // 192.168.0.0/16 — private.
            192 when b[1] == 168 => false,
            // 100.64.0.0/10 — carrier-grade NAT / shared address space.
            100 when b[1] >= 64 && b[1] <= 127 => false,
            // 224.0.0.0/4 multicast and 240.0.0.0/4 reserved (incl. 255.255.255.255 broadcast).
            >= 224 => false,
            _ => true,
        };
    }

    private static bool IsPublicIpv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal ||       // fe80::/10
            address.IsIPv6Multicast ||       // ff00::/8
            address.IsIPv6SiteLocal ||       // fec0::/10 (deprecated, still refused)
            address.IsIPv6UniqueLocal ||     // fc00::/7  — the v6 equivalent of RFC1918
            address.Equals(IPAddress.IPv6Loopback) ||    // ::1
            address.Equals(IPAddress.IPv6Any))           // ::
        {
            return false;
        }

        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out _))
        {
            return false;
        }

        // Two more ways an IPv6 address can carry an IPv4 one past the checks above. Neither is
        // reachable on Container Apps today; both are two lines, and the alternative is trusting that
        // it stays that way.
        //
        // IPv4-compatible (::a.b.c.d — deprecated, but still routed by some stacks): the first 12
        // bytes are zero and it is not ::/:: itself. Judge it as the IPv4 address it embeds, so
        // ::169.254.169.254 cannot smuggle IMDS through.
        var first12Zero = !b[..12].ContainsAnyExcept((byte)0);
        if (first12Zero)
        {
            return IsPublicIpv4(new IPAddress(b[12..].ToArray()));
        }

        // NAT64 (64:ff9b::/96) maps IPv4 space into IPv6; 64:ff9b::a9fe:a9fe is IMDS.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
        {
            return IsPublicIpv4(new IPAddress(b[12..].ToArray()));
        }

        return true;
    }
}
