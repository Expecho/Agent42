using System.Security.Cryptography;
using System.Text;

namespace Agent42.Agents.Sharing;

/// <summary>
/// Generates and hashes read-only conversation share tokens (ADR 0035). The single home of both
/// operations, so a caller can never hash a token differently than the minter did — <see cref="Hash"/>
/// is used both when a link is created (to key the directory grain) and when an incoming
/// <c>/api/share/{token}</c> is resolved (to look it back up), and the two must agree byte-for-byte.
///
/// <para>Modelled on <see cref="ApiKey"/>: tokens are high-entropy (32 random bytes), so a single
/// <b>SHA-256</b> is the right hash (a slow password KDF defends low-entropy secrets, which does not
/// apply), and authentication is a hash-keyed grain lookup, not a secret-string comparison, so there
/// is no timing side channel to defend with <c>FixedTimeEquals</c>.</para>
///
/// <para>Unlike an <see cref="ApiKey"/>, the RAW token is persisted by <see cref="UserShareLinksGrain"/>
/// (not just its hash): the owner legitimately re-displays and copies the link repeatedly, and it is a
/// low-sensitivity, single-conversation, read-only capability the owner already has full access to.</para>
/// </summary>
public static class ShareToken
{
    // A recognizable, greppable prefix on the raw token (like a GitHub `ghp_` token), so a leaked token
    // is identifiable in logs and a caller can sanity-check what they pasted. The prefix is part of the
    // hashed material — it is just the leading bytes of the string that gets hashed.
    private const string Prefix = "shr_";

    /// <summary>
    /// Mints a fresh token: a cryptographically-random 256-bit secret behind the <c>shr_</c> prefix,
    /// paired with its hash (the directory grain key).
    /// </summary>
    public static (string Raw, string Hash) Generate()
    {
        var raw = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    /// <summary>Lowercase-hex SHA-256 of the raw token's UTF-8 bytes — the directory grain key.</summary>
    public static string Hash(string raw)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    // URL-safe base64 with padding stripped, so the token is a clean URL fragment with no '+', '/', or '='.
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
