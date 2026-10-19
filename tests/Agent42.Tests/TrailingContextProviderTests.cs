using Agent42.Agents;
using Hindsight.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// <see cref="TrailingContextProvider"/> (ADR 0104): a per-turn provider's output reaches the model as a trailing
/// user-role message, so the instructions (the front of the cached prefix) stay byte-identical across turns.
/// A real <see cref="ChatClientAgent"/> over a <see cref="RecordingChatClient"/>, and a real Hindsight provider
/// over <see cref="RecordingHindsightClient"/>, because the properties under test live in the framework's merge.
/// </summary>
public class TrailingContextProviderTests
{
    private const string Persona = "You are a test persona.";

    [Fact]
    public async Task Wrapper_PassesRunMessagesToInner()
    {
        var (agent, _, memory) = Build();

        await agent.RunAsync("what is my dog called?", await agent.CreateSessionAsync());

        // An empty isolated context would make this query empty, and recall would silently return nothing.
        Assert.Equal(["what is my dog called?"], memory.RecallsFor("bank"));
    }

    [Fact]
    public async Task Wrapper_DoesNotEchoInput()
    {
        var (agent, model, memory) = Build();
        memory.ReturnsMemories("The user's dog is called Rex.");
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("first", session);
        await agent.RunAsync("second", session);

        // History (first, ok), the new turn, and exactly one context message: nothing of the input came back twice.
        var texts = model.Messages!.Select(m => m.Text).ToList();
        Assert.Equal(4, texts.Count);
        Assert.Equal(["first", "ok", "second"], texts[..3]);
        Assert.Single(texts, t => t == "second");
    }

    [Fact]
    public async Task TrailingContext_FollowsUserTurn()
    {
        var (agent, model, memory) = Build();
        memory.ReturnsMemories("The user's dog is called Rex.");

        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        var last = model.Messages![^1];
        Assert.Equal(ChatRole.User, last.Role);
        Assert.Contains("## Memories", last.Text, StringComparison.Ordinal);
        Assert.Contains("Rex", last.Text, StringComparison.Ordinal);
        Assert.Equal("hello", model.Messages[^2].Text);
        Assert.DoesNotContain("Rex", model.Options!.Instructions ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instructions_AreIdentical_WhenRecallDiffers()
    {
        var (agent, model, memory) = Build();
        var session = await agent.CreateSessionAsync();

        memory.ReturnsMemories("Memory A.");
        await agent.RunAsync("one", session);
        var first = model.Options!.Instructions;

        memory.ReturnsMemories("Memory B, which is different.");
        await agent.RunAsync("two", session);

        Assert.Equal(first, model.Options!.Instructions);
        Assert.Contains("Memory B", model.Messages![^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingRecalled_AddsNoMessage()
    {
        var (agent, model, _) = Build();

        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        Assert.Equal("hello", Assert.Single(model.Messages!).Text);
    }

    [Fact]
    public async Task Tools_And_Messages_PassThrough()
    {
        var model = new RecordingChatClient();
        var tool = AIFunctionFactory.Create(() => "x", "inner_tool");
        var agent = Agent(model, new TrailingContextProvider(new StubProvider(new AIContext
        {
            Instructions = "per-turn text",
            Messages = [new ChatMessage(ChatRole.User, "inner message")],
            Tools = [tool],
        })));

        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        Assert.Contains(model.Options!.Tools!, t => t.Name == "inner_tool");
        Assert.Equal(["hello", "inner message", "per-turn text"], model.Messages!.Select(m => m.Text));
    }

    [Fact]
    public async Task TrailingMemory_IsNotRetained()
    {
        var (agent, _, memory) = Build();
        memory.ReturnsMemories("REMEMBERED-FACT");

        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        // InvokedAsync reached Hindsight (retain ran), and the injected recall is not in what it retained: no
        // feedback loop growing the bank with its own memories (Step 0c).
        var retained = Assert.Single(memory.RetainsFor("bank"));
        Assert.Contains("hello", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("REMEMBERED-FACT", retained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrailingContext_IsNotStored_InTheSession()
    {
        var (agent, _, memory) = Build();
        memory.ReturnsMemories("REMEMBERED-FACT");
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("hello", session);

        var serialized = (await agent.SerializeSessionAsync(session)).GetRawText();
        Assert.Contains("hello", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("REMEMBERED-FACT", serialized, StringComparison.Ordinal);
    }

    private static (ChatClientAgent Agent, RecordingChatClient Model, RecordingHindsightClient Memory) Build()
    {
        var model = new RecordingChatClient();
        var memory = new RecordingHindsightClient();
        var agent = Agent(model, new TrailingContextProvider(
            new HindsightMemoryProvider(memory, new HindsightOptions { BankId = "bank" })));
        return (agent, model, memory);
    }

    // The storage filter is the one BuildAgent uses in spirit: provider-sourced messages are not persisted.
    private static ChatClientAgent Agent(IChatClient model, AIContextProvider provider) => new(model, new ChatClientAgentOptions
    {
        ChatOptions = new ChatOptions { Instructions = Persona },
        AIContextProviders = [provider],
        ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            StorageInputRequestMessageFilter = messages => messages.Where(m =>
                m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External),
        }),
    });

    private sealed class StubProvider(AIContext output) : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(output);
    }
}
