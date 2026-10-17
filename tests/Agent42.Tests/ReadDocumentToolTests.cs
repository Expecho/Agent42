using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// The <c>read_document</c> tool (ADR 0098): sequential, part-by-part reading of a conversation's ingested
/// documents, its per-turn cap and its post-turn compaction. Each test drives the tool through
/// <see cref="FakeChatClient.CallToolPrompt"/>, so it runs exactly as a model-made call would. Documents are
/// seeded straight into the process-global <see cref="InMemoryDocumentIndex"/> under the grain's ownerKey, with
/// the deterministic embedder the retrieval provider uses, and registered on the grain.
/// </summary>
public sealed class ReadDocumentToolTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
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

    // The (decoded) results of this turn's scripted call(s). A result is JSON-serialized on its way to the model,
    // which escapes apostrophes and non-ASCII, so it is decoded before any assertion: an undecoded match on
    // "Document '" would pass vacuously or not at all.
    private static string Result(IReadOnlyList<AgentStreamEvent> events)
    {
        var answer = Answer(events);
        var start = answer.IndexOf(FakeChatClient.ScriptedResultOpen, StringComparison.Ordinal);
        var end = answer.LastIndexOf(FakeChatClient.ScriptedResultClose, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"no scripted result in: {answer}");
        return System.Text.RegularExpressions.Regex.Unescape(answer[(start + FakeChatClient.ScriptedResultOpen.Length)..end]);
    }

    private IAgentGrain Agent(string key) => Cluster.GrainFactory.GetGrain<IAgentGrain>(key);

    private static Task<List<AgentStreamEvent>> Read(IAgentGrain agent, string fileName, int part = 1)
        => CollectAsync(agent.ChatAsync(
            FakeChatClient.CallToolPrompt(ToolCatalog.ReadDocument, new { fileName, part }), TestSiloConfigurator.DefaultModel));

    private static string Sentences(int count)
        => string.Join(" ", Enumerable.Range(1, count).Select(i => $"Sentence {i:D4} says something unique here."));

    // Chunks the segments with the real chunker, embeds them with the deterministic generator and indexes them
    // under the grain's ownerKey, then registers the name the way an upload does.
    private static async Task<IReadOnlyList<DocumentChunk>> SeedAsync(
        IAgentGrain agent, string key, string fileName, IReadOnlyList<DocumentSegment> segments)
    {
        var chunks = new DocumentChunker().Chunk(fileName, segments);
        await IndexAsync(key, chunks);
        await agent.RegisterDocumentAsync(fileName);
        return chunks;
    }

    private static async Task IndexAsync(string key, IReadOnlyList<DocumentChunk> chunks)
    {
        var embeddings = await new DeterministicEmbeddingGenerator().GenerateAsync(chunks.Select(c => c.Text));
        await new InMemoryDocumentIndex().UpsertChunksAsync(
            key, [.. chunks.Zip(embeddings, (c, e) => new EmbeddedChunk(c, e.Vector))]);
    }

    // Twenty-five one-sentence pages: 25 small chunks, so three parts whose whole text fits in the fake's echo.
    private static DocumentSegment[] ShortPages(int count) =>
        [.. Enumerable.Range(1, count).Select(p => new DocumentSegment($"Page {p} text.", p))];

    private async Task<string> StoredSession(IAgentGrain agent)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);
        return state.State.SerializedSession ?? "";
    }

    private static async Task<string> LastToolResult(IAgentGrain agent)
    {
        var turn = (await agent.GetHistoryAsync()).Last(t => t.ToolCalls is { Count: > 0 });
        var raw = turn.ToolCalls![^1].ToolResult!;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString()! : raw;
    }

    // AC5: part 1 and the last part, with the header (part p of P, page range) and the matching footer.
    [Fact]
    public async Task ReadDocument_FirstAndLastPart()
    {
        const string key = "readdoc-parts-user/conv";
        var agent = Agent(key);
        await SeedAsync(agent, key, "report.pdf", ShortPages(25));

        var first = Result(await Read(agent, "report.pdf", 1));
        Assert.StartsWith("Document 'report.pdf' — part 1 of 3 (pages 1–10)", first, StringComparison.Ordinal);
        Assert.Contains("Page 1 text.\n\nPage 2 text.", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Page 11 text.", first, StringComparison.Ordinal);
        Assert.EndsWith("Call read_document with part 2 to continue.", first, StringComparison.Ordinal);
        Assert.DoesNotContain(ReadDocumentLimits.UntrustedMarker, first, StringComparison.Ordinal);

        var last = Result(await Read(agent, "report.pdf", 3));
        Assert.StartsWith("Document 'report.pdf' — part 3 of 3 (pages 21–25)", last, StringComparison.Ordinal);
        Assert.EndsWith("This is the last part.", last, StringComparison.Ordinal);
    }

    // AC6: an unknown name is refused with the names that exist; nothing throws.
    [Fact]
    public async Task ReadDocument_UnknownName_ListsDocuments()
    {
        const string key = "readdoc-unknown-user/conv";
        var agent = Agent(key);
        await SeedAsync(agent, key, "report.pdf", ShortPages(3));

        var result = Result(await Read(agent, "Report.PDF"));

        Assert.Contains("There is no document named 'Report.PDF'", result, StringComparison.Ordinal);
        Assert.Contains("'report.pdf'", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadDocument_NoDocuments_SaysSo()
        => Assert.Contains("has no documents to read", Result(await Read(Agent("readdoc-none-user/conv"), "x.pdf")), StringComparison.Ordinal);

    // AC6: a part outside 1..P is refused with the valid range.
    [Fact]
    public async Task ReadDocument_PartOutOfRange()
    {
        const string key = "readdoc-range-user/conv";
        var agent = Agent(key);
        await SeedAsync(agent, key, "report.pdf", ShortPages(25));

        Assert.Contains("part 4 does not exist. Parts are numbered 1 to 3", Result(await Read(agent, "report.pdf", 4)), StringComparison.Ordinal);
        Assert.Contains("part 0 does not exist", Result(await Read(agent, "report.pdf", 0)), StringComparison.Ordinal);
    }

    // AC14: a registered name with no chunks (a scanned PDF, a legacy name) is distinct from an unknown one.
    [Fact]
    public async Task ReadDocument_RegisteredNameWithoutChunks()
    {
        var agent = Agent("readdoc-empty-user/conv");
        await agent.RegisterDocumentAsync("scan.pdf");

        Assert.Contains("'scan.pdf' has no readable text", Result(await Read(agent, "scan.pdf")), StringComparison.Ordinal);
    }

    // AC5 + AC13: a web document is readable by its "(web: host)" name, and its header carries the UNTRUSTED
    // marker (ADR 0066's provenance split). The upload half is asserted in ReadDocument_FirstAndLastPart.
    [Fact]
    public async Task ReadDocument_WebDocumentName_IsMarkedUntrusted()
    {
        var agent = Agent("readdoc-web-user/conv");
        const string url = "https://example.com/manual.pdf";
        await CollectAsync(agent.ChatAsync($"read the manual at {url}", TestSiloConfigurator.DefaultModel));
        var name = WebDocumentLimits.SanitizeFileName(new Uri(url), DocumentMediaTypes.PlainText);

        var result = Result(await Read(agent, name));

        Assert.StartsWith($"Document '{name}' — part 1 of 1 — {ReadDocumentLimits.UntrustedMarker}", result, StringComparison.Ordinal);
        Assert.Contains(InMemoryWebDocumentFetcher.CannedMarker, result, StringComparison.Ordinal);
    }

    // AC4 end to end: two consecutive parts of a long text document concatenate to the source with no repeat
    // at the boundary.
    [Fact]
    public async Task ReadDocument_ConsecutiveParts_DoNotRepeatTheOverlap()
    {
        const string key = "readdoc-stitch-user/conv";
        var agent = Agent(key);
        var chunks = await SeedAsync(agent, key, "long.txt", [new DocumentSegment(Sentences(300), null)]);
        Assert.True(chunks.Count > ReadDocumentLimits.ChunksPerPart, "the fixture must span two parts");

        // Part 2 starts PAST chunk 10's overlap with chunk 9: inside chunk 10, not at its head.
        var part2 = Result(await Read(agent, "long.txt", 2));
        var body = part2[(part2.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        var chunk10 = chunks[ReadDocumentLimits.ChunksPerPart].Text;
        Assert.False(body.StartsWith(chunk10[..40], StringComparison.Ordinal), "the overlap must be stripped");
        Assert.Contains(body[..40], chunk10, StringComparison.Ordinal);
    }

    // AC7: the constants make the cap a never-binding invariant: a full part of full chunks fits.
    [Fact]
    public void ReadDocumentLimits_PartBudgetCoversChunksPerPart()
        => Assert.True(ReadDocumentLimits.ChunksPerPart * DocumentChunker.ChunkSize <= ReadDocumentLimits.MaxPartTextLength);

    // AC7: the cap is still enforced in code: chunks longer than the chunker makes are cut, with the marker.
    [Fact]
    public async Task ReadDocument_OverlongChunk_IsTruncatedWithMarker()
    {
        const string key = "readdoc-overlong-user/conv";
        var agent = Agent(key);
        await IndexAsync(key, [.. Enumerable.Range(0, 3).Select(i => new DocumentChunk("big.txt", null, i, new string((char)('a' + i), 5000)))]);
        await agent.RegisterDocumentAsync("big.txt");

        var result = Result(await Read(agent, "big.txt"));

        Assert.Contains(ReadDocumentLimits.TruncationMarker + "\n\nThis is the last part.", result, StringComparison.Ordinal);
    }

    // AC8: the 21st part in one turn is refused; the next chat turn reads again (reset site 1 of 2).
    [Fact]
    public async Task ReadDocument_PerTurnCap_RefusesThenResets()
    {
        const string key = "readdoc-cap-user/conv";
        var agent = Agent(key);
        await SeedAsync(agent, key, "long.txt", [new DocumentSegment(Sentences(5500), null)]);

        var capped = Result(await ReadSequence(agent, "long.txt", ReadDocumentLimits.MaxPartsPerTurn + 1));
        Assert.Contains($"You have read {ReadDocumentLimits.MaxPartsPerTurn} parts this turn, the limit", capped, StringComparison.Ordinal);
        Assert.Contains($"stopped before part {ReadDocumentLimits.MaxPartsPerTurn + 1}", capped, StringComparison.Ordinal);

        Assert.StartsWith("Document 'long.txt' — part 1 of", Result(await Read(agent, "long.txt")), StringComparison.Ordinal);
    }

    // AC8: a fired reminder resets the budget too (reset site 2 of 2): ReceiveReminder does not drain the chat
    // core, so without its own reset it would inherit the spent count. The reminder's read is a SUCCESSFUL part,
    // which compaction stubs; a cap refusal would be kept verbatim.
    [Fact]
    public async Task ReadDocument_PerTurnCap_ResetsOnReminder()
    {
        const string key = "readdoc-reminder-user/conv";
        var agent = Agent(key);
        await SeedAsync(agent, key, "long.txt", [new DocumentSegment(Sentences(5500), null)]);

        var readOnFire = FakeChatClient.CallToolPrompt(ToolCatalog.ReadDocument, new { fileName = "long.txt", part = 1 });
        await CollectAsync(agent.ChatAsync(FakeChatClient.CallToolPrompt("schedule_prompt",
            new { name = "read-it", prompt = readOnFire, dueInMinutes = 60.0, repeatEveryMinutes = 60.0 }), TestSiloConfigurator.DefaultModel));
        await ReadSequence(agent, "long.txt", ReadDocumentLimits.MaxPartsPerTurn);

        await agent.AsReference<IRemindable>().ReceiveReminder("read-it", default);

        var fired = await LastToolResult(agent);
        Assert.StartsWith(ReadDocumentLimits.StubPrefix + "part 1 of 'long.txt'", fired, StringComparison.Ordinal);
    }

    private static Task<List<AgentStreamEvent>> ReadSequence(IAgentGrain agent, string fileName, int parts)
        => CollectAsync(agent.ChatAsync(
            FakeChatClient.CallToolSequencePrompt(
                ToolCatalog.ReadDocument, Enumerable.Range(1, parts).Select(p => (object)new { fileName, part = p })),
            TestSiloConfigurator.DefaultModel));

    // AC9: twenty full parts (~200 KB) in one turn leave only stubs in the stored session.
    [Fact]
    public async Task ReadDocumentParts_DoNotBlowTheSessionSizeBudget()
    {
        const string key = "readdoc-budget-user/conv";
        var agent = Agent(key);
        await SeedAsync(agent, key, "long.txt", [new DocumentSegment(Sentences(5500), null)]);

        await ReadSequence(agent, "long.txt", ReadDocumentLimits.MaxPartsPerTurn);

        var bytes = System.Text.Encoding.UTF8.GetByteCount(await StoredSession(agent));
        Assert.True(ReadDocumentLimits.MaxPartsPerTurn * 8 * 1024 > 128 * 1024, "the inlined parts should dwarf the ceiling");
        Assert.InRange(bytes, 1, 48 * 1024);
    }

    // AC9: after compaction every call still pairs with its (stubbed) result, and the next turn works.
    [Fact]
    public async Task CompactedReadDocument_NextTurnSucceeds()
    {
        const string key = "readdoc-next-user/conv";
        var agent = Agent(key);
        await SeedAsync(agent, key, "long.txt", [new DocumentSegment(Sentences(300), null)]);

        await Read(agent, "long.txt", 2);
        var call = (await agent.GetHistoryAsync()).SelectMany(t => t.ToolCalls ?? []).Single();
        Assert.Equal(ToolCatalog.ReadDocument, call.ToolName);
        Assert.Equal(ReadDocumentLimits.Stub("long.txt", 2), JsonSerializer.Deserialize<string>(call.ToolResult!));
        Assert.DoesNotContain("Sentence 0150", await StoredSession(agent), StringComparison.Ordinal);

        Assert.Contains("help you today", Answer(await CollectAsync(agent.ChatAsync("thanks", TestSiloConfigurator.DefaultModel))), StringComparison.Ordinal);
        Assert.StartsWith("Document 'long.txt' — part 2 of", Result(await Read(agent, "long.txt", 2)), StringComparison.Ordinal);
    }

    // AC9: a refusal read nothing and is kept verbatim; a later turn's compaction pass leaves the earlier stub as
    // it was (idempotent).
    [Fact]
    public async Task Compaction_LeavesRefusalsAndStubsAlone()
    {
        const string key = "readdoc-idem-user/conv";
        var agent = Agent(key);
        await SeedAsync(agent, key, "report.pdf", ShortPages(3));

        await Read(agent, "missing.pdf");
        await Read(agent, "report.pdf");
        var before = (await agent.GetHistoryAsync()).SelectMany(t => t.ToolCalls ?? []).Select(c => c.ToolResult).ToList();
        await CollectAsync(agent.ChatAsync("thanks", TestSiloConfigurator.DefaultModel));
        var after = (await agent.GetHistoryAsync()).SelectMany(t => t.ToolCalls ?? []).Select(c => c.ToolResult).ToList();

        Assert.Contains("There is no document named", before[0], StringComparison.Ordinal);
        Assert.Contains(ReadDocumentLimits.Stub("report.pdf", 1), JsonSerializer.Deserialize<string>(before[1]!), StringComparison.Ordinal);
        Assert.Equal(before, after);
    }

    // AC10 (rule 11): ON for the null (== all) selection, OFF for a selection saved before the key existed.
    [Fact]
    public void ReadDocument_OffForSavedSelection()
    {
        Assert.True(ToolCatalog.IsToolEnabled(ToolCatalog.ReadDocument, ToolCatalog.AllSelectableToolKeys.ToHashSet()));
        Assert.False(ToolCatalog.IsToolEnabled(ToolCatalog.ReadDocument, new HashSet<string> { ToolCatalog.WebSearch, ToolCatalog.Artifacts }));
        Assert.Equal(ToolCatalog.ReadDocument, ToolCatalog.KeyFor(ToolCatalog.ReadDocument));
    }

    // The stub compaction writes, built from the call's own arguments.
    [Fact]
    public void Stub_NamesTheFileAndPart()
        => Assert.Equal(
            "[read_document result omitted: part 3 of 'a.pdf' was read in an earlier turn; call read_document again to see it]",
            ReadDocumentLimits.Stub("a.pdf", 3));
}
