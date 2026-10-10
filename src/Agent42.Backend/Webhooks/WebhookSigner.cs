using System.Security.Cryptography;
using System.Text;

namespace Agent42.Backend;

/// <summary>
/// Secret minting and signing for the outbound webhook (ADR 0094), in the Standard Webhooks scheme so a receiver can
/// verify with an off-the-shelf library: the secret is <c>whsec_</c> + base64 of 32 random bytes, the HMAC key is the
/// DECODED bytes, and the signature is <c>v1,</c> + base64 HMAC-SHA256 over <c>{id}.{timestamp}.{body}</c>.
/// </summary>
public static class WebhookSigner
{
    /// <summary>The prefix every secret carries.</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>A fresh signing secret.</summary>
    public static string GenerateSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The <c>webhook-signature</c> header value for one delivery.</summary>
    public static string Sign(string secret, string deliveryId, long timestamp, string body)
    {
        var key = Convert.FromBase64String(secret.StartsWith(SecretPrefix, StringComparison.Ordinal)
            ? secret[SecretPrefix.Length..]
            : secret);
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{deliveryId}.{timestamp}.{body}"));
        return "v1," + Convert.ToBase64String(mac);
    }
}
