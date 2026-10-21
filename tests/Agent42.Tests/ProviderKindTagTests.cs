using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// The provider-kind tag (ADR 0110) that lets /usage price a call. Driven through the REAL M.E.AI
/// <c>UseOpenTelemetry</c> client in the same order <c>MultiProviderChatClientFactory.Build</c> wraps it, because the
/// claim under test is "Activity.Current is the chat span inside OTel", which only the real emitter can prove.
/// </summary>
public sealed class ProviderKindTagTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderKindTag_IsOnChatSpan(bool streaming)
    {
        var source = "provider-kind-tag-" + Guid.NewGuid().ToString("N");
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        using var client = new StubChatClient().AsBuilder()
            .UseOpenTelemetry(sourceName: source)
            .Use(inner => new ProviderKindTagChatClient(inner, ProviderKind.AzureOpenAI))
            .Build();

        if (streaming)
        {
            await foreach (var _ in client.GetStreamingResponseAsync("hi"))
            {
            }
        }
        else
        {
            await client.GetResponseAsync("hi");
        }

        var chat = Assert.Single(stopped, a => a.GetTagItem(UsageSpanProjector.OperationNameTag) as string == "chat");
        Assert.Equal("AzureOpenAI", chat.GetTagItem(UsageSpanProjector.ProviderKindTag));
    }

    // With no chat span (no listener on the OTel source), Activity.Current is the caller's span, which must not be stamped.
    [Fact]
    public async Task ProviderKindTag_NeverLandsOnAParentSpan()
    {
        using var parentSource = new ActivitySource("provider-kind-parent-" + Guid.NewGuid().ToString("N"));
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == parentSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var client = new StubChatClient().AsBuilder()
            .UseOpenTelemetry(sourceName: "provider-kind-unlistened-" + Guid.NewGuid().ToString("N"))
            .Use(inner => new ProviderKindTagChatClient(inner, ProviderKind.Anthropic))
            .Build();

        using var parent = parentSource.StartActivity("invoke_agent")!;
        await client.GetResponseAsync("hi");

        Assert.Null(parent.GetTagItem(UsageSpanProjector.ProviderKindTag));
    }

    // The price table is keyed on these strings, and the tag writes ProviderKind.ToString(): a rename of an enum
    // member would silently make every call of that kind unpriced.
    [Fact]
    public void PriceTableKinds_MatchTheProviderKindEnum()
    {
        Assert.Equal(ModelPricing.AzureOpenAIKind, ProviderKind.AzureOpenAI.ToString());
        Assert.Equal(ModelPricing.AnthropicKind, ProviderKind.Anthropic.ToString());
        Assert.Equal(ModelPricing.GitHubCopilotKind, ProviderKind.GitHubCopilot.ToString());
        Assert.All(ModelPricing.Table, p => Assert.True(Enum.IsDefined(Enum.Parse<ProviderKind>(p.ProviderKind))));
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
