using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The server-side MCP surface (ADR 0070): stored agents exposed as an <c>ask_agent</c> tool to an
/// external MCP client, authenticated by the ADR 0022 per-agent key.
///
/// <para><b>Driven as raw JSON-RPC over HTTP rather than through the client SDK.</b> The wire contract is
/// what an external client actually depends on, and the security property under test — that
/// <c>initialize</c> and <c>tools/list</c> are refused before the protocol is reached — is only visible at
/// that level. It also keeps the test project free of a package it otherwise needs for nothing.</para>
/// </summary>
public sealed class AgentMcpServerTests(AgentMcpFixture fixture) : SharedClusterTests<AgentMcpFixture>(fixture)
{
    private HttpClient Client => Fixture.Client;

    // Every agent created through /api/agents belongs to GetUserId()'s no-principal fallback.
    private const string Owner = "local-dev";

    // ---- The conversation-id shape (step 3, C4.1) -------------------------------------------------
    //
    // A pure unit test of the validator, because it is a security control rather than a formatting rule:
    // the id is concatenated into the grain key `{userId}/{conversationId}`, which is ALSO an attachment
    // blob prefix and a document-index partition. The owner comes from the API key, so an id that escapes
    // its segment reaches that owner's other conversations and their blobs.

    [Theory]
    // A minted id round-trips.
    [InlineData("mcp-8d9c2a1e-0000-4b1a-9c3d-1f2e3a4b5c6d", true)]
    // Traversal — the case a StartsWith check would accept and the grain key would honour.
    [InlineData("mcp-a/../b", false)]
    [InlineData("mcp-../../other-user/secret", false)]
    // An ADR 0016 continuous id: collapses every chat onto one non-reentrant grain, so an agent reachable
    // at its own continuous grain could block on itself until the timeout expired.
    [InlineData("agent-42", false)]
    // Right prefix, wrong remainder — no separator, no empty remainder, no free text.
    [InlineData("mcp-", false)]
    [InlineData("mcp-not-a-guid", false)]
    // A plain browser conversation id, which carries no prefix at all.
    [InlineData("8d9c2a1e-0000-4b1a-9c3d-1f2e3a4b5c6d", false)]
    // Non-canonical GUID spellings are rejected so exactly ONE string addresses a given conversation —
    // two callers must not be able to reach one grain by two different keys.
    [InlineData("mcp-{8d9c2a1e-0000-4b1a-9c3d-1f2e3a4b5c6d}", false)]
    [InlineData("mcp-8d9c2a1e00004b1a9c3d1f2e3a4b5c6d", false)]
    public void IsValidConversationId_AcceptsOnlyAMintedId(string id, bool expected)
        => Assert.Equal(expected, AgentMcpLimits.IsValidConversationId(id));

    [Fact]
    public void NewConversationId_ProducesAnIdItsOwnValidatorAccepts()
        => Assert.True(AgentMcpLimits.IsValidConversationId(AgentMcpLimits.NewConversationId()));

    // ---- The protocol surface ---------------------------------------------------------------------

    // AC1.
    [Fact]
    public async Task AskAgent_ReturnsTheAgentsReply()
    {
        var (_, key) = await CreateExposedAgentAsync("Reply agent");

        var reply = await AskAgentAsync(key, "hello there");

        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("How can I help you today?", reply.Text, StringComparison.Ordinal);
    }

    // AC2. The reason the credential is middleware over the prefix and not a check inside the tool method:
    // MapMcp serves initialize, tools/list and tools/call from ONE endpoint, and the SDK enforces nothing.
    // With only an in-tool check, both of these would be anonymously reachable the moment the
    // excludedPaths entries land — which is exactly what the excludedPaths gotcha forbids.
    [Theory]
    [InlineData("initialize")]
    [InlineData("tools/list")]
    public async Task AgentMcp_AnonymousProtocolCall_IsRefused(string method)
    {
        // Built by concatenation rather than as an interpolated raw string: a JSON-RPC envelope ends in
        // consecutive closing braces, which a `$$"""` literal reads as a hole terminator (CS9007).
        var envelope = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":"
            + JsonSerializer.Serialize(method) + ",\"params\":{}}";

        using var response = await PostRpcAsync(apiKey: null, envelope);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(AgentMcpKeyGate.RejectionBody, await response.Content.ReadAsStringAsync());
    }

    // A holder of a VALID key does reach the protocol — otherwise AC2 would pass against a surface that
    // simply never worked, and the whole feature could ship dead with the suite green (ADR 0035's scar).
    [Fact]
    public async Task AgentMcp_ToolsList_AdvertisesAskAgentToAValidKey()
    {
        var (_, key) = await CreateExposedAgentAsync("Listing agent");

        var listed = await RpcResultAsync(key, """
            {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
            """);

        Assert.Contains("ask_agent", listed, StringComparison.Ordinal);
        // The description IS the contract the calling model reads — the id rule reaches it only here.
        Assert.Contains("conversationId", listed, StringComparison.Ordinal);
    }

    // AC3 and AC4 in one theory, deliberately: the point is not that each is refused but that the four
    // refusals are BYTE-IDENTICAL. A caller holding no valid key must not be able to learn whether an
    // agent exists, nor whether one they once held a key for has merely been un-exposed.
    [Fact]
    public async Task AskAgent_MissingOrUnknownKey_IsRefusedUniformly()
    {
        var (_, validKeyForUnexposed) = await CreateAgentAsync("Not exposed", exposed: false);
        var (id, revokedKey) = await CreateExposedAgentAsync("Deleted agent");
        await Client.DeleteAsync($"/api/agents/{id}");

        var refusals = new List<(HttpStatusCode Status, string Body)>();
        foreach (var candidate in new[] { null, "", "sk_agent_nonsense", validKeyForUnexposed, revokedKey })
        {
            using var response = await PostRpcAsync(candidate, AskAgentRpc("hello"));
            refusals.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(refusals, r => Assert.Equal(HttpStatusCode.Unauthorized, r.Status));
        Assert.All(refusals, r => Assert.Equal(AgentMcpKeyGate.RejectionBody, r.Body));
    }

    // AC4 again, from the other side: the SAME key works once the flag is on, so the refusal above is the
    // flag doing its job rather than the key being broken. This is the opt-in itself — every agent that
    // has ever been given an ADR 0022 key would otherwise be callable here, and that key was minted under
    // a write-only contract.
    [Fact]
    public async Task AskAgent_NotExposed_IsRefusedIdentically()
    {
        var (id, key) = await CreateAgentAsync("Toggling agent", exposed: false);

        using (var before = await PostRpcAsync(key, AskAgentRpc("hello")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        }

        await UpdateAgentAsync(id, "Toggling agent", exposed: true);

        var after = await AskAgentAsync(key, "hello");
        Assert.False(after.IsError, after.Text);
    }

    // ---- Conversation addressing (C4) -------------------------------------------------------------

    // AC5. The end-to-end half of the validator theory above: a malformed or foreign-shaped id is refused
    // at the runner, not merely rejected by a pure function nothing calls.
    [Theory]
    [InlineData("mcp-a/../b")]
    [InlineData("agent-someagent")]
    [InlineData("some-other-users-conversation")]
    public async Task AskAgent_RejectsForeignConversationId(string conversationId)
    {
        var (_, key) = await CreateExposedAgentAsync("Id agent");

        var reply = await AskAgentAsync(key, "hello", conversationId);

        Assert.True(reply.IsError);
        Assert.Contains("Unknown conversationId", reply.Text, StringComparison.Ordinal);
    }

    // AC6. The defence a shape check CANNOT provide, and the one the first draft of this feature lacked.
    // The owner is resolved from the API key, so a WELL-SHAPED id still addresses one of that owner's
    // other MCP conversations — and BindAgentAsync is immutable-once-bound, so agent B's conversation
    // keeps B's persona, prompt, tools and history. Without the binding comparison, a holder of agent A's
    // key supplying that id runs as B and reads B's content back.
    [Fact]
    public async Task AskAgent_RejectsAnotherAgentsConversation()
    {
        var (_, keyA) = await CreateExposedAgentAsync("Agent A");
        var (_, keyB) = await CreateExposedAgentAsync("Agent B");

        var bsConversation = await AskAgentAsync(keyB, "a secret only B should hold");
        Assert.False(bsConversation.IsError, bsConversation.Text);

        var hijack = await AskAgentAsync(keyA, "what did you just say?", bsConversation.ConversationId);

        Assert.True(hijack.IsError);
        Assert.Contains("Unknown conversationId", hijack.Text, StringComparison.Ordinal);
        // And the refusal is indistinguishable from the malformed-id one (AC5), so the conversation's
        // existence cannot be probed by the shape of the failure.
        Assert.DoesNotContain("a secret only B should hold", hijack.Text, StringComparison.Ordinal);
    }

    // AC7.
    [Fact]
    public async Task AskAgent_ContinuesAnExistingConversation()
    {
        var (_, key) = await CreateExposedAgentAsync("Continuing agent");

        var first = await AskAgentAsync(key, "the first question");
        Assert.False(first.IsError, first.Text);

        var second = await AskAgentAsync(key, "the second question", first.ConversationId);

        Assert.False(second.IsError, second.Text);
        Assert.Equal(first.ConversationId, second.ConversationId);

        var turns = await Client.GetFromJsonAsync<List<ChatTurn>>(
            $"/api/conversations/{first.ConversationId}/messages");
        Assert.Contains(turns!, t => t.Text == "the first question");
        Assert.Contains(turns!, t => t.Text == "the second question");
    }

    // AC8. The turn is the OWNER's, so it has to be findable in their sidebar rather than invisible — and
    // it carries the agent's identity, so it groups with that agent's other chats.
    [Fact]
    public async Task AskAgent_ListsTheConversation()
    {
        var (agentId, key) = await CreateExposedAgentAsync("Listed agent");

        var reply = await AskAgentAsync(key, "list me");
        Assert.False(reply.IsError, reply.Text);

        var conversations = await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations");
        var listed = Assert.Single(conversations!, c => c.Id == reply.ConversationId);
        Assert.Equal(agentId, listed.AgentId);
        Assert.Equal("Listed agent", listed.AgentName);
        Assert.Equal("list me", listed.Title);
    }

    // AC6 + AC13 (ADR 0082): the seeded research agent's kind reaches the conversation through the MCP bind, and the
    // caller is handed the FINAL iteration's answer — the one the owner's history shows — not every draft glued on.
    [Fact]
    public async Task AskAgent_ResearchAgent_RepliesWithTheFinalIterationOnly()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(Owner);
        await registry.EnsureResearchAgentAsync(TestSiloConfigurator.DefaultModel);
        var research = (await registry.GetAllAsync()).Single(a => a.Kind == AgentKinds.Research);
        var key = (await registry.SaveAsync(research with { McpExposed = true }, regenerateKey: true)).PlaintextApiKey!;

        var reply = await AskAgentAsync(key, FakeChatClient.ResearchTrigger);

        Assert.False(reply.IsError, reply.Text);
        Assert.StartsWith("[loopfeedback:1]", reply.Text, StringComparison.Ordinal);
        var binding = await Cluster.GrainFactory
            .GetGrain<IAgentGrain>($"{Owner}/{reply.ConversationId}").GetAgentBindingAsync();
        Assert.Equal(AgentKinds.Research, binding!.Kind);
    }

    // AC3 (MCP path): a copilot agent's kind reaches the conversation through the MCP bind, and the caller gets the
    // mirrored answer.
    [Fact]
    public async Task AskAgent_CopilotAgent_BindsItsKindAndReplies()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(Owner);
        var saved = await registry.SaveAsync(new AgentDefinition(
            "", "Pilot", "", TestSiloConfigurator.DefaultModel, "You fly.", default, default,
            McpExposed: true, Kind: AgentKinds.Copilot));

        var reply = await AskAgentAsync(saved.PlaintextApiKey!, "hello");

        Assert.False(reply.IsError, reply.Text);
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
        var binding = await Cluster.GrainFactory
            .GetGrain<IAgentGrain>($"{Owner}/{reply.ConversationId}").GetAgentBindingAsync();
        Assert.Equal(AgentKinds.Copilot, binding!.Kind);
    }

    // ---- Exclusions (C3, C13) ---------------------------------------------------------------------

    // AC9. Asserts the EFFECT — zero recalls and zero retains — never the selection list. The repo's own
    // measured lesson on this gate (ADR 0057) is that a list-shaped assertion is the weak one:
    // MemoryDeselected_NeitherRecallsNorRetains is the fixture shape copied here.
    //
    // The agent is created with a NULL selection, which is the `null == all` sentinel and therefore
    // INCLUDES memory — so this fails if WithoutMemory is dropped, rather than passing trivially.
    [Fact]
    public async Task AskAgent_MakesNoMemoryCalls()
    {
        MemoryReturns("a memory that must not reach an external caller");
        var (_, key) = await CreateExposedAgentAsync("Memoryless agent");

        var reply = await AskAgentAsync(key, "what do you remember about me?");

        Assert.False(reply.IsError, reply.Text);
        Assert.Empty(RecallsFor(Owner));
        Assert.Empty(RetainsFor(Owner));
        Assert.DoesNotContain("a memory that must not reach an external caller", reply.Text, StringComparison.Ordinal);
    }

    // ADR 0112 AC26: an ask_agent binding strips past_chats as it strips memory, asserted as an EFFECT. The owner has
    // another conversation the search would find, and the agent has a NULL selection (null == all, which includes
    // past_chats), so this fails if the filter is dropped rather than passing trivially. The positive control (a
    // browser turn DOES find it) is PastChatToolTests.Search_FindsAnotherConversation_WithTitle.
    [Fact]
    public async Task AskAgent_BindingExcludesPastChats()
    {
        const string secret = "the owner's private axolotl itinerary";
        await new InMemoryChatSearchIndex().IndexTurnsAsync(Owner, "mcp-past-chat-secret", [new ChatSearchTurn(0, "user", secret)]);
        await Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(Owner).TouchAsync("mcp-past-chat-secret", secret);
        var (_, key) = await CreateExposedAgentAsync("No-recall agent");

        var reply = await AskAgentAsync(
            key, FakeChatClient.CallToolPrompt(PastChatLimits.SearchToolName, new { query = "axolotl itinerary" }));

        Assert.False(reply.IsError, reply.Text);
        Assert.DoesNotContain("axolotl", reply.Text, StringComparison.Ordinal);
    }

    // ADR 0115 AC11: an ask_agent binding strips fleet, asserted as an EFFECT: the agent has a NULL selection (null ==
    // all, which includes fleet), and no task reaches the launcher. The positive control is
    // FleetToolTests.Start_ReturnsIdAndLink on the same kind of cluster.
    [Fact]
    public async Task AskAgent_BindingExcludesFleet()
    {
        var (_, key) = await CreateExposedAgentAsync("No-fleet agent");

        var reply = await AskAgentAsync(
            key, FakeChatClient.CallToolPrompt(FleetToolLimits.StartToolName, new { task = "run a shell in the container" }));

        Assert.False(reply.IsError, reply.Text);
        Assert.DoesNotContain("Started fleet task", reply.Text, StringComparison.Ordinal);
        Assert.Empty(Cluster.Silos.SelectMany(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeFleetLauncher>().Starts));
    }

    // ADR 0122/0123: an ask_agent binding has no analyzer tools (the chat key is gone), asserted as an EFFECT: a null
    // selection (== all) and a re-run never reaches the launcher. The positive control is
    // FleetAnalyzerToolTests.Rerun_StartsANewTask, on the panel's kind.
    [Fact]
    public async Task AskAgent_BindingExcludesFleetAnalyzer()
    {
        var (_, key) = await CreateExposedAgentAsync("No-analyzer agent");

        var reply = await AskAgentAsync(
            key, FakeChatClient.CallToolPrompt(FleetAnalyzerLimits.RerunToolName, new { taskId = "task-1" }));

        Assert.False(reply.IsError, reply.Text);
        Assert.DoesNotContain("Started fleet task", reply.Text, StringComparison.Ordinal);
        Assert.Empty(Cluster.Silos.SelectMany(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeFleetLauncher>().Reruns));
    }

    // The control for AC9: a normal browser turn on this same cluster DOES recall and retain. Without it,
    // AC9 passes just as well against a cluster whose memory recorder is never wired at all.
    [Fact]
    public async Task AskAgent_MemoryExclusionIsThisSurfaceOnly()
    {
        MemoryReturns("The user is allergic to shellfish");

        using (var chat = await Client.PostAsJsonAsync(
            "/api/chat/mcp-memory-control-conversation", new ChatRequest("what should I order?", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        Assert.NotEmpty(RecallsFor(Owner));
        Assert.NotEmpty(RetainsFor(Owner));
    }

    // The push exclusion (ADR 0075), asserted as an EFFECT — zero sends — never as a selection list, for
    // the reason the memory pair above states: a list-shaped assertion is the weak one.
    //
    // The agent is created with a NULL selection, the `null == all` sentinel, which therefore INCLUDES
    // push_notification — so this fails if the filter is dropped, rather than passing trivially. A device
    // is subscribed first, so an Assert.Empty here is attributable to the exclusion and not to the tool's
    // own "nobody is subscribed" guard, which would make it pass for the wrong reason.
    [Fact]
    public async Task AskAgent_SendsNoPushNotification()
    {
        await SubscribeOwnerDeviceAsync();
        var (_, key) = await CreateExposedAgentAsync("Quiet agent");

        var reply = await AskAgentAsync(key, "send me a notification about the build");

        Assert.False(reply.IsError, reply.Text);
        Assert.Empty(PushSendsFor(Owner));
    }

    // The control for the test above: a normal browser turn on this same cluster, with the same prompt and
    // a subscribed device, DOES push. Without it the exclusion test passes just as well against a cluster
    // where the tool never worked at all.
    [Fact]
    public async Task AskAgent_PushExclusionIsThisSurfaceOnly()
    {
        await SubscribeOwnerDeviceAsync();

        using (var chat = await Client.PostAsJsonAsync(
            "/api/chat/mcp-push-control-conversation",
            new ChatRequest("send me a notification about the build", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        Assert.NotEmpty(PushSendsFor(Owner));
    }

    // AC11. ChatAsync's run is interactive by default, and SuggestFollowUpsAsync gates on exactly that — so
    // draining it without the `interactive: false` parameter pays one extra, permanently discarded model
    // call on EVERY invocation of this surface.
    [Fact]
    public async Task AskAgent_DoesNotGenerateFollowUps()
    {
        var (_, key) = await CreateExposedAgentAsync("Quiet agent");

        var reply = await AskAgentAsync(key, "no suggestions please");

        Assert.False(reply.IsError, reply.Text);
        Assert.Equal(0, SuggestionCalls());
    }

    // The control for AC11, same reasoning as AC9's: a browser turn on this cluster DOES generate them.
    [Fact]
    public async Task AskAgent_FollowUpSuppressionIsThisSurfaceOnly()
    {
        using (var chat = await Client.PostAsJsonAsync(
            "/api/chat/mcp-followup-control-conversation", new ChatRequest("hello", null)))
        {
            chat.EnsureSuccessStatusCode();
            await chat.Content.ReadAsStringAsync();
        }

        Assert.True(SuggestionCalls() > 0);
    }

    // ---- Budgets and the timeout ------------------------------------------------------------------

    // AC10, and the reason this design exists at all. The session write sits AFTER the streaming loop
    // inside the iterator body, so a consumer that stops pulling leaves the iterator suspended at a
    // `yield return` and the turn is never persisted — tokens spent, the conversation listed with a title
    // and no messages, and no client-side recovery that could repair it. This fails against the obvious
    // implementation (cancel the enumeration when the timeout wins), which is the point of having it.
    [Fact]
    public async Task AskAgent_TimedOut_StillPersistsTheTurn()
    {
        var (agentId, key) = await CreateExposedAgentAsync("Slow agent");
        var caller = await ResolveCallerAsync(key);
        var prompt = $"{FakeChatClient.SlowStreamMarker} and then answer";

        var reply = await AgentMcpRunner.RunAsync(
            caller, prompt, conversationId: null, Cluster.GrainFactory, timeout: TimeSpan.FromMilliseconds(1));

        Assert.True(reply.IsError);
        Assert.Contains("did not finish", reply.Text, StringComparison.Ordinal);
        // The id rides back even on a timeout, precisely because the turn is still recorded under it.
        Assert.True(AgentMcpLimits.IsValidConversationId(reply.ConversationId));
        Assert.Equal(agentId, (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!
            .Single(c => c.Id == reply.ConversationId).AgentId);

        // The abandoned-enumerator failure is silent, so this polls for the persisted turn rather than
        // asserting once: what is under test is that it arrives at all, not when.
        var turns = await PollMessagesAsync(reply.ConversationId, t => t.Any(x => x.Role == "assistant"));
        Assert.Contains(turns, t => t.Role == "user" && t.Text == prompt);
        Assert.Contains(turns, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    // A faulting turn returns a CURATED failure, never the exception. ChatCoreAsync has no try/catch
    // around RunStreamingAsync, so a model or provider fault propagates straight out of the enumerator —
    // the same throw the browser renders as "Transmission failed" (ADR 0046). Here the caller is not
    // Entra-authenticated, so the provider's message, endpoint and stack must not reach them (the
    // ConfigHealth.ProbeAsync discipline). Without this the exception would also hand the MCP SDK an
    // arbitrary throw to render, which this repo has never measured.
    [Fact]
    public async Task AskAgent_AFaultingTurn_ReturnsACuratedFailure()
    {
        var (_, key) = await CreateExposedAgentAsync("Faulting agent");

        var reply = await AskAgentAsync(key, $"{FakeChatClient.FaultingStreamMarker} please");

        Assert.True(reply.IsError);
        Assert.Contains("failed to produce a reply", reply.Text, StringComparison.Ordinal);
        // None of the provider's own detail leaks.
        Assert.DoesNotContain("secret-deployment", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("500", reply.Text, StringComparison.Ordinal);
    }

    // The consequence a curated message does NOT fix, held by a test so it cannot be quietly forgotten:
    // the turn never persists (the write sits after the streaming loop), while TouchAsync already listed
    // the conversation. That orphan is the same one /api/agent-run leaves on a faulted background turn.
    // ADR 0070 records it rather than claiming the timeout design covers it.
    [Fact]
    public async Task AskAgent_AFaultingTurn_LeavesTheListedConversationEmpty()
    {
        var (_, key) = await CreateExposedAgentAsync("Orphaning agent");

        var reply = await AskAgentAsync(key, $"{FakeChatClient.FaultingStreamMarker} please");
        Assert.True(reply.IsError);

        var listed = (await Client.GetFromJsonAsync<List<ConversationInfo>>("/api/conversations"))!
            .SingleOrDefault(c => c.Id == reply.ConversationId);
        Assert.NotNull(listed);

        var turns = await Client.GetFromJsonAsync<List<ChatTurn>>(
            $"/api/conversations/{reply.ConversationId}/messages");
        Assert.Empty(turns!);
    }

    // AC12, inbound half: refused rather than truncated, because the text lands verbatim in the OWNER's
    // 2 MB session (ADR 0004) and is re-sent on every later turn of a reused conversation.
    [Fact]
    public async Task AskAgent_BudgetsPromptAndReply()
    {
        var (_, key) = await CreateExposedAgentAsync("Budget agent");

        var refused = await AskAgentAsync(key, new string('x', AgentMcpLimits.MaxPromptChars + 1));
        Assert.True(refused.IsError);
        Assert.Contains("character limit", refused.Text, StringComparison.Ordinal);

        // At the cap exactly, it goes through — so the refusal is a bound and not an off-by-one wall.
        var accepted = await AskAgentAsync(key, new string('x', AgentMcpLimits.MaxPromptChars));
        Assert.False(accepted.IsError, accepted.Text);

        // Outbound half. The fake model cannot be made to produce 64k characters, so the cap is exercised
        // directly — stated rather than dressed up as an end-to-end assertion it is not.
        var over = new string('y', AgentMcpLimits.MaxReplyChars + 500);
        var capped = AgentMcpLimits.CapReply(over);
        Assert.StartsWith(new string('y', 100), capped, StringComparison.Ordinal);
        Assert.EndsWith("[truncated]", capped, StringComparison.Ordinal);
        // Inside the cap, not past it — the marker is counted, so a method documented as capping at N
        // cannot return N + 13. `capped.Length < over.Length` alone would not catch that.
        Assert.True(capped.Length <= AgentMcpLimits.MaxReplyChars, $"capped to {capped.Length}");
        // And a reply inside the cap is returned byte-identical, never marked.
        Assert.Equal("short reply", AgentMcpLimits.CapReply("short reply"));
    }

    // ---- The opt-in flag --------------------------------------------------------------------------

    // AC13. McpExposed is the ELEVENTH positional parameter of AgentDefinition while both endpoint call
    // sites pass nine, so a positional append would land silently in ApiKeyHash (ADR 0065's scar). This
    // is what catches that: the flag would round-trip as false and the feature would be unreachable.
    [Fact]
    public async Task McpExposed_RoundTrips()
    {
        var (id, _) = await CreateExposedAgentAsync("Round-trip agent");

        var listed = (await Client.GetFromJsonAsync<List<AgentDefinition>>("/api/agents"))!
            .Single(a => a.Id == id);
        Assert.True(listed.McpExposed);
        // The hash is intact, i.e. the flag did not land in the field next to it.
        Assert.NotEmpty(listed.ApiKeyHash);

        // And it survives an edit that does not mention it being turned off.
        var updated = await UpdateAgentAsync(id, "Round-trip agent", exposed: false);
        Assert.False(updated.McpExposed);
        Assert.NotEmpty(updated.ApiKeyHash);
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private async Task<(string Id, string ApiKey)> CreateExposedAgentAsync(string name)
        => await CreateAgentAsync(name, exposed: true);

    private async Task<(string Id, string ApiKey)> CreateAgentAsync(string name, bool exposed)
    {
        var body = (await (await Client.PostAsJsonAsync("/api/agents",
            new AgentUpsertRequest(name, "", "gpt-4o", $"You are {name}.", McpExposed: exposed)))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!;
        return (body.Agent.Id, body.ApiKey!);
    }

    private async Task<AgentDefinition> UpdateAgentAsync(string id, string name, bool exposed)
        => (await (await Client.PutAsJsonAsync($"/api/agents/{id}",
            new AgentUpsertRequest(name, "", "gpt-4o", $"You are {name}.", McpExposed: exposed)))
            .Content.ReadFromJsonAsync<AgentSaveResponse>())!.Agent;

    // Resolves a raw key exactly as the gate does, for the two tests that drive the runner directly.
    private async Task<AgentMcpCaller> ResolveCallerAsync(string apiKey)
        => (await AgentMcpKeyGate.ResolveAsync(apiKey, Cluster.GrainFactory))!;

    private static string AskAgentRpc(string prompt, string? conversationId = null)
    {
        var arguments = "{\"prompt\":" + JsonSerializer.Serialize(prompt)
            + (conversationId is null
                ? ""
                : ",\"conversationId\":" + JsonSerializer.Serialize(conversationId))
            + "}";

        return "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\","
            + "\"params\":{\"name\":\"ask_agent\",\"arguments\":" + arguments + "}}";
    }

    // Calls ask_agent over the wire and splits the reply back into the two things the tool packs into one
    // text block: the conversation id line and the body.
    private async Task<(string ConversationId, string Text, bool IsError)> AskAgentAsync(
        string apiKey, string prompt, string? conversationId = null)
    {
        // IsError comes from the PROTOCOL field, never inferred from the text. That is the whole point
        // of returning a CallToolResult rather than a bare string: a refusal, a timeout and a fault are
        // otherwise indistinguishable from a reply to the calling model.
        var (text, isError) = await RpcToolTextAsync(apiKey, AskAgentRpc(prompt, conversationId));

        const string Marker = "conversationId: ";
        if (!text.StartsWith(Marker, StringComparison.Ordinal))
        {
            return ("", text, isError);
        }

        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return (text[Marker.Length..newline].Trim(), text[newline..].TrimStart('\n'), isError);
    }

    private async Task<HttpResponseMessage> PostRpcAsync(string? apiKey, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentMcpLimits.RoutePrefix)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (apiKey is not null)
        {
            request.Headers.Add("X-Api-Key", apiKey);
        }

        return await Client.SendAsync(request);
    }

    // The transport answers 200 text/event-stream with a single `data:` line carrying the JSON-RPC
    // envelope, so this unwraps that framing and returns the raw result JSON.
    private async Task<string> RpcResultAsync(string? apiKey, string json)
    {
        using var response = await PostRpcAsync(apiKey, json);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        var marker = body.IndexOf("data:", StringComparison.Ordinal);
        Assert.True(marker >= 0, $"No SSE data frame in: {body}");
        return body[(marker + "data:".Length)..].Trim();
    }

    // Unwraps one further level: a tools/call result is
    // {"content":[{"type":"text","text":"…"}],"isError":bool}.
    private async Task<(string Text, bool IsError)> RpcToolTextAsync(string apiKey, string json)
    {
        using var document = JsonDocument.Parse(await RpcResultAsync(apiKey, json));
        var root = document.RootElement;

        // A protocol-level error (the defence-in-depth throw) surfaces here rather than in `result`.
        if (root.TryGetProperty("error", out var error))
        {
            return (error.GetProperty("message").GetString() ?? "", true);
        }

        var result = root.GetProperty("result");
        var text = string.Concat(result.GetProperty("content").EnumerateArray()
            .Select(c => c.GetProperty("text").GetString()));

        return (text, result.TryGetProperty("isError", out var flag) && flag.GetBoolean());
    }

    private async Task<List<ChatTurn>> PollMessagesAsync(string convId, Func<List<ChatTurn>, bool> until)
    {
        List<ChatTurn> turns = [];
        for (var attempt = 0; attempt < 50; attempt++)
        {
            turns = (await Client.GetFromJsonAsync<List<ChatTurn>>($"/api/conversations/{convId}/messages"))!;
            if (until(turns))
            {
                return turns;
            }

            await Task.Delay(100);
        }

        return turns;
    }

    // Aggregated across every silo, for the reason AgentGrainTests gives: the recorder is a per-silo
    // singleton and a grain may activate on any silo of the cluster.
    // Idempotent: IUserPushGrain upserts by endpoint, and SharedClusterFixture.ResetSiloState does not
    // reset that grain's state anyway, so calling this from several tests is safe.
    private Task SubscribeOwnerDeviceAsync() =>
        Cluster.GrainFactory.GetGrain<IUserPushGrain>(Owner)
            .AddAsync(new PushSubscription("https://push.example/mcp-owner", "p256dh-key", "auth-secret"));

    private IReadOnlyList<(string UserId, PushMessage Message)> PushSendsFor(string userId) =>
        [.. Cluster.Silos
            .SelectMany(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<RecordingPushNotificationSender>().Sends)
            .Where(send => send.UserId == userId)];

    private IReadOnlyList<RecordingHindsightClient> MemoryRecorders() =>
        [.. Cluster.Silos.Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<RecordingHindsightClient>())];

    private IReadOnlyList<string> RecallsFor(string bankId) =>
        [.. MemoryRecorders().SelectMany(r => r.RecallsFor(bankId))];

    private IReadOnlyList<string> RetainsFor(string bankId) =>
        [.. MemoryRecorders().SelectMany(r => r.RetainsFor(bankId))];

    private void MemoryReturns(params string[] texts)
    {
        foreach (var recorder in MemoryRecorders())
        {
            recorder.ReturnsMemories(texts);
        }
    }

    private int SuggestionCalls() =>
        Cluster.Silos.Sum(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeChatClient>().SuggestionCalls);
}
