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
        "Agent42.Agents.EmailAddressDirectoryState",
        "Agent42.Agents.LocalBridgeKeyDirectoryState",
        "Agent42.Agents.ShareLinkDirectoryState",
        "Agent42.Agents.UsageRollupState",
        "Agent42.Agents.UserMcpConfigState",
        "Agent42.Agents.UserMcpOAuthPendingState",
        "Agent42.Agents.UserGitHubOAuthPendingState",
        "Agent42.Agents.UserProfileState",
        "Agent42.Agents.UserProviderConfigState",
        "Agent42.Agents.UserPushState",
        "Agent42.Agents.UserShareLinksState",
        "Agent42.Agents.UserSkillsState",
        // ADR 0120: the fleet share links.
        "Agent42.Agents.FleetShareLinkDirectoryState",
        "Agent42.Agents.UserFleetShareLinksState",
        "Agent42.Agents.UserChatConnectionsState",
        "Agent42.Agents.ChatConnectionDirectoryState",
        "Agent42.Agents.ChatRouteState",
        // ADR 0111 phase 3: the Signal number claim.
        "Agent42.Agents.SignalNumberDirectoryState",
        // Nested in AgentGrainState (ADR 0111): the conversation's chat origin.
        "Agent42.Agents.ChatOrigin",
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

    // AC21 (ADR 0066): the committed golden was written before [Id(11)] WebDocumentNames existed, so it
    // carries no such key at all. Newtonsoft binding a MISSING property onto the initializer default is
    // the actual risk here — the ADR 0045 precedent — and it must bind to an EMPTY LIST, not null:
    // WithDocumentAwareness and the widened retrieval gate both read .Count on it unguarded, so a null
    // would NRE on the first turn of every conversation that predates this deployment.
    [Fact]
    public void DocumentWrittenBeforeWebDocumentNames_StillDeserializes()
    {
        var restored = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));

        Assert.NotNull(restored.WebDocumentNames);
        Assert.Empty(restored.WebDocumentNames);

        // The pre-existing document list is untouched by the split — an old conversation's uploads keep
        // their "the user uploaded" provenance, which is the whole reason the second list exists.
        Assert.Equal(["handbook.pdf"], restored.DocumentNames);
        Assert.NotNull(restored.WebDocumentUrls);
        Assert.Empty(restored.WebDocumentUrls);
    }

    // The FORWARD direction for the same two fields, on the path that matters at rest. The golden blob
    // above cannot cover it — it was written before these fields existed and is deliberately never
    // regenerated — so this writes and reads back through the SAME configured Cosmos serializer.
    //
    // The two lists are given deliberately DIFFERENT values because the failure this guards is them
    // swapping: [Id(11)] and [Id(12)] were added together, hold the same type, and are appended in
    // lockstep, so a renumbering would produce a state where every document is named after the wrong URL
    // and nothing else would notice.
    [Fact]
    public void WebDocumentNamesAndUrls_RoundTripWithoutSwapping()
    {
        var written = new AgentGrainState
        {
            DocumentNames = ["uploaded.pdf"],
            WebDocumentNames = ["spec-v2.pdf (web: example.com)"],
            WebDocumentUrls = ["https://example.com/spec-v2.pdf"],
        };

        var restored = Serializer().Deserialize<AgentGrainState>(Serializer().Serialize(written));

        Assert.Equal(["uploaded.pdf"], restored.DocumentNames);
        Assert.Equal(["spec-v2.pdf (web: example.com)"], restored.WebDocumentNames);
        Assert.Equal(["https://example.com/spec-v2.pdf"], restored.WebDocumentUrls);
    }

    // AC14 (ADR 0067): the committed golden predates [Id(13)] SeededFetchableUrls, so it carries no such
    // key. It must bind to an EMPTY LIST rather than null — IsFetchableUrl reads .Contains on it unguarded
    // on every fetch and every document read, so a null would NRE the first web turn of every conversation
    // that predates this deployment. Reading empty is also what makes "nothing is seeded until a user opts
    // a server in" true at rest and not merely in the code.
    [Fact]
    public void StateWrittenBeforeUrlSeeding_StillDeserializes()
    {
        var restored = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));

        Assert.NotNull(restored.SeededFetchableUrls);
        Assert.Empty(restored.SeededFetchableUrls);

        // The pre-existing allowlist is untouched by the split: an old conversation keeps the URLs its
        // user typed and its searches returned, with their stronger provenance.
        Assert.Equal(["https://example.com/a"], restored.FetchableUrls);
    }

    // AC15: the FORWARD direction, on the path that matters at rest. The golden above cannot cover it (it
    // was written before the field existed and is deliberately never regenerated), so this writes and reads
    // back through the SAME configured Cosmos serializer.
    //
    // The two lists are given deliberately DIFFERENT values because the failure this guards is them
    // SWAPPING: they hold the same type, sit next to each other, and are read by one method — a
    // renumbering would silently give every seeded URL the provenance of a user-typed one, which is the
    // exact distinction ADR 0067 rests on, and nothing else would notice.
    [Fact]
    public void SeededFetchableUrls_RoundTripWithoutSwapping()
    {
        var written = new AgentGrainState
        {
            FetchableUrls = ["https://example.com/typed-by-the-user"],
            SeededFetchableUrls = ["https://linked.example/seeded-by-an-mcp-result.pdf"],
        };

        var restored = Serializer().Deserialize<AgentGrainState>(Serializer().Serialize(written));

        Assert.Equal(["https://example.com/typed-by-the-user"], restored.FetchableUrls);
        Assert.Equal(["https://linked.example/seeded-by-an-mcp-result.pdf"], restored.SeededFetchableUrls);
    }

    // AC15 (copilot kind): the copilot kind adds NO persisted grain field. Its two records ride the existing
    // SerializedSession string, which carries Copilot's transcript pointer (sessionId) and the history mirror
    // (stateBag), and both survive the Cosmos JSON path.
    [Fact]
    public async Task CopilotSession_MirrorRoundTripsThroughStorage()
    {
        var agent = new CopilotUnavailableAgent("offline");
        var session = await agent.DeserializeSessionAsync(System.Text.Json.JsonSerializer.SerializeToElement(
            new { sessionId = "copilot-session-1", stateBag = new Microsoft.Agents.AI.AgentSessionStateBag().Serialize() }));
        Microsoft.Agents.AI.AgentSessionExtensions.SetInMemoryChatHistory(session,
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello"),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "hi there"),
        ]);
        var serialized = System.Text.Json.JsonSerializer.Serialize(await agent.SerializeSessionAsync(session));

        var restored = Serializer().Deserialize<AgentGrainState>(Serializer().Serialize(new AgentGrainState
        {
            AgentKind = AgentKinds.Copilot,
            SerializedSession = serialized,
        }));

        Assert.Equal(AgentKinds.Copilot, restored.AgentKind);
        var back = await agent.DeserializeSessionAsync(
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(restored.SerializedSession!));
        Assert.Equal("copilot-session-1",
            (await agent.SerializeSessionAsync(back)).GetProperty("sessionId").GetString());
        Assert.True(Microsoft.Agents.AI.AgentSessionExtensions.TryGetInMemoryChatHistory(back, out var mirror));
        Assert.Equal(["hello", "hi there"], mirror!.Select(m => m.Text));
    }

    // AC5 (ADR 0082): the research fields round-trip on the path that matters at rest. The seeded tool list is built
    // exactly as EnsureResearchAgentAsync builds it: a spread there would be unreadable (the trap pinned below).
    [Fact]
    public void ResearchFields_RoundTrip()
    {
        var grain = Serializer().Deserialize<AgentGrainState>(
            Serializer().Serialize(new AgentGrainState { AgentKind = AgentKinds.Research }));
        Assert.Equal(AgentKinds.Research, grain.AgentKind);

        var registry = Serializer().Deserialize<AgentRegistryState>(Serializer().Serialize(new AgentRegistryState
        {
            Agents =
            [
                new AgentDefinition(
                    "a-1", "Research", "", "fake-model", "sys", default, default,
                    EnabledTools: ResearchAgentDefaults.EnabledTools.ToList(), ApiKeyHash: "h", Kind: AgentKinds.Research),
            ],
            ResearchAgentSeeded = true,
        }));
        Assert.True(registry.ResearchAgentSeeded);
        var agent = Assert.Single(registry.Agents);
        Assert.Equal(AgentKinds.Research, agent.Kind);
        // The fields beside it did not shift into it.
        Assert.Equal("h", agent.ApiKeyHash);
        Assert.False(agent.McpExposed);
    }

    // AC5 (ADR 0082): documents written before the research fields existed read as ordinary. An agent-state
    // document binds AgentKind to null, and a registry document binds Kind to null (NOT the C# default: Newtonsoft
    // passes null for a constructor parameter the document lacks) and the seeded flag to false,
    // which is what makes every existing user get the research agent exactly once.
    [Fact]
    public void DocumentsWithoutResearchFields_Deserialize()
    {
        var grain = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));
        Assert.Null(grain.AgentKind);

        var registry = Serializer().Deserialize<AgentRegistryState>(new BinaryData(PreResearchAgentRegistryJson));
        Assert.False(registry.ResearchAgentSeeded);
        var agent = Assert.Single(registry.Agents);
        Assert.Null(agent.Kind);
        Assert.Equal("sha256-abc", agent.ApiKeyHash);
        Assert.True(agent.McpExposed);
    }

    // AC1 (artifact pane): the committed golden predates [Id(15)] Artifacts, so it carries no such key. It must
    // bind to an EMPTY dictionary, not null: the artifact tools and the list endpoint read it unguarded.
    [Fact]
    public void ArtifactsWrittenBefore_FieldExisted_Deserializes()
    {
        var restored = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));

        Assert.NotNull(restored.Artifacts);
        Assert.Empty(restored.Artifacts);
    }

    // AC1: the forward direction on the path that matters at rest. Two artifacts with two versions each, all with
    // different values, so a swap between artifacts or versions (or a lost JsonIgnore'd AttachmentId — the
    // [JsonIgnore] is System.Text.Json's and must NOT hide it from Cosmos) shows up. The version list is built the
    // way the tool builds it inside the grain, as a List<T>.
    [Fact]
    public void Artifacts_RoundTripThroughCosmosJson()
    {
        var at = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var written = new AgentGrainState
        {
            Artifacts = new Dictionary<string, ArtifactRecord>
            {
                ["art-a"] = new()
                {
                    Id = "art-a",
                    Kind = ArtifactKind.Html,
                    Title = "Clock",
                    CreatedAt = at,
                    Versions = new List<ArtifactVersionRef> { new(1, "blob-a1", 10, at), new(2, "blob-a2", 20, at.AddMinutes(1)) },
                },
                ["art-b"] = new()
                {
                    Id = "art-b",
                    Kind = ArtifactKind.Code,
                    Title = "Sort",
                    Language = "python",
                    CreatedAt = at,
                    Versions = new List<ArtifactVersionRef> { new(1, "blob-b1", 30, at), new(2, "blob-b2", 40, at) },
                },
            },
        };

        var restored = Serializer().Deserialize<AgentGrainState>(Serializer().Serialize(written));

        var a = restored.Artifacts["art-a"];
        Assert.Equal((ArtifactKind.Html, "Clock", (string?)null), (a.Kind, a.Title, a.Language));
        Assert.Equal(["blob-a1", "blob-a2"], a.Versions.Select(v => v.AttachmentId));
        Assert.Equal([10, 20], a.Versions.Select(v => v.SizeBytes));
        var b = restored.Artifacts["art-b"];
        Assert.Equal((ArtifactKind.Code, "Sort", "python"), (b.Kind, b.Title, b.Language));
        Assert.Equal(["blob-b1", "blob-b2"], b.Versions.Select(v => v.AttachmentId));
        Assert.Equal([1, 2], b.Versions.Select(v => v.Version));
    }

    // ADR 0133: [Id(19)] McpApps is the next trailing member of AgentGrainState. The committed golden predates it, so the
    // key is absent: it must bind to an EMPTY list, not null, because the replay path reads it unguarded.
    [Fact]
    public void McpAppsWrittenBefore_FieldExisted_Deserializes()
    {
        var restored = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));

        Assert.NotNull(restored.McpApps);
        Assert.Empty(restored.McpApps);
    }

    // ADR 0133: the forward direction on the Cosmos JSON path. Two records with different values in every field, so a swap
    // or a lost JsonIgnore'd AttachmentId (System.Text.Json's attribute must NOT hide it from Cosmos) shows up. The list
    // is built as a List<T>, the way the grain builds it.
    [Fact]
    public void McpApps_RoundTripThroughCosmosJson()
    {
        var written = new AgentGrainState
        {
            McpApps = new List<McpAppRecord>
            {
                new("call-1", "srv-a", "show_chart", "ui://chart/view.html", "blob-1", 100),
                new("call-2", "srv-b", "show_map", "ui://map/view.html", "blob-2", 200),
            },
        };

        var restored = Serializer().Deserialize<AgentGrainState>(Serializer().Serialize(written));

        Assert.Equal(typeof(List<McpAppRecord>), restored.McpApps.GetType());
        Assert.Equal(["call-1", "call-2"], restored.McpApps.Select(r => r.CallId));
        Assert.Equal(["srv-a", "srv-b"], restored.McpApps.Select(r => r.ServerId));
        Assert.Equal(["show_chart", "show_map"], restored.McpApps.Select(r => r.ToolName));
        Assert.Equal(["ui://chart/view.html", "ui://map/view.html"], restored.McpApps.Select(r => r.ResourceUri));
        Assert.Equal(["blob-1", "blob-2"], restored.McpApps.Select(r => r.AttachmentId));
        Assert.Equal([100, 200], restored.McpApps.Select(r => r.SizeBytes));
    }

    // ADR 0110: a usage-rollup document written before TokenBucket.ProviderKind existed reads back with "" (unpriced),
    // and the new field round-trips through the Cosmos JSON path.
    [Fact]
    public void TokenBucket_WithoutProviderKind_Deserializes()
    {
        var state = new UsageRollupState();
        state.Apply(new UsageEvent(
            UsageEventKind.Token, DateTimeOffset.UtcNow, "Agent42", "openai", "gpt-5.4", "ada", "", 10, 5, 0, 0, false, "AzureOpenAI"));
        var json = Serializer().Serialize(state).ToString();
        var legacy = System.Text.RegularExpressions.Regex.Replace(json, @",?\s*""ProviderKind""\s*:\s*""AzureOpenAI""", "");
        Assert.DoesNotContain("ProviderKind", legacy);

        var roundTripped = Serializer().Deserialize<UsageRollupState>(new BinaryData(json));
        var restored = Serializer().Deserialize<UsageRollupState>(new BinaryData(legacy));

        Assert.Equal("AzureOpenAI", roundTripped.DailyTokens.Values.Single().ProviderKind);
        Assert.Equal("", restored.DailyTokens.Values.Single().ProviderKind);
        Assert.Equal(10, restored.DailyTokens.Values.Single().InputTokens);
    }

    // ADR 0121: a usage-rollup document written before TokenBucket/ToolBucket.UserId existed reads back with "" (an
    // owner's legacy bucket), and the new field round-trips through the Cosmos JSON path.
    [Fact]
    public void UsageBuckets_WithoutUserId_Deserialize()
    {
        var state = new UsageRollupState();
        state.Apply(new UsageEvent(
            UsageEventKind.Token, DateTimeOffset.UtcNow, "Agent42", "openai", "gpt-5.4", "ada@example.com", "", 10, 5, 0, 0, false, "", "oid-ada"));
        state.Apply(new UsageEvent(
            UsageEventKind.Tool, DateTimeOffset.UtcNow, "Agent42", "", "", "ada@example.com", "web_search", 0, 0, 0, 0, false, "", "oid-ada"));
        var json = Serializer().Serialize(state).ToString();
        var legacy = System.Text.RegularExpressions.Regex.Replace(json, @",?\s*""UserId""\s*:\s*""oid-ada""", "");
        Assert.DoesNotContain("UserId", legacy);

        var roundTripped = Serializer().Deserialize<UsageRollupState>(new BinaryData(json));
        var restored = Serializer().Deserialize<UsageRollupState>(new BinaryData(legacy));

        Assert.Equal("oid-ada", roundTripped.DailyTokens.Values.Single().UserId);
        Assert.Equal("oid-ada", roundTripped.DailyTools.Values.Single().UserId);
        Assert.Equal("", restored.DailyTokens.Values.Single().UserId);
        Assert.Equal("", restored.DailyTools.Values.Single().UserId);
        Assert.Equal(10, restored.DailyTokens.Values.Single().InputTokens);
        // A legacy bucket is reachable by an owner through the old email key, and by nobody else.
        Assert.Equal(10, restored.Query("all", null, "oid-ada", "ada@example.com", DateTimeOffset.UtcNow).Totals.InputTokens);
        Assert.Equal(0, restored.Query("all", null, "oid-ada", null, DateTimeOffset.UtcNow).Totals.InputTokens);
    }

    // ADR 0089 AC12: the committed golden predates [Id(16)]/[Id(17)], so an existing conversation reads as having no
    // shared state at version 0.
    [Fact]
    public void SharedStateWrittenBefore_FieldExisted_Deserializes()
    {
        var restored = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));

        Assert.Null(restored.SharedStateJson);
        Assert.Equal(0, restored.SharedStateVersion);
    }

    // ADR 0089 AC12: the state is TEXT holding JSON, and it must come back byte-identical through the Cosmos JSON
    // path, quotes, nesting, unicode and all, rather than being re-parsed into an object by the serializer.
    [Fact]
    public void SharedState_RoundTripsThroughCosmosJson()
    {
        const string state = """{"checklist":[{"id":"a","title":"Bel \"Anna\" — café","done":true}],"notes":{"n":1}}""";
        var written = new AgentGrainState { SharedStateJson = state, SharedStateVersion = 7 };

        var restored = Serializer().Deserialize<AgentGrainState>(Serializer().Serialize(written));

        Assert.Equal(state, restored.SharedStateJson);
        Assert.Equal(7, restored.SharedStateVersion);
    }

    // ADR 0111 AC21: the committed golden predates [Id(18)] Origin, so a conversation stored before the chat gateway
    // delivered reads as a web conversation (no origin), and a document WITHOUT the key must not bind a half-built one.
    [Fact]
    public void ChatOrigin_WrittenBeforeFieldExisted_Deserializes()
    {
        var restored = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));

        Assert.Null(restored.Origin);
    }

    // The FORWARD direction on the path that matters at rest: the origin survives the Cosmos JSON round trip.
    [Fact]
    public void ChatOrigin_RoundTripsThroughCosmosJson()
    {
        var written = new AgentGrainState { Origin = new ChatOrigin { ConnectionId = "conn-1", RouteKey = "-1001#42", IsGroup = true } };
        var dm = new AgentGrainState { Origin = new ChatOrigin { ConnectionId = "conn-2", RouteKey = "555" } };

        var restored = Serializer().Deserialize<AgentGrainState>(Serializer().Serialize(written));
        var restoredDm = Serializer().Deserialize<AgentGrainState>(Serializer().Serialize(dm));

        Assert.NotNull(restored.Origin);
        Assert.Equal(("conn-1", "-1001#42", true), (restored.Origin.ConnectionId, restored.Origin.RouteKey, restored.Origin.IsGroup));
        // A DM origin is written WITHOUT IsGroup (the CLR default is omitted) and must read back false, never a flip.
        Assert.False(restoredDm.Origin!.IsGroup);
    }

    // AC1: ChatTurn.Artifacts crosses the grain boundary (Orleans binary) and the HTTP boundary (System.Text.Json,
    // the shape the frontend reads), so both are checked.
    [Fact]
    public void ChatTurn_Artifacts_RoundTrip()
    {
        var turn = new ChatTurn("assistant", "here it is", Artifacts: [new ArtifactInfo("art-a", 2, ArtifactKind.Svg, "Logo")]);

        var serializer = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredService<Orleans.Serialization.Serializer<ChatTurn>>();
        var viaOrleans = serializer.Deserialize(serializer.SerializeToArray(turn));
        Assert.Equal(turn.Artifacts, viaOrleans.Artifacts);

        var json = System.Text.Json.JsonSerializer.Serialize(turn, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Contains("\"artifacts\":[{\"artifactId\":\"art-a\",\"version\":2,\"kind\":\"svg\",\"title\":\"Logo\"}]", json, StringComparison.Ordinal);
    }

    // AC12 (ADR 0104): AgentStreamEvent is never stored, but CacheWriteTokens crosses the grain boundary (Orleans binary)
    // and the HTTP one (the NDJSON camelCase name showUsage reads), so both are checked.
    [Fact]
    public void AgentStreamEvent_CacheWriteTokens_RoundTrip()
    {
        var done = new AgentStreamEvent(AgentStreamEventType.Done, InputTokens: 10, CacheWriteTokens: 350);

        var serializer = Cluster.GetSiloServiceProvider(Cluster.Primary!.SiloAddress)
            .GetRequiredService<Orleans.Serialization.Serializer<AgentStreamEvent>>();
        Assert.Equal(350, serializer.Deserialize(serializer.SerializeToArray(done))!.CacheWriteTokens);

        var json = System.Text.Json.JsonSerializer.Serialize(done, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Contains("\"cacheWriteTokens\":350", json, StringComparison.Ordinal);
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
        //
        // The MESSAGE is version-sensitive, the BEHAVIOUR is not: on Orleans 10.2.2 this read
        // "Unable to find a constructor to use"; 10.3.1 reports the unresolvable compiler-generated
        // type instead. Both mean the state is unreadable. Update the substring on a bump — never
        // relax the assertion into a bare Assert.NotNull, which would pass on a silent regression.
        Assert.NotNull(ex);
        var message = (ex.InnerException ?? ex).Message;
        Assert.Contains("Unable to resolve type", message, StringComparison.Ordinal);
        // Name the compiler-generated type family too, not just the failure verb. Without this, ANY
        // unrelated $type resolution failure keeps the test green — which would be strictly looser than
        // the 10.2.2 assertion it replaced, and this test's whole job is to notice when the hazard moves.
        Assert.Contains("<>z__", message, StringComparison.Ordinal);
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

    // The other half of the scoping rule, and the one that actually matters in production. Handler-built
    // lists are laundered by the grain-call boundary — but by WHICH mechanism depends on topology, and only
    // one of the two is exercised by the test suite's own shape.
    //
    // `SharedApiFixture` injects the TestCluster's EXTERNAL client, so its handler→grain calls fully
    // serialize (that is what ProfileToolSave_IsReadableBackFromStorage measures). Production does not:
    // `Program.cs` co-hosts the silo via `builder.UseOrleans`, so those same calls are LOCAL and Orleans
    // DEEP-COPIES the arguments instead. Different code path, same question — and the answer has to be
    // measured, not assumed, because the conclusion drawn from it is "the request handlers need no
    // .ToList() and adding one is churn".
    [Fact]
    public void OrleansDeepCopy_LaundersACollectionExpressionList_LikeSerializationDoes()
    {
        IReadOnlyList<string> source = ["web_search", "location"];
        IReadOnlyList<string> collectionExpression = [.. source.Select(x => x).Where(x => x.Length > 0)];

        // Precondition: the hazard is actually present before the copy, or this proves nothing. Asserted on
        // the type NAME, not its full name — a compiler-internal type is still called `<>z__ReadOnlyList`1`,
        // so a substring test against the full name would match the very `List`1[[System.String` it is meant
        // to exclude.
        Assert.StartsWith("<>z__", collectionExpression.GetType().Name, StringComparison.Ordinal);
        Assert.NotEqual(typeof(List<string>), collectionExpression.GetType());

        var copier = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredService<Orleans.Serialization.DeepCopier<UserProfile>>();

        var copied = copier.Copy(new UserProfile("nl-NL", "dark", collectionExpression));

        Assert.Equal(typeof(List<string>), copied.EnabledTools!.GetType());
        // ...and the laundered value survives the Cosmos JSON round trip, which the original would not.
        Serializer().Deserialize<UserProfileState>(Serializer().Serialize(new UserProfileState { Profile = copied }));
    }

    // Palette is a trailing [Id(4)] on the positional UserProfile record (ADR 0129). Cosmos writes JSON, so a
    // populated value must survive that path, and a document written before the field must restore it as null.
    [Fact]
    public void UserProfile_Palette_RoundTripsOnTheCosmosJsonPath()
    {
        var serializer = Serializer();
        var state = new UserProfileState { Profile = new UserProfile("nl-NL", "dark", null, null, "ocean") };

        var restored = serializer.Deserialize<UserProfileState>(serializer.Serialize(state));
        Assert.Equal("ocean", restored.Profile.Palette);

        var legacy = new BinaryData("""{"$type":"Agent42.Agents.UserProfileState","Profile":{"PreferredLanguage":"nl-NL","Theme":"dark"}}""");
        Assert.Null(serializer.Deserialize<UserProfileState>(legacy).Profile.Palette);
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

    // AC12 (ADR 0073): the SAME committed golden also predates custom groups — it carries no "Groups" key
    // on the state and no "GroupId" on either entry, which is why it is reused here rather than a second
    // fixture being minted. Two mechanisms are at risk and they are different: a missing property bound onto
    // a POSITIONAL RECORD (GroupId, the ADR 0045 shape), and a missing property bound onto a CLASS with an
    // initializer (Groups). The second must read as an empty list rather than null — every render path calls
    // .OrderBy on it unguarded, so a null would take out the whole conversation index of every user who
    // predates this deployment.
    [Fact]
    public void ConversationIndexDocument_WrittenBeforeGrouping_StillDeserializes_AsUngrouped()
    {
        var restored = Serializer().Deserialize<ConversationIndexState>(new BinaryData(PrePinConversationIndexStateJson));

        Assert.NotNull(restored.Groups);
        Assert.Empty(restored.Groups);

        Assert.All(restored.Conversations, c => Assert.Null(c.GroupId));
    }

    // AC13: the FORWARD direction, on the path that matters at rest. The golden above cannot cover it — it
    // was written before groups existed and is deliberately never regenerated — so this writes and reads
    // back through the SAME configured Cosmos serializer.
    //
    // This is the direction ADR 0039's scar is actually about: the $type names for List<ChatGroup> and
    // Agent42.Agents.ChatGroup are written INTO the document, and a state root that cannot be reconstructed
    // is unreadable rather than subtly wrong. ReorderGroupsAsync rebuilds this list inside the grain, which
    // is precisely the exposed case in the collection-expression gotcha — so the assertion that matters
    // most is the runtime type, not the values.
    [Fact]
    public void ChatGroups_RoundTripThroughTheCosmosSerializer()
    {
        var written = new ConversationIndexState
        {
            Conversations =
            [
                new ConversationInfo("conv-1", "A filed chat", new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero), GroupId: "grp-1"),
            ],
            Groups = [new ChatGroup("grp-1", "Work", 0), new ChatGroup("grp-2", "Personal", 1)],
        };

        var restored = Serializer().Deserialize<ConversationIndexState>(Serializer().Serialize(written));

        Assert.Equal(typeof(List<ChatGroup>), restored.Groups.GetType());
        Assert.Equal(["grp-1", "grp-2"], restored.Groups.Select(g => g.Id));
        Assert.Equal(["Work", "Personal"], restored.Groups.Select(g => g.Name));
        Assert.Equal([0, 1], restored.Groups.Select(g => g.Order));
        // The filing survives too — the record's trailing [Id(6)] is what the sidebar partitions on.
        Assert.Equal("grp-1", Assert.Single(restored.Conversations).GroupId);
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

        // ADR 0067 appended [Id(13)] SeedsFetchableUrls to the same positional record, and the same golden
        // covers it: an absent key must bind to the constructor default. Reading OFF is what makes "every
        // existing server ships with seeding disabled" true at rest rather than merely in the code.
        Assert.False(server.SeedsFetchableUrls);
    }

    // AC4 (ADR 0076): [Id(14)] BridgeKeyHash is the third trailing member appended to StoredMcpServer after
    // documents already existed. Same mechanism, same golden: an absent key binds to null, so every existing
    // server reads as having no bridge key — which is also correct, since none of them is LocalBridge.
    // ADR 0133: [Id(15)] AppsEnabled is the next trailing member of StoredMcpServer. On the Cosmos JSON path an
    // absent key must bind to false (every stored server reads as apps OFF, no migration), and a written true must
    // survive the round trip.
    [Fact]
    public void StoredMcpServer_AppsEnabled_DefaultsOff_AndRoundTrips()
    {
        var old = Serializer().Deserialize<UserMcpConfigState>(new BinaryData(PreKnownToolNamesMcpConfigJson));
        Assert.False(Assert.Single(old.Config.Servers).AppsEnabled);

        var written = new UserMcpConfigState
        {
            Config = new UserMcpConfig(new List<StoredMcpServer> { new("s", "n", "https://e", "None", true, AppsEnabled: true) }, 1),
        };
        var restored = Serializer().Deserialize<UserMcpConfigState>(Serializer().Serialize(written));

        Assert.True(Assert.Single(restored.Config.Servers).AppsEnabled);
    }

    [Fact]
    public void StoredMcpServer_WithoutBridgeKeyHash_Deserializes()
    {
        var restored = Serializer().Deserialize<UserMcpConfigState>(new BinaryData(PreKnownToolNamesMcpConfigJson));

        var server = Assert.Single(restored.Config.Servers);
        Assert.Equal("cipher-token", server.TokenCiphertext);
        Assert.Null(server.BridgeKeyHash);
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
    /// An <c>agents</c> registry payload in the shape written before ADR 0082 — note the total absence of a
    /// <c>Kind</c> key on the agent and of <c>ResearchAgentSeeded</c> on the root. Committed rather than generated
    /// at run time, for the same reason as <see cref="StoredAgentGrainStateJson"/>.
    /// </summary>
    private const string PreResearchAgentRegistryJson = """
        {"$id":"1","$type":"Agent42.Agents.AgentRegistryState, Agent42.Agents","Agents":{"$type":"System.Collections.Generic.List`1[[Agent42.Agents.AgentDefinition, Agent42.Agents]], System.Private.CoreLib","$values":[{"$id":"2","$type":"Agent42.Agents.AgentDefinition, Agent42.Agents","Id":"agent-golden","Name":"Golden","Description":"A stored persona","Model":"foundry:gpt-5.4","SystemPrompt":"Be golden.","CreatedAt":"2026-01-02T03:04:05+00:00","UpdatedAt":"2026-01-03T03:04:05+00:00","Continuous":true,"EnabledTools":{"$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["web_search"]},"ApiKeyHash":"sha256-abc","McpExposed":true}]}}
        """;

    /// <summary>
    /// A real <c>agent-state</c> payload, captured verbatim from the configured serializer. Committed
    /// rather than generated at run time: a fixture the test writes for itself would agree with whatever
    /// the code currently does and could never fail.
    /// </summary>
    internal const string StoredAgentGrainStateJson = """
        {"$id":"1","$type":"Agent42.Agents.AgentGrainState, Agent42.Agents","SerializedSession":"{\"messages\":[{\"role\":\"user\",\"text\":\"golden\"}]}","ScheduledPrompts":{"$id":"2","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Agent42.Agents.ReminderInfo, Agent42.Agents]], System.Private.CoreLib","daily-standup":{"$id":"3","$type":"Agent42.Agents.ReminderInfo, Agent42.Agents","Name":"daily-standup","Prompt":"Summarise the standup","DueInMinutes":90.5,"RepeatEveryMinutes":1440.25,"CreatedAt":"2026-01-02T03:04:05+00:00","TimesFired":7}},"DeploymentName":"foundry:gpt-5.4","AgentId":"agent-golden","AgentName":"Golden Agent","SystemPrompt":"You are the golden fixture.","TurnAttachments":{"$id":"4","$type":"System.Collections.Generic.List`1[[System.Collections.Generic.List`1[[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib]], System.Private.CoreLib","$values":[{"$id":"5","$type":"System.Collections.Generic.List`1[[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib","$values":[{"$id":"6","$type":"Agent42.Agents.AttachmentRef, Agent42.Agents","Id":"att-1","BlobPath":"user/conv/att-1.png","MediaType":"image/png","FileName":"diagram.png"}]}]},"DocumentNames":{"$id":"7","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["handbook.pdf"]},"FetchableUrls":{"$id":"8","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["https://example.com/a"]},"GeneratedImages":{"$id":"9","$type":"System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Agent42.Agents.AttachmentRef, Agent42.Agents]], System.Private.CoreLib","msg-9":{"$id":"10","$type":"Agent42.Agents.AttachmentRef, Agent42.Agents","Id":"gen-1","BlobPath":"user/conv/gen-1.png","MediaType":"image/png","FileName":"generated.png"}},"EnabledTools":{"$id":"11","$type":"System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib","$values":["web_search","mcp:server-1"]}}
        """;

    // ADR 0094 AC17: the webhook state on the path that matters at rest, with two log entries holding different
    // values so a swap shows up, and the log's runtime type checked (the collection-expression gotcha).
    [Fact]
    public void AgentWebhookState_RoundTripsThroughCosmosJson()
    {
        var at = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        var written = new AgentWebhookState
        {
            Url = "https://hooks.example.com/a",
            SecretCiphertext = "CfDJ8-cipher",
            Deliveries = new List<WebhookDeliveryRecord>
            {
                new() { At = at, DeliveryId = "d1", Type = AgentWebhookEvent.TurnCompleted, Trigger = "interactive", ConversationId = "c1", StatusCode = 200, Attempts = 1, DurationMs = 12 },
                new() { At = at.AddMinutes(1), DeliveryId = "d2", Type = AgentWebhookEvent.Test, Error = "timeout", Attempts = 3, DurationMs = 30_000 },
            },
        };

        var restored = Serializer().Deserialize<AgentWebhookState>(Serializer().Serialize(written));

        Assert.Equal(("https://hooks.example.com/a", "CfDJ8-cipher"), (restored.Url, restored.SecretCiphertext));
        Assert.Equal(typeof(List<WebhookDeliveryRecord>), restored.Deliveries.GetType());
        Assert.Equal(["d1", "d2"], restored.Deliveries.Select(d => d.DeliveryId));
        Assert.Equal([(int?)200, null], restored.Deliveries.Select(d => d.StatusCode));
        Assert.Equal([null, "timeout"], restored.Deliveries.Select(d => d.Error));
        Assert.Equal(["c1", null], restored.Deliveries.Select(d => d.ConversationId));
        Assert.Equal([1, 3], restored.Deliveries.Select(d => d.Attempts));
        Assert.Equal(at.AddMinutes(1), restored.Deliveries[1].At);
    }

    // ADR 0094 AC17: a document carrying nothing but its type still reads, with every default in place — the shape a
    // future trailing field meets in every document written before it.
    [Fact]
    public void AgentWebhookState_DocumentMissingEveryField_Deserializes()
    {
        var full = System.Text.Json.Nodes.JsonNode.Parse(Serializer().Serialize(new AgentWebhookState()).ToString())!.AsObject();
        foreach (var name in full.Select(p => p.Key).Where(k => !k.StartsWith('$')).ToList())
        {
            full.Remove(name);
        }

        var restored = Serializer().Deserialize<AgentWebhookState>(new BinaryData(full.ToJsonString()));

        Assert.Null(restored.Url);
        Assert.Null(restored.SecretCiphertext);
        Assert.NotNull(restored.Deliveries);
        Assert.Empty(restored.Deliveries);
    }

    // ADR 0096 AC17: an agent document written before the email token existed binds EmailToken to null — the agent
    // simply has no address until a regenerate — and the fields beside it do not shift.
    [Fact]
    public void AgentDefinition_PreEmailToken_BindsNull()
    {
        var agent = Assert.Single(
            Serializer().Deserialize<AgentRegistryState>(new BinaryData(PreResearchAgentRegistryJson)).Agents);

        Assert.Null(agent.EmailToken);
        Assert.Null(agent.Kind);
        Assert.Equal("sha256-abc", agent.ApiKeyHash);
        Assert.True(agent.McpExposed);
    }

    // ADR 0096: the token survives the Cosmos JSON path beside every other trailing field.
    [Fact]
    public void AgentDefinition_EmailToken_RoundTripsThroughCosmosJson()
    {
        var restored = Serializer().Deserialize<AgentRegistryState>(Serializer().Serialize(new AgentRegistryState
        {
            Agents =
            [
                new AgentDefinition("a-1", "Mail", "", "fake-model", "sys", default, default,
                    ApiKeyHash: "h", McpExposed: true, Kind: AgentKinds.Copilot, EmailToken: "agent-abc"),
            ],
        }));

        var agent = Assert.Single(restored.Agents);
        Assert.Equal(("agent-abc", "h", AgentKinds.Copilot, true), (agent.EmailToken, agent.ApiKeyHash, agent.Kind, agent.McpExposed));
    }

    // ADR 0096: the email directory's committed document still reads. A security-relevant read: a document that stopped
    // resolving would only make addresses 406, but one that resolved to the wrong owner would hand a stranger the run.
    [Fact]
    public void EmailAddressDirectoryState_ReadsCommittedDocument()
    {
        var restored = Serializer().Deserialize<EmailAddressDirectoryState>(new BinaryData(StoredEmailAddressDirectoryJson));

        Assert.Equal(("user@example.com", "agent-golden-mail"), (restored.UserId, restored.AgentId));
    }

    // ADR 0111 (CLAUDE.md rule 2): a user's chat bots read back from a committed Cosmos document, field for field.
    [Fact]
    public void UserChatConnectionsState_ReadsCommittedDocument()
    {
        var restored = Assert.Single(Serializer().Deserialize<UserChatConnectionsState>(new BinaryData(StoredUserChatConnectionsJson)).Connections);
        var expected = Assert.Single(GoldenStates.UserChatConnections().Connections);

        Assert.Equivalent(expected, restored, strict: true);
    }

    // ADR 0111: the Cosmos JSON serializer omits a member holding its CLR default, so a bool whose initialiser differed
    // from false would flip on every reload. A disabled bot must come back disabled, and an enabled one enabled.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChatConnection_Disabled_StaysDisabledThroughCosmosJson(bool enabled)
    {
        var state = new UserChatConnectionsState { Connections = [new ChatConnection { Id = "c", Platform = "Telegram", Enabled = enabled }] };

        var restored = Serializer().Deserialize<UserChatConnectionsState>(Serializer().Serialize(state));

        Assert.Equal(enabled, Assert.Single(restored.Connections).Enabled);
    }

    // ADR 0111: a connection stored before OwnerTelemetry ([Id(22)]) existed reads it as null, not as a fault.
    [Fact]
    public void ChatConnection_WithoutOwnerTelemetry_Deserializes()
    {
        var legacy = System.Text.RegularExpressions.Regex.Replace(StoredUserChatConnectionsJson, @",""OwnerTelemetry"":\{[^}]*\{[^}]*\}[^}]*\}", "");
        Assert.DoesNotContain("OwnerTelemetry", legacy);

        var restored = Assert.Single(Serializer().Deserialize<UserChatConnectionsState>(new BinaryData(legacy)).Connections);

        Assert.Null(restored.OwnerTelemetry);
        Assert.Equal(("conn-golden", 500000007L), (restored.Id, restored.PollOffset));
    }

    // ADR 0111 phase 3 (AC33): a Signal number claim reads back from a committed Cosmos document. Security-relevant: a
    // claim that resolved to the wrong connection would let one user register over another's number.
    [Fact]
    public void SignalNumberDirectoryState_ReadsCommittedDocument()
    {
        var restored = Serializer().Deserialize<SignalNumberDirectoryState>(new BinaryData(StoredSignalNumberDirectoryJson));

        Assert.Equivalent(GoldenStates.SignalNumberDirectory(), restored, strict: true);
    }

    // ADR 0111 phase 3 (AC33, rule 2): the trailing PendingSignalNumber ([Id(23)]) round-trips on the Cosmos JSON path,
    // and the committed connection document, written before it existed, reads it as null.
    [Fact]
    public void ChatConnection_PendingSignalNumber_RoundTripsThroughCosmosJson()
    {
        var state = new UserChatConnectionsState
        {
            Connections = [new ChatConnection { Id = "c", Platform = "Signal", PendingSignalNumber = "+31612345678" }],
        };

        var restored = Assert.Single(Serializer().Deserialize<UserChatConnectionsState>(Serializer().Serialize(state)).Connections);
        var legacy = Assert.Single(Serializer().Deserialize<UserChatConnectionsState>(new BinaryData(StoredUserChatConnectionsJson)).Connections);

        Assert.Equal("+31612345678", restored.PendingSignalNumber);
        Assert.Null(legacy.PendingSignalNumber);
    }

    [Fact]
    public void ChatDirectoryAndRoute_ReadCommittedDocuments()
    {
        var directory = Serializer().Deserialize<ChatConnectionDirectoryState>(new BinaryData(StoredChatConnectionDirectoryJson));
        var route = Serializer().Deserialize<ChatRouteState>(new BinaryData(StoredChatRouteJson));

        Assert.Equal(GoldenStates.ChatConnectionDirectory().Entries, directory.Entries);
        Assert.Equivalent(GoldenStates.ChatRoute(), route, strict: true);
    }

    // ADR 0108 (CLAUDE.md rule 2): the new pending sign-in state reads back through the Cosmos JSON path.
    [Fact]
    public void GitHubOAuthPending_RoundTripsThroughCosmosJson()
    {
        var restored = Serializer().Deserialize<UserGitHubOAuthPendingState>(Serializer().Serialize(new UserGitHubOAuthPendingState
        {
            Pending = [new GitHubOAuthPending("state-1", "verifier-1", "https://chat.agent42.nl/api/providers/github/callback")],
        }));

        var pending = Assert.Single(restored.Pending);
        Assert.Equal(("state-1", "verifier-1"), (pending.State, pending.CodeVerifier));
    }

    // ADR 0108: the provider the sign-in writes, built exactly as ConnectGitHubCopilotAsync builds it (a real List for
    // Models, no endpoint), reads back through the Cosmos JSON path.
    [Fact]
    public void ConnectedGitHubCopilotProvider_RoundTripsThroughCosmosJson()
    {
        var written = new UserProviderConfigState
        {
            Config = new UserProviderConfig(
                new List<StoredProvider>
                {
                    new(GitHubCopilotProvider.Key, GitHubCopilotProvider.Kind, GitHubCopilotProvider.DisplayName,
                        Endpoint: null, "cipher", Models: new List<StoredModel>(), UseResponsesApi: false),
                },
                Default: null, Version: 1),
        };

        var restored = Serializer().Deserialize<UserProviderConfigState>(Serializer().Serialize(written));

        var provider = Assert.Single(restored.Config.Providers);
        Assert.Equal((GitHubCopilotProvider.Key, GitHubCopilotProvider.Kind, "cipher"), (provider.Key, provider.Kind, provider.ApiKeyCiphertext));
        Assert.Null(provider.Endpoint);
        Assert.Empty(provider.Models);
    }

    // ADR 0137: StoredModel.Hint ([Id(9)]) and AgentGrainState.AutoModel ([Id(20)]) round-trip on the Cosmos JSON path,
    // and a document written before they existed reads back null/false (no migration).
    [Fact]
    public void RouterFields_RoundTripThroughCosmosJson()
    {
        var provider = new StoredProvider("p", "OpenAICompatible", null, "https://x/v1", "cipher",
            new List<StoredModel> { new("m1", "M1", true, false, Hint: "Use for hard maths"), new("m2", "M2", true, false) },
            UseResponsesApi: false);
        var config = new UserProviderConfigState { Config = new UserProviderConfig(new List<StoredProvider> { provider }, null, 1) };

        var restoredConfig = Serializer().Deserialize<UserProviderConfigState>(Serializer().Serialize(config));
        var models = Assert.Single(restoredConfig.Config.Providers).Models;
        Assert.Equal("Use for hard maths", models[0].Hint);
        Assert.Null(models[1].Hint);

        var restoredGrain = Serializer().Deserialize<AgentGrainState>(
            Serializer().Serialize(new AgentGrainState { DeploymentName = "p:m1", AutoModel = true }));
        Assert.True(restoredGrain.AutoModel);
        Assert.Equal("p:m1", restoredGrain.DeploymentName);
    }

    [Fact]
    public void RouterFields_WrittenBeforeTheyExisted_ReadBackAsDefaults()
    {
        var restored = Serializer().Deserialize<AgentGrainState>(new BinaryData(StoredAgentGrainStateJson));
        Assert.False(restored.AutoModel);
    }

    // ADR 0113: a provider overlay persisted before SupportsTranscription ([Id(8)]) and TranscriptionDefault ([Id(5)])
    // existed has neither property in its Cosmos document. It must read back false/null with no migration.
    [Fact]
    public void ProviderOverlay_WithoutTranscriptionFields_ReadsBackAsDefaults()
    {
        var written = new UserProviderConfigState
        {
            Config = new UserProviderConfig(
                new List<StoredProvider>
                {
                    new("mine", "OpenAICompatible", "Mine", "https://api.example/v1", "cipher",
                        new List<StoredModel> { new("m1", "M1", true, false, SupportsSpeech: true, SupportsTranscription: true) },
                        UseResponsesApi: false),
                },
                Default: "mine:m1", Version: 3, SpeechDefault: "mine:m1", TranscriptionDefault: "mine:m1"),
        };
        var json = Serializer().Serialize(written).ToString();
        var legacy = System.Text.RegularExpressions.Regex.Replace(json, @",?\s*""(SupportsTranscription|TranscriptionDefault)""\s*:\s*(true|"".*?"")", "");
        Assert.DoesNotContain("Transcription", legacy);

        var restored = Serializer().Deserialize<UserProviderConfigState>(new BinaryData(legacy));

        var model = restored.Config.Providers.Single().Models.Single();
        Assert.True(model.SupportsSpeech);
        Assert.False(model.SupportsTranscription);
        Assert.Null(restored.Config.TranscriptionDefault);
        Assert.Equal("mine:m1", restored.Config.SpeechDefault);
    }

    // The new fields, set, survive the Cosmos JSON path (the binary golden predates them and is never regenerated).
    [Fact]
    public void ProviderOverlay_TranscriptionFields_RoundTripThroughCosmosJson()
    {
        var written = new UserProviderConfigState
        {
            Config = new UserProviderConfig(
                new List<StoredProvider>
                {
                    new("mine", "OpenAICompatible", "Mine", "https://api.example/v1", "cipher",
                        new List<StoredModel>
                        {
                            new("whisper-1", "Whisper", false, false, SupportsTranscription: true),
                            new("tts-1", "TTS", false, false, SupportsSpeech: true),
                        },
                        UseResponsesApi: false),
                },
                Default: null, Version: 2, SpeechDefault: "mine:tts-1", TranscriptionDefault: "mine:whisper-1"),
        };

        var restored = Serializer().Deserialize<UserProviderConfigState>(Serializer().Serialize(written));

        Assert.Equal("mine:whisper-1", restored.Config.TranscriptionDefault);
        Assert.Equal("mine:tts-1", restored.Config.SpeechDefault);
        var models = restored.Config.Providers.Single().Models;
        Assert.True(models[0].SupportsTranscription);
        Assert.False(models[0].SupportsSpeech);
        Assert.True(models[1].SupportsSpeech);
        Assert.False(models[1].SupportsTranscription);
    }

    private const string StoredEmailAddressDirectoryJson = """
        {"$id":"1","$type":"Agent42.Agents.EmailAddressDirectoryState, Agent42.Agents","UserId":"user@example.com","AgentId":"agent-golden-mail"}
        """;

    // Serialized once from GoldenStates when ADR 0111 added the chat gateway, then committed. Note the missing "Enabled":
    // the golden connection is disabled, and false is omitted (see ChatConnection_Disabled_StaysDisabledThroughCosmosJson).
    private const string StoredUserChatConnectionsJson = """
        {"$id":"1","$type":"Agent42.Agents.UserChatConnectionsState, Agent42.Agents","Connections":{"$type":"System.Collections.Generic.List`1[[Agent42.Agents.ChatConnection, Agent42.Agents]], System.Private.CoreLib","$values":[{"$id":"2","$type":"Agent42.Agents.ChatConnection, Agent42.Agents","Id":"conn-golden","Platform":"Telegram","Name":"My phone","TargetAgentId":"agent-golden","CredentialsCiphertext":"cipher-golden","BotUserId":"7000000001","BotUserName":"agent42_golden_bot","WorkspaceId":"T-golden","OwnerPlatformUserId":"111111111","OwnerWorkspaceId":"T-owner","PairingCode":"ABCDE23456","PairingExpiresAt":"2026-06-07T08:09:10+00:00","PairingFailures":3,"GroupsEnabled":true,"VoiceNotesEnabled":true,"NotificationsEnabled":true,"HomeRouteKey":"111111111","PollOffset":500000007,"FaultReason":"golden fault","CreatedAt":"2026-01-02T03:04:05+00:00","UpdatedAt":"2026-06-07T08:09:10+00:00","OwnerTelemetry":{"$id":"3","$type":"Agent42.Agents.UserTelemetryContext, Agent42.Agents","Id":"user@example.com","Name":"Golden","Email":"user@example.com","FullName":"Golden User","Roles":{"$type":"System.String[], System.Private.CoreLib","$values":["user"]},"Idp":"aad"},"IsPaired":true}]}}
        """;

    // Serialized once from GoldenStates.SignalNumberDirectory() when ADR 0111 phase 3 added the type, then committed.
    private const string StoredSignalNumberDirectoryJson = """
        {"$id":"1","$type":"Agent42.Agents.SignalNumberDirectoryState, Agent42.Agents","UserId":"user@example.com","ConnectionId":"conn-golden-signal","Active":true,"ClaimedAt":"2026-10-21T08:09:10+00:00"}
        """;

    private const string StoredChatConnectionDirectoryJson = """
        {"$id":"1","$type":"Agent42.Agents.ChatConnectionDirectoryState, Agent42.Agents","Entries":{"$type":"System.Collections.Generic.List`1[[Agent42.Agents.ChatConnectionRef, Agent42.Agents]], System.Private.CoreLib","$values":[{"$id":"2","$type":"Agent42.Agents.ChatConnectionRef, Agent42.Agents","UserId":"user@example.com","ConnectionId":"conn-golden"}]}}
        """;

    private const string StoredChatRouteJson = """
        {"$id":"1","$type":"Agent42.Agents.ChatRouteState, Agent42.Agents","ConversationId":"chat-11111111-2222-3333-4444-555555555555","TurnCount":42,"ModelOverride":"foundry:gpt-5.4","TargetAgentOverride":"agent-golden"}
        """;
}
