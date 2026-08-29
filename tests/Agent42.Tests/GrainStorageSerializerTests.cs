using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

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
public sealed class GrainStorageSerializerTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private IGrainStorageSerializer Serializer() =>
        Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress).GetRequiredService<IGrainStorageSerializer>();

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
        "Agent42.Agents.UserGoogleOAuthPendingState",
        "Agent42.Agents.UserGoogleState",
        "Agent42.Agents.UserMcpConfigState",
        "Agent42.Agents.UserMcpOAuthPendingState",
        "Agent42.Agents.UserProfileState",
        "Agent42.Agents.UserProviderConfigState",
        "Agent42.Agents.UserPushState",
        "Agent42.Agents.UserShareLinksState",
        "Agent42.Agents.UserSkillsState",
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
    /// State a grain builds <b>itself</b> and writes, round-tripped through the real Cosmos serializer.
    ///
    /// <para><b>The trap.</b> A collection expression targeting <c>IReadOnlyList&lt;T&gt;</c> does not compile
    /// to a <c>List&lt;T&gt;</c>. Measured on .NET 10 it compiles to a compiler-internal type —
    /// <c>&lt;&gt;z__ReadOnlyList`1</c> for a spread of unknown length, <c>&lt;&gt;z__ReadOnlyArray`1</c> for a
    /// plain spread or multi-element literal, <c>&lt;&gt;z__ReadOnlySingleElementList`1</c> for a one-element
    /// literal. <c>JsonGrainStorageSerializer</c> writes that name into the document's <c>$type</c> and then
    /// <b>cannot construct it on read</b>: "Unable to find a constructor to use for type". The document is not
    /// subtly wrong, it is unreadable, and it strands the entire state root.</para>
    ///
    /// <para><b>Why it is nevertheless rare, and where it actually bites — established by measurement, not by
    /// reading the code.</b> Almost every persisted list is built in a request handler and reaches the grain as
    /// a CALL ARGUMENT. Orleans' binary serializer round-trips arguments across that boundary and rebuilds them
    /// as a plain <c>List&lt;T&gt;</c>, so the compiler-internal type never survives to the write. Verified: with
    /// <c>AgentApi.Users.cs</c> deliberately reverted to a bare collection expression,
    /// <c>ToolSelectionApiTests.ProfileToolSave_IsReadableBackFromStorage</c> still passed and the stored
    /// runtime type read back as <c>System.Collections.Generic.List`1[System.String]</c>. So the handlers are
    /// NOT at risk, and "fixing" them is churn.</para>
    ///
    /// <para><b>The exposed case is state a grain constructs INSIDE itself</b>, after the boundary, and writes
    /// directly — there is no serializer in between to launder it. That is exactly why
    /// <c>AgentGrain.BuildAgent.cs</c> carries a lone <c>?.ToList()</c> that looks gratuitous next to every
    /// handler that has none, and why <c>UserMcpConfigGrain.RecordToolNamesAsync</c> needs one too. This test
    /// covers that class of write; see the gotcha for the rule.</para>
    /// </summary>
    [Fact]
    public void StateBuiltInsideAGrain_WithACollectionExpression_CannotBeReadBack()
    {
        IReadOnlyList<string> discovered = ["alpha", "beta"];

        // Exactly what RecordToolNamesAsync would produce with `KnownToolNames = [.. toolNames]`.
        var broken = new UserMcpConfigState
        {
            Config = new UserMcpConfig(
                new List<StoredMcpServer>
                {
                    new("s1", "n", "https://e/mcp", nameof(McpAuthMode.None), Enabled: true,
                        KnownToolNames: [.. discovered]),
                },
                1),
        };

        var ex = Record.Exception(() => Serializer().Deserialize<UserMcpConfigState>(Serializer().Serialize(broken)));

        // The hazard is real and this is what it looks like. If this ever stops throwing, .NET changed how
        // collection expressions materialise (or Newtonsoft learned to construct them) — at which point the
        // .ToList() calls guarding it can be revisited, and not before.
        Assert.NotNull(ex);
        Assert.Contains("Unable to find a constructor to use", (ex.InnerException ?? ex).Message, StringComparison.Ordinal);
    }

    // The same write built the way the grain actually builds it — .ToList() — is readable. This is the half
    // that fails if UserMcpConfigGrain.RecordToolNamesAsync regresses to a collection expression.
    [Fact]
    public void KnownToolNames_WrittenWithToList_SurviveTheCosmosJsonRoundTrip()
    {
        IReadOnlyList<string> discovered = ["alpha", "beta"];

        var state = new UserMcpConfigState
        {
            Config = new UserMcpConfig(
                new List<StoredMcpServer>
                {
                    new("s1", "n", "https://e/mcp", nameof(McpAuthMode.None), Enabled: true,
                        KnownToolNames: discovered.ToList()),
                },
                1),
        };

        var restored = Serializer().Deserialize<UserMcpConfigState>(Serializer().Serialize(state));

        Assert.Equal(["alpha", "beta"], Assert.Single(restored.Config.Servers).KnownToolNames);
    }

    // The pin flag (ADR 0045) appended a trailing [Id(5)] member to the POSITIONAL record ConversationInfo,
    // and every conversation-index document already in Cosmos was written without it. The mechanism at risk
    // is Newtonsoft constructor-binding a MISSING property onto a positional record — which only this path
    // exercises. SerializationCompatibilityTests cannot answer it: those blobs go through Orleans' BINARY
    // serializer, which Cosmos does not use.
    [Fact]
    public void ConversationIndexDocument_WrittenBeforePinning_StillDeserializes_AsUnpinned()
    {
        var restored = Serializer().Deserialize<ConversationIndexState>(new BinaryData(PrePinConversationIndexStateJson));

        Assert.Equal(2, restored.Conversations.Count);

        var agentChat = Assert.Single(restored.Conversations, c => c.Id == "conv-1");
        Assert.Equal("A golden chat", agentChat.Title);
        Assert.Equal("agent-golden", agentChat.AgentId);
        Assert.Equal("Golden", agentChat.AgentName);
        // The whole point: an absent "Pinned" key must bind to the constructor default, not throw and not
        // strand the document. Every existing conversation reads as unpinned — zero state migration.
        Assert.False(agentChat.Pinned);

        var plainChat = Assert.Single(restored.Conversations, c => c.Id == "conv-2");
        Assert.Null(plainChat.AgentId);
        Assert.False(plainChat.Pinned);
    }

    // Per-tool selection appended a trailing [Id(12)] KnownToolNames to the POSITIONAL record StoredMcpServer,
    // and every mcp-servers document already in Cosmos was written without it. The mechanism at risk is
    // Newtonsoft constructor-binding a MISSING property onto a positional record — which only this path
    // exercises, exactly as ADR 0045 established for the pin flag. Getting this wrong does not lose a
    // checkbox; it strands the whole document and takes every one of that user's MCP servers with it,
    // including their encrypted secrets and OAuth token.
    [Fact]
    public void UserMcpConfigDocument_WrittenBeforeKnownToolNames_StillDeserializes()
    {
        var restored = Serializer().Deserialize<UserMcpConfigState>(new BinaryData(PreKnownToolNamesMcpConfigJson));

        var server = Assert.Single(restored.Config.Servers);
        Assert.Equal("srv-1", server.Id);
        Assert.Equal("Dataverse", server.Name);
        Assert.Equal("cipher-header", server.HeaderValueCiphertext);   // the secrets still bind
        Assert.Equal("cipher-token", server.TokenCiphertext);
        Assert.Equal(["scope.read", "scope.write"], server.Scopes);    // ...and so does the prior list member
        Assert.Equal(3, restored.Config.Version);

        // The whole point: an absent "KnownToolNames" key must bind to the constructor default, not throw and
        // not strand the document. Every existing server reads as never-connected — zero state migration — and
        // re-learns its tool names on that user's next chat activation.
        Assert.Null(server.KnownToolNames);
    }

    /// <summary>
    /// An <c>mcp-servers</c> payload in the shape written before per-tool selection — note the total absence
    /// of a <c>KnownToolNames</c> key. Committed rather than generated at run time, for the same reason as
    /// <see cref="StoredAgentGrainStateJson"/>: a fixture the test writes for itself would agree with whatever
    /// the code currently does and could never fail.
    /// </summary>
    private const string PreKnownToolNamesMcpConfigJson = """
        {"$id":"1","$type":"Agent42.Agents.UserMcpConfigState, Agent42.Agents","Config":{"$id":"2","$type":"Agent42.Agents.UserMcpConfig, Agent42.Agents","Servers":{"$type":"System.Collections.Generic.List`1[[Agent42.Agents.StoredMcpServer, Agent42.Agents]], System.Private.CoreLib","$values":[{"$id":"3","$type":"Agent42.Agents.StoredMcpServer, Agent42.Agents","Id":"srv-1","Name":"Dataverse","Endpoint":"https://mcp.example/sse","Auth":"InteractiveOAuth","Enabled":true,"HeaderName":"X-Api-Key","HeaderValueCiphertext":"cipher-header","Authority":"https://login.example/tenant","ClientId":"client-1","ClientSecretCiphertext":"cipher-secret","Scopes":{"$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["scope.read","scope.write"]},"TokenCiphertext":"cipher-token"}]},"Version":3}}
        """;

    /// <summary>
    /// A <c>conversations</c> payload in the shape written before ADR 0045 — note the total absence of a
    /// <c>Pinned</c> key on either entry. Committed rather than generated at run time, for the same reason
    /// as <see cref="StoredAgentGrainStateJson"/>: a fixture the test writes for itself would agree with
    /// whatever the code currently does and could never fail.
    /// </summary>
    private const string PrePinConversationIndexStateJson = """
        {"$id":"1","$type":"Agent42.Agents.ConversationIndexState, Agent42.Agents","Conversations":{"$type":"System.Collections.Generic.List`1[[Agent42.Agents.ConversationInfo, Agent42.Agents]], System.Private.CoreLib","$values":[{"$id":"2","$type":"Agent42.Agents.ConversationInfo, Agent42.Agents","Id":"conv-1","Title":"A golden chat","UpdatedAt":"2026-06-07T08:09:10+00:00","AgentId":"agent-golden","AgentName":"Golden"},{"$id":"3","$type":"Agent42.Agents.ConversationInfo, Agent42.Agents","Id":"conv-2","Title":"A plain chat","UpdatedAt":"2026-06-06T08:09:10+00:00","AgentId":null,"AgentName":null}]}}
        """;

    /// <summary>
    /// A real <c>agent-state</c> payload, captured verbatim from the configured serializer. Committed
    /// rather than generated at run time: a fixture the test writes for itself would agree with whatever
    /// the code currently does and could never fail.
    /// </summary>
    private const string StoredAgentGrainStateJson = """
        {"$id":"1","$type":"Agent42.Agents.AgentGrainState, Agent42.Agents","SerializedSession":"{\"messages\":[{\"role\":\"user\",\"text\":\"golden\"}]}","ScheduledPrompts":{"$id":"2","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Agent42.Agents.ReminderInfo, Agent42.Agents]], System.Private.CoreLib","daily-standup":{"$id":"3","$type":"Agent42.Agents.ReminderInfo, Agent42.Agents","Name":"daily-standup","Prompt":"Summarise the standup","DueInMinutes":90.5,"RepeatEveryMinutes":1440.25,"CreatedAt":"2026-01-02T03:04:05+00:00","TimesFired":7}},"DeploymentName":"foundry:gpt-5.4","AgentId":"agent-golden","AgentName":"Golden Agent","SystemPrompt":"You are the golden fixture.","TurnAttachments":{"$id":"4","$type":"System.Collections.Generic.List`1[[System.Collections.Generic.List`1[[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib]], System.Private.CoreLib","$values":[{"$id":"5","$type":"System.Collections.Generic.List`1[[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib","$values":[{"$id":"6","$type":"Agent42.Agents.AttachmentRef, Agent42.Agents","Id":"att-1","BlobPath":"user/conv/att-1.png","MediaType":"image/png","FileName":"diagram.png"}]}]},"DocumentNames":{"$id":"7","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["handbook.pdf"]},"FetchableUrls":{"$id":"8","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["https://example.com/a"]},"GeneratedImages":{"$id":"9","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib","msg-9":{"$id":"10","$type":"Agent42.Agents.AttachmentRef, Agent42.Agents","Id":"gen-1","BlobPath":"user/conv/gen-1.png","MediaType":"image/png","FileName":"generated.png"}},"EnabledTools":{"$id":"11","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["web_search","mcp:server-1"]}}
        """;
}
