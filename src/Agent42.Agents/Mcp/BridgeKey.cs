using System.Security.Cryptography;
using System.Text;

namespace Agent42.Agents;

/// <summary>
/// Generates and hashes local-bridge keys (ADR 0076) — the credential a user's
/// <c>Agent42.LocalBridge</c> helper presents in <c>X-Api-Key</c> when it dials <c>/api/local-bridge</c>.
/// The single home of both operations, so the minter and the upgrade-time resolver can never disagree.
///
/// <para>A copy of <see cref="ApiKey"/>'s shape (the precedent <see cref="ShareToken"/> set): a
/// 256-bit random secret, so a single SHA-256 is the right hash and there is no timing side channel to
/// defend (authentication is a hash-keyed grain lookup). Like <see cref="ApiKey"/> and unlike
/// <see cref="ShareToken"/>, only the hash is ever persisted — the raw key is shown once.</para>
/// </summary>
public static class BridgeKey
{
    // Greppable, and distinct from `sk_agent_`, so a leaked key says which door it opens.
    private const string Prefix = "sk_bridge_";

    /// <summary>Mints a fresh key and its hash. The raw key is returned exactly once.</summary>
    public static (string Raw, string Hash) Generate()
    {
        var raw = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    /// <summary>Lowercase-hex SHA-256 of the raw key's UTF-8 bytes — the directory grain key.</summary>
    public static string Hash(string raw)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
