using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Pins how grain state is ACTUALLY persisted, through the <see cref="IGrainStorageSerializer"/> the
/// silo really resolves — not a hand-built <c>Serializer&lt;T&gt;</c>.
///
/// <para>This distinction is the whole reason the test exists. ADR 0013 left "which serializer does
/// Cosmos grain storage use?" open, and <see cref="SerializationCompatibilityTests"/> cannot answer it:
/// those golden blobs go through Orleans' BINARY serializer, which is what grain-to-grain calls use.
/// Cosmos does not. It uses <c>JsonGrainStorageSerializer</c>, which writes the full CLR type name into
/// every document as a <c>$type</c> discriminator. That makes the NAMESPACE of every persisted type
/// part of the on-disk format — a fact no binary-serializer test can surface, and one that nearly
/// shipped a data-loss bug during ADR 0039.</para>
///
/// <para>Nothing else in the suite covers this path, so if these tests are ever deleted, the namespace
/// of a persisted type becomes silently changeable again.</para>
/// </summary>
public sealed class GrainStorageSerializerTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    private IGrainStorageSerializer Serializer() =>
        _cluster.GetSiloServiceProvider(_cluster.Primary.SiloAddress).GetRequiredService<IGrainStorageSerializer>();

    // The persisted format is JSON carrying assembly-qualified type names. Asserted explicitly, because
    // every other test in this file only makes sense once this is true — and because a future Orleans
    // upgrade that switched the default to binary would make the namespace freeze unnecessary, which is
    // worth noticing deliberately rather than discovering.
    [Fact]
    public void GrainState_IsPersistedAsJson_WithAssemblyQualifiedTypeNames()
    {
        var json = System.Text.Encoding.UTF8.GetString(Serializer().Serialize(GoldenStates.AgentGrain()).ToArray());

        Assert.Contains("\"$type\":\"Agent42.Agents.AgentGrainState, Agent42.Agents\"", json, StringComparison.Ordinal);
    }

    /// <summary>Every persisted state root, with the exact <c>$type</c> string its documents carry.</summary>
    public static TheoryData<string> PersistedTypeNames() =>
    [
        "Agent42.Agents.AgentGrainState",
        "Agent42.Agents.AgentRegistryState",
        "Agent42.Agents.AgentKeyDirectoryState",
        "Agent42.Agents.ConversationIndexState",
        "Agent42.Agents.ShareLinkDirectoryState",
        "Agent42.Agents.UsageRollupState",
        "Agent42.Agents.UserMcpConfigState",
        "Agent42.Agents.UserMcpOAuthPendingState",
        "Agent42.Agents.UserProfileState",
        "Agent42.Agents.UserProviderConfigState",
        "Agent42.Agents.UserPushState",
        "Agent42.Agents.UserShareLinksState",
    ];

    // The type name a document is keyed on must keep resolving. Hard-coded literals, deliberately: a
    // test that derived the name via typeof(T).FullName would move with any namespace change and prove
    // nothing — which is precisely the failure mode being guarded.
    [Theory]
    [MemberData(nameof(PersistedTypeNames))]
    public void PersistedTypeName_StillResolves(string typeName)
    {
        var resolved = typeof(AgentGrainState).Assembly.GetType(typeName);

        Assert.True(
            resolved is not null,
            $"'{typeName}' no longer resolves. Every Cosmos document written for it carries this exact "
                + "string in its $type discriminator; changing the type's namespace strands all of them "
                + "(ADR 0039).");
    }

    // The end-to-end property, stated the way it actually matters: a document written by the PREVIOUS
    // deployment must still load. The fixture is a real serialized payload, so this fails for any change
    // that alters the persisted shape — namespace, type name, or member layout.
    [Fact]
    public void DocumentWrittenByAnEarlierDeployment_StillDeserializes()
    {
        var restored = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));

        Assert.Equal("foundry:gpt-5.4", restored.DeploymentName);
        Assert.Equal("agent-golden", restored.AgentId);
        Assert.Equal("Golden Agent", restored.AgentName);
        Assert.Equal("""{"messages":[{"role":"user","text":"golden"}]}""", restored.SerializedSession);
        Assert.Equal(["web_search", "mcp:server-1"], restored.EnabledTools);

        // Nested types carry their own $type, so they are the other half of the same guarantee.
        var reminder = Assert.Single(restored.ScheduledPrompts).Value;
        Assert.Equal("daily-standup", reminder.Name);
        Assert.Equal(7, reminder.TimesFired);

        var attachment = Assert.Single(Assert.Single(restored.TurnAttachments));
        Assert.Equal("att-1", attachment.Id);
        Assert.Equal("diagram.png", attachment.FileName);

        var generated = Assert.Single(restored.GeneratedImages);
        Assert.Equal("msg-9", generated.Key);
        Assert.Equal("gen-1", generated.Value.Id);
    }

    /// <summary>
    /// A real <c>agent-state</c> payload, captured verbatim from the configured serializer. Committed
    /// rather than generated at run time: a fixture the test writes for itself would agree with whatever
    /// the code currently does and could never fail.
    /// </summary>
    private const string StoredAgentGrainStateJson = """
        {"$id":"1","$type":"Agent42.Agents.AgentGrainState, Agent42.Agents","SerializedSession":"{\"messages\":[{\"role\":\"user\",\"text\":\"golden\"}]}","ScheduledPrompts":{"$id":"2","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Agent42.Agents.ReminderInfo, Agent42.Agents]], System.Private.CoreLib","daily-standup":{"$id":"3","$type":"Agent42.Agents.ReminderInfo, Agent42.Agents","Name":"daily-standup","Prompt":"Summarise the standup","DueInMinutes":90.5,"RepeatEveryMinutes":1440.25,"CreatedAt":"2026-01-02T03:04:05+00:00","TimesFired":7}},"DeploymentName":"foundry:gpt-5.4","AgentId":"agent-golden","AgentName":"Golden Agent","SystemPrompt":"You are the golden fixture.","TurnAttachments":{"$id":"4","$type":"System.Collections.Generic.List`1[[System.Collections.Generic.List`1[[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib]], System.Private.CoreLib","$values":[{"$id":"5","$type":"System.Collections.Generic.List`1[[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib","$values":[{"$id":"6","$type":"Agent42.Agents.AttachmentRef, Agent42.Agents","Id":"att-1","BlobPath":"user/conv/att-1.png","MediaType":"image/png","FileName":"diagram.png"}]}]},"DocumentNames":{"$id":"7","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["handbook.pdf"]},"FetchableUrls":{"$id":"8","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["https://example.com/a"]},"GeneratedImages":{"$id":"9","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib","msg-9":{"$id":"10","$type":"Agent42.Agents.AttachmentRef, Agent42.Agents","Id":"gen-1","BlobPath":"user/conv/gen-1.png","MediaType":"image/png","FileName":"generated.png"}},"EnabledTools":{"$id":"11","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["web_search","mcp:server-1"]}}
        """;
}
