using System.Security.Cryptography;
using System.Text;

namespace Agent42.Agents;

/// <summary>
/// Generates, hashes and parses per-agent inbound email tokens (ADR 0095). The single home of all three, so the
/// minter and the request-time resolver can never disagree on the digest — the same rule as <see cref="ApiKey"/>.
///
/// <para><b>Why this is not <see cref="ApiKey"/>.</b> An API key is base64url, which is case-sensitive. The local part
/// of an email address is not reliably case-preserving: mail servers and providers normalise it, and a person retyping
/// an address will not keep the case either. A base64url token would hash to a different digest on arrival and every
/// inbound message would be rejected while the address looked correct in the UI. So the token is lowercase base32
/// (RFC 4648 alphabet, no padding) and <see cref="Hash"/> lowercases before hashing, making the whole path
/// case-insensitive end to end.</para>
///
/// <para>The token is a credential with 160 bits of entropy, so a single SHA-256 is the right hash (see
/// <see cref="ApiKey"/>). Unlike an API key it is stored raw as well, because the owner must be able to read the
/// address again to set up a forwarding rule (ADR 0035's share-token reasoning).</para>
/// </summary>
public static class EmailAddress
{
    // A recognisable, greppable prefix on the local part, so a leaked address is identifiable as an agent address.
    // Lowercase only, and part of the hashed material.
    private const string Prefix = "agent-";

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>
    /// Mints a fresh token: <c>agent-</c> plus 20 random bytes (160 bits) as 32 lowercase base32 characters, paired
    /// with its hash. 38 characters, well inside the 64-character local-part limit.
    /// </summary>
    public static (string Token, string Hash) Generate()
    {
        var token = Prefix + Base32(RandomNumberGenerator.GetBytes(20));
        return (token, Hash(token));
    }

    /// <summary>
    /// Lowercase-hex SHA-256 of the lowercased token — the directory grain key. Lowercasing here, not in the caller,
    /// is what makes an address that arrives uppercased still resolve.
    /// </summary>
    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim().ToLowerInvariant())));

    /// <summary>
    /// The token in an address: the local part with any <c>+tag</c> sub-address dropped, or <c>null</c> when there is
    /// no local part. Display names (<c>"Name" &lt;addr&gt;</c>) are unwrapped first.
    /// </summary>
    public static string? TokenFromAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var value = address.Trim();
        var open = value.LastIndexOf('<');
        if (open >= 0 && value.EndsWith('>'))
        {
            value = value[(open + 1)..^1];
        }

        var at = value.LastIndexOf('@');
        var local = at >= 0 ? value[..at] : value;
        var plus = local.IndexOf('+');
        if (plus >= 0)
        {
            local = local[..plus];
        }

        local = local.Trim();
        return local.Length == 0 ? null : local.ToLowerInvariant();
    }

    /// <summary>The full address for <paramref name="token"/> at <paramref name="domain"/>, or "" when either is blank.</summary>
    public static string Format(string? token, string? domain)
        => string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(domain) ? "" : $"{token}@{domain.Trim()}";

    private static string Base32(byte[] bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }
}
