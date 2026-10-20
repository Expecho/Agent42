using System.Runtime.CompilerServices;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// Copilot agents on the user's own GitHub account (ADR 0108), without a runtime process: the session config in GitHub
/// mode (token per session, no BYOK provider, telemetry off), the environments that must never carry a token, the
/// "not connected" refusal instead of a fallback, the revoked-token answer, and the in-process twin's refusal.
/// </summary>

public sealed class CopilotGitHubAccountTests : IDisposable
{
    private const string Token = "gho_test-token-123";

    private readonly string _installed = Path.GetTempFileName();

    public void Dispose() => File.Delete(_installed);

    private static CopilotAgentRequest Request(string modelId, IChatClientFactory? factory = null)
        => new("user-1", "conv-1", factory ?? new SingleChatClientFactory(new FakeChatClient(), "fake-model"), modelId,
            "Pilot", "You fly.", [AIFunctionFactory.Create(() => "ok", "get_word")], []);

    // AC5: the chat session signs in with the token, per session, with no BYOK provider and GitHub telemetry off.
    [Fact]
    public void ChatSessionConfig_GitHubMode_TokenNoProviderTelemetryOff()
    {
        var config = CopilotAgentFactory.BuildSessionConfig(
            Request("github-copilot:claude-sonnet-5"), CopilotProviderSettings.GitHub(Token), "claude-sonnet-5");

        Assert.Equal("claude-sonnet-5", config.Model);
        Assert.Null(config.Provider);
        Assert.Equal(Token, config.GitHubToken);
        Assert.False(config.EnableSessionTelemetry);
    }

    // BYOK is unchanged: the provider, no token, and telemetry left to the SDK (BYOK always disables it).
    [Fact]
    public void ChatSessionConfig_Byok_Unchanged()
    {
        var config = CopilotAgentFactory.BuildSessionConfig(
            Request("foundry:gpt-5-mini"), CopilotProviderSettings.Foundry(new Uri("https://x.example"), "k", false), "gpt-5-mini");

        Assert.NotNull(config.Provider);
        Assert.Null(config.GitHubToken);
        Assert.Null(config.EnableSessionTelemetry);
    }

    // AC5: the fleet's session follows the same rule.
    [Fact]
    public void FleetSessionConfig_GitHubMode_TokenNoProviderTelemetryOff()
    {
        using var workspace = new FleetWorkspace(Guid.NewGuid().ToString("N"));
        using var canvas = new FleetCanvas(new FleetStore(new InMemoryFleetBlobs()), "alice", "w1", new RecordingFleetSink());
        var factory = new FleetCopilotAgentFactory(
            new FleetCopilotHost(new CopilotRuntimeOptions { RuntimePath = _installed }, new FleetOptions { CopilotAgentsEnabled = true }, NullLoggerFactory.Instance),
            new SingleChatClientFactory(new FakeChatClient(), "fake:model-a"), ["fake:model-a"], "fake:model-a",
            workspace, canvas, FleetPromptSet.Defaults,
            new FleetCopilotPermissionPolicy(workspace.Root, new ScriptedFleetInteraction(), CancellationToken.None));

        var config = factory.BuildSessionConfig(CopilotProviderSettings.GitHub(Token), "gpt-6-sol", "You build.");

        Assert.Null(config.Provider);
        Assert.Equal(Token, config.GitHubToken);
        Assert.False(config.EnableSessionTelemetry);
    }

    // The settings carry the token, and a record's generated ToString would print it into any log line.
    [Fact]
    public void GitHubSettings_TokenNeverInToString()
        => Assert.DoesNotContain(Token, CopilotProviderSettings.GitHub(Token).ToString(), StringComparison.Ordinal);

    // AC6: the runtime gets an explicit environment in both modes, so a GitHub token in the host's own environment never
    // reaches it (and with it the shell, or a client-wide identity).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeEnvironment_CarriesNoGitHubToken(bool builtIns)
    {
        string[] names = ["GH_TOKEN", "GITHUB_TOKEN", "COPILOT_GITHUB_TOKEN"];
        foreach (var name in names)
        {
            Environment.SetEnvironmentVariable(name, Token);
        }

        try
        {
            var options = CopilotAgentFactory.BuildClientOptions(
                new CopilotRuntimeOptions { RuntimePath = "/app/copilot", BuiltInToolsEnabled = builtIns });

            Assert.Null(options.GitHubToken);
            Assert.False(options.UseLoggedInUser);
            Assert.DoesNotContain(options.Environment!, e => names.Contains(e.Key) || e.Value.Contains(Token, StringComparison.Ordinal));
        }
        finally
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    // AC7a: a copilot chat pinned to a GitHub model whose provider is gone is refused with "not connected", never run on
    // the user's default BYOK model.
    [Fact]
    public void CreateAgent_DisconnectedGitHubModel_IsNotConnected()
    {
        var factory = new CopilotAgentFactory(
            new InMemoryCopilotSessionStore(), new CopilotRuntimeOptions { RuntimePath = _installed }, NullLoggerFactory.Instance);

        var ex = Assert.Throws<CopilotUnavailableException>(
            () => factory.CreateAgent(Request("github-copilot:claude-sonnet-5", ByokFactory(withGitHub: false))));

        Assert.Equal(CopilotAgentFactory.GitHubNotConnected, ex.Message);
    }

    // A connected GitHub model builds an agent that answers a rejected token with Reconnect; a BYOK one is not wrapped.
    [Fact]
    public async Task CreateAgent_GitHubModel_IsWrapped_ByokIsNot()
    {
        await using var factory = new CopilotAgentFactory(
            new InMemoryCopilotSessionStore(), new CopilotRuntimeOptions { RuntimePath = _installed }, NullLoggerFactory.Instance);
        var users = ByokFactory(withGitHub: true);

        Assert.IsType<GitHubAuthFailureAgent>(factory.CreateAgent(Request("github-copilot:claude-sonnet-5", users)));
        Assert.IsNotType<GitHubAuthFailureAgent>(factory.CreateAgent(Request("github:openai/gpt-4o-mini", users)));
    }

    // AC11a: GitHub rejecting the token before any output becomes the Reconnect answer, streamed and not.
    [Fact]
    public async Task RevokedToken_AnswersReconnect()
    {
        var auth = new IOException("Communication error with Copilot CLI: Request models.list failed with message: "
            + "Failed to fetch Copilot user info: 401 Unauthorized: {\"message\":\"Bad credentials\"}");
        var agent = new GitHubAuthFailureAgent(new ChatClientAgent(new ThrowingChatClient(auth)));

        var streamed = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync("hi"))
        {
            streamed.Add(update);
        }

        Assert.Equal(GitHubAuthFailureAgent.Reconnect, Assert.Single(streamed).Text);
        Assert.Equal(GitHubAuthFailureAgent.Reconnect, (await agent.RunAsync("hi")).Text);
    }

    // Anything else, and an auth failure after output started, propagates unchanged.
    [Fact]
    public async Task OtherFailures_AndLateAuthFailures_Propagate()
    {
        var other = new GitHubAuthFailureAgent(new ChatClientAgent(new ThrowingChatClient(new IOException("socket closed"))));
        await Assert.ThrowsAsync<IOException>(() => other.RunAsync("hi"));

        var late = new GitHubAuthFailureAgent(new ChatClientAgent(
            new ThrowingChatClient(new IOException("401 Unauthorized"), firstText: "partial")));
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in late.RunStreamingAsync("hi"))
            {
            }
        });
    }

    // The texts the SDK raised in the Step 0 spike, and a negative.
    [Theory]
    [InlineData("Failed to fetch Copilot user info: 401 Unauthorized: {\"message\":\"Bad credentials\"}", true)]
    [InlineData("Session error: Execution failed: InvalidArg, No GitHub OAuth token or Copilot HMAC key provided", true)]
    [InlineData("Request models.list failed with message: Not authenticated. Please authenticate first.", true)]
    [InlineData("Timed out after 401 seconds", false)]
    public void IsGitHubAuthFailure_MatchesTheMeasuredTexts(string message, bool expected)
        => Assert.Equal(expected, GitHubAuthFailureAgent.IsGitHubAuthFailure(new InvalidOperationException("outer", new IOException(message))));

    // AC7 (critic F6): the in-process twin has no runtime, and the GitHub model's chat client only refuses, so it
    // refuses up front with a message instead of failing the run.
    [Fact]
    public void Twin_RefusesGitHubModel()
    {
        var ex = Assert.Throws<CopilotUnavailableException>(
            () => new ChatClientCopilotAgentFactory().CreateAgent(Request("github-copilot:claude-sonnet-5")));

        Assert.Equal(ChatClientCopilotAgentFactory.GitHubModelNeedsRuntime, ex.Message);
    }

    private static MultiProviderChatClientFactory ByokFactory(bool withGitHub)
    {
        var options = new ModelProvidersOptions
        {
            Default = "github:openai/gpt-4o-mini",
            Providers =
            {
                ["github"] = new ProviderOptions
                {
                    Kind = ProviderKind.OpenAICompatible,
                    Endpoint = "https://models.github.ai/inference",
                    ApiKey = "pat",
                    Models = [new ConfiguredModel { Id = "openai/gpt-4o-mini", SupportsTools = true }],
                },
            },
        };
        if (withGitHub)
        {
            options.Providers[GitHubCopilotProvider.Key] = new ProviderOptions { Kind = ProviderKind.GitHubCopilot, ApiKey = Token };
        }

        var setup = ModelProviderBuilder.Build(options, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        return new MultiProviderChatClientFactory(
            setup.Providers, "github:openai/gpt-4o-mini", "test", NullLogger<MultiProviderChatClientFactory>.Instance);
    }

    // Fails every request, optionally after streaming one text update first.
    private sealed class ThrowingChatClient(Exception failure, string? firstText = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw failure;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (firstText is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, firstText);
            }

            throw failure;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
