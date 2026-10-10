using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Agent42.Backend;

/// <summary>
/// Verifies a Mailgun Routes <c>forward()</c> POST (ADR 0095): HMAC-SHA256 over <c>timestamp + token</c> (no separator),
/// keyed by the account's HTTP webhook signing key, hex-encoded, compared to <c>signature</c>.
///
/// <para><b>This compares with <see cref="CryptographicOperations.FixedTimeEquals"/>, unlike <see cref="Agents.ApiKey"/>.</b>
/// <c>ApiKey</c> needs no constant-time compare because it authenticates by a hash-keyed grain lookup, not by comparing
/// a secret. This check is the opposite: a computed HMAC against an attacker-supplied one, the classic timing channel.
/// Do not "harmonise" the two.</para>
///
/// <para>The HMAC covers the timestamp and token only — <b>not the body or the recipient</b>. It proves Mailgun sent
/// <i>a</i> request at that time, not this content, so a captured triple could be replayed with any body. That is why
/// <see cref="MailgunReplayGuard"/> exists, and why the address token, not this signature, is the credential.</para>
/// </summary>
public static class MailgunWebhook
{
    /// <summary>
    /// The oldest timestamp accepted. Mailgun retries a failed forward for 8 hours, and its docs do not say whether a
    /// retry is re-signed, so the window covers the whole span plus slack. A narrower one would turn an hour-long 503
    /// (signing key not yet set) into permanently lost mail.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(8) + TimeSpan.FromMinutes(15);

    /// <summary>How far in the future a timestamp may be (clock skew).</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    /// <summary>The lowercase-hex HMAC-SHA256 Mailgun puts in <c>signature</c>.</summary>
    public static string Sign(string signingKey, string timestamp, string token)
        => Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(timestamp + token)));

    /// <summary>
    /// Whether the triple is signed by <paramref name="signingKey"/> and its timestamp is inside the window. A blank
    /// key never verifies: an unset key must refuse every request, never skip the check.
    /// </summary>
    public static bool IsValid(
        string? signingKey, string? timestamp, string? token, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signingKey) || string.IsNullOrEmpty(timestamp)
            || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        var sent = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (sent < now - MaxAge || sent > now + MaxSkew)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(signingKey, timestamp, token));
        var actual = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
