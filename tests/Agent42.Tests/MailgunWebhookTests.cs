using System.Globalization;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Mailgun signature verification and the replay guard (ADR 0096). The known vector is computed independently of
/// <see cref="MailgunWebhook.Sign"/> (HMAC-SHA256 over timestamp+token, hex), so a bug in Sign cannot hide behind
/// IsValid agreeing with itself.
/// </summary>
public class MailgunWebhookTests
{
    private const string Key = "key-3ax6xnjp29jd6fds4gc373sgvjxteol0";
    private const string Timestamp = "1700000000";
    private const string Token = "a8ce0edb2dd8301dee6c2405235584e45aa91d1e9f979f3de0";

    // HMAC-SHA256(Key, Timestamp + Token), hex — computed with openssl, not with the code under test:
    // printf '%s' "1700000000a8ce...9f3de0" | openssl dgst -sha256 -hmac "key-3ax6...ol0"
    private const string KnownSignature = "bbae38b90b188e8ae408fa55c78fd0a60ed10c03b4812e310ca9f1be258371f5";

    private static readonly DateTimeOffset SentAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void KnownVector_Verifies()
    {
        Assert.Equal(KnownSignature, MailgunWebhook.Sign(Key, Timestamp, Token));
        Assert.True(MailgunWebhook.IsValid(Key, Timestamp, Token, KnownSignature, SentAt));
        Assert.True(MailgunWebhook.IsValid(Key, Timestamp, Token, KnownSignature.ToUpperInvariant(), SentAt));
    }

    [Fact]
    public void TamperedSignatureOrToken_Fails()
    {
        Assert.False(MailgunWebhook.IsValid(Key, Timestamp, Token, KnownSignature[..^1] + "0", SentAt));
        Assert.False(MailgunWebhook.IsValid(Key, Timestamp, Token + "x", KnownSignature, SentAt));
        Assert.False(MailgunWebhook.IsValid(Key, "1700000001", Token, KnownSignature, SentAt));
        Assert.False(MailgunWebhook.IsValid(Key, Timestamp, Token, KnownSignature[..10], SentAt));
    }

    [Theory]
    [InlineData(-8 * 60 - 14, true)] // inside the 8h15m retry window
    [InlineData(-8 * 60 - 16, false)] // beyond it
    [InlineData(4, true)] // small clock skew
    [InlineData(6, false)] // a timestamp too far in the future
    public void Timestamp_IsCheckedAgainstTheRetryWindow(int offsetMinutes, bool expected)
    {
        var now = SentAt.AddMinutes(-offsetMinutes);

        Assert.Equal(expected, MailgunWebhook.IsValid(Key, Timestamp, Token, KnownSignature, now));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BlankSigningKey_NeverVerifies(string? key)
        => Assert.False(MailgunWebhook.IsValid(key, Timestamp, Token, MailgunWebhook.Sign(key ?? "", Timestamp, Token), SentAt));

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-5")]
    [InlineData("99999999999999999999")]
    public void MalformedTimestamp_Fails(string timestamp)
        => Assert.False(MailgunWebhook.IsValid(Key, timestamp, Token, MailgunWebhook.Sign(Key, timestamp, Token), SentAt));

    [Fact]
    public void ReplayGuard_ClaimsOnce_ReleaseAllowsARetry_AndExpires()
    {
        var guard = new MailgunReplayGuard();

        Assert.True(guard.TryClaim("t1", SentAt));
        Assert.False(guard.TryClaim("t1", SentAt));

        guard.Release("t1");
        Assert.True(guard.TryClaim("t1", SentAt));

        // Fill past the prune threshold with tokens that have all expired by "later": pruning frees them.
        for (var i = 0; i < MailgunReplayGuard.Capacity / 2; i++)
        {
            guard.TryClaim($"old-{i.ToString(CultureInfo.InvariantCulture)}", SentAt);
        }

        var later = SentAt + MailgunWebhook.MaxAge + MailgunWebhook.MaxSkew + TimeSpan.FromSeconds(1);
        Assert.True(guard.TryClaim("t1", later));
    }
}
