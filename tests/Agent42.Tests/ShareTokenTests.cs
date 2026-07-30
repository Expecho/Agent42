using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The share-token helper (ADR 0035). The load-bearing property is that <see cref="ShareToken.Hash"/>
/// applied to a freshly-<see cref="ShareToken.Generate">generated</see> raw token equals the hash returned
/// alongside it — the minter and the request-time resolver share this one method, so if they ever diverged
/// an issued link would never resolve.
/// </summary>
public class ShareTokenTests
{
    [Fact]
    public void Generate_RawHashesToTheReturnedHash()
    {
        var (raw, hash) = ShareToken.Generate();

        Assert.Equal(hash, ShareToken.Hash(raw));
    }

    [Fact]
    public void Generate_ProducesAPrefixedTokenAndAHexSha256()
    {
        var (raw, hash) = ShareToken.Generate();

        Assert.StartsWith("shr_", raw);
        // Lowercase hex of a 256-bit digest: 64 chars, hex alphabet only.
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void Generate_IsUniquePerCall()
    {
        var first = ShareToken.Generate();
        var second = ShareToken.Generate();

        Assert.NotEqual(first.Raw, second.Raw);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Hash_IsStableForTheSameInput()
        => Assert.Equal(ShareToken.Hash("shr_example"), ShareToken.Hash("shr_example"));
}
