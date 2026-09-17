using Orleans.Runtime;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// Pins the grain ids of every persisted grain, because <c>GrainId</c> — not <c>[Alias]</c> — is what
/// keys a Cosmos document: the item id is <c>{GrainType}/{key}</c>. No grain in this repo declares
/// <c>[GrainType]</c>, so every type name falls through to Orleans' naming convention. If that
/// convention ever consults the namespace (it does not today), a folder move would silently orphan
/// every conversation in production, and nothing else in the suite would notice.
/// <para>The expected strings are deliberately hard-coded literals captured from the pre-move tree,
/// NOT computed from the type — a symmetric test would move with the code and prove nothing.</para>
/// </summary>
public sealed class GrainIdentityTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    /// <summary>
    /// The grain type token of every persisted grain, frozen. A rename of any of these classes — or a
    /// convention change that pulls in the namespace — changes the Cosmos item id and strands state.
    /// </summary>
    public static TheoryData<string, string> GrainTypeTokens() => new()
    {
        { nameof(IAgentGrain), "agent" },
        { nameof(IAgentRegistryGrain), "agentregistry" },
        { nameof(IAgentKeyDirectoryGrain), "agentkeydirectory" },
        { nameof(IConversationIndexGrain), "conversationindex" },
        { nameof(ILocalBridgeKeyDirectoryGrain), "localbridgekeydirectory" },
        { nameof(IShareLinkDirectoryGrain), "sharelinkdirectory" },
        { nameof(IUsageRollupGrain), "usagerollup" },
        { nameof(IUserMcpConfigGrain), "usermcpconfig" },
        { nameof(IUserMcpOAuthPendingGrain), "usermcpoauthpending" },
        { nameof(IUserProfileGrain), "userprofile" },
        { nameof(IUserProviderConfigGrain), "userproviderconfig" },
        { nameof(IUserPushGrain), "userpush" },
        { nameof(IUserShareLinksGrain), "usersharelinks" },
    };

    /// <summary>Resolves one reference per grain interface, keyed identically, so only the type token varies.</summary>
    private GrainId Reference(string interfaceName) => interfaceName switch
    {
        nameof(IAgentGrain) => Cluster.GrainFactory.GetGrain<IAgentGrain>(Key).GetGrainId(),
        nameof(IAgentRegistryGrain) => Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(Key).GetGrainId(),
        nameof(IAgentKeyDirectoryGrain) => Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(Key).GetGrainId(),
        nameof(IConversationIndexGrain) => Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(Key).GetGrainId(),
        nameof(ILocalBridgeKeyDirectoryGrain) => Cluster.GrainFactory.GetGrain<ILocalBridgeKeyDirectoryGrain>(Key).GetGrainId(),
        nameof(IShareLinkDirectoryGrain) => Cluster.GrainFactory.GetGrain<IShareLinkDirectoryGrain>(Key).GetGrainId(),
        nameof(IUsageRollupGrain) => Cluster.GrainFactory.GetGrain<IUsageRollupGrain>(Key).GetGrainId(),
        nameof(IUserMcpConfigGrain) => Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(Key).GetGrainId(),
        nameof(IUserMcpOAuthPendingGrain) => Cluster.GrainFactory.GetGrain<IUserMcpOAuthPendingGrain>(Key).GetGrainId(),
        nameof(IUserProfileGrain) => Cluster.GrainFactory.GetGrain<IUserProfileGrain>(Key).GetGrainId(),
        nameof(IUserProviderConfigGrain) => Cluster.GrainFactory.GetGrain<IUserProviderConfigGrain>(Key).GetGrainId(),
        nameof(IUserPushGrain) => Cluster.GrainFactory.GetGrain<IUserPushGrain>(Key).GetGrainId(),
        nameof(IUserShareLinksGrain) => Cluster.GrainFactory.GetGrain<IUserShareLinksGrain>(Key).GetGrainId(),
        _ => throw new ArgumentOutOfRangeException(nameof(interfaceName), interfaceName, "Unmapped grain interface."),
    };

    /// <summary>A key shaped like a real one: the conversation grains are keyed <c>{userId}/{conversationId}</c>.</summary>
    private const string Key = "user/conv";

    // AC1: the grain type token of every persisted grain is unchanged by the folder move.
    [Theory]
    [MemberData(nameof(GrainTypeTokens))]
    public void GrainType_IsUnchanged(string interfaceName, string expectedType)
    {
        var id = Reference(interfaceName);

        Assert.Equal(expectedType, id.Type.ToString());
    }

    // AC1: the full Cosmos item id — type AND key together — is unchanged. The '/' inside the key is
    // carried through verbatim rather than escaped, so the composite id is literally "agent/{userId}/{conversationId}".
    // That whole string is the persisted document identity, which is why it is asserted entire, not just by part.
    [Fact]
    public void AgentGrainId_IsUnchanged_IncludingTheKey()
    {
        var id = Cluster.GrainFactory.GetGrain<IAgentGrain>("user/conv").GetGrainId();

        Assert.Equal("agent/user/conv", id.ToString());
    }
}
