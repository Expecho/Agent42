using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// The artifact tools: <c>create_artifact</c>, <c>update_artifact</c>, <c>read_artifact</c>. Each test drives the
/// tool through <see cref="FakeChatClient.CallToolPrompt"/>, so it runs exactly as a model-made call would.
///
/// <para>Refusal phrases asserted here are apostrophe-free: a tool result is JSON-serialized on its way to the
/// model, which escapes apostrophes (see <c>WebDocumentToolTests</c>).</para>
/// </summary>
public sealed class ArtifactToolTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> events)
    {
        var collected = new List<AgentStreamEvent>();
        await foreach (var evt in events)
        {
            collected.Add(evt);
        }

        return collected;
    }

    private static string Answer(IReadOnlyList<AgentStreamEvent> events) =>
        string.Concat(events.Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));

    // The result of this turn's scripted call only, so an assertion cannot pass on an earlier turn's result.
    private static string Result(IReadOnlyList<AgentStreamEvent> events)
    {
        var answer = Answer(events);
        var start = answer.IndexOf(FakeChatClient.ScriptedResultOpen, StringComparison.Ordinal);
        var end = answer.LastIndexOf(FakeChatClient.ScriptedResultClose, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"no scripted result in: {answer}");
        return answer[(start + FakeChatClient.ScriptedResultOpen.Length)..end];
    }

    private IAgentGrain Agent(string key) => Cluster.GrainFactory.GetGrain<IAgentGrain>(key);

    private IAttachmentStore Store() =>
        Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress).GetRequiredService<IAttachmentStore>();

    private static Task<List<AgentStreamEvent>> Call(IAgentGrain agent, string tool, object args, bool ephemeral = false)
        => CollectAsync(agent.ChatAsync(
            FakeChatClient.CallToolPrompt(tool, args), TestSiloConfigurator.DefaultModel, null, ephemeral));

    private static Task<List<AgentStreamEvent>> Create(IAgentGrain agent, string content, string kind = "html", string? language = null)
        => Call(agent, "create_artifact", new { kind, title = "Demo", content, language });

    // The tool's result for the latest call, as the history replays it (a JSON string holding the result).
    private static async Task<string> LastToolResult(IAgentGrain agent)
    {
        var turn = (await agent.GetHistoryAsync()).Last(t => t.ToolCalls is { Count: > 0 });
        var raw = turn.ToolCalls![^1].ToolResult!;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString()! : raw;
    }

    private static string Content(int bytes) => new('x', bytes);

    // AC2: create → update → read. Versions increment, the title changes only when asked, and read returns
    // the requested version's content (the latest by default, v1 when asked).
    [Fact]
    public async Task ArtifactTools_CreateUpdateRead()
    {
        var agent = Agent("art-crud-user/conv");
        await Create(agent, "<p>v1 body</p>");
        var created = JsonSerializer.Deserialize<ArtifactInfo>(await LastToolResult(agent), JsonSerializerOptions.Web)!;
        Assert.Equal((1, "html", "Demo"), (created.Version, created.Kind, created.Title));

        await Call(agent, "update_artifact", new { artifactId = created.ArtifactId, content = "<p>v2 body</p>", title = "Renamed" });
        var updated = JsonSerializer.Deserialize<ArtifactInfo>(await LastToolResult(agent), JsonSerializerOptions.Web)!;
        Assert.Equal((created.ArtifactId, 2, "Renamed"), (updated.ArtifactId, updated.Version, updated.Title));

        Assert.Contains("v2 body", Result(await Call(agent, "read_artifact", new { artifactId = created.ArtifactId })), StringComparison.Ordinal);
        Assert.Contains("v1 body", Result(await Call(agent, "read_artifact", new { artifactId = created.ArtifactId, version = 1 })), StringComparison.Ordinal);

        var record = Assert.Single(await agent.GetArtifactsAsync());
        Assert.Equal([1, 2], record.Versions.Select(v => v.Version));
        Assert.Equal("Renamed", record.Title);
    }

    // AC3: every refusal stores nothing. Each call is checked against the artifact count and the
    // version count afterwards, so a refusal that still wrote a blob-and-version would fail here.
    [Fact]
    public async Task ArtifactTools_RefuseInvalidAndOverCaps()
    {
        var agent = Agent("art-refuse-user/conv");

        Assert.Contains("kind must be one of", Result(await Create(agent, "x", kind: "pdf")), StringComparison.Ordinal);
        Assert.Contains("needs a language", Result(await Create(agent, "x", kind: "code")), StringComparison.Ordinal);
        Assert.Contains("needs a language", Result(await Create(agent, "x", kind: "code", language: "Not A Lang!")), StringComparison.Ordinal);
        Assert.Contains("may be at most 64 KB", Result(await Create(agent, Content(ArtifactLimits.MaxContentBytes + 1))), StringComparison.Ordinal);
        Assert.Contains("is a placeholder", Result(await Create(agent, ArtifactLimits.Stub("art_x", 1))), StringComparison.Ordinal);
        Assert.Contains("content is empty", Result(await Create(agent, "   ")), StringComparison.Ordinal);
        Assert.Contains("There is no artifact", Result(await Call(agent, "update_artifact", new { artifactId = "art_nope", content = "x" })), StringComparison.Ordinal);
        Assert.Contains("There is no artifact", Result(await Call(agent, "read_artifact", new { artifactId = "art_nope" })), StringComparison.Ordinal);
        Assert.Empty(await agent.GetArtifactsAsync());

        // Exactly at the cap is accepted: the boundary is inclusive.
        await Create(agent, Content(ArtifactLimits.MaxContentBytes));
        var id = Assert.Single(await agent.GetArtifactsAsync()).Id;
        Assert.Contains("there is no version 9", Result(await Call(agent, "read_artifact", new { artifactId = id, version = 9 })), StringComparison.Ordinal);

        for (var v = 2; v <= ArtifactLimits.MaxVersionsPerArtifact; v++)
        {
            await Call(agent, "update_artifact", new { artifactId = id, content = $"v{v}" });
        }

        Assert.Contains("versions, the limit", Result(await Call(agent, "update_artifact", new { artifactId = id, content = "one too many" })), StringComparison.Ordinal);
        Assert.Equal(ArtifactLimits.MaxVersionsPerArtifact, Assert.Single(await agent.GetArtifactsAsync()).Versions.Count);

        for (var a = 2; a <= ArtifactLimits.MaxArtifactsPerConversation; a++)
        {
            await Create(agent, $"artifact {a}");
        }

        Assert.Contains("artifacts, the limit", Result(await Create(agent, "one too many")), StringComparison.Ordinal);
        Assert.Equal(ArtifactLimits.MaxArtifactsPerConversation, (await agent.GetArtifactsAsync()).Count);
    }

    // AC4: the result the session keeps is short, even with a maximal title and content.
    [Fact]
    public async Task ArtifactTools_ReturnCompactResult()
    {
        var agent = Agent("art-compact-user/conv");
        await Call(agent, "create_artifact", new { kind = "markdown", title = new string('T', 500), content = Content(ArtifactLimits.MaxContentBytes) });

        var result = await LastToolResult(agent);
        Assert.True(result.Length < 256, $"result was {result.Length} chars: {result}");
        Assert.Equal(ArtifactLimits.MaxTitleLength, Assert.Single(await agent.GetArtifactsAsync()).Title.Length);
    }

    // AC5: an incognito turn refuses before anything is stored, and the turn-scoped flag does not leak into the
    // next normal turn on the same warm activation.
    [Fact]
    public async Task ArtifactTools_EphemeralChat_RefusesAndStoresNothing()
    {
        var agent = Agent("art-incognito-user/conv");

        var answer = Result(await Call(agent, "create_artifact", new { kind = "html", title = "x", content = "<p>secret</p>" }, ephemeral: true));
        Assert.Contains("cannot create artifacts in an incognito chat", answer, StringComparison.Ordinal);
        Assert.Empty(await agent.GetArtifactsAsync());

        await Create(agent, "<p>kept</p>");
        Assert.Single(await agent.GetArtifactsAsync());
    }

    // AC6: whatever the kind, the blob is octet-stream, so the attachment proxies can never render it as HTML.
    [Fact]
    public async Task ArtifactTools_StoreOctetStream()
    {
        var agent = Agent("art-octet-user/conv");
        await Create(agent, "<script>alert(1)</script>");
        await Create(agent, "<svg xmlns='http://www.w3.org/2000/svg'/>", kind: "svg");

        foreach (var record in await agent.GetArtifactsAsync())
        {
            var stored = await Store().OpenReadAsync("art-octet-user/conv", record.Versions[0].AttachmentId);
            Assert.Equal("application/octet-stream", stored!.MediaType);
        }
    }

    // AC7: deleting the conversation clears its artifact metadata.
    [Fact]
    public async Task Purge_ClearsArtifacts()
    {
        var agent = Agent("art-purge-user/conv");
        await Create(agent, "<p>x</p>");
        Assert.Single(await agent.GetArtifactsAsync());

        await agent.PurgeAsync();

        Assert.Empty(await Agent("art-purge-user/conv").GetArtifactsAsync());
    }

    // AC9: the `artifacts` key gates all three tools. Asserted in both directions on one grain, so the second half
    // proves the first was the selection doing the work.
    [Fact]
    public async Task Artifacts_Deselected_RegistersNoArtifactTools()
    {
        var agent = Agent("art-selection-user/conv");

        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch]);
        var tools = Answer(await CollectAsync(agent.ChatAsync("list tools", TestSiloConfigurator.DefaultModel)));
        await Create(agent, "<p>x</p>");
        Assert.Empty(await agent.GetArtifactsAsync());

        await agent.SetEnabledToolsAsync([ToolCatalog.Artifacts]);
        var selected = Answer(await CollectAsync(agent.ChatAsync("list tools", TestSiloConfigurator.DefaultModel)));
        await Create(agent, "<p>x</p>");
        Assert.Single(await agent.GetArtifactsAsync());

        foreach (var name in new[] { "create_artifact", "update_artifact", "read_artifact" })
        {
            Assert.DoesNotContain(name, tools, StringComparison.Ordinal);
            Assert.Contains(name, selected, StringComparison.Ordinal);
        }
    }

    private async Task<string> StoredSession(IAgentGrain agent)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);
        return state.State.SerializedSession ?? "";
    }

    // AC8 (compaction, the spike's branch (a)): the session that reaches Cosmos does not grow with the artifact.
    // Eleven 60 KB versions plus a read would be over 700 KB inlined; compacted, the session holds stubs only.
    [Fact]
    public async Task ArtifactUpdates_DoNotBlowTheSessionSizeBudget()
    {
        var agent = Agent("art-budget-user/conv");
        const int Size = 60 * 1024;
        await Create(agent, Content(Size));
        var id = Assert.Single(await agent.GetArtifactsAsync()).Id;
        for (var v = 0; v < 10; v++)
        {
            await Call(agent, "update_artifact", new { artifactId = id, content = new string((char)('a' + v), Size) });
        }

        await Call(agent, "read_artifact", new { artifactId = id });

        var bytes = System.Text.Encoding.UTF8.GetByteCount(await StoredSession(agent));
        Assert.True(Size * 11 > 512 * 1024, "the inlined versions should dwarf the ceiling, or this test proves nothing");
        Assert.InRange(bytes, 1, 64 * 1024);
    }

    // AC8: after compaction the history still pairs every call with its result, the stub names the stored version,
    // and the next turn works: read_artifact brings back the real content, which compaction did not touch.
    [Fact]
    public async Task CompactedArtifactCall_NextTurnSucceeds()
    {
        var agent = Agent("art-compacted-user/conv");
        await Create(agent, "<p>original body</p>");
        var id = Assert.Single(await agent.GetArtifactsAsync()).Id;
        await Call(agent, "update_artifact", new { artifactId = id, content = "<p>second body</p>" });

        var session = await StoredSession(agent);
        Assert.DoesNotContain("original body", session, StringComparison.Ordinal);
        Assert.DoesNotContain("second body", session, StringComparison.Ordinal);

        var calls = (await agent.GetHistoryAsync()).SelectMany(t => t.ToolCalls ?? []).ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.NotNull(c.ToolResult));
        Assert.Contains(ArtifactLimits.StubPrefix + id + " v1", calls[0].ToolInput, StringComparison.Ordinal);
        Assert.Contains(ArtifactLimits.StubPrefix + id + " v2", calls[1].ToolInput, StringComparison.Ordinal);

        Assert.Contains("second body", Result(await Call(agent, "read_artifact", new { artifactId = id })), StringComparison.Ordinal);

        // The read's result is stubbed once its turn is over. (The fake echoes the result into its reply, so the
        // assertion is on the stored result itself, not on the whole session.)
        var read = (await agent.GetHistoryAsync()).SelectMany(t => t.ToolCalls ?? []).Last();
        Assert.Equal("read_artifact", read.ToolName);
        Assert.Contains(ArtifactLimits.StubPrefix + id + " v2", read.ToolResult, StringComparison.Ordinal);
        Assert.Contains("help you today", Answer(await CollectAsync(agent.ChatAsync("thanks", TestSiloConfigurator.DefaultModel))), StringComparison.Ordinal);
    }

    // AC10: history projects the artifact cards from the turn's tool calls, also when the call rode on a message that
    // carried text (the narrated shape, which a forward-only call/result match drops). A refusal projects no card.
    [Fact]
    public async Task History_ProjectsArtifacts_ForNarratedCall()
    {
        var agent = Agent("art-history-user/conv");
        await CollectAsync(agent.ChatAsync(
            FakeChatClient.NarratingCallTrigger + " " + FakeChatClient.CallToolPrompt(
                "create_artifact", new { kind = "svg", title = "Logo", content = "<svg/>" }),
            TestSiloConfigurator.DefaultModel));
        var id = Assert.Single(await agent.GetArtifactsAsync()).Id;
        await Call(agent, "update_artifact", new { artifactId = id, content = "<svg></svg>", title = "Logo 2" });
        await Call(agent, "update_artifact", new { artifactId = "art_nope", content = "x" });

        var history = await agent.GetHistoryAsync();
        var narrated = Assert.Single(history, t => t.Text.Contains(FakeChatClient.NarratedCallText, StringComparison.Ordinal));
        Assert.Equal(new ArtifactInfo(id, 1, ArtifactKind.Svg, "Logo"), Assert.Single(narrated.Artifacts!));

        var cards = history.Where(t => t.Artifacts is not null).SelectMany(t => t.Artifacts!).ToList();
        Assert.Equal([new ArtifactInfo(id, 1, ArtifactKind.Svg, "Logo"), new ArtifactInfo(id, 2, ArtifactKind.Svg, "Logo 2")], cards);
    }

    // AC8: content that merely STARTS with '[' (a JSON-array code file, a markdown document opening with [TOC]) is
    // real content and is compacted too. Only the stubs compaction itself writes are left alone.
    [Fact]
    public async Task BracketLeadingContent_IsStillCompacted()
    {
        var agent = Agent("art-bracket-user/conv");
        await Create(agent, "[1, 2, \"bracket-leading-body\"]", kind: "code", language: "json");
        await Call(agent, "create_artifact", new { kind = "markdown", title = "Doc", content = "[TOC]\n\n# toc-leading-body" });

        var session = await StoredSession(agent);
        Assert.DoesNotContain("bracket-leading-body", session, StringComparison.Ordinal);
        Assert.DoesNotContain("toc-leading-body", session, StringComparison.Ordinal);
    }
}
