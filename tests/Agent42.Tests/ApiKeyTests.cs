using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The per-agent key helper (ADR 0022). The load-bearing property is that <see cref="ApiKey.Hash"/>
/// applied to a freshly-<see cref="ApiKey.Generate">generated</see> raw key equals the hash returned
/// alongside it — the minter and the request-time resolver share this one method, so if they ever
/// diverged an issued key would never resolve.
/// </summary>
public class ApiKeyTests
{
    [Fact]
    public void Generate_RawHashesToTheReturnedHash()
    {
        var (raw, hash) = ApiKey.Generate();

        Assert.Equal(hash, ApiKey.Hash(raw));
    }

    [Fact]
    public void Generate_ProducesAPrefixedKeyAndAHexSha256()
    {
        var (raw, hash) = ApiKey.Generate();

        Assert.StartsWith("sk_agent_", raw);
        // Lowercase hex of a 256-bit digest: 64 chars, hex alphabet only.
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
    }

    [Fact]
    public void Generate_IsUniquePerCall()
    {
        var first = ApiKey.Generate();
        var second = ApiKey.Generate();

        Assert.NotEqual(first.Raw, second.Raw);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Hash_IsStableForTheSameInput()
        => Assert.Equal(ApiKey.Hash("sk_agent_example"), ApiKey.Hash("sk_agent_example"));
}
