using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// URL seeding from MCP tool results (ADR 0067): a per-server opt-in that lets a URL arriving in a remote
/// MCP server's tool result become fetchable by <c>fetch_web_page</c> and readable by
/// <c>read_web_document</c>, without the user pasting it back by hand.
///
/// <para><b>Most assertions read PERSISTED GRAIN STATE rather than the reply text</b>, following the
/// <c>WebDocumentToolTests</c> precedent: the fake echoes only a trimmed slice of the tool payloads, and a
/// URL appears in the link tool's own result as well as in anything derived from it — so a text search
/// cannot tell "seeded" from "merely mentioned", and cannot count at all.</para>
///
/// <para><b>The two-call chain is not incidental.</b> <c>FakeChatClient</c> short-circuits to a text answer
/// once any tool has run for a turn, so a single-trigger test would silently only ever make the FIRST call
/// and every chained assertion would pass vacuously. The seeding triggers are handled ahead of that guard —
/// see <c>FakeChatClient.SeedingChainTrigger</c>.</para>
/// </summary>
public sealed class McpUrlSeedingTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string SeedingServerId = "srv-scraper";
    private const string PlainServerId = "srv-plain";

    // The URL the fake link tool reports. Nobody typed it and no search returned it, so it is fetchable
    // only if seeding put it there — which is what makes every assertion below non-vacuous.
    private const string LinkedDocumentUrl = "https://linked.example/whitepaper.pdf";

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> events)
    {
        var collected = new List<AgentStreamEvent>();
        await foreach (var evt in events)
        {
            collected.Add(evt);
        }

        return collected;
    }

    private static Task<List<AgentStreamEvent>> Chat(IAgentGrain agent, string message)
        => CollectAsync(agent.ChatAsync(message, TestSiloConfigurator.DefaultModel));

    private static string Answer(IReadOnlyList<AgentStreamEvent> events) =>
        string.Concat(events.Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));

    // A stand-in for the sibling repo's find_links: a tool whose RESULT is JSON carrying absolute hrefs.
    // Quoted exactly as an MCP server would return them, because the JSON quoting is one of the traps
    // (a naive harvest seeds `https://…pdf"` and never matches anything).
    private static AIFunction LinkTool(params string[] urls) =>
        AIFunctionFactory.Create(
            (string filter) => "{\"links\":["
                + string.Join(",", urls.Select(u => $"{{\"text\":\"a document\",\"href\":\"{u}\"}}"))
                + "]}",
            FakeChatClient.SeedingLinkToolName,
            "lists the links on a page");

    // Points the per-user MCP seam at one seeding server and one ordinary one. Both are always registered
    // so the per-server gate is exercised by which group a test SELECTS, not by which exists.
    private void GiveServers(bool seeding, params string[] urls)
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<SingleUserMcpToolProvider>().Servers =
                [
                    new McpServerTools(
                        seeding ? SeedingServerId : PlainServerId,
                        [LinkTool(urls.Length > 0 ? urls : [LinkedDocumentUrl])],
                        [FakeChatClient.SeedingLinkToolName],
                        SeedsFetchableUrls: seeding),
                ];
        }
    }

    // The grain's persisted state — the only place that can COUNT seeded URLs. Same helper shape as
    // WebDocumentToolTests.ReadStateAsync, and for the same reason.
    private async Task<AgentGrainState> ReadStateAsync(IAgentGrain agent)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);

        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);
        return state.State;
    }

    // ---- The core property: seeding on, seeding off ----

    // AC1: a seeding server's tool result makes its URLs fetchable. Asserted in state, on the normalized
    // form — the same form IsFetchableUrl compares, so a divergent normalization would fail here rather
    // than turn up as a mysteriously refused fetch in production.
    [Fact]
    public async Task SeedingServer_ResultUrls_BecomeFetchable()
    {
        GiveServers(seeding: true);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-fetchable-conversation");

        await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        var state = await ReadStateAsync(agent);
        Assert.Contains(LinkedDocumentUrl, state.SeededFetchableUrls);

        // The split is real: the user-typed/search list is untouched by a harvest.
        Assert.Empty(state.FetchableUrls);
    }

    // AC2: an ordinary (non-seeding) server's result does NOT. This is the whole per-server opt-in — the
    // same tool, the same result, the same prompt, and nothing is seeded.
    [Fact]
    public async Task NonSeedingServer_ResultUrls_StayRefused()
    {
        GiveServers(seeding: false);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-nonseeding-conversation");

        var answer = Answer(await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document"));

        Assert.Empty((await ReadStateAsync(agent)).SeededFetchableUrls);

        // ...and the read really was attempted and really was refused, so this is not passing merely
        // because the chain stopped early. Apostrophe-free: a tool result is JSON-escaped on its way to
        // the model, which is the trap WebDocumentToolTests documents.
        Assert.Contains("A document can only be read", answer, StringComparison.Ordinal);
    }

    // AC3: the payoff case — a seeded URL is READABLE as a document, which is the thing the feature exists
    // for (find_links → a PDF). The canned document's marker proves the ingest really happened.
    [Fact]
    public async Task SeededUrl_IsReadableAsADocument()
    {
        GiveServers(seeding: true);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-readable-conversation");

        await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        var state = await ReadStateAsync(agent);
        Assert.Contains(LinkedDocumentUrl, state.WebDocumentUrls);
        Assert.NotEmpty(state.WebDocumentNames);

        // ...and it really is IN the conversation now: the next turn calls no tool at all and still gets
        // retrieval injected, so the document was ingested rather than merely fetched. The ingest turn's
        // own reply cannot show this — the fake trims its tool-payload echo across all of history and
        // this chain spends that budget on the link tool's JSON.
        Assert.Contains("[ctx:", Answer(await Chat(agent, "what did it say")), StringComparison.Ordinal);
    }

    // AC4: and fetchable as a PAGE. Both consumers of IsFetchableUrl are covered, because they are two
    // separate call sites with two separate refusal messages and only a test per path notices a miss.
    [Fact]
    public async Task SeededUrl_IsFetchableAsAPage()
    {
        GiveServers(seeding: true);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-page-conversation");

        var answer = Answer(await Chat(agent, $"{FakeChatClient.SeedingFetchChainTrigger} a page"));

        // A POSITIVE from the page the fetch actually returned. A negative on the refusal text was the
        // first attempt and was worse than useless: the refusal contains an apostrophe ("I won't fetch"),
        // and the needle written for it did not — so it matched nothing, and the test still passed with
        // IsFetchableUrl reading only FetchableUrls. That is the exact miss this test exists to catch.
        Assert.Contains("Canned offline page content", answer, StringComparison.Ordinal);
        Assert.Contains(LinkedDocumentUrl, (await ReadStateAsync(agent)).SeededFetchableUrls);
    }

    // ---- Harvest mechanics ----

    // AC5: the URL arrives JSON-quoted (`"href":"https://…"`). A seeded string carrying the closing quote
    // never matches in IsFetchableUrl, and the feature would look broken while every regex unit test
    // passed. Asserted as an exact-equality membership, so a trailing character fails it.
    //
    // What delivers this is AbsoluteUrlPattern()'s CHARACTER CLASS, which stops at JSON structure — not a
    // post-hoc trim. Worth stating because the first version of this code had both, and the trim was dead:
    // every character it removed was already excluded by the pattern, so it could only ever damage a URL
    // that legitimately ended in one (see the parenthesis test below).
    [Fact]
    public async Task SeededUrl_IsTrimmedOfJsonPunctuation()
    {
        GiveServers(seeding: true);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-json-trim-conversation");

        await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        var seeded = Assert.Single((await ReadStateAsync(agent)).SeededFetchableUrls);
        Assert.Equal(LinkedDocumentUrl, seeded);
    }

    // The other half of AC5, and the one that is easy to get wrong in the "safe" direction: a URL may
    // legitimately END in a closing parenthesis or a full stop. A post-hoc trim of "trailing punctuation"
    // produces a seeded string the model's own URL can never match — a silent refusal of exactly the link
    // this feature exists to deliver. Over-trimming fails closed, so it is a functional bug rather than a
    // security one, and a quieter one than the stray quote such a trim would be added to remove.
    //
    // This is a REGRESSION PIN, not a description of current behaviour: nothing trims today. It was
    // mutation-checked by re-adding `.TrimEnd('.', ')')`, which fails it.
    [Fact]
    public async Task SeededUrl_KeepsATrailingParenthesisThatIsPartOfThePath()
    {
        const string Parenthesised = "https://linked.example/wiki/Douglas_Adams_(author)";
        GiveServers(seeding: true, Parenthesised);

        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-parenthesis-conversation");
        await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        var state = await ReadStateAsync(agent);
        Assert.Equal(Parenthesised, Assert.Single(state.SeededFetchableUrls));

        // ...and it is genuinely usable, not merely stored intact.
        Assert.Contains(Parenthesised, state.WebDocumentUrls);
    }

    // AC6: bare hostnames are NOT seeded. UrlPattern() matches them because user prose says "look at
    // wikipedia.org"; an MCP result is JSON, where a bare host turns up in prose, error strings and
    // unrelated fields — seeding each would let the model whitelist homepages out of its own noise.
    [Fact]
    public async Task BareHostnameInResult_IsNotSeeded()
    {
        var bareHostTool = AIFunctionFactory.Create(
            (string filter) => "{\"note\":\"see example.com and docs.contoso.com for details\"}",
            FakeChatClient.SeedingLinkToolName,
            "lists the links on a page");

        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<SingleUserMcpToolProvider>().Servers =
                [new McpServerTools(SeedingServerId, [bareHostTool], [FakeChatClient.SeedingLinkToolName], true)];
        }

        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-bare-host-conversation");
        await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        Assert.Empty((await ReadStateAsync(agent)).SeededFetchableUrls);
    }

    // AC7: one result contributes at most MaxSeededUrlsPerResult URLs. Without this cap, a single scrape of
    // a link-heavy page (an 8 KB MCP result holds 150–250 URLs) fills the seeded list and evicts the very
    // link the feature exists to deliver — the defect the separate list was introduced to prevent,
    // reproduced one level down.
    [Fact]
    public async Task Harvest_IsCappedPerResult()
    {
        var many = Enumerable.Range(0, AgentGrain.MaxSeededUrlsPerResult + 15)
            .Select(i => $"https://linked.example/doc-{i}.pdf")
            .ToArray();
        GiveServers(seeding: true, many);

        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-per-result-cap-conversation");
        await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        var state = await ReadStateAsync(agent);
        Assert.Equal(AgentGrain.MaxSeededUrlsPerResult, state.SeededFetchableUrls.Count);

        // Truncated in DOCUMENT ORDER, so the head of the list survives — a filtered find_links puts what
        // the model asked for first, and dropping from the front would defeat the feature.
        Assert.Equal(many[0], state.SeededFetchableUrls[0]);
        Assert.DoesNotContain(many[^1], state.SeededFetchableUrls);
    }

    // AC8: seeding cannot evict URLs the user typed. This is the FUNCTIONAL half of why the two lists are
    // separate, and it bites before any security argument does: FetchableUrls holds 50, so one link-heavy
    // result harvested into it would silently drop everything the user had pasted.
    [Fact]
    public async Task SeededUrls_DoNotEvictUserTypedUrls()
    {
        var many = Enumerable.Range(0, AgentGrain.MaxSeededUrlsPerResult)
            .Select(i => $"https://linked.example/bulk-{i}.pdf")
            .ToArray();
        GiveServers(seeding: true, many);

        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-no-eviction-conversation");

        // A URL the user wrote, allowlisted the ordinary way (RememberFetchableUrlsInUserText).
        await Chat(agent, "have a look at https://example.com/mine.pdf later");
        await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        var state = await ReadStateAsync(agent);
        Assert.Contains("https://example.com/mine.pdf", state.FetchableUrls);
        Assert.NotEmpty(state.SeededFetchableUrls);
    }

    // AC9: the seeded list is itself bounded, oldest first — it lives in the 2 MB session item (ADR 0004),
    // and the trigger is a model-chosen tool call, so "it will stay small" is not a property anyone can
    // rely on. Driven over several turns because one result is capped at MaxSeededUrlsPerResult.
    [Fact]
    public async Task SeededUrls_AreBounded()
    {
        // A tool that returns a FRESH batch on every call. Swapping the provider's groups between turns
        // would not work: AgentGrain resolves its MCP tools once in OnActivateAsync, so every turn of one
        // conversation sees the instances the FIRST turn got — and a test built that way silently asserts
        // one turn's harvest, deduplicated, forever.
        var call = 0;
        var rotating = AIFunctionFactory.Create(
            (string filter) =>
            {
                var turn = Interlocked.Increment(ref call) - 1;
                return "{\"links\":[" + string.Join(",", Enumerable
                    .Range(0, AgentGrain.MaxSeededUrlsPerResult)
                    .Select(i => $"{{\"href\":\"https://linked.example/t{turn}-{i}.pdf\"}}")) + "]}";
            },
            FakeChatClient.SeedingLinkToolName,
            "lists the links on a page");

        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<SingleUserMcpToolProvider>().Servers =
                [new McpServerTools(SeedingServerId, [rotating], [FakeChatClient.SeedingLinkToolName], true)];
        }

        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-bounded-conversation");

        var turns = (AgentGrain.MaxSeededFetchableUrls / AgentGrain.MaxSeededUrlsPerResult) + 2;
        for (var turn = 0; turn < turns; turn++)
        {
            await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");
        }

        var state = await ReadStateAsync(agent);
        Assert.Equal(AgentGrain.MaxSeededFetchableUrls, state.SeededFetchableUrls.Count);

        // Oldest out first: the first turn's URLs are gone, the last turn's are kept.
        Assert.DoesNotContain("https://linked.example/t0-0.pdf", state.SeededFetchableUrls);
        Assert.Contains($"https://linked.example/t{turns - 1}-0.pdf", state.SeededFetchableUrls);
    }

    // AC10: selection still gates. A deselected MCP tool is never registered, so it is never called and
    // seeds nothing — the ADR 0021/0026/0058 hierarchy is upstream of seeding, and the opt-in cannot
    // re-grant a tool the user turned off.
    [Fact]
    public async Task DeselectedMcpTool_SeedsNothing()
    {
        GiveServers(seeding: true);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-deselected-conversation");
        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch, ToolCatalog.ReadWebDocument]); // no mcp key

        var answer = Answer(await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document"));

        Assert.DoesNotContain(FakeChatClient.SeedingLinkToolName, ToolNames(answer), StringComparison.Ordinal);
        Assert.Empty((await ReadStateAsync(agent)).SeededFetchableUrls);
    }

    // AC11: a URL cut in half by the 8 KB truncation is not seeded as a fragment. UrlHarvestingMcpTool wraps
    // OUTSIDE TruncatingMcpTool, so the harvest sees the already-truncated text: without the boundary check
    // the trailing garbage prefix consumes a seed slot and can never match anything.
    [Fact]
    public async Task TruncatedUrl_IsNotSeeded()
    {
        // Sized so the FIRST URL sits comfortably inside the budget and the second one genuinely straddles
        // the cut: the padding is computed backwards from MaxToolResultLength so the second URL begins 40
        // characters before it. (Getting this wrong is quiet — a fixture whose string is SHORTER than the
        // budget throws out of the range operator, the tool fails, and nothing is harvested at all, which
        // reads as the boundary check working.)
        const string Head = "{\"a\":\"https://linked.example/kept.pdf\",\"pad\":\"";
        const string Join = "\",\"b\":\"";
        const string CutUrl = "https://linked.example/cut-in-half-by-the-budget.pdf";
        var padding = new string('x', McpToolLimits.MaxToolResultLength - Head.Length - Join.Length - 40);
        var truncatingTool = AIFunctionFactory.Create(
            (string filter) => (Head + padding + Join + CutUrl + "\"}")[..McpToolLimits.MaxToolResultLength]
                + McpToolLimits.TruncationMarker,
            FakeChatClient.SeedingLinkToolName,
            "lists the links on a page");

        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<SingleUserMcpToolProvider>().Servers =
                [new McpServerTools(SeedingServerId, [truncatingTool], [FakeChatClient.SeedingLinkToolName], true)];
        }

        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("seed-truncated-conversation");
        await Chat(agent, $"{FakeChatClient.SeedingChainTrigger} a document");

        // Only the intact URL is seeded — the 40-character fragment of the second one (which does carry
        // the distinctive "cut-in-half" text, so this is not passing on a mismatch) consumes no slot.
        var seeded = (await ReadStateAsync(agent)).SeededFetchableUrls;
        Assert.Equal("https://linked.example/kept.pdf", Assert.Single(seeded));
        Assert.Contains("cut-in-half", CutUrl[..40], StringComparison.Ordinal);
    }

    // ---- The model's own instructions ----

    // AC16: the prohibition the model reads on EVERY turn must permit a seeded URL. The tool descriptions
    // sit in the JSON schema unconditionally, so a description still saying "you must NEVER take a URL out
    // of a tool result" leaves a compliant model refusing to use the feature — which would look like a bug
    // while every behavioural test above passed (they drive the fake, which reads no descriptions).
    [Fact]
    public void SeedingToolDescriptions_PermitASeededUrl()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Agent42.Agents", "Tools", "AgentGrain.BuildAgent.cs"));

        // The carve-out is named in both web tool descriptions...
        Assert.Equal(2, CountOccurrences(source, "a URL that a tool result gave you"));

        // ...and the old absolute prohibition on page text is still there, unweakened: only the tool-result
        // case is carved out, never softened into "use your judgement".
        Assert.Contains("out of the text of a page", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // The fake echoes the tool schema it was handed as [tools:…]; this pulls that list out of a reply.
    private static string ToolNames(string answer)
    {
        const string Marker = "[tools:";
        var start = answer.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += Marker.Length;
        var end = answer.IndexOf(']', start);
        return end < 0 ? answer[start..] : answer[start..end];
    }
}
