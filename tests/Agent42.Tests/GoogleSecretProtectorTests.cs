using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The third Data Protection purpose (ADR 0053). Mirrors <see cref="McpSecretProtectorTests"/>, plus one
/// property the other two do not need to state: a Google purpose must NOT be able to read an MCP one's
/// ciphertext, because that scope isolation is the whole reason for a third protector.
/// </summary>
public sealed class GoogleSecretProtectorTests
{
    private static GoogleSecretProtector NewProtector(IDataProtectionProvider provider) =>
        new(provider, NullLogger<GoogleSecretProtector>.Instance);

    [Fact]
    public void Protect_Unprotect_RoundTrips()
    {
        var protector = NewProtector(new EphemeralDataProtectionProvider());

        const string secret = "1//0gRefreshTokenThatIsTheWholeConnection";
        var ciphertext = protector.Protect(secret);

        Assert.NotEqual(secret, ciphertext);
        Assert.DoesNotContain(secret, ciphertext, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(ciphertext));
    }

    [Fact]
    public void Unprotect_BlankOrNull_ReturnsNull()
    {
        var protector = NewProtector(new EphemeralDataProtectionProvider());

        Assert.Null(protector.Unprotect(null));
        Assert.Null(protector.Unprotect(""));
    }

    // The failure mode this makes survivable: in prod with an unpersisted key ring, a restart makes
    // every stored refresh token undecryptable. Returning null degrades the user to "not connected" —
    // a throw here would surface as a broken turn instead of an actionable notice.
    [Fact]
    public void Unprotect_UndecryptableCiphertext_ReturnsNull_DoesNotThrow()
    {
        var ciphertext = NewProtector(new EphemeralDataProtectionProvider()).Protect("refresh-token");
        var otherRing = NewProtector(new EphemeralDataProtectionProvider());

        Assert.Null(otherRing.Unprotect(ciphertext));
        Assert.Null(otherRing.Unprotect("not-even-valid-base64-$$$"));
    }

    // The purposes really are isolated: sharing a key ring is not enough to cross scopes. Without this
    // a future refactor could collapse the three protectors into one and nothing would notice.
    [Fact]
    public void GooglePurpose_CannotReadAnMcpSecret_OnTheSameKeyRing()
    {
        var ring = new EphemeralDataProtectionProvider();
        var mcpCiphertext = new McpSecretProtector(ring, NullLogger<McpSecretProtector>.Instance)
            .Protect("an-mcp-header-value");

        Assert.Null(NewProtector(ring).Unprotect(mcpCiphertext));
    }
}
