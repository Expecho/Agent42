using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agent42.Tests;

/// <summary>
/// <see cref="PromptCacheKeyChatClient"/> (ADR 0104): the conversation's hashed key reaches an Azure OpenAI request as
/// <c>prompt_cache_key</c>, on both wire paths, and nothing else. Real SDK adapters over a stub transport, because
/// what matters is the request body; nothing here talks to a network.
/// </summary>
public class PromptCacheKeyChatClientTests
{
    private const string GrainKey = "user-123/conv-456";

    private static readonly ProviderOptions AzureResponses =
        new() { Kind = ProviderKind.AzureOpenAI, UseResponsesApi = true };
    private static readonly ProviderOptions AzureChat =
        new() { Kind = ProviderKind.AzureOpenAI, UseResponsesApi = false };
    private static readonly ProviderOptions Compatible = new() { Kind = ProviderKind.OpenAICompatible };
    private static readonly ProviderOptions Claude = new() { Kind = ProviderKind.Anthropic };

    [Fact]
    public void Key_IsHashed_AndStable()
    {
        var key = PromptCacheKey.For(GrainKey);

        Assert.Equal(key, PromptCacheKey.For(GrainKey));
        Assert.NotEqual(key, PromptCacheKey.For("user-123/conv-other"));
        Assert.Matches("^a42-[0-9a-f]{32}$", key);
        Assert.DoesNotContain("user-123", key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Azure_SendsHashedPromptCacheKey(bool responses)
    {
        var handler = OpenAIHandler(responses);
        var client = new PromptCacheKeyChatClient(OpenAIClient(handler, responses), responses ? AzureResponses : AzureChat);

        await SendAsync(client, WithKey());

        var body = Assert.Single(handler.Recorded).Body;
        Assert.Equal(PromptCacheKey.For(GrainKey), Json(body).GetProperty("prompt_cache_key").GetString());
        Assert.DoesNotContain("user-123", body, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptCacheKey.PropertyName, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherKinds_SendNoKeyOrProperty()
    {
        var openAi = OpenAIHandler(responses: false);
        await SendAsync(new PromptCacheKeyChatClient(OpenAIClient(openAi, responses: false), Compatible), WithKey());

        var anthropic = AnthropicStub.Sequence(() => RecordingHttpHandler.Json(AnthropicStub.JsonText));
        await SendAsync(new PromptCacheKeyChatClient(AnthropicClient(anthropic), Claude), WithKey());

        foreach (var body in new[] { openAi.Recorded[0].Body, anthropic.Recorded[0].Body })
        {
            Assert.DoesNotContain("prompt_cache_key", body, StringComparison.Ordinal);
            Assert.DoesNotContain(PromptCacheKey.PropertyName, body, StringComparison.Ordinal);
            Assert.DoesNotContain("a42-", body, StringComparison.Ordinal);
        }

        // AC14: the shim's top-level cache_control is still the only one.
        Assert.Equal("ephemeral", Json(anthropic.Recorded[0].Body).GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task NoProperty_IsPassThrough()
    {
        var inner = new RecordingChatClient();
        var options = new ChatOptions { Instructions = "x" };

        await new PromptCacheKeyChatClient(inner, AzureResponses).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        Assert.Same(options, inner.Options);
        Assert.Null(inner.Options!.RawRepresentationFactory);
    }

    [Fact]
    public async Task CallerFactory_Wins()
    {
        var handler = OpenAIHandler(responses: true);
        var client = new PromptCacheKeyChatClient(OpenAIClient(handler, responses: true), AzureResponses);
        var options = WithKey();
#pragma warning disable OPENAI001
        options.RawRepresentationFactory = _ => new CreateResponseOptions { MaxOutputTokenCount = 77 };
#pragma warning restore OPENAI001

        await SendAsync(client, options);

        var body = Json(Assert.Single(handler.Recorded).Body);
        Assert.Equal(77, body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(PromptCacheKey.For(GrainKey), body.GetProperty("prompt_cache_key").GetString());

        // A caller that already chose a key keeps it.
#pragma warning disable OPENAI001
        options.RawRepresentationFactory = _ => new CreateResponseOptions { PromptCacheKey = "caller-key" };
#pragma warning restore OPENAI001
        await SendAsync(client, options);
        Assert.Equal("caller-key", Json(handler.Recorded[1].Body).GetProperty("prompt_cache_key").GetString());
    }

    [Fact]
    public async Task Options_AreCloned()
    {
        var inner = new RecordingChatClient();
        var options = WithKey();
        options.AdditionalProperties!["other"] = "kept";

        await new PromptCacheKeyChatClient(inner, AzureResponses).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        Assert.NotSame(options, inner.Options);
        Assert.True(options.AdditionalProperties.ContainsKey(PromptCacheKey.PropertyName));
        Assert.Null(options.RawRepresentationFactory);
        Assert.False(inner.Options!.AdditionalProperties!.ContainsKey(PromptCacheKey.PropertyName));
        Assert.Equal("kept", inner.Options.AdditionalProperties["other"]);
    }

    // The grain's half: ChatClientAgent hands the agent-level AdditionalProperties to the chat client on each run,
    // which is what lets BuildAgent set the key once per conversation.
    [Fact]
    public async Task Agent_ForwardsTheProperty()
    {
        var inner = new RecordingChatClient();
        var agent = new ChatClientAgent(inner, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { AdditionalProperties = new() { [PromptCacheKey.PropertyName] = "k" } },
        });

        await agent.RunAsync("hi", await agent.CreateSessionAsync());

        Assert.Equal("k", inner.Options!.AdditionalProperties![PromptCacheKey.PropertyName]);
    }

    private static ChatOptions WithKey()
        => new() { AdditionalProperties = new() { [PromptCacheKey.PropertyName] = PromptCacheKey.For(GrainKey) } };

    private static async Task SendAsync(IChatClient client, ChatOptions options)
    {
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
        }
    }

    private static JsonElement Json(string body) => JsonSerializer.Deserialize<JsonElement>(body);

    private static RecordingHttpHandler OpenAIHandler(bool responses) => new()
    {
        Responder = _ => AnthropicStub.Sse(responses ? ResponsesStream : ChatStream),
    };

    // Built as Provider.Foundry builds each path, over the stub transport.
    private static IChatClient OpenAIClient(RecordingHttpHandler handler, bool responses)
    {
        var client = new OpenAIClient(new ApiKeyCredential("k"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/openai/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        });
#pragma warning disable OPENAI001, MAAI001
        return responses
            ? client.GetResponsesClient().AsIChatClientWithStoredOutputDisabled("m", includeReasoningEncryptedContent: false)
            : client.GetChatClient("m").AsIChatClient();
#pragma warning restore OPENAI001, MAAI001
    }

    private static IChatClient AnthropicClient(RecordingHttpHandler handler) => new AnthropicCompatibilityChatClient(
        new AnthropicClient
        {
            ApiKey = "test-key",
            AuthToken = null,
            BaseUrl = "https://anthropic.stub",
            HttpClient = new HttpClient(handler),
        }.AsIChatClient("claude-test", AnthropicLimits.DefaultMaxOutputTokens),
        "claude-test");

    private const string ResponsesStream = """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp_1","object":"response","created_at":0,"status":"in_progress","model":"m","output":[]}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp_1","object":"response","created_at":0,"status":"completed","model":"m","output":[]}}


        """;

    private const string ChatStream = """
        data: {"id":"c1","object":"chat.completion.chunk","created":0,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}

        data: [DONE]


        """;
}
