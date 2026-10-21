using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The past-chat tools (ADR 0112): <c>search_past_chats</c> and <c>read_past_chat</c> over the user's own chat-search
/// projection. Each test drives a tool through <see cref="FakeChatClient.CallToolPrompt"/>, so it runs exactly as a
/// model-made call would. Other conversations are seeded straight into the process-global
/// <see cref="InMemoryChatSearchIndex"/> and listed on the user's <see cref="IConversationIndexGrain"/> (the endpoint
/// does that in production), under a userId unique to each test.
/// </summary>
public sealed class PastChatToolTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
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

    // The decoded results of this turn's scripted call(s) (the ReadDocumentToolTests helper).
    private static string Result(IReadOnlyList<AgentStreamEvent> events)
    {
        var answer = Answer(events);
        var start = answer.IndexOf(FakeChatClient.ScriptedResultOpen, StringComparison.Ordinal);
        var end = answer.LastIndexOf(FakeChatClient.ScriptedResultClose, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"no scripted result in: {answer}");
        return System.Text.RegularExpressions.Regex.Unescape(answer[(start + FakeChatClient.ScriptedResultOpen.Length)..end]);
    }

    private IAgentGrain Agent(string user, string conversation)
        => Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/{conversation}");

    private static Task<List<AgentStreamEvent>> Search(IAgentGrain agent, string query)
        => CollectAsync(agent.ChatAsync(
            FakeChatClient.CallToolPrompt(PastChatLimits.SearchToolName, new { query }), TestSiloConfigurator.DefaultModel));

    private static Task<List<AgentStreamEvent>> Read(IAgentGrain agent, string conversationId, int ordinal)
        => CollectAsync(agent.ChatAsync(
            FakeChatClient.CallToolPrompt(PastChatLimits.ReadToolName, new { conversationId, ordinal }),
            TestSiloConfigurator.DefaultModel));

    // Indexes another conversation's turns and lists it for the user, titled by its first turn.
    private async Task SeedAsync(string user, string conversation, params string[] texts)
    {
        await new InMemoryChatSearchIndex().IndexTurnsAsync(user, conversation,
            [.. texts.Select((text, i) => new ChatSearchTurn(i, i % 2 == 0 ? "user" : "assistant", text))]);
        await ListAsync(user, conversation, texts[0]);
    }

    private Task ListAsync(string user, string conversation, string firstPrompt)
        => Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(user).TouchAsync(conversation, firstPrompt);

    private static async Task<string> LastToolResult(IAgentGrain agent)
    {
        var turn = (await agent.GetHistoryAsync()).Last(t => t.ToolCalls is { Count: > 0 });
        var raw = turn.ToolCalls![^1].ToolResult!;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString()! : raw;
    }

    // AC12.
    [Fact]
    public async Task Search_FindsAnotherConversation_WithTitle()
    {
        const string user = "pastchat-find-user";
        await SeedAsync(user, "older", "how do we rotate the deploy keys", "rotate them monthly in the vault");

        var result = Result(await Search(Agent(user, "now"), "deploy vault"));

        Assert.StartsWith(PastChatLimits.SearchHeaderPrefix, result, StringComparison.Ordinal);
        Assert.Contains("'how do we rotate the deploy keys'", result, StringComparison.Ordinal);
        Assert.Contains("conversationId=older ordinal=1 assistant: rotate them monthly in the vault", result, StringComparison.Ordinal);
    }

    // AC13: the current conversation is already in context, so it is never a hit, even when it matches.
    [Fact]
    public async Task Search_ExcludesTheCurrentConversation()
    {
        const string user = "pastchat-current-user";
        await SeedAsync(user, "now", "the walrus plan");
        await SeedAsync(user, "other", "the walrus budget");

        var result = Result(await Search(Agent(user, "now"), "walrus"));

        Assert.Contains("conversationId=other", result, StringComparison.Ordinal);
        Assert.DoesNotContain("conversationId=now", result, StringComparison.Ordinal);
    }

    // AC14: an index document whose conversation the user no longer has (a purge's failed delete, ADR 0068) is
    // never returned.
    [Fact]
    public async Task Search_DropsHitsForConversationsNotInTheIndexGrain()
    {
        const string user = "pastchat-orphan-user";
        await new InMemoryChatSearchIndex().IndexTurnsAsync(user, "orphan", [new ChatSearchTurn(0, "user", "the okapi secret")]);

        var result = Result(await Search(Agent(user, "now"), "okapi"));

        Assert.StartsWith("No other conversation matches 'okapi'", result, StringComparison.Ordinal);
    }

    // AC15: incognito text never reaches the index (ADR 0034/0061), so a search can never find it.
    [Fact]
    public async Task Search_NeverFindsAnIncognitoTurn()
    {
        const string user = "pastchat-incognito-user";
        await CollectAsync(Agent(user, "secret").ChatAsync("the incognito capybara", null, ephemeral: true));
        await ListAsync(user, "secret", "the incognito capybara");

        var result = Result(await Search(Agent(user, "now"), "capybara"));

        Assert.StartsWith("No other conversation matches", result, StringComparison.Ordinal);
    }

    // AC27: one chat with many matching turns cannot fill every slot.
    [Fact]
    public async Task Search_CapsHitsPerConversation()
    {
        const string user = "pastchat-percap-user";
        await SeedAsync(user, "busy", "heron one", "heron two", "heron three", "heron four");
        await SeedAsync(user, "quiet", "a heron once");

        var result = Result(await Search(Agent(user, "now"), "heron"));

        Assert.Equal(PastChatLimits.MaxHitsPerConversation,
            result.Split("conversationId=busy ", StringSplitOptions.None).Length - 1);
        Assert.Contains("conversationId=quiet", result, StringComparison.Ordinal);
    }

    // AC16 (first half).
    [Fact]
    public async Task Read_ReturnsTheWindowAroundTheOrdinal()
    {
        const string user = "pastchat-read-user";
        await SeedAsync(user, "older", "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8", "t9");

        var result = Result(await Read(Agent(user, "now"), "older", 5));

        Assert.StartsWith("Past chat 't0'", result, StringComparison.Ordinal);
        Assert.Contains("turns 3-8:", result, StringComparison.Ordinal);
        Assert.Contains("[3] assistant: t3", result, StringComparison.Ordinal);
        Assert.Contains("[8] user: t8", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[2]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[9]", result, StringComparison.Ordinal);
    }

    // AC16 (second half): it is already in context, and reading it through the index would be stale anyway.
    [Fact]
    public async Task Read_OfTheCurrentConversation_IsRefused()
    {
        const string user = "pastchat-readself-user";
        await SeedAsync(user, "now", "anything");

        var result = Result(await Read(Agent(user, "now"), "now", 0));

        Assert.StartsWith("That is this conversation", result, StringComparison.Ordinal);
    }

    // AC17: another user's conversation id finds nothing, and none of its text.
    [Fact]
    public async Task Read_OfAnotherUsersConversation_FindsNothing()
    {
        await SeedAsync("pastchat-victim-user", "theirs", "the victim's private gecko");

        var result = Result(await Read(Agent("pastchat-attacker-user", "now"), "theirs", 0));

        Assert.StartsWith("No conversation with id 'theirs' was found", result, StringComparison.Ordinal);
        Assert.DoesNotContain("gecko", result, StringComparison.Ordinal);
    }

    // AC18 (first half): the shared per-turn budget refuses the call past the cap.
    [Fact]
    public async Task PastChatCalls_PastTheCap_AreRefused()
    {
        const string user = "pastchat-cap-user";
        await SeedAsync(user, "older", "the ibis note");

        var events = await CollectAsync(Agent(user, "now").ChatAsync(
            FakeChatClient.CallToolSequencePrompt(
                PastChatLimits.SearchToolName,
                Enumerable.Range(0, PastChatLimits.MaxCallsPerTurn + 1).Select(_ => (object)new { query = "ibis" })),
            TestSiloConfigurator.DefaultModel));

        Assert.Contains($"You have made {PastChatLimits.MaxCallsPerTurn} past-chat calls this turn, the limit",
            Result(events), StringComparison.Ordinal);
    }

    // AC18 (second half): outside a run the budget reads exhausted, so the tools fail closed (ADR 0106).
    [Fact]
    public void PastChatTools_OutsideARun_Refuse()
    {
        Assert.True(TurnContext.Closed.PastChatCalls >= PastChatLimits.MaxCallsPerTurn);
        TurnContext.Closed.RecordPastChatCall();
        Assert.Equal(int.MaxValue, TurnContext.Closed.PastChatCalls);
    }

    // AC19: a successful search and read become stubs after the turn; a refusal stays verbatim.
    [Fact]
    public async Task Results_AreCompactedAfterTheTurn_RefusalsKept()
    {
        const string user = "pastchat-compact-user";
        await SeedAsync(user, "older", "the tapir itinerary");
        var agent = Agent(user, "now");

        await Search(agent, "tapir");
        Assert.Equal(PastChatLimits.SearchStub("tapir"), await LastToolResult(agent));

        await Read(agent, "older", 0);
        Assert.Equal(PastChatLimits.ReadStub("older", 0), await LastToolResult(agent));

        await Read(agent, "now", 0);
        Assert.StartsWith("That is this conversation", await LastToolResult(agent), StringComparison.Ordinal);
    }

    // AC20: an index outage is reported to the model, and the turn completes.
    [Fact]
    public async Task ThrowingIndex_ReturnsAnError_AndTheReplyCompletes()
    {
        const string user = "pastchat-outage-user";
        TestChatSearchIndex.FailFor(user, new InvalidOperationException("Injected search outage."));

        var events = await Search(Agent(user, "now"), "anything");

        Assert.Contains("I could not search past chats: Injected search outage.", Result(events), StringComparison.Ordinal);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    // AC23: deselecting the key registers neither tool, so the model cannot call them.
    [Fact]
    public async Task PastChats_Deselected_RegistersNeitherTool()
    {
        const string user = "pastchat-off-user";
        await SeedAsync(user, "older", "the narwhal recipe");
        var agent = Agent(user, "now");
        await agent.SetEnabledToolsAsync([ToolCatalog.WebSearch]);

        // The fake makes a scripted call only when the tool is offered; otherwise the turn's result echo is empty.
        Assert.Empty(Result(await Search(agent, "narwhal")));
        Assert.Empty(Result(await Read(agent, "older", 0)));
    }

    // AC28 (rule 11): the description is the policy the model reads.
    [Fact]
    public void PastChatTools_DescriptionsStateThePolicy()
    {
        Assert.Contains("other conversations", PastChatLimits.SearchDescription, StringComparison.Ordinal);
        Assert.Contains("keywords", PastChatLimits.SearchDescription, StringComparison.Ordinal);
        Assert.Contains(PastChatLimits.ReadToolName, PastChatLimits.SearchDescription, StringComparison.Ordinal);
        Assert.Contains($"{PastChatLimits.MaxCallsPerTurn} past-chat calls", PastChatLimits.SearchDescription, StringComparison.Ordinal);
        Assert.Contains("conversationId and ordinal", PastChatLimits.ReadDescription, StringComparison.Ordinal);
    }
}
