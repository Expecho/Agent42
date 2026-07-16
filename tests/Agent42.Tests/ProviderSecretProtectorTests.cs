using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

public sealed class ProviderSecretProtectorTests
{
    private static ProviderSecretProtector NewProtector(IDataProtectionProvider provider) =>
        new(provider, NullLogger<ProviderSecretProtector>.Instance);

    // AC4: a key is persisted as ciphertext and recovered — the round-trip the grain relies on.
    [Fact]
    public void Protect_Unprotect_RoundTrips()
    {
        var protector = NewProtector(new EphemeralDataProtectionProvider());

        const string secret = "ghp_a-very-secret-token-value";
        var ciphertext = protector.Protect(secret);

        Assert.NotEqual(secret, ciphertext);
        Assert.DoesNotContain(secret, ciphertext);
        Assert.Equal(secret, protector.Unprotect(ciphertext));
    }

    [Fact]
    public void Unprotect_BlankOrNull_ReturnsNull()
    {
        var protector = NewProtector(new EphemeralDataProtectionProvider());

        Assert.Null(protector.Unprotect(null));
        Assert.Null(protector.Unprotect(""));
    }

    // A rotated key ring (or corrupt ciphertext) degrades to null rather than throwing — an overlay is
    // best-effort and must never brick resolution (ADR 0018). A DISTINCT ephemeral provider stands in
    // for the rotated ring: it cannot decrypt the first provider's ciphertext.
    [Fact]
    public void Unprotect_UndecryptableCiphertext_ReturnsNull_DoesNotThrow()
    {
        var ciphertext = NewProtector(new EphemeralDataProtectionProvider()).Protect("secret");
        var otherRing = NewProtector(new EphemeralDataProtectionProvider());

        Assert.Null(otherRing.Unprotect(ciphertext));
        Assert.Null(otherRing.Unprotect("not-even-valid-base64-$$$"));
    }
}
