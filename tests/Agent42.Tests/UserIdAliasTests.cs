using System.Text.RegularExpressions;
using Agent42.Backend;
using Microsoft.Extensions.Configuration;

namespace Agent42.Tests;

/// <summary>
/// The identity alias map (ADR 0074): a second Entra object id resolving to the object id that owns the
/// data, so one person's two accounts reach one dataset with no data movement.
///
/// <para>Most of these exercise <see cref="UserIdAliases"/> <b>directly</b> rather than through a host —
/// an alias entry is an authorization grant, so the rule is the security surface and is asserted as a
/// rule, the <c>DashboardOwnerGateTests</c> shape.</para>
/// </summary>
public sealed class UserIdAliasTests
{
    private const string AliasOid = "11111111-1111-1111-1111-111111111111";
    private const string CanonicalOid = "22222222-2222-2222-2222-222222222222";
    private const string ThirdOid = "33333333-3333-3333-3333-333333333333";
    private const string FourthOid = "44444444-4444-4444-4444-444444444444";

    /// <summary>AC1. The whole point: a configured alias reaches the canonical dataset.</summary>
    [Fact]
    public void Resolve_MapsAConfiguredAlias_ToTheCanonicalId()
    {
        var aliases = From((AliasOid, CanonicalOid));

        Assert.Equal(CanonicalOid, aliases.Resolve(AliasOid));
        Assert.Equal(1, aliases.Count);
        Assert.Empty(aliases.DroppedEntries);
    }

    /// <summary>
    /// AC2 (security set). <b>An unlisted principal is never rewritten.</b> If this ever fails, an alias
    /// map is silently redirecting a third party's requests at someone else's data.
    /// </summary>
    [Fact]
    public void Resolve_LeavesAnUnlistedPrincipal_Unchanged()
    {
        var aliases = From((AliasOid, CanonicalOid));

        Assert.Equal(ThirdOid, aliases.Resolve(ThirdOid));
        // The canonical id itself is not a key, so signing in as the data's owner is also untouched.
        Assert.Equal(CanonicalOid, aliases.Resolve(CanonicalOid));
    }

    /// <summary>AC3. No aliases configured — and no section at all — is a pure passthrough.</summary>
    [Fact]
    public void Resolve_WithNoAliasesConfigured_IsAPassthrough()
    {
        var absent = UserIdAliases.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.True(absent.IsEmpty);
        Assert.Equal(AliasOid, absent.Resolve(AliasOid));
        Assert.True(UserIdAliases.Empty.IsEmpty);
        Assert.Equal(AliasOid, UserIdAliases.Empty.Resolve(AliasOid));
    }

    /// <summary>
    /// AC4. An Entra object id is a GUID, and a GUID pasted in different casing is the same principal —
    /// the reason <see cref="DashboardOwnerGate.IsOwner"/> compares ignore-case too. This also pins that
    /// the map is built with an explicit ignore-case comparer: a bare
    /// <c>Get&lt;Dictionary&lt;string,string&gt;&gt;()</c> would return an ordinal one and fail here.
    /// </summary>
    [Fact]
    public void Resolve_MatchesTheAliasIdCaseInsensitively()
    {
        var aliases = From((AliasOid.ToUpperInvariant(), CanonicalOid));

        Assert.Equal(CanonicalOid, aliases.Resolve(AliasOid.ToLowerInvariant()));
    }

    /// <summary>
    /// AC5 (security set). A blank canonical id must never rewrite a principal to the empty string —
    /// that would collapse a real user onto a grain key of <c>"/{conversationId}"</c>.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfiguration_DropsAnEntryWithABlankValue(string canonical)
    {
        var aliases = From((AliasOid, canonical));

        Assert.True(aliases.IsEmpty);
        Assert.Equal(AliasOid, aliases.Resolve(AliasOid));
        Assert.Single(aliases.DroppedEntries);
    }

    /// <summary>AC5, the other side: a blank alias key is refused too.</summary>
    [Fact]
    public void FromConfiguration_DropsAnEntryWithABlankKey()
    {
        var aliases = From(("   ", CanonicalOid));

        Assert.True(aliases.IsEmpty);
        Assert.Single(aliases.DroppedEntries);
    }

    /// <summary>
    /// AC6 (security set). <c>local-dev</c> is <c>CurrentUser.ResolveUserId</c>'s fallback for a request
    /// with no Easy Auth principal header. As a canonical id it would attach a real account to the
    /// unauthenticated identity's data; as an alias key it would rewrite every header-less request. The
    /// <c>Dashboard:OwnerOid</c> <c>//DANGER</c> invariant, applied to both sides.
    /// </summary>
    [Theory]
    [InlineData(UserIdAliases.OfflineFallbackUserId, CanonicalOid)]
    [InlineData(AliasOid, UserIdAliases.OfflineFallbackUserId)]
    [InlineData("LOCAL-DEV", CanonicalOid)]
    public void FromConfiguration_DropsAnyEntryNamingTheOfflineFallbackUser(string alias, string canonical)
    {
        var aliases = From((alias, canonical));

        Assert.True(aliases.IsEmpty);
        Assert.Single(aliases.DroppedEntries);
    }

    /// <summary>AC7 preamble: aliasing an id to itself is inert and is dropped rather than stored.</summary>
    [Fact]
    public void FromConfiguration_DropsASelfAlias()
    {
        var aliases = From((AliasOid, AliasOid));

        Assert.True(aliases.IsEmpty);
        Assert.Single(aliases.DroppedEntries);
    }

    /// <summary>
    /// AC7a (security set). A chain drops <b>every entry in it</b>, never just the head. Resolution is a
    /// single hop, so keeping <c>B→C</c> alive after refusing <c>A→B</c> would be a half-applied
    /// authorization grant — the outcome this rule exists to prevent.
    /// </summary>
    [Fact]
    public void FromConfiguration_DropsEveryEntryOfAChain()
    {
        var aliases = From((AliasOid, CanonicalOid), (CanonicalOid, ThirdOid));

        Assert.True(aliases.IsEmpty);
        Assert.Equal(AliasOid, aliases.Resolve(AliasOid));
        Assert.Equal(CanonicalOid, aliases.Resolve(CanonicalOid));
        Assert.Equal(2, aliases.DroppedEntries.Count);
    }

    /// <summary>AC7b (security set). A cycle is the same defect and is refused the same way.</summary>
    [Fact]
    public void FromConfiguration_DropsACyclicAlias()
    {
        var aliases = From((AliasOid, CanonicalOid), (CanonicalOid, AliasOid));

        Assert.True(aliases.IsEmpty);
        Assert.Equal(2, aliases.DroppedEntries.Count);
    }

    /// <summary>
    /// AC7c (security set). The chain check must be ignore-case, or a chain written with a recased id
    /// evades it while <see cref="UserIdAliases.Resolve"/> still matches it — a chain that resolves.
    /// </summary>
    [Fact]
    public void FromConfiguration_DetectsAChainCaseInsensitively()
    {
        var aliases = From((AliasOid, CanonicalOid.ToUpperInvariant()), (CanonicalOid, ThirdOid));

        Assert.True(aliases.IsEmpty);
    }

    /// <summary>
    /// AC7, the deliberate non-case: <b>fan-in is legitimate and must survive.</b> Two accounts aliased
    /// onto one dataset is the three-accounts-one-person configuration, and it is not a chain — the
    /// canonical id is never itself an alias.
    /// </summary>
    [Fact]
    public void FromConfiguration_KeepsTwoAliasesPointingAtOneCanonicalId()
    {
        var aliases = From((AliasOid, ThirdOid), (CanonicalOid, ThirdOid));

        Assert.Equal(2, aliases.Count);
        Assert.Equal(ThirdOid, aliases.Resolve(AliasOid));
        Assert.Equal(ThirdOid, aliases.Resolve(CanonicalOid));
        Assert.Empty(aliases.DroppedEntries);
    }

    /// <summary>
    /// AC8a. A malformed map degrades to "no alias" and reports; it never throws. Identity aliasing is
    /// not on the availability path, so an app that boots inert is recoverable and one that refuses to
    /// boot is not.
    /// </summary>
    [Fact]
    public void FromConfiguration_WithMalformedEntries_DoesNotThrow()
    {
        var aliases = From(
            ("   ", CanonicalOid),
            (AliasOid, ""),
            (ThirdOid, "not-a-guid"),
            (FourthOid, UserIdAliases.OfflineFallbackUserId));

        Assert.True(aliases.IsEmpty);
        Assert.Equal(4, aliases.DroppedEntries.Count);
    }

    /// <summary>
    /// AC8b (security set). The canonical id is concatenated into the grain key
    /// <c>{userId}/{conversationId}</c>, which <c>AgentGrain</c> splits on the <b>first</b> <c>/</c>, and
    /// it is a blob-path prefix too. Requiring the bare hyphenated GUID form Entra emits makes a
    /// re-partitioned grain key structurally impossible.
    /// </summary>
    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("2da7fa48-e41e-4ebd-b665-96bc4210fefd/../other")]
    [InlineData("../2da7fa48-e41e-4ebd-b665-96bc4210fefd")]
    [InlineData("{2da7fa48-e41e-4ebd-b665-96bc4210fefd}")]
    public void FromConfiguration_DropsAValueThatIsNotABareGuid(string canonical)
    {
        var aliases = From((AliasOid, canonical));

        Assert.True(aliases.IsEmpty);
        Assert.Equal(AliasOid, aliases.Resolve(AliasOid));
        Assert.Single(aliases.DroppedEntries);
    }

    // ————— The shipped map —————

    /// <summary>
    /// AC9. The map this repo actually ships must survive its own validation. Asserting on
    /// <see cref="UserIdAliases.DroppedEntries"/> rather than re-implementing the rules means every rule
    /// added later is covered here for free — and a shipped entry that is silently inert (a typo'd GUID,
    /// a chain) is exactly the failure that would otherwise be discovered by a person not being able to
    /// see their data.
    /// </summary>
    [Fact]
    public void ShippedConfiguration_ContainsNoUnsafeAlias()
    {
        var shipped = UserIdAliases.FromConfiguration(ShippedConfiguration());

        Assert.Empty(shipped.DroppedEntries);
    }

    /// <summary>
    /// AC10 (security set). Every id on either side of a shipped alias must be a principal Easy Auth
    /// already admits — the <c>authAllowedUserObjectIds</c> allowlist in <c>infra/main.bicep</c>.
    ///
    /// <para>This is the strongest check available and it catches what a GUID parse cannot: a
    /// well-formed object id belonging to the wrong person, or a mistyped digit. It cannot prove the
    /// grant is <em>intended</em> — nothing can — but it bounds the blast radius to principals that can
    /// already sign in, which with this allowlist is the operator's own two accounts.</para>
    /// </summary>
    [Fact]
    public void ShippedAliases_NameOnlyPrincipalsEasyAuthAdmits()
    {
        var allowed = EasyAuthAllowedObjectIds();
        var section = ShippedConfiguration().GetSection(UserIdAliases.SectionName).GetChildren();

        foreach (var entry in section.Where(child => !child.Key.StartsWith("//", StringComparison.Ordinal)))
        {
            Assert.True(
                allowed.Contains(entry.Key, StringComparer.OrdinalIgnoreCase),
                $"Alias key '{entry.Key}' is not in authAllowedUserObjectIds in infra/main.bicep — "
                    + "it names a principal Easy Auth will never admit, so the alias is dead config at "
                    + "best and points at the wrong person at worst.");

            Assert.True(
                allowed.Contains(entry.Value, StringComparer.OrdinalIgnoreCase),
                $"Alias '{entry.Key}' resolves to '{entry.Value}', which is not in "
                    + "authAllowedUserObjectIds in infra/main.bicep — an alias must never hand a signed-in "
                    + "user the dataset of a principal that is not even allowed to sign in.");
        }
    }

    private static IConfiguration ShippedConfiguration()
        => new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "src", "Agent42.Backend", "appsettings.json"))
            .Build();

    /// <summary>Reads the literal <c>authAllowedUserObjectIds</c> array out of the Bicep template.</summary>
    private static IReadOnlyList<string> EasyAuthAllowedObjectIds()
    {
        var bicep = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "main.bicep"));
        var start = bicep.IndexOf("param authAllowedUserObjectIds array = [", StringComparison.Ordinal);
        Assert.True(start >= 0, "No authAllowedUserObjectIds parameter found in infra/main.bicep");

        var end = bicep.IndexOf(']', start);
        Assert.True(end > start, "Unterminated authAllowedUserObjectIds array in infra/main.bicep");

        var ids = Regex
            .Matches(bicep[start..end], "'([^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(ids);
        return ids;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    private static UserIdAliases From(params (string Alias, string Canonical)[] entries)
        => UserIdAliases.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(entries.Select(entry =>
                    new KeyValuePair<string, string?>(
                        $"{UserIdAliases.SectionName}:{entry.Alias}", entry.Canonical)))
                .Build());
}
