using Agent42.Backend;
using Microsoft.Extensions.Configuration;

namespace Agent42.Tests;

/// <summary>
/// The owner rule itself (ADR 0121), asserted as a rule rather than only through a host, the
/// <c>SearxngProxyTests</c> shape. The host-level cases live in <see cref="DashboardOwnerGateTests"/>.
/// </summary>
public sealed class OwnerPolicyTests
{
    private const string OwnerOid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    /// <summary>Fail closed: nothing configured makes nobody an owner, including the unauthenticated fallback.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OwnerPolicy_WithNoOwnersConfigured_DeniesEveryone(string? configured)
    {
        var policy = new OwnerPolicy([configured]);

        Assert.False(policy.IsOwner(OwnerOid));
        Assert.False(policy.IsOwner("local-dev"));
        Assert.False(policy.IsOwner(configured));
        Assert.False(OwnerPolicy.None.IsOwner(OwnerOid));
    }

    /// <summary>A GUID in different casing is the same principal; an ordinal compare would lock the owner out.</summary>
    [Fact]
    public void OwnerPolicy_IsCaseInsensitive_ButNotFuzzy()
    {
        var policy = new OwnerPolicy([OwnerOid]);

        Assert.True(policy.IsOwner(OwnerOid.ToUpperInvariant()));
        Assert.False(policy.IsOwner(OwnerOid + "x"));
        Assert.False(policy.IsOwner(""));
        Assert.False(policy.IsOwner(null));
    }

    /// <summary>Binds the committed array shape, ignoring the <c>//</c> comment keys that sit beside it.</summary>
    [Fact]
    public void OwnerPolicy_FromConfiguration_ReadsTheArrayAndIgnoresCommentKeys()
    {
        var policy = OwnerPolicy.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Access:Owners:0"] = OwnerOid,
                    ["Access:Owners:1"] = "",
                    ["Access://Owners"] = "a comment",
                })
                .Build());

        Assert.True(policy.IsOwner(OwnerOid));
        Assert.False(policy.IsOwner("a comment"));
        Assert.False(policy.IsOwner(""));
    }

    /// <summary>
    /// The shipped file, not the rule. <c>local-dev</c> is the no-header fallback identity, so listing it
    /// would make every unauthenticated request an owner. Every shipped entry must also be a GUID: a typo
    /// fails closed and locks the owner out while looking like a broken feature.
    /// </summary>
    [Fact]
    public void ShippedConfiguration_NeverListsTheOfflineFallbackUserAsOwner_AndEveryOwnerIsAGuid()
    {
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(BackendDir(), "appsettings.json"))
            .Build();

        var owners = shipped.GetSection(OwnerPolicy.SectionName).GetChildren().Select(c => c.Value).ToList();

        Assert.NotEmpty(owners);
        Assert.DoesNotContain(owners, o => string.Equals(o, "local-dev", StringComparison.OrdinalIgnoreCase));
        Assert.All(owners, o => Assert.True(Guid.TryParse(o, out _), $"Access:Owners entry '{o}' is not a GUID"));
        Assert.False(OwnerPolicy.FromConfiguration(shipped).IsOwner("local-dev"));
    }

    private static string BackendDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return Path.Combine(dir!.FullName, "src", "Agent42.Backend");
    }
}
