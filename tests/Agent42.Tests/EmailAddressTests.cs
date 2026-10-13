using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The inbound email token helper (ADR 0096). Beyond <see cref="ApiKeyTests"/>' minter/resolver agreement, the
/// load-bearing property is case-insensitivity: a local part that arrives uppercased must hash to the same digest, or
/// every forwarded message would be rejected.
/// </summary>
public class EmailAddressTests
{
    [Fact]
    public void Generate_TokenHashesToTheReturnedHash()
    {
        var (token, hash) = EmailAddress.Generate();

        Assert.Equal(hash, EmailAddress.Hash(token));
    }

    [Fact]
    public void Generate_IsUniquePerCall()
    {
        var first = EmailAddress.Generate();
        var second = EmailAddress.Generate();

        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Generate_IsAPrefixedLowercaseBase32Token()
    {
        var (token, hash) = EmailAddress.Generate();

        Assert.Matches("^agent-[a-z2-7]{32}$", token);
        Assert.True(token.Length <= 64, "a local part is at most 64 characters");
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Hash_IsStableAndCaseInsensitive()
    {
        var (token, hash) = EmailAddress.Generate();

        Assert.Equal(hash, EmailAddress.Hash(token));
        Assert.Equal(hash, EmailAddress.Hash(token.ToUpperInvariant()));
        Assert.Equal(hash, EmailAddress.Hash(EmailAddress.TokenFromAddress(token.ToUpperInvariant() + "@IN.EXAMPLE.COM")!));
    }

    [Theory]
    [InlineData("agent-abc@in.example.com", "agent-abc")]
    [InlineData("AGENT-ABC@in.example.com", "agent-abc")]
    [InlineData("agent-abc+invoices@in.example.com", "agent-abc")]
    [InlineData("\"My Agent\" <agent-abc@in.example.com>", "agent-abc")]
    [InlineData("  agent-abc@in.example.com  ", "agent-abc")]
    [InlineData("agent-abc", "agent-abc")]
    [InlineData("@in.example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TokenFromAddress_ExtractsTheLowercasedLocalPart(string? address, string? expected)
        => Assert.Equal(expected, EmailAddress.TokenFromAddress(address));

    [Theory]
    [InlineData("agent-abc", "in.example.com", "agent-abc@in.example.com")]
    [InlineData("agent-abc", "", "")]
    [InlineData("", "in.example.com", "")]
    public void Format_JoinsTokenAndDomain(string token, string domain, string expected)
        => Assert.Equal(expected, EmailAddress.Format(token, domain));
}
