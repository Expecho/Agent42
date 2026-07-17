using System.Security.Cryptography;
using System.Text;

namespace Agent42.Agents;

/// <summary>
/// Generates and hashes per-agent API keys (ADR 0022). The single home of both operations, so a
/// caller can never hash a key differently than the minter did — <see cref="Hash"/> is used both
/// when a key is created (to store its digest) and when an incoming <c>X-Api-Key</c> is resolved
/// (to look up the directory grain), and the two must agree byte-for-byte.
///
/// <para>Keys are high-entropy (32 random bytes), so a single <b>SHA-256</b> is the right hash: a
/// slow password KDF (bcrypt/PBKDF2) defends low-entropy secrets against brute force, which does
/// not apply here. Authentication is a hash-keyed grain lookup, not a secret-string comparison, so
/// there is no timing side channel to defend with <c>FixedTimeEquals</c> either.</para>
/// </summary>
public static class ApiKey
{
    // A recognizable, greppable prefix on the raw key (like a GitHub `ghp_` token), so a leaked key
    // is identifiable in logs/config and a caller can sanity-check what they pasted. The prefix is
    // part of the hashed material — it is just the leading bytes of the string that gets hashed.
    private const string Prefix = "sk_agent_";

    /// <summary>
    /// Mints a fresh key: a cryptographically-random 256-bit secret behind the <c>sk_agent_</c>
    /// prefix, paired with its hash. The raw key is returned exactly once (never persisted); only
    /// the hash is stored.
    /// </summary>
    public static (string Raw, string Hash) Generate()
    {
        var raw = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    /// <summary>Lowercase-hex SHA-256 of the raw key's UTF-8 bytes — the directory grain key.</summary>
    public static string Hash(string raw)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    // URL-safe base64 with padding stripped, so the key is a clean token with no '+', '/', or '='.
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
