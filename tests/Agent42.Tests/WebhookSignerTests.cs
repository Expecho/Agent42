using System.Security.Cryptography;
using System.Text;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>Standard Webhooks signing for the outbound webhook (ADR 0094).</summary>
public sealed class WebhookSignerTests
{
    // AC10: the signature is HMAC-SHA256 keyed with the DECODED secret over "{id}.{timestamp}.{body}", computed here
    // independently so the signer and the test cannot share a bug.
    [Fact]
    public void Sign_MatchesIndependentHmac()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var secret = "whsec_" + Convert.ToBase64String(key);
        const string id = "msg_2KWPBgLlAfxdpx2AI54pPJ85f4W";
        const long timestamp = 1_674_087_231;
        const string body = """{"type":"agent.turn.completed","reply":"héllo"}""";

        var signature = WebhookSigner.Sign(secret, id, timestamp, body);

        using var hmac = new HMACSHA256(key);
        var expected = "v1," + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
        Assert.Equal(expected, signature);
    }

    // The published Standard Webhooks test vector, so a receiver's off-the-shelf verifier accepts what we send.
    [Fact]
    public void Sign_MatchesTheStandardWebhooksVector()
    {
        var signature = WebhookSigner.Sign(
            "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", "msg_p5jXN8AQM9LWM0D4loKWxJek", 1_614_265_330,
            """{"test": 2432232314}""");

        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", signature);
    }

    [Fact]
    public void GenerateSecret_IsPrefixedBase64Of32RandomBytes()
    {
        var first = WebhookSigner.GenerateSecret();
        var second = WebhookSigner.GenerateSecret();

        Assert.StartsWith(WebhookSigner.SecretPrefix, first, StringComparison.Ordinal);
        Assert.Equal(32, Convert.FromBase64String(first[WebhookSigner.SecretPrefix.Length..]).Length);
        Assert.NotEqual(first, second);
    }
}
